using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class LaneModel : ObservableObject
    {
        private int _laneNumber;
        private string _bibNumber = string.Empty;
        private string _swimmerName = string.Empty;
        private string _club = string.Empty;
        private string _seedTime = string.Empty;
        private LaneStatus _status = LaneStatus.OFF;
        private TimeSpan? _finishTime;
        private string _formattedTime = "00.00.00";
        private string _timer1 = "00.00.00";
        private string _timer2 = "00.00.00";
        private List<TimeSpan> _splits = new();
        private bool _isRefereeConnected;
        private double _refereeLatencyMs;
        private int? _rank;

        public int LaneNumber
        {
            get => _laneNumber;
            set
            {
                if (SetProperty(ref _laneNumber, value))
                {
                    OnPropertyChanged(nameof(BibNumber));
                }
            }
        }

        public string BibNumber
        {
            get => !string.IsNullOrWhiteSpace(_bibNumber) ? _bibNumber : LaneNumber.ToString();
            set => SetProperty(ref _bibNumber, value);
        }

        /// <summary>
        /// Returns true only if a BibNumber was explicitly set (not just inferred from LaneNumber).
        /// Used to prevent ambiguous matching in OWS mode when multiple lanes share the same fallback bib.
        /// </summary>
        [JsonIgnore]
        public bool HasExplicitBibNumber => !string.IsNullOrWhiteSpace(_bibNumber);

        /// <summary>
        /// When true, status change callbacks are suppressed to avoid re-entrant saves during heat loading.
        /// </summary>
        [JsonIgnore]
        public bool SuppressStatusCallbacks { get; set; }

        public string SwimmerName
        {
            get => _swimmerName;
            set
            {
                if (SetProperty(ref _swimmerName, value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        if ((Status == LaneStatus.OFF || Status == LaneStatus.Empty) && !FinishTime.HasValue)
                        {
                            Status = LaneStatus.Ready;
                            if (!SuppressStatusCallbacks)
                            {
                                StatusOverrideChangedCallback?.Invoke(this, LaneStatus.Ready);
                            }
                        }
                    }
                    else
                    {
                        if (Status == LaneStatus.Ready || Status == LaneStatus.Empty)
                        {
                            Status = LaneStatus.OFF;
                            if (!SuppressStatusCallbacks)
                            {
                                StatusOverrideChangedCallback?.Invoke(this, LaneStatus.OFF);
                            }
                        }
                    }
                }
            }
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

        [JsonIgnore]
        public Action<LaneModel, LaneStatus>? StatusOverrideChangedCallback { get; set; }

        [JsonIgnore]
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
                        if (FinishTime.HasValue || Status == LaneStatus.Finished)
                        {
                            newStatus = LaneStatus.Finished;
                        }
                        else
                        {
                            newStatus = string.IsNullOrWhiteSpace(SwimmerName) ? LaneStatus.OFF : LaneStatus.Ready;
                        }
                        break;
                }

                if (Status != newStatus)
                {
                    Status = newStatus;
                    if (!SuppressStatusCallbacks)
                    {
                        StatusOverrideChangedCallback?.Invoke(this, newStatus);
                    }
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

        [JsonIgnore]
        public string Timer3 => _formattedTime;

        [JsonIgnore]
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
                // Protect finished lane with recorded time from mistakenly reverting to Ready
                if (value == LaneStatus.Ready && FinishTime.HasValue)
                {
                    value = LaneStatus.Finished;
                }

                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusDisplay));
                    OnPropertyChanged(nameof(CanStop));
                    OnPropertyChanged(nameof(IsFinished));
                    OnPropertyChanged(nameof(IsOff));
                    OnPropertyChanged(nameof(IsActive));
                    OnPropertyChanged(nameof(OfficialTime));
                    OnPropertyChanged(nameof(OfficialTimeColor));
                    OnPropertyChanged(nameof(RankDisplay));
                    OnPropertyChanged(nameof(StatusOverride));
                }
            }
        }

        [JsonIgnore]
        public bool IsFinished => Status == LaneStatus.Finished;
        [JsonIgnore]
        public bool IsOff => Status == LaneStatus.OFF;
        [JsonIgnore]
        public bool IsActive => Status != LaneStatus.OFF && Status != LaneStatus.Empty;

        public TimeSpan? FinishTime
        {
            get => _finishTime;
            set
            {
                if (SetProperty(ref _finishTime, value))
                {
                    if (value.HasValue)
                    {
                        FormattedTime = FormatTime(value.Value);
                        if (Status != LaneStatus.DQ && Status != LaneStatus.DNS && Status != LaneStatus.DNF && Status != LaneStatus.OFF)
                        {
                            Status = LaneStatus.Finished;
                        }
                    }
                    else
                    {
                        FormattedTime = "00.00.00";
                    }
                    OnPropertyChanged(nameof(OfficialTime));
                    OnPropertyChanged(nameof(RankDisplay));
                    OnPropertyChanged(nameof(IsFinished));
                }
            }
        }

        [JsonIgnore]
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

        [JsonIgnore]
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

        [JsonIgnore]
        public string RankDisplay => (Rank.HasValue && Status == LaneStatus.Finished)
            ? Rank.Value.ToString()
            : "—";

        [JsonIgnore]
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
                    OnPropertyChanged(nameof(SignalBarsCount));
                }
            }
        }

        [JsonIgnore]
        public double RefereeLatencyMs
        {
            get => _refereeLatencyMs;
            set
            {
                if (SetProperty(ref _refereeLatencyMs, value))
                {
                    OnPropertyChanged(nameof(FormattedLatency));
                    OnPropertyChanged(nameof(IsLatencyGood));
                    OnPropertyChanged(nameof(IsLatencyMedium));
                    OnPropertyChanged(nameof(IsLatencyHigh));
                    OnPropertyChanged(nameof(RefereeConnectionTooltip));
                    OnPropertyChanged(nameof(SignalBarsCount));
                }
            }
        }

        [JsonIgnore]
        public string FormattedLatency => _refereeLatencyMs > 0 
            ? $"{_refereeLatencyMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}ms" 
            : "--";

        /// <summary>
        /// Signal bars count from 0 to 4 based on connection status and latency:
        /// 0: Disconnected
        /// 4: Connected with latency &lt; 50ms (or unmeasured)
        /// 3: Connected with latency 50ms – &lt;100ms
        /// 2: Connected with latency 100ms – 150ms
        /// 1: Connected with latency &gt; 150ms
        /// </summary>
        [JsonIgnore]
        public int SignalBarsCount
        {
            get
            {
                if (!IsRefereeConnected) return 0;
                if (_refereeLatencyMs <= 0 || _refereeLatencyMs < 50.0) return 4;
                if (_refereeLatencyMs < 150.0) return 3;
                if (_refereeLatencyMs <= 300.0) return 2;
                return 1;
            }
        }

        /// <summary>One-way latency below 150 ms — good quality.</summary>
        [JsonIgnore]
        public bool IsLatencyGood   => _refereeLatencyMs > 0 && _refereeLatencyMs < 150.0;
        /// <summary>One-way latency between 150 and 300 ms — moderate quality.</summary>
        [JsonIgnore]
        public bool IsLatencyMedium => _refereeLatencyMs >= 150.0 && _refereeLatencyMs <= 300.0;
        /// <summary>One-way latency above 300 ms — high latency quality gate.</summary>
        [JsonIgnore]
        public bool IsLatencyHigh   => _refereeLatencyMs > 300.0;

        [JsonIgnore]
        public string RefereeConnectionTooltip => IsRefereeConnected
            ? $"Referee Connected (RTT Latency: {FormattedLatency})"
            : "No Phone Connected";

        private TimingAuditTrail? _lastAuditTrail;

        [JsonIgnore]
        public TimingAuditTrail? LastAuditTrail
        {
            get => _lastAuditTrail;
            set
            {
                if (SetProperty(ref _lastAuditTrail, value))
                {
                    OnPropertyChanged(nameof(AuditTrailTooltip));
                }
            }
        }

        [JsonIgnore]
        public string AuditTrailTooltip => LastAuditTrail != null
            ? LastAuditTrail.Summary
            : RefereeConnectionTooltip;

        public List<TimeSpan> Splits
        {
            get => _splits;
            set => SetProperty(ref _splits, value ?? new());
        }

        [JsonIgnore]
        public string StatusDisplay => Status.ToString();

        [JsonIgnore]
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
            Status = string.IsNullOrWhiteSpace(SwimmerName) ? LaneStatus.OFF : LaneStatus.Ready;
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

        public static bool TryParseFormattedTime(string? formattedTime, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(formattedTime) || formattedTime == "00.00.00" || formattedTime == "--:--.--")
            {
                return false;
            }

            try
            {
                string[] parts = formattedTime.Trim().Replace(':', '.').Split('.');
                if (parts.Length == 3 &&
                    int.TryParse(parts[0], out int min) &&
                    int.TryParse(parts[1], out int sec) &&
                    int.TryParse(parts[2], out int frac))
                {
                    int ms = frac * (parts[2].Length == 2 ? 10 : (parts[2].Length == 1 ? 100 : 1));
                    result = new TimeSpan(0, 0, min, sec, ms);
                    return true;
                }
                else if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int s) &&
                    int.TryParse(parts[1], out int frac2))
                {
                    int ms = frac2 * (parts[1].Length == 2 ? 10 : 1);
                    result = new TimeSpan(0, 0, 0, s, ms);
                    return true;
                }

                return TimeSpan.TryParse(formattedTime, out result);
            }
            catch
            {
                return false;
            }
        }
    }

    public class TimingAuditTrail
    {
        public string SourceRole { get; set; } = string.Empty;
        public string SourceIp { get; set; } = string.Empty;
        public string LocalStopwatchTime { get; set; } = string.Empty;
        public string ServerArrivalTime { get; set; } = string.Empty;
        public double MeasuredLatencyMs { get; set; }
        public double AppliedCompensationMs { get; set; }
        public string QualityGrade { get; set; } = "A";
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public string Summary =>
            $"[Grade {QualityGrade}] Src: {SourceRole} ({SourceIp}) | Local: {LocalStopwatchTime} | Arrival: {ServerArrivalTime} | Latency: {MeasuredLatencyMs:F1}ms | Comp: {AppliedCompensationMs:F1}ms";
    }
}
