using System;
using System.Collections.Generic;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class LaneModel : ObservableObject
    {
        private int _laneNumber;
        private string _swimmerName = string.Empty;
        private string _club = string.Empty;
        private string _seedTime = string.Empty;
        private LaneStatus _status = LaneStatus.Ready;
        private TimeSpan? _finishTime;
        private string _formattedTime = "00.00.00";
        private string _timer1 = "00.00.00";
        private string _timer2 = "00.00.00";
        private readonly List<TimeSpan> _splits = new();
        private bool _isRefereeConnected;
        private double _refereeLatencyMs;
        private int? _rank;

        public int LaneNumber
        {
            get => _laneNumber;
            set => SetProperty(ref _laneNumber, value);
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

        public string SeedTime
        {
            get => _seedTime;
            set => SetProperty(ref _seedTime, value);
        }

        public static IReadOnlyList<string> StatusOptions { get; } = new[]
        {
            "—",
            "DQ",
            "DNS",
            "DNF",
            "OFF"
        };

        public Action<LaneModel, LaneStatus>? StatusOverrideChangedCallback { get; set; }

        public string StatusOverride
        {
            get
            {
                return Status switch
                {
                    LaneStatus.DQ => "DQ",
                    LaneStatus.DNS => "DNS",
                    LaneStatus.DNF => "DNF",
                    LaneStatus.OFF => "OFF",
                    _ => "—"
                };
            }
            set
            {
                LaneStatus newStatus;
                switch (value)
                {
                    case "DQ":
                        newStatus = LaneStatus.DQ;
                        break;
                    case "DNS":
                        newStatus = LaneStatus.DNS;
                        break;
                    case "DNF":
                        newStatus = LaneStatus.DNF;
                        break;
                    case "OFF":
                        newStatus = LaneStatus.OFF;
                        break;
                    case "—":
                    default:
                        newStatus = FinishTime.HasValue ? LaneStatus.Finished : LaneStatus.Ready;
                        break;
                }

                if (Status != newStatus)
                {
                    Status = newStatus;
                    StatusOverrideChangedCallback?.Invoke(this, newStatus);
                }
                OnPropertyChanged(nameof(StatusOverride));
            }
        }

        public string Timer1
        {
            get => _timer1;
            set => SetProperty(ref _timer1, value);
        }

        public string Timer2
        {
            get => _timer2;
            set => SetProperty(ref _timer2, value);
        }

        public string Timer3 => _formattedTime;

        public string ResultTime
        {
            get => _formattedTime;
            set
            {
                if (SetProperty(ref _formattedTime, value))
                {
                    OnPropertyChanged(nameof(FormattedTime));
                    OnPropertyChanged(nameof(Timer3));
                    OnPropertyChanged(nameof(OfficialTime));
                }
            }
        }

        public string FormattedTime
        {
            get => _formattedTime;
            set
            {
                if (SetProperty(ref _formattedTime, value))
                {
                    OnPropertyChanged(nameof(ResultTime));
                    OnPropertyChanged(nameof(Timer3));
                    OnPropertyChanged(nameof(OfficialTime));
                }
            }
        }

        public LaneStatus Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusDisplay));
                    OnPropertyChanged(nameof(CanStop));
                    OnPropertyChanged(nameof(IsFinished));
                    OnPropertyChanged(nameof(OfficialTime));
                    OnPropertyChanged(nameof(OfficialTimeColor));
                    OnPropertyChanged(nameof(RankDisplay));
                    OnPropertyChanged(nameof(StatusOverride));
                }
            }
        }

        public bool IsFinished => Status == LaneStatus.Finished;

        public TimeSpan? FinishTime
        {
            get => _finishTime;
            set
            {
                if (SetProperty(ref _finishTime, value))
                {
                    FormattedTime = value.HasValue ? FormatTime(value.Value) : "00.00.00";
                    OnPropertyChanged(nameof(OfficialTime));
                    OnPropertyChanged(nameof(RankDisplay));
                }
            }
        }

        public string OfficialTime
        {
            get
            {
                return Status switch
                {
                    LaneStatus.DNS => "DNS",
                    LaneStatus.DQ => "DQ",
                    LaneStatus.OFF => "OFF",
                    LaneStatus.DNF => "DNF",
                    LaneStatus.Finished => FinishTime.HasValue ? FormatTime(FinishTime.Value) : ResultTime,
                    LaneStatus.Running => ResultTime,
                    _ => "00.00.00"
                };
            }
        }

        public string OfficialTimeColor
        {
            get
            {
                return Status switch
                {
                    LaneStatus.DQ => "#DC2626",
                    LaneStatus.DNS => "#D97706",
                    LaneStatus.DNF => "#E11D48",
                    LaneStatus.OFF => "#94A3B8",
                    LaneStatus.Finished => "#15803D",
                    _ => "#0F172A"
                };
            }
        }

        public int? Rank
        {
            get => _rank;
            set
            {
                if (SetProperty(ref _rank, value))
                {
                    OnPropertyChanged(nameof(RankDisplay));
                }
            }
        }

        public string RankDisplay => (Rank.HasValue && Status == LaneStatus.Finished)
            ? Rank.Value.ToString()
            : "—";

        public bool IsRefereeConnected
        {
            get => _isRefereeConnected;
            set
            {
                if (SetProperty(ref _isRefereeConnected, value))
                {
                    if (!value)
                    {
                        RefereeLatencyMs = 0;
                    }
                    OnPropertyChanged(nameof(RefereeConnectionTooltip));
                }
            }
        }

        public double RefereeLatencyMs
        {
            get => _refereeLatencyMs;
            set
            {
                if (SetProperty(ref _refereeLatencyMs, value))
                {
                    OnPropertyChanged(nameof(FormattedLatency));
                    OnPropertyChanged(nameof(RefereeConnectionTooltip));
                }
            }
        }

        public string FormattedLatency => _refereeLatencyMs > 0 
            ? $"{_refereeLatencyMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}ms" 
            : "--";

        public string RefereeConnectionTooltip => IsRefereeConnected
            ? $"Referee Connected (RTT Latency: {FormattedLatency})"
            : "No Phone Connected";

        public IReadOnlyList<TimeSpan> Splits => _splits.AsReadOnly();

        public string StatusDisplay => Status.ToString();

        public bool CanStop => Status == LaneStatus.Running;

        public void SetRunning()
        {
            if (Status == LaneStatus.Ready || Status == LaneStatus.Empty)
            {
                Status = LaneStatus.Running;
                FinishTime = null;
                FormattedTime = "00.00.00";
                _splits.Clear();
            }
        }

        public void SetFinished(TimeSpan elapsed)
        {
            if (Status == LaneStatus.Running)
            {
                FinishTime = elapsed;
                Status = LaneStatus.Finished;
                FormattedTime = FormatTime(elapsed);
            }
        }

        public void AddSplit(TimeSpan splitTime)
        {
            _splits.Add(splitTime);
            OnPropertyChanged(nameof(Splits));
        }

        public void Reset()
        {
            Status = LaneStatus.Ready;
            FinishTime = null;
            FormattedTime = "00.00.00";
            _timer1 = "00.00.00";
            _timer2 = "00.00.00";
            OnPropertyChanged(nameof(Timer1));
            OnPropertyChanged(nameof(Timer2));
            OnPropertyChanged(nameof(Timer3));
            _splits.Clear();
            OnPropertyChanged(nameof(Splits));
            Rank = null;
        }

        public static string FormatTime(TimeSpan time)
        {
            // Format: mm.ss.ff
            return $"{(int)time.TotalMinutes:D2}.{time.Seconds:D2}.{time.Milliseconds / 10:D2}";
        }
    }
}
