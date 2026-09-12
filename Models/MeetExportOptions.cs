using System;
using System.Collections.Generic;
using System.Linq;
using boston_timing_system.Models;

namespace boston_timing_system.Models
{
    public enum ExportScope
    {
        FullMeet,
        SelectedEvents
    }

    public class MeetExportOptions
    {
        public ExportScope Scope { get; set; } = ExportScope.FullMeet;
        public TimingMode Mode { get; set; } = TimingMode.Pool;

        /// <summary>
        /// Set of Event numbers to export when Scope == SelectedEvents.
        /// Can contain 1 or more event numbers.
        /// </summary>
        public HashSet<int> SelectedEventNumbers { get; set; } = new();

        /// <summary>
        /// Whether to create a separate sheet tab for each event in the single Excel file.
        /// </summary>
        public bool SeparateSheetsPerEvent { get; set; } = true;

        /// <summary>
        /// Whether to highlight Rank 1 (Gold), Rank 2 (Silver), Rank 3 (Bronze) in the result table.
        /// </summary>
        public bool HighlightMedals { get; set; } = true;

        /// <summary>
        /// Determines if a lane has an official recorded race result (Finished time or DQ/DNS/DNF).
        /// Untouched/empty/running lanes without results are never exported.
        /// </summary>
        public static bool HasResult(LaneModel lane)
        {
            if (lane == null) return false;

            if (lane.Status == LaneStatus.Finished)
            {
                return lane.FinishTime.HasValue || 
                       (!string.IsNullOrEmpty(lane.FormattedTime) && lane.FormattedTime != "00.00.00");
            }

            return lane.Status == LaneStatus.DQ || 
                   lane.Status == LaneStatus.DNS || 
                   lane.Status == LaneStatus.DNF;
        }

        public static MeetExportOptions CreateFullMeet(TimingMode mode = TimingMode.Pool)
        {
            return new MeetExportOptions
            {
                Scope = ExportScope.FullMeet,
                Mode = mode
            };
        }

        public static MeetExportOptions CreateSelectedEvents(IEnumerable<int> eventNumbers, TimingMode mode = TimingMode.Pool)
        {
            var options = new MeetExportOptions
            {
                Scope = ExportScope.SelectedEvents,
                Mode = mode
            };
            foreach (var ev in eventNumbers)
            {
                options.SelectedEventNumbers.Add(ev);
            }
            return options;
        }
    }
}
