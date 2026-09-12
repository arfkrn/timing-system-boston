using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using boston_timing_system.Models;

namespace boston_timing_system.Services
{
    public class ExcelMeetDataService
    {
        public CompetitionMeetModel ImportMeetFromExcel(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Excel file not found: {filePath}");
            }

            using var workbook = new XLWorkbook(filePath);
            var worksheet = workbook.Worksheets.FirstOrDefault() 
                ?? throw new InvalidOperationException("Excel file contains no worksheets.");

            string meetName = Path.GetFileNameWithoutExtension(filePath);
            var meet = new CompetitionMeetModel
            {
                MeetName = meetName,
                MeetDate = DateTime.Today
            };

            // Locate header columns
            var headerRow = worksheet.Row(1);
            int colEventNumber = FindColumnIndex(headerRow, "EventNumber", "Event No", "Event#", "Event");
            int colEventName = FindColumnIndex(headerRow, "EventName", "Event Name", "Discipline", "Name");
            int colHeatNumber = FindColumnIndex(headerRow, "HeatNumber", "Heat No", "Heat#", "Heat");
            int colLaneNumber = FindColumnIndex(headerRow, "LaneNumber", "Lane No", "Lane#", "Lane");
            int colSwimmerName = FindColumnIndex(headerRow, "SwimmerName", "Swimmer Name", "Athlete", "Swimmer");
            int colClub = FindColumnIndex(headerRow, "Club", "Team", "Affiliation", "Club Name");
            int colSeedTime = FindColumnIndex(headerRow, "SeedTime", "Seed Time", "EntryTime", "Entry Time");

            if (colEventNumber <= 0 || colHeatNumber <= 0 || colLaneNumber <= 0 || colSwimmerName <= 0)
            {
                throw new FormatException("Excel header is missing required columns: EventNumber, HeatNumber, LaneNumber, SwimmerName.");
            }

            var eventDict = new Dictionary<int, RaceEventModel>();

            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
            for (int row = 2; row <= lastRow; row++)
            {
                var xlRow = worksheet.Row(row);
                if (xlRow.IsEmpty())
                {
                    continue;
                }

                string eventNumStr = xlRow.Cell(colEventNumber).GetString().Trim();
                if (string.IsNullOrWhiteSpace(eventNumStr) || !int.TryParse(eventNumStr, out int eventNumber))
                {
                    continue;
                }

                string eventName = colEventName > 0 ? xlRow.Cell(colEventName).GetString().Trim() : $"Event {eventNumber}";
                if (string.IsNullOrWhiteSpace(eventName))
                {
                    eventName = $"Event {eventNumber}";
                }

                string heatNumStr = xlRow.Cell(colHeatNumber).GetString().Trim();
                if (!int.TryParse(heatNumStr, out int heatNumber))
                {
                    heatNumber = 1;
                }

                string laneNumStr = xlRow.Cell(colLaneNumber).GetString().Trim();
                if (!int.TryParse(laneNumStr, out int laneNumber) || laneNumber < 0 || laneNumber > 10)
                {
                    continue;
                }
                if (laneNumber == 10) laneNumber = 0;

                string swimmerName = xlRow.Cell(colSwimmerName).GetString().Trim();
                string club = colClub > 0 ? xlRow.Cell(colClub).GetString().Trim() : string.Empty;
                string seedTime = colSeedTime > 0 ? xlRow.Cell(colSeedTime).GetString().Trim() : "--:--.--";

                if (!eventDict.TryGetValue(eventNumber, out var raceEvent))
                {
                    raceEvent = new RaceEventModel(eventNumber, eventName);
                    eventDict[eventNumber] = raceEvent;
                    meet.Events.Add(raceEvent);
                }

                var heat = raceEvent.Heats.FirstOrDefault(h => h.HeatNumber == heatNumber);
                if (heat == null)
                {
                    heat = new HeatModel(heatNumber, eventNumber, eventName);
                    raceEvent.Heats.Add(heat);
                }

                var targetLane = heat.Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
                if (targetLane != null)
                {
                    targetLane.SwimmerName = swimmerName;
                    targetLane.Club = club;
                    targetLane.SeedTime = string.IsNullOrWhiteSpace(seedTime) ? "--:--.--" : seedTime;
                    targetLane.Status = LaneStatus.Ready;
                }
            }

            // Set default selections
            if (meet.Events.Count > 0)
            {
                meet.SelectedEvent = meet.Events[0];
                if (meet.SelectedEvent.Heats.Count > 0)
                {
                    meet.SelectedHeat = meet.SelectedEvent.Heats[0];
                }
            }

            return meet;
        }

        public void GenerateSampleTemplate(string filePath)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("StartList");

            // Header definition
            string[] headers = { "EventNumber", "EventName", "HeatNumber", "LaneNumber", "SwimmerName", "Club", "SeedTime" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            // Sample rows: Event 1 (50m Freestyle Men, 2 Heats)
            var sampleData = new (int EventNum, string EventName, int HeatNum, int LaneNum, string Swimmer, string Club, string SeedTime)[]
            {
                // Event 1, Heat 1
                (1, "50m Freestyle Men", 1, 1, "Rizky Pratama", "Tirta Jaya Aquatic", "00:29.50"),
                (1, "50m Freestyle Men", 1, 2, "Budi Santoso", "Millennium Aquatic", "00:28.10"),
                (1, "50m Freestyle Men", 1, 3, "Ahmad Fauzi", "Jaq Aquatic Club", "00:27.40"),
                (1, "50m Freestyle Men", 1, 4, "Kevin Wijaya", "Tirta Kencana", "00:26.80"),
                (1, "50m Freestyle Men", 1, 5, "Dimas Anggara", "Surabaya Aquatic Club", "00:27.10"),
                (1, "50m Freestyle Men", 1, 6, "Fajar Nugraha", "Bandung Swimming Club", "00:27.90"),
                (1, "50m Freestyle Men", 1, 7, "Bayu Permana", "Garuda SC", "00:28.70"),
                (1, "50m Freestyle Men", 1, 8, "Rian Hidayat", "Nusantara AC", "00:29.80"),

                // Event 1, Heat 2
                (1, "50m Freestyle Men", 2, 2, "Gede Arya", "Bali Aquatic Club", "00:26.50"),
                (1, "50m Freestyle Men", 2, 3, "Jonathan Tan", "Medan Swimming Club", "00:25.90"),
                (1, "50m Freestyle Men", 2, 4, "Michael Setiawan", "Millennium Aquatic", "00:25.20"),
                (1, "50m Freestyle Men", 2, 5, "Hendro Kusumo", "Jaq Aquatic Club", "00:25.60"),
                (1, "50m Freestyle Men", 2, 6, "Aldo Saputra", "Tirta Kencana", "00:26.10"),
                (1, "50m Freestyle Men", 2, 7, "Farhan Akbar", "Semarang SC", "00:26.90"),

                // Event 2 (100m Breaststroke Women, Heat 1)
                (2, "100m Breaststroke Women", 1, 2, "Siti Rahma", "Millennium Aquatic", "01:18.50"),
                (2, "100m Breaststroke Women", 1, 3, "Nadia Utami", "Jaq Aquatic Club", "01:16.20"),
                (2, "100m Breaststroke Women", 1, 4, "Clara Anastasia", "Tirta Kencana", "01:14.80"),
                (2, "100m Breaststroke Women", 1, 5, "Putri Anggraini", "Bandung SC", "01:15.50"),
                (2, "100m Breaststroke Women", 1, 6, "Aisyah Bella", "Surabaya AC", "01:17.30")
            };

            for (int r = 0; r < sampleData.Length; r++)
            {
                var row = ws.Row(r + 2);
                var d = sampleData[r];
                row.Cell(1).Value = d.EventNum;
                row.Cell(2).Value = d.EventName;
                row.Cell(3).Value = d.HeatNum;
                row.Cell(4).Value = d.LaneNum;
                row.Cell(5).Value = d.Swimmer;
                row.Cell(6).Value = d.Club;
                row.Cell(7).Value = d.SeedTime;

                // Center align numbers
                row.Cell(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                row.Cell(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                row.Cell(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                row.Cell(7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            ws.Columns().AdjustToContents();
            workbook.SaveAs(filePath);
        }

        public void ExportResultsToExcel(CompetitionMeetModel meet, string filePath)
        {
            ExportResultsToExcel(meet, filePath, MeetExportOptions.CreateFullMeet());
        }

        public void ExportResultsToExcel(CompetitionMeetModel meet, string filePath, MeetExportOptions options)
        {
            options ??= MeetExportOptions.CreateFullMeet();

            var targetEvents = meet.Events
                .Where(ev => options.Scope == ExportScope.FullMeet || options.SelectedEventNumbers.Contains(ev.EventNumber))
                .ToList();

            if (targetEvents.Count == 0)
            {
                throw new InvalidOperationException("Tidak ada nomor event yang dipilih untuk diekspor.");
            }

            // Verify that there is at least one result
            int totalResultsCount = targetEvents
                .SelectMany(ev => ev.Heats)
                .SelectMany(h => h.Lanes)
                .Count(MeetExportOptions.HasResult);

            if (totalResultsCount == 0)
            {
                throw new InvalidOperationException("Tidak ada hasil balapan pada event yang dipilih. Hanya lomba yang memiliki hasil yang dapat diekspor.");
            }

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Official Results");

            // Title block
            ws.Cell(1, 1).Value = $"{meet.MeetName} - OFFICIAL RACE RESULTS";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#0F172A");

            string scopeDesc = options.Scope == ExportScope.FullMeet 
                ? "Full Meet (All Events)" 
                : $"Selected Events ({targetEvents.Count} Events)";

            ws.Cell(2, 1).Value = $"Date: {meet.MeetDate:dd MMMM yyyy} | Scope: {scopeDesc} | Generated: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell(2, 1).Style.Font.Italic = true;
            ws.Cell(2, 1).Style.Font.FontSize = 10;
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#64748B");

            // Header row
            string[] headers = { "Event #", "Event Name", "Heat", "Rank", "Lane", "Athlete Name", "Club / Team", "Seed Time", "Finish Time", "Status" };
            int headerRowIndex = 4;
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(headerRowIndex, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int currentRow = headerRowIndex + 1;

            foreach (var raceEvent in targetEvents)
            {
                foreach (var heat in raceEvent.Heats)
                {
                    heat.CalculateRanks();

                    // Strictly filter: ONLY lanes that have results!
                    var resultLanes = heat.Lanes
                        .Where(MeetExportOptions.HasResult)
                        .OrderBy(l => l.Rank ?? 99)
                        .ThenBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber)
                        .ToList();

                    if (resultLanes.Count == 0)
                    {
                        continue; // Skip heat with no results
                    }

                    foreach (var lane in resultLanes)
                    {
                        var row = ws.Row(currentRow);
                        row.Cell(1).Value = raceEvent.EventNumber;
                        row.Cell(2).Value = raceEvent.EventName;
                        row.Cell(3).Value = heat.HeatNumber;
                        row.Cell(4).Value = lane.Rank.HasValue ? lane.Rank.Value.ToString() : "-";
                        row.Cell(5).Value = lane.LaneNumber;
                        row.Cell(6).Value = lane.SwimmerName;
                        row.Cell(7).Value = lane.Club;
                        row.Cell(8).Value = lane.SeedTime;
                        row.Cell(9).Value = lane.FormattedTime;
                        row.Cell(10).Value = lane.Status.ToString();

                        // Center align numeric columns
                        row.Cell(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        row.Cell(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        row.Cell(4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        row.Cell(5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        row.Cell(8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        row.Cell(9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        row.Cell(10).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                        // Medals coloring for Rank 1, 2, 3
                        if (options.HighlightMedals)
                        {
                            if (lane.Rank == 1)
                            {
                                row.Cell(4).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF08A"); // Gold tint
                                row.Cell(4).Style.Font.Bold = true;
                            }
                            else if (lane.Rank == 2)
                            {
                                row.Cell(4).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0"); // Silver tint
                                row.Cell(4).Style.Font.Bold = true;
                            }
                            else if (lane.Rank == 3)
                            {
                                row.Cell(4).Style.Fill.BackgroundColor = XLColor.FromHtml("#FED7AA"); // Bronze tint
                                row.Cell(4).Style.Font.Bold = true;
                            }
                        }

                        currentRow++;
                    }
                }
            }

            ws.Columns().AdjustToContents();

            // Optional: Create individual sheets per Event if enabled and multiple events exist
            if (options.SeparateSheetsPerEvent)
            {
                foreach (var raceEvent in targetEvents)
                {
                    // Check if this event has any results
                    var eventResultLanes = raceEvent.Heats
                        .SelectMany(h => h.Lanes)
                        .Where(MeetExportOptions.HasResult)
                        .ToList();

                    if (eventResultLanes.Count == 0) continue;

                    string sheetName = $"Event {raceEvent.EventNumber}";
                    if (sheetName.Length > 31) sheetName = sheetName.Substring(0, 31);

                    var eventWs = workbook.Worksheets.Add(sheetName);
                    eventWs.Cell(1, 1).Value = $"{meet.MeetName} - EVENT #{raceEvent.EventNumber}: {raceEvent.EventName}";
                    eventWs.Cell(1, 1).Style.Font.Bold = true;
                    eventWs.Cell(1, 1).Style.Font.FontSize = 13;
                    eventWs.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#0F172A");

                    eventWs.Cell(2, 1).Value = $"Date: {meet.MeetDate:dd MMMM yyyy} | Total Heats with Results: {raceEvent.Heats.Count(h => h.Lanes.Any(MeetExportOptions.HasResult))}";
                    eventWs.Cell(2, 1).Style.Font.Italic = true;
                    eventWs.Cell(2, 1).Style.Font.FontSize = 10;
                    eventWs.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#64748B");

                    string[] evHeaders = { "Heat", "Rank", "Lane", "Athlete Name", "Club / Team", "Seed Time", "Finish Time", "Status" };
                    int evHeaderRow = 4;
                    for (int i = 0; i < evHeaders.Length; i++)
                    {
                        var cell = eventWs.Cell(evHeaderRow, i + 1);
                        cell.Value = evHeaders[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                        cell.Style.Font.FontColor = XLColor.White;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }

                    int evRow = evHeaderRow + 1;
                    foreach (var heat in raceEvent.Heats)
                    {
                        var heatResults = heat.Lanes.Where(MeetExportOptions.HasResult).OrderBy(l => l.Rank ?? 99).ThenBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber).ToList();
                        if (heatResults.Count == 0) continue;

                        foreach (var lane in heatResults)
                        {
                            var r = eventWs.Row(evRow);
                            r.Cell(1).Value = heat.HeatNumber;
                            r.Cell(2).Value = lane.Rank.HasValue ? lane.Rank.Value.ToString() : "-";
                            r.Cell(3).Value = lane.LaneNumber;
                            r.Cell(4).Value = lane.SwimmerName;
                            r.Cell(5).Value = lane.Club;
                            r.Cell(6).Value = lane.SeedTime;
                            r.Cell(7).Value = lane.FormattedTime;
                            r.Cell(8).Value = lane.Status.ToString();

                            r.Cell(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            r.Cell(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            r.Cell(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            r.Cell(6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            r.Cell(7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                            r.Cell(8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                            if (options.HighlightMedals)
                            {
                                if (lane.Rank == 1) { r.Cell(2).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF08A"); r.Cell(2).Style.Font.Bold = true; }
                                else if (lane.Rank == 2) { r.Cell(2).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0"); r.Cell(2).Style.Font.Bold = true; }
                                else if (lane.Rank == 3) { r.Cell(2).Style.Fill.BackgroundColor = XLColor.FromHtml("#FED7AA"); r.Cell(2).Style.Font.Bold = true; }
                            }
                            evRow++;
                        }
                    }
                    eventWs.Columns().AdjustToContents();
                }
            }

            workbook.SaveAs(filePath);
        }

        private static int FindColumnIndex(IXLRow headerRow, params string[] possibleNames)
        {
            foreach (var cell in headerRow.CellsUsed())
            {
                string headerText = cell.GetString().Trim();
                foreach (var name in possibleNames)
                {
                    if (string.Equals(headerText, name, StringComparison.OrdinalIgnoreCase) ||
                        headerText.Replace(" ", "").Equals(name.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    {
                        return cell.Address.ColumnNumber;
                    }
                }
            }
            return -1;
        }
    }
}
