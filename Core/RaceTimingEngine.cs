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

        private RaceStatus _status = RaceStatus.Ready;
        private string _formattedElapsedTime = "00.00.00";

        public ObservableCollection<LaneModel> Lanes { get; } = new();

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
                        SwimmerName = $"Swimmer {i}",
                        Club = $"Club {i}",
                        Status = LaneStatus.Ready
                    };
                    lane.StatusOverrideChangedCallback = (l, s) => SetLaneStatus(l.LaneNumber, s);
                    Lanes.Add(lane);
                }

                if (laneCount >= 10)
                {
                    var lane0 = new LaneModel
                    {
                        LaneNumber = 0,
                        SwimmerName = "Swimmer 0",
                        Club = "Club 0",
                        Status = LaneStatus.Ready
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
            }

            RaceReset?.Invoke();
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
