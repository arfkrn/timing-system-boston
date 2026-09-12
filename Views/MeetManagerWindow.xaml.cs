using System.Windows.Input;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using boston_timing_system.Models;
using boston_timing_system.Services;

namespace boston_timing_system.Views
{
    public partial class MeetManagerWindow : Window
    {
        private readonly ExcelMeetDataService _excelService;
        public CompetitionMeetModel Meet { get; private set; }

        private RaceEventModel? _selectedEvent;
        private HeatModel? _selectedHeat;
        private bool _isUpdatingUi;

        public MeetManagerWindow(CompetitionMeetModel currentMeet, ExcelMeetDataService excelService)
        {
            InitializeComponent();

            Meet = currentMeet ?? new CompetitionMeetModel();
            _excelService = excelService ?? new ExcelMeetDataService();

            BindMeetData();
        }

        private void BindMeetData()
        {
            txtMeetName.Text = Meet.MeetName;
            tvEvents.ItemsSource = Meet.Events;

            if (Meet.SelectedHeat != null && Meet.SelectedEvent != null && Meet.Events.Contains(Meet.SelectedEvent))
            {
                SelectHeat(Meet.SelectedHeat, Meet.SelectedEvent);
            }
            else
            {
                SelectFirstAvailable();
            }
        }

        private void SelectHeat(HeatModel heat, RaceEventModel parentEvent)
        {
            _isUpdatingUi = true;
            try
            {
                _selectedEvent = parentEvent;
                _selectedHeat = heat;

                txtEventNumber.Text = parentEvent.EventNumber.ToString();
                txtEventName.Text = parentEvent.EventName;
                txtEventNumber.IsEnabled = true;
                txtEventName.IsEnabled = true;
                btnClearHeat.IsEnabled = true;

                txtSelectedHeatTitle.Text = $"{parentEvent.DisplayTitle} - {heat.DisplayTitle}";
                UpdateSelectedHeatSubtitle();

                dgHeatLanes.ItemsSource = heat.Lanes;
                Meet.SelectedEvent = parentEvent;
                Meet.SelectedHeat = heat;
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void SelectEventOnly(RaceEventModel raceEvent)
        {
            _isUpdatingUi = true;
            try
            {
                _selectedEvent = raceEvent;
                _selectedHeat = raceEvent.Heats.FirstOrDefault();

                txtEventNumber.Text = raceEvent.EventNumber.ToString();
                txtEventName.Text = raceEvent.EventName;
                txtEventNumber.IsEnabled = true;
                txtEventName.IsEnabled = true;

                if (_selectedHeat != null)
                {
                    btnClearHeat.IsEnabled = true;
                    txtSelectedHeatTitle.Text = $"{raceEvent.DisplayTitle} - {_selectedHeat.DisplayTitle}";
                    UpdateSelectedHeatSubtitle();
                    dgHeatLanes.ItemsSource = _selectedHeat.Lanes;
                    Meet.SelectedEvent = raceEvent;
                    Meet.SelectedHeat = _selectedHeat;
                }
                else
                {
                    btnClearHeat.IsEnabled = false;
                    txtSelectedHeatTitle.Text = $"{raceEvent.DisplayTitle} (Belum ada Heat)";
                    txtSelectedHeatSubtitle.Text = "Klik tombol '+ Heat' untuk menambahkan heat baru.";
                    dgHeatLanes.ItemsSource = null;
                    Meet.SelectedEvent = raceEvent;
                    Meet.SelectedHeat = null;
                }
            }
            finally
            {
                _isUpdatingUi = false;
            }
        }

        private void SelectFirstAvailable()
        {
            if (Meet.Events.Count > 0)
            {
                var ev = Meet.Events[0];
                if (ev.Heats.Count > 0)
                {
                    SelectHeat(ev.Heats[0], ev);
                }
                else
                {
                    SelectEventOnly(ev);
                }
            }
            else
            {
                _selectedEvent = null;
                _selectedHeat = null;
                Meet.SelectedEvent = null;
                Meet.SelectedHeat = null;

                _isUpdatingUi = true;
                try
                {
                    txtEventNumber.Text = string.Empty;
                    txtEventName.Text = string.Empty;
                    txtEventNumber.IsEnabled = false;
                    txtEventName.IsEnabled = false;
                    btnClearHeat.IsEnabled = false;
                    txtSelectedHeatTitle.Text = "Tidak ada event yang terdaftar";
                    txtSelectedHeatSubtitle.Text = "Klik tombol '+ Event' untuk membuat nomor perlombaan baru.";
                    dgHeatLanes.ItemsSource = null;
                }
                finally
                {
                    _isUpdatingUi = false;
                }
            }
        }

        private void UpdateSelectedHeatSubtitle()
        {
            if (_selectedHeat != null)
            {
                int swimmerCount = _selectedHeat.Lanes.Count(l => l.Status != LaneStatus.OFF && !string.IsNullOrWhiteSpace(l.SwimmerName));
                txtSelectedHeatSubtitle.Text = $"{swimmerCount} Atlet aktif dari 10 Lintasan";
            }
            else
            {
                txtSelectedHeatSubtitle.Text = string.Empty;
            }
        }

        private void TvEvents_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is HeatModel heat)
            {
                var parentEvent = Meet.Events.FirstOrDefault(ev => ev.Heats.Contains(heat));
                if (parentEvent != null)
                {
                    SelectHeat(heat, parentEvent);
                }
            }
            else if (e.NewValue is RaceEventModel raceEvent)
            {
                SelectEventOnly(raceEvent);
            }
        }

        private void BtnAddEvent_Click(object sender, RoutedEventArgs e)
        {
            int nextNum = Meet.Events.Count > 0 ? Meet.Events.Max(ev => ev.EventNumber) + 1 : 1;
            var newEvent = Meet.AddEvent(nextNum, $"Event Baru #{nextNum:D2}");
            SelectHeat(newEvent.Heats[0], newEvent);
        }

        private void BtnAddHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedEvent == null)
            {
                if (Meet.Events.Count > 0)
                {
                    _selectedEvent = Meet.Events[0];
                }
                else
                {
                    MessageBox.Show("Silakan buat Event terlebih dahulu dengan tombol '+ Event'.", "Informasi", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            var newHeat = _selectedEvent.AddHeat();
            SelectHeat(newHeat, _selectedEvent);
        }

        private void BtnDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedItem = tvEvents.SelectedItem ?? _selectedHeat ?? (object?)_selectedEvent;
            if (selectedItem is HeatModel heat)
            {
                var parentEvent = Meet.Events.FirstOrDefault(ev => ev.Heats.Contains(heat));
                if (parentEvent != null)
                {
                    if (parentEvent.Heats.Count <= 1)
                    {
                        var result = MessageBox.Show(
                            $"Heat ini adalah satu-satunya heat pada {parentEvent.DisplayTitle}.\nMenghapusnya akan menghapus seluruh event.\n\nLanjutkan hapus Event?",
                            "Konfirmasi Hapus",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            Meet.RemoveEvent(parentEvent);
                            SelectFirstAvailable();
                        }
                    }
                    else
                    {
                        var result = MessageBox.Show(
                            $"Hapus {heat.DisplayTitle} dari {parentEvent.DisplayTitle}?",
                            "Konfirmasi Hapus Heat",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            parentEvent.RemoveHeat(heat);
                            if (parentEvent.Heats.Count > 0)
                            {
                                SelectHeat(parentEvent.Heats[0], parentEvent);
                            }
                            else
                            {
                                SelectEventOnly(parentEvent);
                            }
                        }
                    }
                }
            }
            else if (selectedItem is RaceEventModel raceEvent)
            {
                var result = MessageBox.Show(
                    $"Hapus {raceEvent.DisplayTitle} beserta seluruh heat dan atlet di dalamnya?",
                    "Konfirmasi Hapus Event",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    Meet.RemoveEvent(raceEvent);
                    SelectFirstAvailable();
                }
            }
            else
            {
                MessageBox.Show("Silakan pilih Event atau Heat dari daftar di sebelah kiri untuk dihapus.", "Pilih Item", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void TxtEventNumber_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedEvent == null) return;

            if (int.TryParse(txtEventNumber.Text.Trim(), out int newNum) && newNum > 0)
            {
                _selectedEvent.EventNumber = newNum;
                if (_selectedHeat != null)
                {
                    txtSelectedHeatTitle.Text = $"{_selectedEvent.DisplayTitle} - {_selectedHeat.DisplayTitle}";
                }
            }
        }

        private void TxtEventName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedEvent == null) return;

            _selectedEvent.EventName = txtEventName.Text.Trim();
            if (_selectedHeat != null)
            {
                txtSelectedHeatTitle.Text = $"{_selectedEvent.DisplayTitle} - {_selectedHeat.DisplayTitle}";
            }
        }

        private void BtnClearHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedHeat == null) return;

            var result = MessageBox.Show(
                $"Kosongkan seluruh atlet pada {_selectedHeat.DisplayTitle}?",
                "Konfirmasi Kosongkan Heat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _selectedHeat.ClearSwimmers();
                UpdateSelectedHeatSubtitle();
            }
        }

        private void DgHeatLanes_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Row.Item is LaneModel lane)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (!string.IsNullOrWhiteSpace(lane.SwimmerName))
                    {
                        if (lane.Status == LaneStatus.OFF)
                        {
                            lane.Status = LaneStatus.Ready;
                        }
                    }
                    else
                    {
                        if (lane.Status == LaneStatus.Ready)
                        {
                            lane.Status = LaneStatus.OFF;
                        }
                    }
                    UpdateSelectedHeatSubtitle();
                }));
            }
        }

        private void BtnClearLane_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LaneModel lane)
            {
                lane.SwimmerName = string.Empty;
                lane.Club = string.Empty;
                lane.SeedTime = string.Empty;
                lane.FinishTime = null;
                lane.FormattedTime = "00.00.00";
                lane.Rank = null;
                lane.Status = LaneStatus.OFF;

                UpdateSelectedHeatSubtitle();
            }
        }

        private async void BtnImportExcel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                Title = "Import Swimming Meet Start List"
            };

            if (dialog.ShowDialog() == true)
            {
                btnImportExcel.IsEnabled = false;
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    var importedMeet = await System.Threading.Tasks.Task.Run(() => _excelService.ImportMeetFromExcel(dialog.FileName));
                    Meet = importedMeet;

                    BindMeetData();

                    MessageBox.Show($"Start list imported successfully!\n\nEvents: {Meet.Events.Count}\nTotal Heats: {Meet.Events.Sum(ev => ev.Heats.Count)}",
                        "Import Successful", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to import Excel file:\n\n{ex.Message}", 
                        "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    btnImportExcel.IsEnabled = true;
                }
            }
        }

        private async void BtnDownloadTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = "Swimming_StartList_Template.xlsx",
                Title = "Download Start List Template"
            };

            if (dialog.ShowDialog() == true)
            {
                btnDownloadTemplate.IsEnabled = false;
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    await System.Threading.Tasks.Task.Run(() => _excelService.GenerateSampleTemplate(dialog.FileName));
                    MessageBox.Show($"Excel template saved successfully to:\n{dialog.FileName}\n\nYou can fill in your meet data and import it anytime.", 
                        "Template Created", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to create template:\n{ex.Message}", "Template Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    btnDownloadTemplate.IsEnabled = true;
                }
            }
        }

        private void BtnExportResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportDialog = new ExportSelectionDialog(Meet, _excelService)
                {
                    Owner = this
                };

                exportDialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Gagal membuka dialog ekspor hasil:\n\n{ex.Message}", "Error Ekspor", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            Meet.MeetName = txtMeetName.Text.Trim();

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
