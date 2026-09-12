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
                string defaultBib = !string.IsNullOrWhiteSpace(bibNumber) ? bibNumber : $"{newRank:D2}";
                string defaultName = !string.IsNullOrWhiteSpace(swimmerName) ? swimmerName : $"Swimmer {newRank}";
                string defaultClub = club ?? string.Empty;

                if (_currentHeat != null)
                {
                    var matchedLane = _currentHeat.Lanes.FirstOrDefault(l => 
                        l.BibNumber.Equals(defaultBib, StringComparison.OrdinalIgnoreCase) || 
                        l.LaneNumber.ToString() == defaultBib);

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
                var ordered = OwsRecords.OrderBy(r => r.FinishTime).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    ordered[i].Rank = i + 1;
                    if (i == 0)
                    {
                        ordered[i].GapTime = "+00.00.00";
                    }
                    else
                    {
                        TimeSpan diff = ordered[i].FinishTime - ordered[0].FinishTime;
                        ordered[i].GapTime = "+" + LaneModel.FormatTime(diff);
                    }
                }
            }
        }

        public void RemoveOwsRecord(OwsRecordModel record)
        {
            lock (_syncLock)
            {
                if (OwsRecords.Remove(record))
                {
                    RecalculateOwsRanks();
                }
            }
        }

        public void SetLaneStatus(int laneNumber, LaneStatus status)
        {
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
                foreach (var lane in Lanes)
                {
                    if (lane.Status != LaneStatus.Finished)
                    {
                        lane.Rank = null;
                    }
                }

                var finishedLanes = Lanes
                    .Where(l => l.Status == LaneStatus.Finished && l.FinishTime.HasValue)
                    .OrderBy(l => l.FinishTime!.Value)
                    .ToList();

                int currentRank = 1;
                for (int i = 0; i < finishedLanes.Count; i++)
                {
                    if (i > 0 && finishedLanes[i].FinishTime == finishedLanes[i - 1].FinishTime)
                    {
                        finishedLanes[i].Rank = finishedLanes[i - 1].Rank;
                    }
                    else
                    {
                        finishedLanes[i].Rank = currentRank;
                    }
                    currentRank++;
                }
            }
        }

        public void LoadHeat(HeatModel heat)
        {
            lock (_syncLock)
            {
                _currentHeat = heat;

                if (CurrentMode == TimingMode.OpenWater)
                {
                    OwsRecords.Clear();
                    foreach (var l in heat.Lanes.Where(x => x.Status == LaneStatus.Finished && x.FinishTime.HasValue).OrderBy(x => x.Rank ?? 999))
                    {
                        OwsRecords.Add(new OwsRecordModel
                        {
                            Rank = l.Rank ?? 0,
                            BibNumber = l.BibNumber,
                            SwimmerName = l.SwimmerName,
                            Club = l.Club,
                            FinishTime = l.FinishTime!.Value,
                            FormattedTime = l.FormattedTime
                        });
                    }
                    if (OwsRecords.Count > 0)
                    {
                        RecalculateOwsRanks();
                    }
                }

                foreach (var engineLane in Lanes)
                {
                    var heatLane = heat.Lanes.FirstOrDefault(l => l.LaneNumber == engineLane.LaneNumber);

                    if (heatLane != null && !string.IsNullOrWhiteSpace(heatLane.SwimmerName))
                    {
                        engineLane.SwimmerName = heatLane.SwimmerName;
                        engineLane.Club = heatLane.Club;
                        engineLane.SeedTime = heatLane.SeedTime;
                        engineLane.Status = heatLane.Status != LaneStatus.OFF ? heatLane.Status : LaneStatus.Ready;
                        engineLane.FinishTime = heatLane.FinishTime;
                        engineLane.FormattedTime = heatLane.FinishTime.HasValue 
                            ? LaneModel.FormatTime(heatLane.FinishTime.Value) 
                            : "00.00.00";
                        engineLane.Rank = heatLane.Rank;
                    }
                    else
                    {
                        engineLane.SwimmerName = string.Empty;
                        engineLane.Club = string.Empty;
                        engineLane.SeedTime = string.Empty;
                        engineLane.Status = LaneStatus.OFF;
                        engineLane.FinishTime = null;
                        engineLane.FormattedTime = "00.00.00";
                        engineLane.Rank = null;
                    }
                }

                // Check if the loaded heat already has recorded results
                bool hasResults = heat.HasResults;
                if (hasResults)
                {
                    heat.IsCompleted = true;
                    Status = RaceStatus.Finished;
                    var maxFinish = heat.Lanes
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
        }

        public void SaveResultsToHeat(HeatModel heat)
        {
            lock (_syncLock)
            {
                if (CurrentMode == TimingMode.OpenWater)
                {
                    foreach (var record in OwsRecords)
                    {
                        var targetLane = heat.Lanes.FirstOrDefault(l => l.BibNumber.Equals(record.BibNumber, StringComparison.OrdinalIgnoreCase));
                        if (targetLane != null)
                        {
                            targetLane.Status = LaneStatus.Finished;
                            targetLane.FinishTime = record.FinishTime;
                            targetLane.FormattedTime = record.FormattedTime;
                            targetLane.Rank = record.Rank;
                        }
                        else
                        {
                            heat.Lanes.Add(new LaneModel
                            {
                                LaneNumber = heat.Lanes.Count + 1,
                                BibNumber = record.BibNumber,
                                SwimmerName = record.SwimmerName,
                                Club = record.Club,
                                Status = LaneStatus.Finished,
                                FinishTime = record.FinishTime,
                                FormattedTime = record.FormattedTime,
                                Rank = record.Rank
                            });
                        }
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
                        heatLane.Status = engineLane.Status;
                        heatLane.FinishTime = engineLane.FinishTime;
                        heatLane.FormattedTime = engineLane.FormattedTime;
                    }
                }
                heat.CalculateRanks();
                heat.IsCompleted = heat.HasResults;
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
                CalculateRanks();
                FormattedElapsedTime = LaneModel.FormatTime(finalTime);
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
