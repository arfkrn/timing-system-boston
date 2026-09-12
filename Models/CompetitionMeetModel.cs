using System;
using System.Collections.ObjectModel;
using System.Linq;
using boston_timing_system.Helpers;

namespace boston_timing_system.Models
{
    public class CompetitionMeetModel : ObservableObject
    {
        private string _meetName = "Swimming Competition 2026";
        private DateTime _meetDate = DateTime.Today;

        private RaceEventModel? _selectedEvent;
        private HeatModel? _selectedHeat;

        public string MeetName
        {
            get => _meetName;
            set => SetProperty(ref _meetName, value);
        }

        public DateTime MeetDate
        {
            get => _meetDate;
            set => SetProperty(ref _meetDate, value);
        }

        public ObservableCollection<RaceEventModel> Events { get; set; } = new();

        public RaceEventModel? SelectedEvent
        {
            get => _selectedEvent;
            set
            {
                if (SetProperty(ref _selectedEvent, value))
                {
                    OnPropertyChanged(nameof(CanGoNextHeat));
                    OnPropertyChanged(nameof(CanGoPrevHeat));
                    OnPropertyChanged(nameof(CanGoNextEvent));
                    OnPropertyChanged(nameof(CanGoPrevEvent));
                }
            }
        }

        public HeatModel? SelectedHeat
        {
            get => _selectedHeat;
            set
            {
                if (SetProperty(ref _selectedHeat, value))
                {
                    OnPropertyChanged(nameof(CurrentHeatDisplay));
                    OnPropertyChanged(nameof(CanGoNextHeat));
                    OnPropertyChanged(nameof(CanGoPrevHeat));
                }
            }
        }

        public string CurrentHeatDisplay => SelectedHeat != null && SelectedEvent != null
            ? $"{SelectedEvent.DisplayTitle} - Heat {SelectedHeat.HeatNumber} of {SelectedEvent.Heats.Count}"
            : "No Heat Selected";

        public bool CanGoNextEvent
        {
            get
            {
                if (SelectedEvent == null || Events.Count == 0) return false;
                int currentEventIdx = Events.IndexOf(SelectedEvent);
                return currentEventIdx >= 0 && currentEventIdx < Events.Count - 1;
            }
        }

        public bool CanGoPrevEvent
        {
            get
            {
                if (SelectedEvent == null || Events.Count == 0) return false;
                int currentEventIdx = Events.IndexOf(SelectedEvent);
                return currentEventIdx > 0;
            }
        }

        public bool NextEvent()
        {
            if (SelectedEvent == null || Events.Count == 0) return false;
            int currentEventIdx = Events.IndexOf(SelectedEvent);
            if (currentEventIdx >= 0 && currentEventIdx < Events.Count - 1)
            {
                SelectedEvent = Events[currentEventIdx + 1];
                SelectedHeat = SelectedEvent.Heats.FirstOrDefault();
                return true;
            }
            return false;
        }

        public bool PreviousEvent()
        {
            if (SelectedEvent == null || Events.Count == 0) return false;
            int currentEventIdx = Events.IndexOf(SelectedEvent);
            if (currentEventIdx > 0)
            {
                SelectedEvent = Events[currentEventIdx - 1];
                SelectedHeat = SelectedEvent.Heats.FirstOrDefault();
                return true;
            }
            return false;
        }

        public bool CanGoNextHeat
        {
            get
            {
                if (SelectedEvent == null || SelectedHeat == null) return false;
                int currentEventIdx = Events.IndexOf(SelectedEvent);
                int currentHeatIdx = SelectedEvent.Heats.IndexOf(SelectedHeat);

                if (currentHeatIdx < SelectedEvent.Heats.Count - 1) return true;
                return currentEventIdx < Events.Count - 1 && Events[currentEventIdx + 1].Heats.Count > 0;
            }
        }

        public bool CanGoPrevHeat
        {
            get
            {
                if (SelectedEvent == null || SelectedHeat == null) return false;
                int currentEventIdx = Events.IndexOf(SelectedEvent);
                int currentHeatIdx = SelectedEvent.Heats.IndexOf(SelectedHeat);

                if (currentHeatIdx > 0) return true;
                return currentEventIdx > 0 && Events[currentEventIdx - 1].Heats.Count > 0;
            }
        }

        public bool NextHeat()
        {
            if (SelectedEvent == null || SelectedHeat == null) return false;

            int currentEventIdx = Events.IndexOf(SelectedEvent);
            int currentHeatIdx = SelectedEvent.Heats.IndexOf(SelectedHeat);

            if (currentHeatIdx < SelectedEvent.Heats.Count - 1)
            {
                SelectedHeat = SelectedEvent.Heats[currentHeatIdx + 1];
                return true;
            }

            if (currentEventIdx < Events.Count - 1)
            {
                var nextEvent = Events[currentEventIdx + 1];
                if (nextEvent.Heats.Count > 0)
                {
                    SelectedEvent = nextEvent;
                    SelectedHeat = nextEvent.Heats[0];
                    return true;
                }
            }

            return false;
        }

        public bool PreviousHeat()
        {
            if (SelectedEvent == null || SelectedHeat == null) return false;

            int currentEventIdx = Events.IndexOf(SelectedEvent);
            int currentHeatIdx = SelectedEvent.Heats.IndexOf(SelectedHeat);

            if (currentHeatIdx > 0)
            {
                SelectedHeat = SelectedEvent.Heats[currentHeatIdx - 1];
                return true;
            }

            if (currentEventIdx > 0)
            {
                var prevEvent = Events[currentEventIdx - 1];
                if (prevEvent.Heats.Count > 0)
                {
                    SelectedEvent = prevEvent;
                    SelectedHeat = prevEvent.Heats[^1]; // Last heat of previous event
                    return true;
                }
            }

            return false;
        }
        public RaceEventModel AddEvent(int? eventNumber = null, string eventName = "New Event")
        {
            int nextEventNum = eventNumber ?? (Events.Count > 0 ? Events.Max(e => e.EventNumber) + 1 : 1);
            var ev = new RaceEventModel(nextEventNum, eventName);
            ev.AddHeat(); // Add Heat 1 by default
            Events.Add(ev);
            return ev;
        }

        public bool RemoveEvent(RaceEventModel raceEvent)
        {
            bool removed = Events.Remove(raceEvent);
            if (removed)
            {
                if (SelectedEvent == raceEvent)
                {
                    SelectedEvent = Events.FirstOrDefault();
                    SelectedHeat = SelectedEvent?.Heats.FirstOrDefault();
                }
            }
            return removed;
        }
    }
}
