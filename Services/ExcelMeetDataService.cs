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
        /// <summary>
        /// Inspects the header row of an Excel file to determine whether it is a Pool or OWS
        /// start-list format, without performing a full import.
        /// Returns <see langword="null"/> if the file format cannot be determined.
        /// </summary>
        /// <param name="filePath">Full path to the .xlsx file to probe.</param>
        /// <returns>
        /// <see cref="TimingMode.OpenWater"/> — file has a BibNumber / No Bib column (OWS format).<br/>
        /// <see cref="TimingMode.Pool"/> — file has both Heat and Lane columns (Pool format).<br/>
        /// <see langword="null"/> — header columns are ambiguous or missing.
        /// </returns>
        public TimingMode? DetectTimingMode(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var workbook = new XLWorkbook(stream);
                var worksheet = workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) return null;

                // Scan rows 1–10 for the header row (same heuristic as ImportMeetFromExcel)
                int maxScanRow = Math.Min(10, worksheet.LastRowUsed()?.RowNumber() ?? 3);
                for (int r = 1; r <= maxScanRow; r++)
                {
                    var row = worksheet.Row(r);

                    // OWS signature: has a BibNumber/No Bib column
                    int colBib = FindColumnIndex(row,
                        "BibNumber", "Bib Number", "Bib#", "BIB",
                        "No Bib", "No. Bib", "Bib No", "Bib No.",
                        "Nomor Bib", "No Dada", "Nomor Dada");

                    // Pool signature: has both Heat AND Lane columns
                    int colHeat = FindColumnIndex(row,
                        "HeatNumber", "Heat No", "Heat No.", "Heat#",
                        "Heat", "No Heat", "Nomor Heat", "Seri");
                    int colLane = FindColumnIndex(row,
                        "LaneNumber", "Lane No", "Lane No.", "Lane#",
                        "Lane", "Lintasan", "LN", "No Lane", "No Lintasan");

                    // Also need EventNumber column to confirm this is really a header row
                    int colEvent = FindColumnIndex(row,
                        "EventNumber", "Event No", "Event No.", "Event#",
                        "Event", "No Event", "Nomor Event");

                    if (colEvent <= 0) continue; // not a recognized header row, keep scanning

                    if (colBib > 0)
                    {
                        return TimingMode.OpenWater;
                    }

                    if (colHeat > 0 && colLane > 0)
                    {
                        return TimingMode.Pool;
                    }

                    // Header row found but columns don't match either mode
                    return null;
                }

                return null; // no recognizable header found
            }
            catch
            {
                return null; // treat unreadable files as unknown format
            }
        }

        public CompetitionMeetModel ImportMeetFromExcel(string filePath, TimingMode mode = TimingMode.Pool)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Excel file not found: {filePath}");
            }

            // Open with FileShare.ReadWrite so the file can be read even if currently opened in Microsoft Excel
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var workbook = new XLWorkbook(fileStream);
            var worksheet = workbook.Worksheets.FirstOrDefault() 
                ?? throw new InvalidOperationException("Excel file contains no worksheets.");

            string meetName = Path.GetFileNameWithoutExtension(filePath);

            // Step 1: Extract Competition Name from Row 1 or 2
            string cellB1 = worksheet.Cell(1, 2).GetString().Trim();
            string cellA1 = worksheet.Cell(1, 1).GetString().Trim();

            if (!string.IsNullOrWhiteSpace(cellB1) && (cellA1.Contains("Competition", StringComparison.OrdinalIgnoreCase) || 
                                                      cellA1.Contains("Kompetisi", StringComparison.OrdinalIgnoreCase) || 
                                                      cellA1.Contains("Kejuaraan", StringComparison.OrdinalIgnoreCase) || 
                                                      cellA1.Contains("Meet", StringComparison.OrdinalIgnoreCase)))
            {
                meetName = cellB1;
            }
            else if (!string.IsNullOrWhiteSpace(cellA1) && 
                     !cellA1.StartsWith("Event", StringComparison.OrdinalIgnoreCase) && 
                     !cellA1.StartsWith("No", StringComparison.OrdinalIgnoreCase))
            {
                meetName = CleanCompetitionTitle(cellA1);
            }
            else
            {
                // Search row 1 and 2 for competition title/label
                for (int r = 1; r <= 2; r++)
                {
                    var rCells = worksheet.Row(r).CellsUsed().ToList();
                    foreach (var cell in rCells)
                    {
                        string txt = cell.GetString().Trim();
                        if (txt.Contains("Competition", StringComparison.OrdinalIgnoreCase) || 
                            txt.Contains("Kompetisi", StringComparison.OrdinalIgnoreCase) || 
                            txt.Contains("Kejuaraan", StringComparison.OrdinalIgnoreCase))
                        {
                            var nextCell = cell.CellRight();
                            string nextVal = nextCell?.GetString().Trim() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(nextVal))
                            {
                                meetName = nextVal;
                                break;
                            }
                            else
                            {
                                string cleaned = CleanCompetitionTitle(txt);
                                if (!string.IsNullOrWhiteSpace(cleaned))
                                {
                                    meetName = cleaned;
                                    break;
                                }
                            }
                        }
                    }
                    if (meetName != Path.GetFileNameWithoutExtension(filePath)) break;
                }
            }

            // Step 2: Dynamically locate event table header row (Row 3 by default, or scan rows 1-10)
            int headerRowNum = 3;
            IXLRow headerRow = worksheet.Row(3);
            int colEventNumber = -1;

            int maxScanRow = Math.Min(10, worksheet.LastRowUsed()?.RowNumber() ?? 3);
            for (int r = 1; r <= maxScanRow; r++)
            {
                var testRow = worksheet.Row(r);
                int testCol = FindColumnIndex(testRow, "EventNumber", "Event No", "Event No.", "Event#", "Event", "No Event", "Nomor Event");
                if (testCol > 0)
                {
                    headerRowNum = r;
                    headerRow = testRow;
                    colEventNumber = testCol;
                    break;
                }
            }

            int colCompetitionName = FindColumnIndex(headerRow, "Competition Name", "CompetitionName", "Competition", "Meet Name", "MeetName", "Meet", "Nama Kompetisi", "Nama Kejuaraan", "Kejuaraan");
            int colEventName = FindColumnIndex(headerRow, "EventName", "Event Name", "Discipline", "Name", "Nama Event", "Nama Lomba");
            int colHeatNumber = FindColumnIndex(headerRow, "HeatNumber", "Heat No", "Heat No.", "Heat#", "Heat", "No Heat", "Nomor Heat", "Seri");
            int colLaneNumber = FindColumnIndex(headerRow, "LaneNumber", "Lane No", "Lane No.", "Lane#", "Lane", "Lintasan", "LN", "No Lane", "No Lintasan");
            int colBibNumber = FindColumnIndex(headerRow, "BibNumber", "Bib Number", "Bib#", "BIB", "No Bib", "No. Bib", "Bib No", "Bib No.", "Nomor Bib", "No Dada", "Nomor Dada");
            int colSwimmerName = FindColumnIndex(headerRow, "SwimmerName", "Swimmer Name", "Athlete", "Swimmer", "Nama Atlet", "Nama Perenang", "Peserta", "Nama Peserta");
            int colClub = FindColumnIndex(headerRow, "Club", "Team", "Affiliation", "Club Name", "Klub", "Tim", "Nama Klub");

            if (mode == TimingMode.OpenWater)
            {
                if (colEventNumber <= 0 || (colBibNumber <= 0 && colLaneNumber <= 0) || colSwimmerName <= 0)
                {
                    throw new FormatException("Excel header OWS required columns: Event No, No Bib, and Athlete.");
                }
            }
            else
            {
                if (colEventNumber <= 0 || colHeatNumber <= 0 || colLaneNumber <= 0 || colSwimmerName <= 0)
                {
                    throw new FormatException("Excel header is missing required columns: Event No, Heat, Lane, Athlete.");
                }
            }

            var meet = new CompetitionMeetModel
            {
                MeetName = meetName,
                MeetDate = DateTime.Today
            };

            var eventDict = new Dictionary<int, RaceEventModel>();

            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? headerRowNum;
            bool meetNameFoundInColumn = false;

            for (int row = headerRowNum + 1; row <= lastRow; row++)
            {
                var xlRow = worksheet.Row(row);
                if (xlRow.IsEmpty())
                {
                    continue;
                }

                if (!meetNameFoundInColumn && colCompetitionName > 0)
                {
                    string compVal = xlRow.Cell(colCompetitionName).GetString().Trim();
                    if (!string.IsNullOrWhiteSpace(compVal))
                    {
                        meet.MeetName = compVal;
                        meetNameFoundInColumn = true;
                    }
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

                int heatNumber = 1;
                if (mode != TimingMode.OpenWater && colHeatNumber > 0)
                {
                    string heatNumStr = xlRow.Cell(colHeatNumber).GetString().Trim();
                    if (!int.TryParse(heatNumStr, out heatNumber))
                    {
                        heatNumber = 1;
                    }
                }

                string swimmerName = xlRow.Cell(colSwimmerName).GetString().Trim();
                string club = colClub > 0 ? xlRow.Cell(colClub).GetString().Trim() : string.Empty;

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
                    if (mode == TimingMode.OpenWater)
                    {
                        heat.Lanes.Clear(); // In OWS, start with no dummy lanes
                    }
                    raceEvent.Heats.Add(heat);
                }
                else if (mode == TimingMode.OpenWater && heat.Lanes.Count == 10 && heat.Lanes.All(l => string.IsNullOrWhiteSpace(l.SwimmerName)))
                {
                    heat.Lanes.Clear();
                }

                if (mode == TimingMode.OpenWater)
                {
                    string bibStr = colBibNumber > 0 ? xlRow.Cell(colBibNumber).GetString().Trim() : (colLaneNumber > 0 ? xlRow.Cell(colLaneNumber).GetString().Trim() : string.Empty);
                    if (string.IsNullOrWhiteSpace(bibStr))
                    {
                        continue;
                    }

                    int nextLaneNum = heat.Lanes.Count + 1;
                    var targetLane = heat.Lanes.FirstOrDefault(l => l.BibNumber.Equals(bibStr, StringComparison.OrdinalIgnoreCase));
                    if (targetLane != null)
                    {
                        targetLane.SwimmerName = swimmerName;
                        targetLane.Club = club;
                        targetLane.Status = LaneStatus.Ready;
                    }
                    else
                    {
                        heat.Lanes.Add(new LaneModel
                        {
                            LaneNumber = nextLaneNum,
                            BibNumber = bibStr,
                            SwimmerName = swimmerName,
                            Club = club,
                            Status = LaneStatus.Ready
                        });
                    }
                }
                else
                {
                    string laneNumStr = xlRow.Cell(colLaneNumber).GetString().Trim();
                    if (!int.TryParse(laneNumStr, out int laneNumber) || laneNumber < 0 || laneNumber > 10)
                    {
                        continue;
                    }
                    if (laneNumber == 10) laneNumber = 0;

                    var targetLane = heat.Lanes.FirstOrDefault(l => l.LaneNumber == laneNumber);
                    if (targetLane != null)
                    {
                        targetLane.BibNumber = laneNumber.ToString();
                        targetLane.SwimmerName = swimmerName;
                        targetLane.Club = club;
                        targetLane.Status = !string.IsNullOrWhiteSpace(swimmerName) ? LaneStatus.Ready : LaneStatus.OFF;
                    }
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

        public void GenerateSampleTemplate(string filePath, TimingMode mode = TimingMode.Pool)
        {
            using var workbook = new XLWorkbook();

            if (mode == TimingMode.OpenWater)
            {
                var ws = workbook.Worksheets.Add("OWS_StartList");

                // Row 1: Competition Name
                var cellLabel = ws.Cell(1, 1);
                cellLabel.Value = "Competition Name:";
                cellLabel.Style.Font.Bold = true;
                cellLabel.Style.Font.FontSize = 11;
                cellLabel.Style.Font.FontColor = XLColor.FromHtml("#0E7490");

                var cellValue = ws.Cell(1, 2);
                cellValue.Value = "Competition name or Meet name";
                cellValue.Style.Font.Bold = true;
                cellValue.Style.Font.FontSize = 12;

                // Row 2: Empty space / blank row

                // Row 3: Event Table Headers (No Heat for OWS)
                string[] headers = { "Event No", "Event Name", "No Bib", "Athlete", "Club" };
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = ws.Cell(3, i + 1);
                    cell.Value = headers[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0E7490"); // Ocean Teal
                    cell.Style.Font.FontColor = XLColor.White;
                }

                // Row 4: Sample row for user guidance
                var row4 = ws.Row(4);
                row4.Cell(1).Value = 1;
                row4.Cell(2).Value = "Event name";
                row4.Cell(3).Value = "No BIB";
                row4.Cell(4).Value = "Athlete Name";
                row4.Cell(5).Value = "Club Name";

                ws.Columns().AdjustToContents();
                workbook.SaveAs(filePath);
            }
            else
            {
                var ws = workbook.Worksheets.Add("StartList");

                // Row 1: Competition Name
                var cellLabel = ws.Cell(1, 1);
                cellLabel.Value = "Competition Name:";
                cellLabel.Style.Font.Bold = true;
                cellLabel.Style.Font.FontSize = 11;
                cellLabel.Style.Font.FontColor = XLColor.FromHtml("#1E293B");

                var cellValue = ws.Cell(1, 2);
                cellValue.Value = "Competition name or Meet name";
                cellValue.Style.Font.Bold = true;
                cellValue.Style.Font.FontSize = 12;

                // Row 2: Empty space / blank row

                // Row 3: Event Table Headers
                string[] headers = { "Event No", "Event Name", "Heat", "Lane", "Athlete", "Club" };
                for (int i = 0; i < headers.Length; i++)
                {
                    var cell = ws.Cell(3, i + 1);
                    cell.Value = headers[i];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                    cell.Style.Font.FontColor = XLColor.White;
                }

                // Row 4: Sample row for user guidance
                var row4 = ws.Row(4);
                row4.Cell(1).Value = 1;
                row4.Cell(2).Value = "Event name";
                row4.Cell(3).Value = 1;
                row4.Cell(4).Value = 1;
                row4.Cell(5).Value = "Athlete Name";
                row4.Cell(6).Value = "Club Name";

                ws.Columns().AdjustToContents();
                workbook.SaveAs(filePath);
            }
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
                throw new InvalidOperationException("No event number selected for export.");
            }

            // Verify that there is at least one result
            int totalResultsCount = targetEvents
                .SelectMany(ev => ev.Heats)
                .SelectMany(h => h.Lanes)
                .Count(MeetExportOptions.HasResult);

            if (totalResultsCount == 0)
            {
                throw new InvalidOperationException("There are no race results for the selected event. Only races with results can be exported.");
            }

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Official Results");

            // Title block
            string meetTitleSuffix = options.Mode == TimingMode.OpenWater ? "OFFICIAL OPEN WATER SWIMMING RESULTS" : "OFFICIAL RACE RESULTS";
            ws.Cell(1, 1).Value = $"{meet.MeetName} - {meetTitleSuffix}";
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#0F172A");

            string scopeDesc = options.Scope == ExportScope.FullMeet 
                ? "Full Meet (All Events)" 
                : $"Selected Events ({targetEvents.Count} Events)";

            ws.Cell(2, 1).Value = $"Date: {meet.MeetDate:dd MMMM yyyy} | Scope: {scopeDesc} | Mode: {options.Mode} | Generated: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell(2, 1).Style.Font.Italic = true;
            ws.Cell(2, 1).Style.Font.FontSize = 10;
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#64748B");

            // Header row
            string[] headers = options.Mode == TimingMode.OpenWater
                ? new[] { "Event #", "Event Name", "Rank", "BIB", "Athlete", "Club", "Finish Time", "Gap (+Diff)", "Status" }
                : new[] { "Event #", "Event Name", "Heat", "Rank", "Lane", "Athlete", "Club", "Finish Time", "Status" };

            int headerRowIndex = 4;
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(headerRowIndex, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml(options.Mode == TimingMode.OpenWater ? "#0E7490" : "#1E293B");
                cell.Style.Font.FontColor = XLColor.White;
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

                    TimeSpan? winnerTime = resultLanes.FirstOrDefault(l => l.Rank == 1)?.FinishTime;

                    foreach (var lane in resultLanes)
                    {
                        var row = ws.Row(currentRow);

                        if (options.Mode == TimingMode.OpenWater)
                        {
                            string gapDisplay = "-";
                            if (lane.Rank == 1)
                            {
                                gapDisplay = "+00.00.00";
                            }
                            else if (lane.Status == LaneStatus.Finished && lane.FinishTime.HasValue && winnerTime.HasValue)
                            {
                                var diff = lane.FinishTime.Value - winnerTime.Value;
                                if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
                                gapDisplay = "+" + LaneModel.FormatTime(diff);
                            }

                            row.Cell(1).Value = raceEvent.EventNumber;
                            row.Cell(2).Value = raceEvent.EventName;
                            row.Cell(3).Value = lane.Rank.HasValue ? lane.Rank.Value.ToString() : "-";
                            row.Cell(4).Value = lane.BibNumber;
                            row.Cell(5).Value = lane.SwimmerName;
                            row.Cell(6).Value = lane.Club;
                            row.Cell(7).Value = lane.FormattedTime;
                            row.Cell(8).Value = gapDisplay;
                            row.Cell(9).Value = lane.Status.ToString();

                            // Medals coloring for Rank 1, 2, 3 (Cell 3)
                            if (options.HighlightMedals)
                            {
                                if (lane.Rank == 1)
                                {
                                    row.Cell(3).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF08A"); // Gold tint
                                    row.Cell(3).Style.Font.Bold = true;
                                }
                                else if (lane.Rank == 2)
                                {
                                    row.Cell(3).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0"); // Silver tint
                                    row.Cell(3).Style.Font.Bold = true;
                                }
                                else if (lane.Rank == 3)
                                {
                                    row.Cell(3).Style.Fill.BackgroundColor = XLColor.FromHtml("#FED7AA"); // Bronze tint
                                    row.Cell(3).Style.Font.Bold = true;
                                }
                            }
                        }
                        else
                        {
                            row.Cell(1).Value = raceEvent.EventNumber;
                            row.Cell(2).Value = raceEvent.EventName;
                            row.Cell(3).Value = heat.HeatNumber;
                            row.Cell(4).Value = lane.Rank.HasValue ? lane.Rank.Value.ToString() : "-";
                            row.Cell(5).Value = lane.LaneNumber;
                            row.Cell(6).Value = lane.SwimmerName;
                            row.Cell(7).Value = lane.Club;
                            row.Cell(8).Value = lane.FormattedTime;
                            row.Cell(9).Value = lane.Status.ToString();

                            // Medals coloring for Rank 1, 2, 3 (Cell 4)
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

                    string eventSubInfo = options.Mode == TimingMode.OpenWater
                        ? $"Date: {meet.MeetDate:dd MMMM yyyy} | Mode: {options.Mode} | Total Finishers: {eventResultLanes.Count}"
                        : $"Date: {meet.MeetDate:dd MMMM yyyy} | Mode: {options.Mode} | Total Heats with Results: {raceEvent.Heats.Count(h => h.Lanes.Any(MeetExportOptions.HasResult))}";
                    eventWs.Cell(2, 1).Value = eventSubInfo;
                    eventWs.Cell(2, 1).Style.Font.Italic = true;
                    eventWs.Cell(2, 1).Style.Font.FontSize = 10;
                    eventWs.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#64748B");

                    string[] evHeaders = options.Mode == TimingMode.OpenWater
                        ? new[] { "Rank", "BIB", "Athlete", "Club", "Finish Time", "Gap (+Diff)", "Status" }
                        : new[] { "Heat", "Rank", "Lane", "Athlete", "Club", "Finish Time", "Status" };

                    int evHeaderRow = 4;
                    for (int i = 0; i < evHeaders.Length; i++)
                    {
                        var cell = eventWs.Cell(evHeaderRow, i + 1);
                        cell.Value = evHeaders[i];
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml(options.Mode == TimingMode.OpenWater ? "#0E7490" : "#1E293B");
                        cell.Style.Font.FontColor = XLColor.White;
                    }

                    int evRow = evHeaderRow + 1;
                    foreach (var heat in raceEvent.Heats)
                    {
                        var heatResults = heat.Lanes.Where(MeetExportOptions.HasResult).OrderBy(l => l.Rank ?? 99).ThenBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber).ToList();
                        if (heatResults.Count == 0) continue;

                        TimeSpan? evWinnerTime = heatResults.FirstOrDefault(l => l.Rank == 1)?.FinishTime;

                        foreach (var lane in heatResults)
                        {
                            var r = eventWs.Row(evRow);

                            if (options.Mode == TimingMode.OpenWater)
                            {
                                string gapDisplay = "-";
                                if (lane.Rank == 1)
                                {
                                    gapDisplay = "+00.00.00";
                                }
                                else if (lane.Status == LaneStatus.Finished && lane.FinishTime.HasValue && evWinnerTime.HasValue)
                                {
                                    var diff = lane.FinishTime.Value - evWinnerTime.Value;
                                    if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
                                    gapDisplay = "+" + LaneModel.FormatTime(diff);
                                }

                                r.Cell(1).Value = lane.Rank.HasValue ? lane.Rank.Value.ToString() : "-";
                                r.Cell(2).Value = lane.BibNumber;
                                r.Cell(3).Value = lane.SwimmerName;
                                r.Cell(4).Value = lane.Club;
                                r.Cell(5).Value = lane.FormattedTime;
                                r.Cell(6).Value = gapDisplay;
                                r.Cell(7).Value = lane.Status.ToString();

                                if (options.HighlightMedals)
                                {
                                    if (lane.Rank == 1) { r.Cell(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF08A"); r.Cell(1).Style.Font.Bold = true; }
                                    else if (lane.Rank == 2) { r.Cell(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0"); r.Cell(1).Style.Font.Bold = true; }
                                    else if (lane.Rank == 3) { r.Cell(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FED7AA"); r.Cell(1).Style.Font.Bold = true; }
                                }
                            }
                            else
                            {
                                r.Cell(1).Value = heat.HeatNumber;
                                r.Cell(2).Value = lane.Rank.HasValue ? lane.Rank.Value.ToString() : "-";
                                r.Cell(3).Value = lane.LaneNumber;
                                r.Cell(4).Value = lane.SwimmerName;
                                r.Cell(5).Value = lane.Club;
                                r.Cell(6).Value = lane.FormattedTime;
                                r.Cell(7).Value = lane.Status.ToString();

                                if (options.HighlightMedals)
                                {
                                    if (lane.Rank == 1) { r.Cell(2).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF08A"); r.Cell(2).Style.Font.Bold = true; }
                                    else if (lane.Rank == 2) { r.Cell(2).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0"); r.Cell(2).Style.Font.Bold = true; }
                                    else if (lane.Rank == 3) { r.Cell(2).Style.Fill.BackgroundColor = XLColor.FromHtml("#FED7AA"); r.Cell(2).Style.Font.Bold = true; }
                                }
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

        private static string CleanCompetitionTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            string[] prefixes = { "competition name:", "nama kompetisi:", "nama kejuaraan:", "competition:", "meet name:", "kejuaraan:" };
            foreach (var p in prefixes)
            {
                if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    return text.Substring(p.Length).Trim();
                }
            }
            return text.Trim();
        }
    }
}
