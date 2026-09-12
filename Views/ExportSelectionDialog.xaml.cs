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
            ? $"({TotalHeats} Heat | {ResultsCount} hasil tercatat)" 
            : $"({TotalHeats} Heat | Belum ada hasil)";

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
        private readonly ObservableCollection<EventCheckItem> _eventItems = new();

        public IReadOnlyList<EventCheckItem> EventItems => _eventItems;
        public bool HasEventsWithResults => _eventItems.Count > 0;

        public bool ExportSuccessful { get; private set; }
        public string? ExportedFilePath { get; private set; }

        public ExportSelectionDialog(CompetitionMeetModel meet, ExcelMeetDataService excelService)
        {
            InitializeComponent();

            _meet = meet ?? throw new ArgumentNullException(nameof(meet));
            _excelService = excelService ?? new ExcelMeetDataService();

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

        private void BtnExportNow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedEventNumbers = _eventItems
                    .Where(i => i.IsSelected)
                    .Select(i => i.EventNumber)
                    .ToList();

                if (selectedEventNumbers.Count == 0)
                {
                    MessageBox.Show("Silakan centang minimal satu nomor lomba (event) untuk diekspor.", 
                        "Peringatan", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                MeetExportOptions options;
                string suggestedFileName;

                if (selectedEventNumbers.Count == _meet.Events.Count)
                {
                    options = MeetExportOptions.CreateFullMeet();
                    suggestedFileName = $"{_meet.MeetName.Replace(" ", "_")}_AllResults_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
                }
                else
                {
                    options = MeetExportOptions.CreateSelectedEvents(selectedEventNumbers);
                    suggestedFileName = selectedEventNumbers.Count == 1
                        ? $"{_meet.MeetName.Replace(" ", "_")}_Event{selectedEventNumbers[0]}_Results_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
                        : $"{_meet.MeetName.Replace(" ", "_")}_Selected_{selectedEventNumbers.Count}Events_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
                }

                var saveDialog = new SaveFileDialog
                {
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = suggestedFileName,
                    Title = "Simpan Hasil Balapan ke File Excel"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    _excelService.ExportResultsToExcel(_meet, saveDialog.FileName, options);

                    ExportSuccessful = true;
                    ExportedFilePath = saveDialog.FileName;

                    MessageBox.Show($"Hasil balapan berhasil diekspor ke file Excel:\n\n{saveDialog.FileName}", 
                        "Ekspor Berhasil", MessageBoxButton.OK, MessageBoxImage.Information);

                    DialogResult = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Gagal mengekspor hasil ke Excel:\n\n{ex.Message}", 
                    "Ekspor Gagal", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
