using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Printing;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using boston_timing_system.Models;

namespace boston_timing_system.Services
{
    public class ThermalPrintService
    {
        private const int LineWidth = 32; // 32 characters for standard 58mm receipt

        /// <summary>
        /// Retrieves list of installed printer names from the local Windows print server.
        /// </summary>
        public List<string> GetInstalledPrinters()
        {
            var list = new List<string>();
            try
            {
                var printServer = new LocalPrintServer();
                var queues = printServer.GetPrintQueues(new[] {
                    EnumeratedPrintQueueTypes.Local,
                    EnumeratedPrintQueueTypes.Connections
                });

                foreach (var queue in queues)
                {
                    list.Add(queue.Name);
                }
            }
            catch
            {
                // Fallback: Use standard .NET drawing if needed, or return default
            }

            if (list.Count == 0)
            {
                try
                {
                    var server = new LocalPrintServer();
                    if (server.DefaultPrintQueue != null)
                    {
                        list.Add(server.DefaultPrintQueue.Name);
                    }
                }
                catch { }
            }

            return list;
        }

        /// <summary>
        /// Attempts to find a recommended 58mm thermal printer name among installed printers.
        /// </summary>
        public string? FindRecommendedThermalPrinter(IEnumerable<string> printers)
        {
            var keywords = new[] { "58", "pos", "thermal", "receipt", "struk", "xprinter", "xp-", "epson" };
            foreach (var printer in printers)
            {
                string lower = printer.ToLowerInvariant();
                if (keywords.Any(k => lower.Contains(k)))
                {
                    return printer;
                }
            }
            return printers.FirstOrDefault();
        }

        /// <summary>
        /// Generates clean 32-character monospace receipt text suitable for 58mm printers.
        /// </summary>
        public string GenerateReceiptText(CompetitionMeetModel meet, HeatModel heat)
        {
            var sb = new StringBuilder();
            heat.CalculateRanks();

            // Header
            sb.AppendLine("================================");
            sb.AppendLine(CenterText("BOSTON TIMING PRO", LineWidth));
            sb.AppendLine(CenterText("OFFICIAL RACE RESULTS", LineWidth));
            sb.AppendLine("================================");

            // Competition Info matching reference layout
            string meetTitle = string.IsNullOrWhiteSpace(meet.MeetName) ? "SWIMMING CHAMPIONSHIP" : meet.MeetName.ToUpperInvariant();
            sb.AppendLine(CenterText(meetTitle, LineWidth));

            string dateStr = meet.MeetDate.ToString("dd MMMM yyyy", new System.Globalization.CultureInfo("id-ID")).ToUpperInvariant();
            sb.AppendLine(CenterText(dateStr, LineWidth));
            sb.AppendLine("--------------------------------");

            // Event & Heat Info
            sb.AppendLine(TruncateOrPad($"EVNT #{heat.EventNumber} : {heat.EventName}", LineWidth));
            sb.AppendLine(TruncateOrPad($"HEAT #{heat.HeatNumber}", LineWidth));
            sb.AppendLine("--------------------------------");

            // Columns Header: LN NAME                  TIME RK
            // Format: " 1 BUDI SANTOSO      00.27.45  1"
            sb.AppendLine("LN NAME                  TIME RK");
            sb.AppendLine("--------------------------------");

            var activeLanes = heat.Lanes
                .Where(l => l.Status != LaneStatus.OFF || !string.IsNullOrWhiteSpace(l.SwimmerName))
                .OrderBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber)
                .ToList();

            if (activeLanes.Count == 0)
            {
                // If all are OFF, show all 10 lanes ordered by lane number (1 to 9, then 0)
                activeLanes = heat.Lanes.OrderBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber).ToList();
            }

            foreach (var lane in activeLanes)
            {
                string ln = lane.LaneNumber.ToString().PadLeft(2);
                
                string name = string.IsNullOrWhiteSpace(lane.SwimmerName) ? $"LANE {lane.LaneNumber}" : lane.SwimmerName.Trim();
                if (name.Length > 17) name = name.Substring(0, 17);
                name = name.PadRight(17);

                string timeStr = lane.Status switch
                {
                    LaneStatus.Finished => lane.FormattedTime,
                    LaneStatus.DQ => "DQ",
                    LaneStatus.DNS => "DNS",
                    LaneStatus.DNF => "DNF",
                    LaneStatus.OFF => "OFF",
                    _ => lane.FormattedTime
                };

                if (timeStr.Length > 8) timeStr = timeStr.Substring(0, 8);
                timeStr = timeStr.PadLeft(8);

                string rk = (lane.Rank.HasValue && lane.Status == LaneStatus.Finished)
                    ? lane.Rank.Value.ToString().PadLeft(2)
                    : " -";
                if (rk.Length > 2) rk = rk.Substring(0, 2);
                rk = rk.PadLeft(2);

                sb.AppendLine($"{ln} {name} {timeStr} {rk}");
            }

            sb.AppendLine("--------------------------------");
            sb.AppendLine($"Total Swimmers: {activeLanes.Count(l => l.Status != LaneStatus.OFF)}");
            sb.AppendLine();
            sb.AppendLine("Chief Referee:");
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("(______________________________)");
            sb.AppendLine();
            sb.AppendLine(CenterText($"Print: {DateTime.Now:dd/MM/yy HH:mm:ss}", LineWidth));
            sb.AppendLine("================================");
            sb.AppendLine();
            sb.AppendLine(); // Feed lines for paper cut

            return sb.ToString();
        }

        /// <summary>
        /// Creates a WPF Visual (Border element) formatted precisely for 58mm thermal paper width (~190-200 DIPs).
        /// High contrast pure black on pure white for thermal print heads.
        /// </summary>
        public FrameworkElement CreateReceiptVisual(CompetitionMeetModel meet, HeatModel heat)
        {
            heat.CalculateRanks();

            var container = new Border
            {
                Width = 200, // ~52.9mm at 96 DPI (exact printable area for 58mm paper)
                Background = Brushes.White,
                Padding = new Thickness(6, 8, 6, 8),
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };

            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Width = 188
            };
            container.Child = stack;

            // 1. Logo at top center (Matches Reference Image)
            var logoSource = LoadLogo();
            if (logoSource != null)
            {
                var logoImg = new Image
                {
                    Source = logoSource,
                    MaxWidth = 155,
                    MaxHeight = 55,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                stack.Children.Add(logoImg);
            }
            else
            {
                // Fallback text if logo image is unavailable
                stack.Children.Add(new TextBlock
                {
                    Text = "BOSTON TIMING SYSTEM",
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                    Foreground = Brushes.Black,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 6)
                });
            }

            // 2. Meet Name (Bold, uppercase, centered, wrapped - Matches Reference Image)
            string meetTitle = string.IsNullOrWhiteSpace(meet.MeetName) ? "SWIMMING CHAMPIONSHIP" : meet.MeetName.ToUpperInvariant();
            stack.Children.Add(new TextBlock
            {
                Text = meetTitle,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4)
            });

            // 3. Meet Date (e.g. "10 SEPTEMBER 2026" - Matches Reference Image)
            string dateStr = meet.MeetDate.ToString("dd MMMM yyyy", new System.Globalization.CultureInfo("id-ID")).ToUpperInvariant();
            stack.Children.Add(new TextBlock
            {
                Text = dateStr,
                FontWeight = FontWeights.SemiBold,
                FontSize = 10,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            });

            // 4. Event & Heat 3-Column Block (Matches Reference Image)
            // Left Column: EVENT / {EventNumber}
            // Center Column: {EventName}
            // Right Column: HEAT / {HeatNumber}
            var eventHeatGrid = new Grid { Margin = new Thickness(0, 4, 0, 6) };
            eventHeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) }); // EVENT
            eventHeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // EVENT NAME
            eventHeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) }); // HEAT

            // Left: EVENT
            var eventPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            eventPanel.Children.Add(new TextBlock
            {
                Text = "EVENT",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center
            });
            eventPanel.Children.Add(new TextBlock
            {
                Text = heat.EventNumber.ToString(),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(eventPanel, 0);
            eventHeatGrid.Children.Add(eventPanel);

            // Center: EVENT NAME (e.g. "50M GAYA BEBAS PUTRI")
            string eventName = string.IsNullOrWhiteSpace(heat.EventName) ? "EVENT" : heat.EventName.ToUpperInvariant();
            var eventNameBlock = new TextBlock
            {
                Text = eventName,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0)
            };
            Grid.SetColumn(eventNameBlock, 1);
            eventHeatGrid.Children.Add(eventNameBlock);

            // Right: HEAT
            var heatPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            heatPanel.Children.Add(new TextBlock
            {
                Text = "HEAT",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center
            });
            heatPanel.Children.Add(new TextBlock
            {
                Text = heat.HeatNumber.ToString(),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(heatPanel, 2);
            eventHeatGrid.Children.Add(heatPanel);

            stack.Children.Add(eventHeatGrid);
            stack.Children.Add(CreateDivider(false));

            // Table Header: LN | NAME | TIME | RK
            var tableHeader = new Grid { Margin = new Thickness(0, 2, 0, 3) };
            tableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // LN
            tableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // NAME
            tableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); // TIME
            tableHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // RK

            AddGridCell(tableHeader, "LN", 0, 0, FontWeights.Bold, TextAlignment.Center, 8.5);
            AddGridCell(tableHeader, "NAME", 0, 1, FontWeights.Bold, TextAlignment.Left, 8.5);
            AddGridCell(tableHeader, "TIME", 0, 2, FontWeights.Bold, TextAlignment.Right, 8.5);
            AddGridCell(tableHeader, "RK", 0, 3, FontWeights.Bold, TextAlignment.Center, 8.5);

            stack.Children.Add(tableHeader);
            stack.Children.Add(CreateDivider(false));

            // Lane Rows ordered by LaneNumber (penalized swimmers remain in their lane order)
            var activeLanes = heat.Lanes
                .Where(l => l.Status != LaneStatus.OFF || !string.IsNullOrWhiteSpace(l.SwimmerName))
                .OrderBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber)
                .ToList();

            if (activeLanes.Count == 0)
            {
                activeLanes = heat.Lanes.OrderBy(l => l.LaneNumber == 0 ? 10 : l.LaneNumber).ToList();
            }

            foreach (var lane in activeLanes)
            {
                var rowGrid = new Grid { Margin = new Thickness(0, 1.5, 0, 1.5) };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // LN
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // NAME
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); // TIME
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) }); // RK

                string lnText = lane.LaneNumber.ToString();
                string nameText = string.IsNullOrWhiteSpace(lane.SwimmerName) ? $"Lane {lane.LaneNumber}" : lane.SwimmerName;

                string timeText = lane.Status switch
                {
                    LaneStatus.Finished => lane.FormattedTime,
                    LaneStatus.DQ => "DQ",
                    LaneStatus.DNS => "DNS",
                    LaneStatus.DNF => "DNF",
                    LaneStatus.OFF => "OFF",
                    _ => lane.FormattedTime
                };

                string rkText = (lane.Rank.HasValue && lane.Status == LaneStatus.Finished)
                    ? lane.Rank.Value.ToString()
                    : "-";

                AddGridCell(rowGrid, lnText, 0, 0, FontWeights.SemiBold, TextAlignment.Center, 8.5);
                AddGridCell(rowGrid, nameText, 0, 1, FontWeights.Normal, TextAlignment.Left, 8.5, true);
                AddGridCell(rowGrid, timeText, 0, 2, FontWeights.Bold, TextAlignment.Right, 8.5);
                AddGridCell(rowGrid, rkText, 0, 3, FontWeights.SemiBold, TextAlignment.Center, 8.5);

                stack.Children.Add(rowGrid);
            }

            stack.Children.Add(CreateDivider(false));

            // Summary
            int totalFinished = activeLanes.Count(l => l.Status == LaneStatus.Finished);
            var summaryGrid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddGridCell(summaryGrid, $"Total: {activeLanes.Count}", 0, 0, FontWeights.Normal, TextAlignment.Left, 8.0);
            AddGridCell(summaryGrid, $"Finish: {totalFinished}", 0, 1, FontWeights.Normal, TextAlignment.Right, 8.0);
            stack.Children.Add(summaryGrid);

            // Footer info
            stack.Children.Add(new TextBlock
            {
                Text = $"Printed: {DateTime.Now:dd/MM/yyyy HH:mm:ss}",
                FontSize = 7.5,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });

            stack.Children.Add(new TextBlock
            {
                Text = "* Boston Timing System *",
                FontSize = 7.5,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 1, 0, 6)
            });

            return container;
        }

        /// <summary>
        /// Prints the FrameworkElement visual directly to a specified printer or default printer.
        /// </summary>
        public (bool Success, string Message) PrintVisualToPrinter(FrameworkElement visual, string? printerName = null)
        {
            try
            {
                var printDialog = new PrintDialog();

                if (!string.IsNullOrWhiteSpace(printerName))
                {
                    var printServer = new LocalPrintServer();
                    var queue = printServer.GetPrintQueue(printerName);
                    if (queue != null)
                    {
                        printDialog.PrintQueue = queue;
                    }
                }

                // Measure and arrange visual if it is not currently rendered in visual tree
                visual.Measure(new Size(200, double.PositiveInfinity));
                visual.Arrange(new Rect(0, 0, 200, visual.DesiredSize.Height));
                visual.UpdateLayout();

                printDialog.PrintVisual(visual, "Race Result 58mm");
                return (true, $"Printed successfully to {printDialog.PrintQueue?.Name ?? "Default Printer"}");
            }
            catch (Exception ex)
            {
                return (false, $"Print error: {ex.Message}");
            }
        }

        private static ImageSource? LoadLogo()
        {
            try
            {
                if (Application.Current != null)
                {
                    var packUri = new Uri("pack://application:,,,/Assets/logo-boston-new.png", UriKind.Absolute);
                    var bmp = new BitmapImage(packUri);
                    return bmp;
                }
            }
            catch { }

            try
            {
                string[] candidates = {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "logo-boston-new.png"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Assets", "logo-boston-new.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Assets", "logo-boston-new.png"),
                    @"d:\coding\boston-timing-system\boston-timing-system\boston-timing-system\Assets\logo-boston-new.png"
                };

                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch { }

            return null;
        }

        private static Border CreateDivider(bool isDouble)
        {
            return new Border
            {
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(0, 0, 0, isDouble ? 1.5 : 0.8),
                Height = isDouble ? 2 : 1,
                Margin = new Thickness(0, 3, 0, 3)
            };
        }

        private static Grid CreateTwoColText(string label, string val, bool isBold)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lblBlock = new TextBlock
            {
                Text = label,
                FontSize = 8.5,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(lblBlock, 0);
            g.Children.Add(lblBlock);

            var valBlock = new TextBlock
            {
                Text = val,
                FontSize = 8.5,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(valBlock, 1);
            g.Children.Add(valBlock);

            return g;
        }

        private static void AddGridCell(Grid grid, string text, int row, int col, FontWeight weight, TextAlignment align, double fontSize, bool trim = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                FontFamily = new FontFamily("Segoe UI, Arial, sans-serif"),
                Foreground = Brushes.Black,
                FontWeight = weight,
                TextAlignment = align
            };
            if (trim)
            {
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
            }
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        private static string CenterText(string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return new string(' ', width);
            if (text.Length >= width) return text.Substring(0, width);
            int left = (width - text.Length) / 2;
            int right = width - text.Length - left;
            return new string(' ', left) + text + new string(' ', right);
        }

        private static string TruncateOrPad(string text, int width)
        {
            if (text.Length > width) return text.Substring(0, width);
            return text.PadRight(width);
        }
    }
}
