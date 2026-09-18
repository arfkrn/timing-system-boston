using System.Windows.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using boston_timing_system.Helpers;
using boston_timing_system.Models;
using boston_timing_system.Services;

namespace boston_timing_system.Views
{
    public class EventCheckItem : ObservableObject
    {
        private bool _isSelected = true;

        public RaceEventModel Event { get; }
        public int EventNumber => Event.EventNumber;
        public string DisplayTitle => Event.DisplayTitle;
        public int TotalHeats => Event.Heats.Count;
        public int HeatsWithResults => Event.Heats.Count(h => h.Lanes.Any(MeetExportOptions.HasResult));
        public int ResultsCount { get; }
        public string ResultCountText => ResultsCount > 0 
            ? $"({TotalHeats} Heat | {ResultsCount} results recorded)" 
            : $"({TotalHeats} Heat | No results yet)";

        public Brush ResultStatusBrush => ResultsCount > 0
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public EventCheckItem(RaceEventModel raceEvent)
        {
            Event = raceEvent ?? throw new ArgumentNullException(nameof(raceEvent));
            ResultsCount = raceEvent.Heats.SelectMany(h => h.Lanes).Count(MeetExportOptions.HasResult);
        }
    }

    public partial class ExportSelectionDialog : Window
    {
        private readonly CompetitionMeetModel _meet;
        private readonly ExcelMeetDataService _excelService;
        private readonly TimingMode _timingMode;
        private readonly ObservableCollection<EventCheckItem> _eventItems = new();

        public IReadOnlyList<EventCheckItem> EventItems => _eventItems;
        public bool HasEventsWithResults => _eventItems.Count > 0;

        public bool ExportSuccessful { get; private set; }
        public string? ExportedFilePath { get; private set; }

        public ExportSelectionDialog(CompetitionMeetModel meet, ExcelMeetDataService excelService, TimingMode timingMode = TimingMode.Pool)
        {
            InitializeComponent();

            _meet = meet ?? throw new ArgumentNullException(nameof(meet));
            _excelService = excelService ?? new ExcelMeetDataService();
            _timingMode = timingMode;

            PopulateEvents();
        }

        private void PopulateEvents()
        {
            _eventItems.Clear();

            foreach (var raceEvent in _meet.Events)
            {
                var item = new EventCheckItem(raceEvent)
                {
                    IsSelected = true
                };

                // Hanya tampilkan event yang sudah memiliki hasil balapan tercatat
                if (item.ResultsCount > 0)
                {
                    _eventItems.Add(item);
                }
            }

            lbEvents.ItemsSource = _eventItems;
            UpdateDisplayState();
        }

        private void UpdateDisplayState()
        {
            bool hasItems = _eventItems.Count > 0;
            if (pnlEmptyState != null)
            {
                pnlEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            }
            if (lbEvents != null)
            {
                lbEvents.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            }
            if (btnSelectAll != null)
            {
                btnSelectAll.IsEnabled = hasItems;
            }
            if (btnDeselectAll != null)
            {
                btnDeselectAll.IsEnabled = hasItems;
            }
            if (btnExportNow != null)
            {
                btnExportNow.IsEnabled = hasItems;
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _eventItems)
            {
                item.IsSelected = true;
            }
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _eventItems)
            {
                item.IsSelected = false;
            }
        }

        private async void BtnExportNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedEventNumbers = _eventItems
                    .Where(i => i.IsSelected)
                    .Select(i => i.EventNumber)
                    .ToList();

                if (selectedEventNumbers.Count == 0)
                {
                    MessageBox.Show("Please check at least one race event to export.", 
                        "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MeetExportOptions options;
                string suggestedFileName;

                if (selectedEventNumbers.Count == _meet.Events.Count)
                {
                    options = MeetExportOptions.CreateFullMeet(_timingMode);
                    suggestedFileName = _timingMode == TimingMode.OpenWater
                        ? $"{_meet.MeetName.Replace(" ", "_")}_OWS_Results_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
                        : $"{_meet.MeetName.Replace(" ", "_")}_AllResults_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
                }
                else
                {
                    options = MeetExportOptions.CreateSelectedEvents(selectedEventNumbers, _timingMode);
                    suggestedFileName = selectedEventNumbers.Count == 1
                        ? $"{_meet.MeetName.Replace(" ", "_")}_Event{selectedEventNumbers[0]}_Results_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
                        : $"{_meet.MeetName.Replace(" ", "_")}_Selected_{selectedEventNumbers.Count}Events_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
                }

                string selectedSavePath = string.Empty;
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = suggestedFileName,
                    Title = "Save Race Results to Excel File"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    selectedSavePath = saveDialog.FileName;
                    btnExportNow.IsEnabled = false;
                    btnCancel.IsEnabled = false;
                    Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                    try
                    {
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            _excelService.ExportResultsToExcel(_meet, saveDialog.FileName, options);
                        });

                        ExportSuccessful = true;
                        ExportedFilePath = saveDialog.FileName;

                        MessageBox.Show($"Race results successfully exported to Excel file:\n\n{saveDialog.FileName}", 
                            "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);

                        DialogResult = true;
                        Close();
                    }
                    finally
                    {
                        Mouse.OverrideCursor = null;
                        btnExportNow.IsEnabled = true;
                        btnCancel.IsEnabled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is IOException ioEx && ((ioEx.HResult & 0xFFFF) == 32 || (ioEx.HResult & 0xFFFF) == 33 || ioEx.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase) || ioEx.Message.Contains("digunakan oleh proses lain", StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show(
                        "The Excel file is currently open in another program (such as Microsoft Excel).\n\nPlease close the file in Microsoft Excel first, or choose a different file name.",
                        "Excel File Is Currently Open",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"Failed to export results to Excel:\n\n{ex.Message}", 
                        "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
