using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class RaceEventModel : ObservableObject
    {
        private int _eventNumber;
        private string _eventName = string.Empty;
        private string _distance = string.Empty;
        private string _stroke = string.Empty;
        private string _gender = string.Empty;

        public int EventNumber
        {
            get => _eventNumber;
            set
            {
                if (SetProperty(ref _eventNumber, value))
                {
                    OnPropertyChanged(nameof(DisplayTitle));
                    foreach (var heat in Heats)
                    {
                        heat.EventNumber = value;
                    }
                }
            }
        }

        public string EventName
        {
            get => _eventName;
            set
            {
                if (SetProperty(ref _eventName, value))
                {
                    OnPropertyChanged(nameof(DisplayTitle));
                    foreach (var heat in Heats)
                    {
                        heat.EventName = value;
                    }
                }
            }
        }

        public string Distance
        {
            get => _distance;
            set => SetProperty(ref _distance, value);
        }

        public string Stroke
        {
            get => _stroke;
            set => SetProperty(ref _stroke, value);
        }

        public string Gender
        {
            get => _gender;
            set => SetProperty(ref _gender, value);
        }

        public ObservableCollection<HeatModel> Heats { get; set; } = new();

        [JsonIgnore]
        public string DisplayTitle => $"Event #{EventNumber:D2} - {EventName}";

        [JsonConstructor]
        public RaceEventModel()
        {
        }

        public RaceEventModel(int eventNumber = 1, string eventName = "50m Freestyle")
        {
            _eventNumber = eventNumber;
            _eventName = eventName;
        }

        public HeatModel AddHeat()
        {
            int nextHeatNum = Heats.Count > 0 ? Heats.Max(h => h.HeatNumber) + 1 : 1;
            var heat = new HeatModel(nextHeatNum, EventNumber, EventName);
            Heats.Add(heat);
            return heat;
        }

        public bool RemoveHeat(HeatModel heat)
        {
            bool removed = Heats.Remove(heat);
            if (removed)
            {
                // Re-sequence remaining heat numbers
                for (int i = 0; i < Heats.Count; i++)
                {
                    Heats[i].HeatNumber = i + 1;
                }
            }
            return removed;
        }
    }
}
