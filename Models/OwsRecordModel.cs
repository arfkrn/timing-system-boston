using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class OwsRecordModel : ObservableObject
    {
        private string _id = Guid.NewGuid().ToString();

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

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
            set
            {
                if (SetProperty(ref _rank, value))
                {
                    OnPropertyChanged(nameof(RankDisplay));
                }
            }
        }

        [JsonIgnore]
        public string RankDisplay => (Status == LaneStatus.Finished && Rank > 0) ? Rank.ToString() : "—";

        [JsonIgnore]
        public bool HasBib => !string.IsNullOrWhiteSpace(BibNumber);

        [JsonIgnore]
        public string StatusToolTip => HasBib
            ? "Pilih status peserta (Finished, DQ, DNS, DNF)"
            : "Status hanya dapat diubah setelah nomor BIB diisi";

        public string BibNumber
        {
            get => _bibNumber;
            set
            {
                if (SetProperty(ref _bibNumber, value))
                {
                    if (!HasBib && Status != LaneStatus.Finished)
                    {
                        Status = LaneStatus.Finished;
                        StatusChangedCallback?.Invoke(this, LaneStatus.Finished);
                    }
                    OnPropertyChanged(nameof(HasBib));
                    OnPropertyChanged(nameof(StatusToolTip));
                }
            }
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
                // Pengaman: Di mode OWS, perubahan status hanya diperbolehkan jika sudah ada nomor BIB
                if (!HasBib && value != LaneStatus.Finished)
                {
                    return;
                }

                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusDisplay));
                    OnPropertyChanged(nameof(StatusOverride));
                    OnPropertyChanged(nameof(RankDisplay));
                }
            }
        }

        [JsonIgnore]
        public string StatusDisplay => Status.ToString();

        [JsonIgnore]
        public Action<OwsRecordModel, LaneStatus>? StatusChangedCallback { get; set; }

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
                    _ => "Finished"
                };
            }
            set
            {
                // Pengaman: Di mode OWS, perubahan status hanya bisa dilakukan jika sudah ada BIB
                if (!HasBib)
                {
                    OnPropertyChanged(nameof(StatusOverride));
                    return;
                }

                LaneStatus newStatus = value switch
                {
                    "DQ" => LaneStatus.DQ,
                    "DNS" => LaneStatus.DNS,
                    "DNF" => LaneStatus.DNF,
                    _ => LaneStatus.Finished
                };

                if (Status != newStatus)
                {
                    Status = newStatus;
                    StatusChangedCallback?.Invoke(this, newStatus);
                }
                OnPropertyChanged(nameof(StatusOverride));
            }
        }

        public static IReadOnlyList<string> StatusOptions { get; } = new[]
        {
            "Finished",
            "DQ",
            "DNS",
            "DNF"
        };
    }
}
