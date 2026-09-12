using System;
using System.Collections.Generic;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class OwsRecordModel : ObservableObject
    {
        private int _rank;
        private string _bibNumber = string.Empty;
        private string _swimmerName = string.Empty;
        private string _club = string.Empty;
        private TimeSpan _finishTime = TimeSpan.Zero;
        private string _formattedTime = "00.00.00";
        private string _gapTime = "+00.00.00";
        private LaneStatus _status = LaneStatus.Finished;

        public int Rank
        {
            get => _rank;
            set => SetProperty(ref _rank, value);
        }

        public string BibNumber
        {
            get => _bibNumber;
            set => SetProperty(ref _bibNumber, value);
        }

        public string SwimmerName
        {
            get => _swimmerName;
            set => SetProperty(ref _swimmerName, value);
        }

        public string Club
        {
            get => _club;
            set => SetProperty(ref _club, value);
        }

        public TimeSpan FinishTime
        {
            get => _finishTime;
            set
            {
                if (SetProperty(ref _finishTime, value))
                {
                    FormattedTime = LaneModel.FormatTime(value);
                }
            }
        }

        public string FormattedTime
        {
            get => _formattedTime;
            set => SetProperty(ref _formattedTime, value);
        }

        public string GapTime
        {
            get => _gapTime;
            set => SetProperty(ref _gapTime, value);
        }

        public LaneStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusDisplay));
                }
            }
        }

        public string StatusDisplay => Status.ToString();

        public static IReadOnlyList<string> StatusOptions { get; } = new[]
        {
            "Finished",
            "DNF",
            "DQ"
        };
    }
}
