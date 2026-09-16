using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using boston_timing_system.Helpers;
using boston_timing_system.Models;

namespace boston_timing_system.Core
{
    public class RaceTimingEngine : ObservableObject, IDisposable
    {
        private readonly HighPrecisionTimer _timer = new();
        private readonly object _syncLock = new();

        public object SyncRoot => _syncLock;

        private RaceStatus _status = RaceStatus.Ready;
        private string _formattedElapsedTime = "00.00.00";
        private TimingMode _currentMode = TimingMode.Pool;
        private HeatModel? _currentHeat;
        public HeatModel? CurrentHeat => _currentHeat;

        public ObservableCollection<LaneModel> Lanes { get; } = new();
        public ObservableCollection<OwsRecordModel> OwsRecords { get; } = new();

        public TimingMode CurrentMode
        {
            get => _currentMode;
            set
            {
                if (SetProperty(ref _currentMode, value))
                {
                    OnPropertyChanged(nameof(IsPoolMode));
                    OnPropertyChanged(nameof(IsOpenWaterMode));
                    ModeChanged?.Invoke(value);
                }
            }
        }

        public bool IsPoolMode => CurrentMode == TimingMode.Pool;
        public bool IsOpenWaterMode => CurrentMode == TimingMode.OpenWater;

        public void SetMode(TimingMode mode)
        {
            CurrentMode = mode;
        }

        public RaceStatus Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(CanStart));
                    OnPropertyChanged(nameof(CanStop));
                    OnPropertyChanged(nameof(CanReset));
                }
            }
        }

        public string FormattedElapsedTime
        {
            get => _formattedElapsedTime;
            private set => SetProperty(ref _formattedElapsedTime, value);
        }

        public TimeSpan Elapsed => _timer.Elapsed;

        public bool IsRunning => Status == RaceStatus.Running;
        public bool CanStart => Status == RaceStatus.Ready;
        public bool CanStop => Status == RaceStatus.Running;
        public bool CanReset => Status != RaceStatus.Running;

        private bool _isLoadingHeat;
        public bool IsLoadingHeat => _isLoadingHeat;

        // Events for subscribers (UI, WebSocket broadcaster, Logger, etc.)
        public event Action? RaceStarted;
        public event Action? RaceStopped;
        public event Action? RaceReset;
        public event Action<LaneModel, TimeSpan>? LaneFinished;
        public event Action<LaneModel, LaneStatus>? LaneStatusChanged;
        public event Action<LaneModel, TimeSpan>? LaneSplitRecorded;
        public event Action<TimeSpan>? Tick;
        public event Action<TimingMode>? ModeChanged;
        public event Action<OwsRecordModel>? OwsFinishRecorded;
        public event Action<OwsRecordModel, LaneStatus>? OwsRecordStatusChanged;

        public RaceTimingEngine(int defaultLaneCount = 10)
        {
            _timer.OnTick += HandleTimerTick;
            InitializeLanes(defaultLaneCount);
        }

        public void InitializeLanes(int laneCount = 10)
        {
            lock (_syncLock)
            {
                Lanes.Clear();
                int normalLanes = Math.Min(laneCount, 9);
                for (int i = 1; i <= normalLanes; i++)
                {
                    var lane = new LaneModel
                    {
                        LaneNumber = i,
                        SwimmerName = string.Empty,
                        Club = string.Empty,
                        SeedTime = string.Empty,
                        Status = LaneStatus.OFF
                    };
                    lane.StatusOverrideChangedCallback = (l, s) => SetLaneStatus(l.LaneNumber, s);
                    Lanes.Add(lane);
                }

                if (laneCount >= 10)
                {
                    var lane0 = new LaneModel
                    {
                        LaneNumber = 0,
                        SwimmerName = string.Empty,
                        Club = string.Empty,
                        SeedTime = string.Empty,
                        Status = LaneStatus.OFF
                    };
                    lane0.StatusOverrideChangedCallback = (l, s) => SetLaneStatus(l.LaneNumber, s);
                    Lanes.Add(lane0);
                }
            }
        }

        public bool StartRace()
        {
            lock (_syncLock)
            {
                if (!CanStart)
                {
                    return false;
                }

                // Jangan izinkan start jika belum ada data event/heat atau peserta kosong
                bool hasParticipants = false;
                if (CurrentMode == TimingMode.OpenWater)
                {
                    hasParticipants = _currentHeat != null && _currentHeat.Lanes.Any(l => 
                        l.Status != LaneStatus.OFF && 
                        (!string.IsNullOrWhiteSpace(l.SwimmerName) || !string.IsNullOrWhiteSpace(l.BibNumber)));
                }
                else
                {
                    hasParticipants = Lanes.Any(l => l.Status == LaneStatus.Ready);
                }

                if (!hasParticipants)
                {
                    return false;
                }

                _timer.Reset();
                _timer.Start(intervalMs: 15);

                Status = RaceStatus.Running;

                foreach (var lane in Lanes)
                {
                    if (lane.Status == LaneStatus.Ready)
                    {
                        lane.SetRunning();
                    }
                }
            }

            RaceStarted?.Invoke();
            return true;
        }

        public bool StopLane(int laneNumber, TimeSpan? compensatedTime = null)
        {
            TimeSpan finishTime;
            LaneModel? targetLane;
            bool autoStopped = false;

            lock (_syncLock)
            {
                if (Status != RaceStatus.Running)
                {
                    return false;
                }

                targetLane = Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
                if (targetLane == null || targetLane.Status != LaneStatus.Running)
                {
                    return false;
                }

                TimeSpan currentElapsed = _timer.Elapsed;

                // Apply compensated time if provided, safely bounded
                if (compensatedTime.HasValue)
                {
                    finishTime = compensatedTime.Value;
                    if (finishTime < TimeSpan.Zero)
                    {
                        finishTime = TimeSpan.Zero;
                    }
                    else if (finishTime > currentElapsed)
                    {
                        finishTime = currentElapsed;
                    }
                }
                else
                {
                    finishTime = currentElapsed;
                }

                targetLane.SetFinished(finishTime);

                autoStopped = CheckAndAutoFinishRace();
            }

            LaneFinished?.Invoke(targetLane, finishTime);

            if (autoStopped)
            {
                RaceStopped?.Invoke();
            }

            return true;
        }

        public bool RecordLaneSplit(int laneNumber, TimeSpan? compensatedTime = null)
        {
            TimeSpan splitTime;
            LaneModel? targetLane;

            lock (_syncLock)
            {
                if (Status != RaceStatus.Running)
                {
                    return false;
                }

                targetLane = Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
                if (targetLane == null || targetLane.Status != LaneStatus.Running)
                {
                    return false;
                }

                TimeSpan currentElapsed = _timer.Elapsed;
                if (compensatedTime.HasValue)
                {
                    splitTime = compensatedTime.Value;
                    if (splitTime < TimeSpan.Zero)
                    {
                        splitTime = TimeSpan.Zero;
                    }
                    else if (splitTime > currentElapsed)
                    {
                        splitTime = currentElapsed;
                    }
                }
                else
                {
                    splitTime = currentElapsed;
                }

                targetLane.AddSplit(splitTime);
            }

            LaneSplitRecorded?.Invoke(targetLane, splitTime);
            return true;
        }

        public bool StopRace()
        {
            TimeSpan finalElapsed;

            lock (_syncLock)
            {
                if (Status != RaceStatus.Running)
                {
                    return false;
                }

                finalElapsed = _timer.Stop();
                Status = RaceStatus.Finished;

                // Any lane still marked running is now stopped with final time
                foreach (var lane in Lanes)
                {
                    if (lane.Status == LaneStatus.Running)
                    {
                        lane.SetFinished(finalElapsed);
                    }
                }

                CalculateRanks();
                FormattedElapsedTime = LaneModel.FormatTime(finalElapsed);
            }

            RaceStopped?.Invoke();
            return true;
        }

        public int StopActiveLanes(TimeSpan? compensatedTime = null)
        {
            int stoppedCount = 0;
            var finishedLanes = new List<LaneModel>();
            bool autoStopped = false;

            lock (_syncLock)
            {
                if (Status != RaceStatus.Running)
                {
                    return 0;
                }

                TimeSpan currentElapsed = _timer.Elapsed;
                TimeSpan stopTime = currentElapsed;

                if (compensatedTime.HasValue)
                {
                    stopTime = compensatedTime.Value;
                    if (stopTime < TimeSpan.Zero)
                    {
                        stopTime = TimeSpan.Zero;
                    }
                    else if (stopTime > currentElapsed)
                    {
                        stopTime = currentElapsed;
                    }
                }

                foreach (var lane in Lanes)
                {
                    if (lane.Status == LaneStatus.Running)
                    {
                        lane.SetFinished(stopTime);
                        finishedLanes.Add(lane);
                        stoppedCount++;
                    }
                }

                autoStopped = CheckAndAutoFinishRace();
            }

            foreach (var lane in finishedLanes)
            {
                LaneFinished?.Invoke(lane, lane.FinishTime ?? TimeSpan.Zero);
            }

            if (autoStopped)
            {
                RaceStopped?.Invoke();
            }

            return stoppedCount;
        }

        public void ResetRace()
        {
            lock (_syncLock)
            {
                _timer.Reset();
                Status = RaceStatus.Ready;
                FormattedElapsedTime = "00.00.00";

                foreach (var lane in Lanes)
                {
                    lane.Reset();
                }

                OwsRecords.Clear();
            }

            RaceReset?.Invoke();
        }

        public OwsRecordModel? RecordOwsFinish(TimeSpan? compensatedTime = null, string? bibNumber = null, string? swimmerName = null, string? club = null)
        {
            OwsRecordModel record;
            bool autoStopped = false;
            lock (_syncLock)
            {
                if (Status != RaceStatus.Running)
                {
                    return null;
                }

                // Check quota peserta maksimal OWS: tidak boleh melebihi jumlah peserta yang terdaftar pada heat/event ini
                int maxParticipants = 0;
                if (_currentHeat != null)
                {
                    maxParticipants = _currentHeat.Lanes.Count(l => 
                        l.Status != LaneStatus.OFF && 
                        (!string.IsNullOrWhiteSpace(l.SwimmerName) || !string.IsNullOrWhiteSpace(l.BibNumber)));
                }

                if (maxParticipants > 0 && OwsRecords.Count >= maxParticipants)
                {
                    return null;
                }

                TimeSpan currentElapsed = _timer.Elapsed;
                TimeSpan finishTime = currentElapsed;

                if (compensatedTime.HasValue)
                {
                    finishTime = compensatedTime.Value;
                    if (finishTime < TimeSpan.Zero) finishTime = TimeSpan.Zero;
                    else if (finishTime > currentElapsed) finishTime = currentElapsed;
                }

                // Invariant: Finisher berikutnya dalam lorong finis OWS tidak boleh memiliki waktu lebih kecil dari finisher sebelumnya
                if (OwsRecords.Count > 0)
                {
                    var lastFinisher = OwsRecords.Last();
                    if (finishTime < lastFinisher.FinishTime)
                    {
                        finishTime = lastFinisher.FinishTime;
                    }
                }

                int newRank = OwsRecords.Count + 1;

                // If no bib provided, leave it empty — operator will fill it manually in the UI.
                // SwimmerName and Club will be resolved when the operator enters the BibNumber.
                string defaultBib  = !string.IsNullOrWhiteSpace(bibNumber)  ? bibNumber  : string.Empty;
                string defaultName = !string.IsNullOrWhiteSpace(swimmerName) ? swimmerName : string.Empty;
                string defaultClub = !string.IsNullOrWhiteSpace(club)        ? club        : string.Empty;

                if (_currentHeat != null)
                {
                    // Use strict matching: prefer lanes with explicit BibNumber set,
                    // only fall back to LaneNumber matching for lanes without an explicit bib.
                    // This prevents ambiguous double-match when a LaneNumber coincidentally
                    // equals another participant's explicit BibNumber.
                    var matchedLane = _currentHeat.Lanes.FirstOrDefault(l =>
                        l.HasExplicitBibNumber
                            ? l.BibNumber.Equals(defaultBib, StringComparison.OrdinalIgnoreCase)
                            : l.LaneNumber.ToString() == defaultBib);

                    if (matchedLane != null)
                    {
                        if (string.IsNullOrWhiteSpace(swimmerName) && !string.IsNullOrWhiteSpace(matchedLane.SwimmerName))
                        {
                            defaultName = matchedLane.SwimmerName;
                        }
                        if (string.IsNullOrWhiteSpace(club) && !string.IsNullOrWhiteSpace(matchedLane.Club))
                        {
                            defaultClub = matchedLane.Club;
                        }

                        matchedLane.Status = LaneStatus.Finished;
                        matchedLane.FinishTime = finishTime;
                        matchedLane.FormattedTime = LaneModel.FormatTime(finishTime);
                        matchedLane.Rank = newRank;
                    }
                }

                string gap = "+00.00.00";
                if (OwsRecords.Count > 0)
                {
                    TimeSpan leadTime = OwsRecords[0].FinishTime;
                    TimeSpan diff = finishTime - leadTime;
                    if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
                    gap = "+" + LaneModel.FormatTime(diff);
                }

                record = new OwsRecordModel
                {
                    Rank = newRank,
                    BibNumber = defaultBib,
                    SwimmerName = defaultName,
                    Club = defaultClub,
                    FinishTime = finishTime,
                    GapTime = gap,
                    Status = LaneStatus.Finished
                };
                record.StatusChangedCallback = HandleOwsRecordStatusChanged;

                OwsRecords.Add(record);

                // Jika seluruh peserta yang terdaftar pada heat/event ini telah finis, auto-stop race!
                if (maxParticipants > 0 && OwsRecords.Count >= maxParticipants)
                {
                    _timer.Stop();
                    Status = RaceStatus.Finished;
                    FormattedElapsedTime = LaneModel.FormatTime(finishTime);
                    autoStopped = true;
                }
            }

            OwsFinishRecorded?.Invoke(record);

            if (autoStopped)
            {
                RaceStopped?.Invoke();
            }

            return record;
        }

        public void RecalculateOwsRanks()
        {
            lock (_syncLock)
            {
                if (OwsRecords.Count == 0) return;

                // Hanya peserta dengan status Finished yang berhak mendapatkan peringkat numerik (1, 2, 3...)
                var finishedList = OwsRecords.Where(r => r.Status == LaneStatus.Finished).OrderBy(r => r.FinishTime).ToList();
                for (int i = 0; i < finishedList.Count; i++)
                {
                    finishedList[i].Rank = i + 1;
                    if (i == 0)
                    {
                        finishedList[i].GapTime = "+00.00.00";
                    }
                    else
                    {
                        TimeSpan diff = finishedList[i].FinishTime - finishedList[0].FinishTime;
                        finishedList[i].GapTime = "+" + LaneModel.FormatTime(diff);
                    }

                    if (_currentHeat != null && !string.IsNullOrWhiteSpace(finishedList[i].BibNumber))
                    {
                        var matchedLane = _currentHeat.Lanes.FirstOrDefault(l =>
                            l.HasExplicitBibNumber
                                ? l.BibNumber.Equals(finishedList[i].BibNumber, StringComparison.OrdinalIgnoreCase)
                                : l.LaneNumber.ToString() == finishedList[i].BibNumber);
                        if (matchedLane != null)
                        {
                            matchedLane.Rank = finishedList[i].Rank;
                            matchedLane.Status = LaneStatus.Finished;
                        }
                    }
                }

                // Peserta dengan status non-Finished (DQ, DNS, DNF) tidak mendapatkan nomor rank
                var nonFinishedList = OwsRecords.Where(r => r.Status != LaneStatus.Finished).ToList();
                foreach (var nonFinisher in nonFinishedList)
                {
                    nonFinisher.Rank = 0;
                    nonFinisher.GapTime = "—";

                    if (_currentHeat != null && !string.IsNullOrWhiteSpace(nonFinisher.BibNumber))
                    {
                        var matchedLane = _currentHeat.Lanes.FirstOrDefault(l =>
                            l.HasExplicitBibNumber
                                ? l.BibNumber.Equals(nonFinisher.BibNumber, StringComparison.OrdinalIgnoreCase)
                                : l.LaneNumber.ToString() == nonFinisher.BibNumber);
                        if (matchedLane != null)
                        {
                            matchedLane.Rank = null;
                            matchedLane.Status = nonFinisher.Status;
                        }
                    }
                }
            }
        }

        private void HandleOwsRecordStatusChanged(OwsRecordModel record, LaneStatus newStatus)
        {
            lock (_syncLock)
            {
                if (_currentHeat != null && !string.IsNullOrWhiteSpace(record.BibNumber))
                {
                    var matchedLane = _currentHeat.Lanes.FirstOrDefault(l =>
                        l.HasExplicitBibNumber
                            ? l.BibNumber.Equals(record.BibNumber, StringComparison.OrdinalIgnoreCase)
                            : l.LaneNumber.ToString() == record.BibNumber);
                    if (matchedLane != null)
                    {
                        matchedLane.Status = newStatus;
                        if (newStatus != LaneStatus.Finished)
                        {
                            matchedLane.Rank = null;
                        }
                    }

                    var engLane = Lanes.FirstOrDefault(l => matchedLane != null && l.LaneNumber == matchedLane.LaneNumber);
                    if (engLane != null)
                    {
                        engLane.Status = newStatus;
                        if (newStatus != LaneStatus.Finished)
                        {
                            engLane.Rank = null;
                        }
                    }
                }

                RecalculateOwsRanks();
            }

            OwsRecordStatusChanged?.Invoke(record, newStatus);
        }

        public void RemoveOwsRecord(OwsRecordModel record)
        {
            lock (_syncLock)
            {
                if (OwsRecords.Remove(record))
                {
                    if (_currentHeat != null && !string.IsNullOrWhiteSpace(record.BibNumber))
                    {
                        var matchedLane = _currentHeat.Lanes.FirstOrDefault(l =>
                            l.HasExplicitBibNumber
                                ? l.BibNumber.Equals(record.BibNumber, StringComparison.OrdinalIgnoreCase)
                                : l.LaneNumber.ToString() == record.BibNumber);
                        if (matchedLane != null)
                        {
                            matchedLane.Status = Status == RaceStatus.Running ? LaneStatus.Running : LaneStatus.Ready;
                            matchedLane.FinishTime = null;
                            matchedLane.FormattedTime = "00.00.00";
                            matchedLane.Rank = null;
                        }
                    }

                    RecalculateOwsRanks();
                }
            }
        }

        public void SetLaneStatus(int laneNumber, LaneStatus status)
        {
            if (_isLoadingHeat) return;

            bool autoStopped = false;
            LaneModel? targetLane;

            lock (_syncLock)
            {
                targetLane = Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
                if (targetLane != null)
                {
                    targetLane.Status = status;
                    if (Status == RaceStatus.Running)
                    {
                        autoStopped = CheckAndAutoFinishRace();
                    }
                    else if (Status == RaceStatus.Finished)
                    {
                        CalculateRanks();
                    }
                }
            }

            if (targetLane != null)
            {
                LaneStatusChanged?.Invoke(targetLane, status);
            }

            if (autoStopped)
            {
                RaceStopped?.Invoke();
            }
        }

        public void CalculateRanks()
        {
            lock (_syncLock)
            {
                RankingHelper.CalculateRanks(Lanes);
            }
        }

        public void LoadHeat(HeatModel heat)
        {
            lock (_syncLock)
            {
                _isLoadingHeat = true;
                try
                {
                    _currentHeat = heat;

                    foreach (var lane in Lanes)
                    {
                        lane.SuppressStatusCallbacks = true;
                    }

                    if (CurrentMode == TimingMode.OpenWater)
                    {
                        OwsRecords.Clear();
                        foreach (var l in heat.Lanes.Where(x => (x.Status == LaneStatus.Finished || x.Status == LaneStatus.DQ || x.Status == LaneStatus.DNF || x.Status == LaneStatus.DNS) && x.FinishTime.HasValue).OrderBy(x => x.Rank ?? 999))
                        {
                            var rec = new OwsRecordModel
                            {
                                Rank = l.Rank ?? 0,
                                BibNumber = l.BibNumber,
                                SwimmerName = l.SwimmerName,
                                Club = l.Club,
                                FinishTime = l.FinishTime!.Value,
                                FormattedTime = l.FormattedTime,
                                Status = l.Status
                            };
                            rec.StatusChangedCallback = HandleOwsRecordStatusChanged;
                            OwsRecords.Add(rec);
                        }
                        if (OwsRecords.Count > 0)
                        {
                            RecalculateOwsRanks();
                        }
                    }

                    foreach (var engineLane in Lanes)
                    {
                        var heatLane = heat.Lanes.FirstOrDefault(l => l.LaneNumber == engineLane.LaneNumber);

                        bool hasSwimmer = heatLane != null && !string.IsNullOrWhiteSpace(heatLane.SwimmerName);
                        bool hasRecordedData = heatLane != null && (heatLane.FinishTime.HasValue || 
                            heatLane.Status == LaneStatus.Finished || 
                            heatLane.Status == LaneStatus.DQ || 
                            heatLane.Status == LaneStatus.DNF || 
                            heatLane.Status == LaneStatus.DNS ||
                            (!string.IsNullOrEmpty(heatLane.FormattedTime) && heatLane.FormattedTime != "00.00.00"));
                        bool isActiveLane = heatLane != null && heatLane.Status != LaneStatus.OFF && heatLane.Status != LaneStatus.Empty;

                        if (heatLane != null && (hasSwimmer || hasRecordedData || isActiveLane))
                        {
                            TimeSpan? finalFinishTime = heatLane.FinishTime;
                            if (!finalFinishTime.HasValue && !string.IsNullOrEmpty(heatLane.FormattedTime) && heatLane.FormattedTime != "00.00.00")
                            {
                                if (LaneModel.TryParseFormattedTime(heatLane.FormattedTime, out var parsedTime))
                                {
                                    finalFinishTime = parsedTime;
                                    heatLane.FinishTime = parsedTime;
                                }
                            }

                            // 1. FinishTime & Timers FIRST
                            engineLane.FinishTime = finalFinishTime;
                            engineLane.FormattedTime = finalFinishTime.HasValue 
                                ? LaneModel.FormatTime(finalFinishTime.Value) 
                                : (!string.IsNullOrEmpty(heatLane.FormattedTime) ? heatLane.FormattedTime : "00.00.00");

                            engineLane.Timer1 = !string.IsNullOrEmpty(heatLane.Timer1) && heatLane.Timer1 != "00.00.00"
                                ? heatLane.Timer1
                                : (finalFinishTime.HasValue ? LaneModel.FormatTime(finalFinishTime.Value) : "00.00.00");
                            engineLane.Timer2 = !string.IsNullOrEmpty(heatLane.Timer2) ? heatLane.Timer2 : "00.00.00";

                            // 2. Status & Swimmer Info
                            LaneStatus targetStatus = heatLane.Status;
                            if (finalFinishTime.HasValue && targetStatus != LaneStatus.DQ && targetStatus != LaneStatus.DNS && targetStatus != LaneStatus.DNF && targetStatus != LaneStatus.OFF)
                            {
                                targetStatus = LaneStatus.Finished;
                            }
                            else if (targetStatus == LaneStatus.OFF && hasSwimmer)
                            {
                                targetStatus = LaneStatus.Ready;
                            }
                            engineLane.Status = targetStatus;
                            heatLane.Status = targetStatus;
                            heatLane.Timer1 = engineLane.Timer1;

                            engineLane.SwimmerName = heatLane.SwimmerName ?? string.Empty;
                            engineLane.Club = heatLane.Club ?? string.Empty;
                            engineLane.SeedTime = heatLane.SeedTime ?? string.Empty;

                            engineLane.Rank = heatLane.Rank;
                            if (heatLane.Splits != null && heatLane.Splits.Count > 0)
                            {
                                engineLane.Splits = new List<TimeSpan>(heatLane.Splits);
                            }
                            else
                            {
                                engineLane.Splits.Clear();
                            }
                        }
                        else
                        {
                            engineLane.FinishTime = null;
                            engineLane.FormattedTime = "00.00.00";
                            engineLane.Rank = null;
                            engineLane.Timer1 = "00.00.00";
                            engineLane.Timer2 = "00.00.00";
                            engineLane.Status = LaneStatus.OFF;
                            engineLane.SwimmerName = string.Empty;
                            engineLane.Club = string.Empty;
                            engineLane.SeedTime = string.Empty;
                            engineLane.Splits.Clear();
                        }
                    }

                    // Check if the loaded heat already has recorded results
                    bool hasResults = heat.HasResults;
                    if (hasResults)
                    {
                        heat.IsCompleted = true;
                        Status = RaceStatus.Finished;
                        CalculateRanks();
                        foreach (var engLane in Lanes)
                        {
                            var hl = heat.Lanes.FirstOrDefault(l => l.LaneNumber == engLane.LaneNumber);
                            if (hl != null)
                            {
                                hl.Rank = engLane.Rank;
                                hl.Status = engLane.Status;
                                hl.Timer1 = engLane.Timer1;
                                hl.Timer2 = engLane.Timer2;
                                hl.FinishTime = engLane.FinishTime;
                                hl.FormattedTime = engLane.FormattedTime;
                            }
                        }
                        var maxFinish = Lanes
                            .Where(l => l.FinishTime.HasValue)
                            .Select(l => l.FinishTime!.Value)
                            .DefaultIfEmpty(TimeSpan.Zero)
                            .Max();
                        FormattedElapsedTime = maxFinish > TimeSpan.Zero ? LaneModel.FormatTime(maxFinish) : "00.00.00";
                    }
                    else
                    {
                        heat.IsCompleted = false;
                        Status = RaceStatus.Ready;
                        FormattedElapsedTime = "00.00.00";
                    }
                }
                finally
                {
                    foreach (var lane in Lanes)
                    {
                        lane.SuppressStatusCallbacks = false;
                    }
                    _isLoadingHeat = false;
                }
            }
        }

        public void SaveResultsToHeat(HeatModel heat)
        {
            lock (_syncLock)
            {
                if (_isLoadingHeat)
                {
                    return;
                }

                if (CurrentMode == TimingMode.OpenWater)
                {
                    foreach (var record in OwsRecords)
                    {
                        // Only update existing participants — never add new lanes from OWS records.
                        // New lanes are only created via Meet Manager / Excel import.
                        // Records whose BibNumber has not yet been identified (empty bib) are skipped.
                        if (string.IsNullOrWhiteSpace(record.BibNumber))
                        {
                            continue;
                        }

                        // Use the same strict-matching strategy: explicit bib takes precedence,
                        // fall back to LaneNumber only when no explicit bib is assigned.
                        var targetLane = heat.Lanes.FirstOrDefault(l =>
                            l.HasExplicitBibNumber
                                ? l.BibNumber.Equals(record.BibNumber, StringComparison.OrdinalIgnoreCase)
                                : l.LaneNumber.ToString() == record.BibNumber);

                        if (targetLane != null)
                        {
                            targetLane.Status = record.Status;
                            targetLane.FinishTime = record.FinishTime;
                            targetLane.FormattedTime = record.FormattedTime;
                            targetLane.Rank = record.Rank > 0 ? record.Rank : null;
                            // Keep name/club in sync in case operator edited them in OWS table
                            if (!string.IsNullOrWhiteSpace(record.SwimmerName))
                                targetLane.SwimmerName = record.SwimmerName;
                            if (!string.IsNullOrWhiteSpace(record.Club))
                                targetLane.Club = record.Club;
                        }
                        // If no matching lane found, skip — do NOT create a new lane
                    }
                    heat.CalculateRanks();
                    heat.IsCompleted = heat.HasResults;
                    return;
                }

                foreach (var engineLane in Lanes)
                {
                    var heatLane = heat.Lanes.FirstOrDefault(l => l.LaneNumber == engineLane.LaneNumber);

                    if (heatLane != null)
                    {
                        if (engineLane.FinishTime.HasValue && engineLane.Status != LaneStatus.DQ && engineLane.Status != LaneStatus.DNS && engineLane.Status != LaneStatus.DNF && engineLane.Status != LaneStatus.OFF)
                        {
                            engineLane.Status = LaneStatus.Finished;
                        }
                        heatLane.Status = engineLane.Status;
                        heatLane.FinishTime = engineLane.FinishTime;
                        heatLane.FormattedTime = engineLane.FormattedTime;
                        heatLane.Timer1 = !string.IsNullOrEmpty(engineLane.Timer1) && engineLane.Timer1 != "00.00.00"
                            ? engineLane.Timer1
                            : (engineLane.FinishTime.HasValue ? LaneModel.FormatTime(engineLane.FinishTime.Value) : "00.00.00");
                        heatLane.Timer2 = engineLane.Timer2;
                        heatLane.Rank = engineLane.Rank;
                        if (engineLane.Splits.Count > 0)
                        {
                            heatLane.Splits = new List<TimeSpan>(engineLane.Splits);
                        }
                    }
                }
                heat.CalculateRanks();
                heat.IsCompleted = heat.HasResults;
            }
        }

        /// <summary>
        /// Looks up a registered participant from the currently loaded heat by their BIB number.
        /// Used by the OWS results table to auto-fill SwimmerName and Club when the operator types a BIB.
        /// Returns null if no participant with the given BIB is registered in the current heat.
        /// </summary>
        public LaneModel? LookupParticipantByBib(string bibNumber)
        {
            if (string.IsNullOrWhiteSpace(bibNumber) || _currentHeat == null)
            {
                return null;
            }

            lock (_syncLock)
            {
                return _currentHeat.Lanes.FirstOrDefault(l =>
                    l.HasExplicitBibNumber
                        ? l.BibNumber.Equals(bibNumber, StringComparison.OrdinalIgnoreCase)
                        : l.LaneNumber.ToString() == bibNumber);
            }
        }

        public void UpdateSwimmerInfo(int laneNumber, string swimmerName, string club)
        {
            lock (_syncLock)
            {
                var targetLane = Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
                if (targetLane != null)
                {
                    targetLane.SwimmerName = swimmerName;
                    targetLane.Club = club;
                }
            }
        }

        private bool CheckAndAutoFinishRace()
        {
            // If all lanes that were participating have finished (or DNS/DQ/DNF), stop the race automatically
            bool anyRunning = Lanes.Any(l => l.Status == LaneStatus.Running);
            if (!anyRunning && Status == RaceStatus.Running)
            {
                var finalTime = _timer.Stop();
                Status = RaceStatus.Finished;

                var lastFinisherTime = Lanes.Where(l => l.Status == LaneStatus.Finished && l.FinishTime.HasValue).Select(l => l.FinishTime!.Value).DefaultIfEmpty(_timer.Elapsed).Max();

                CalculateRanks();
                FormattedElapsedTime = LaneModel.FormatTime(lastFinisherTime);
                return true;
            }
            return false;
        }

        private void HandleTimerTick(TimeSpan elapsed)
        {
            FormattedElapsedTime = LaneModel.FormatTime(elapsed);
            Tick?.Invoke(elapsed);
        }

        public void Dispose()
        {
            _timer.OnTick -= HandleTimerTick;
            _timer.Dispose();
        }
    }
}
