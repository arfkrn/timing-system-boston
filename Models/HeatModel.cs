using System;
using System.Collections.ObjectModel;
using System.Linq;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class HeatModel : ObservableObject
    {
        private int _heatNumber;
        private int _eventNumber;
        private string _eventName = string.Empty;
        private bool _isCompleted;

        public int HeatNumber
        {
            get => _heatNumber;
            set
            {
                if (SetProperty(ref _heatNumber, value))
                {
                    OnPropertyChanged(nameof(DisplayTitle));
                }
            }
        }

        public int EventNumber
        {
            get => _eventNumber;
            set => SetProperty(ref _eventNumber, value);
        }

        public string EventName
        {
            get => _eventName;
            set => SetProperty(ref _eventName, value);
        }

        public bool IsCompleted
        {
            get => _isCompleted && HasResults;
            set => SetProperty(ref _isCompleted, value);
        }

        public bool HasResults => Lanes.Any(l => 
            (l.Status == LaneStatus.Finished && (l.FinishTime.HasValue || (!string.IsNullOrEmpty(l.FormattedTime) && l.FormattedTime != "00.00.00"))) ||
            l.Status == LaneStatus.DQ || 
            l.Status == LaneStatus.DNF);

        public ObservableCollection<LaneModel> Lanes { get; set; } = new();

        public string DisplayTitle => $"Heat {HeatNumber}";

        public HeatModel(int heatNumber = 1, int eventNumber = 1, string eventName = "")
        {
            _heatNumber = heatNumber;
            _eventNumber = eventNumber;
            _eventName = eventName;

            // Initialize 10 lanes by default (Lanes 1 to 9, and Lane 0 for the 10th lane)
            for (int i = 1; i <= 9; i++)
            {
                Lanes.Add(new LaneModel
                {
                    LaneNumber = i,
                    Status = LaneStatus.OFF
                });
            }
            Lanes.Add(new LaneModel
            {
                LaneNumber = 0,
                Status = LaneStatus.OFF
            });
        }

        public void ClearSwimmers()
        {
            foreach (var lane in Lanes)
            {
                lane.SwimmerName = string.Empty;
                lane.Club = string.Empty;
                lane.SeedTime = "--:--.--";
                lane.FinishTime = null;
                lane.FormattedTime = "00.00.00";
                lane.Rank = null;
                lane.Status = LaneStatus.OFF;
            }
            IsCompleted = false;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(IsCompleted));
        }

        public void CalculateRanks()
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

            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(IsCompleted));
        }
    }
}
