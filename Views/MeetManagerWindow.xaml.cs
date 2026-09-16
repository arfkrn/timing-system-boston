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
        private readonly MeetPersistenceService _persistenceService;
        private readonly TimingMode _timingMode;
        public CompetitionMeetModel Meet { get; private set; }

        private RaceEventModel? _selectedEvent;
        private HeatModel? _selectedHeat;
        private bool _isUpdatingUi;

        public MeetManagerWindow(CompetitionMeetModel currentMeet, ExcelMeetDataService excelService, TimingMode timingMode = TimingMode.Pool, MeetPersistenceService? persistenceService = null)
        {
            InitializeComponent();

            _timingMode = timingMode;
            Meet = currentMeet ?? new CompetitionMeetModel();
            _excelService = excelService ?? new ExcelMeetDataService();
            _persistenceService = persistenceService ?? new MeetPersistenceService();

            ConfigureModeUi();
            BindMeetData();
        }

        private void ConfigureModeUi()
        {
            if (_timingMode == TimingMode.OpenWater)
            {
                Title = "Open Water Swimming Meet & Data Manager";
                txtMeetHeaderSubtitle.Text = "OWS Event Management";
                btnAddNewOwsSwimmer.Visibility = Visibility.Visible;
                colLaneOrBib.Header = "BIB";
                colLaneOrBib.IsReadOnly = false;
                colLaneOrBib.Width = new DataGridLength(65);
            }
            else
            {
                Title = "Swimming Meet & Data Manager";
                txtMeetHeaderSubtitle.Text = "Pool Event Management";
                btnAddNewOwsSwimmer.Visibility = Visibility.Collapsed;
                colLaneOrBib.Header = "LN";
                colLaneOrBib.IsReadOnly = true;
                colLaneOrBib.Width = new DataGridLength(46);
            }
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

                txtSelectedHeatTitle.Text = $"{heat.DisplayTitle}";
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
                    txtSelectedHeatTitle.Text = $"{_selectedHeat.DisplayTitle}";
                    UpdateSelectedHeatSubtitle();
                    dgHeatLanes.ItemsSource = _selectedHeat.Lanes;
                    Meet.SelectedEvent = raceEvent;
                    Meet.SelectedHeat = _selectedHeat;
                }
                else
                {
                    btnClearHeat.IsEnabled = false;
                    txtSelectedHeatTitle.Text = $"{raceEvent.DisplayTitle} (No Heat yet)";
                    txtSelectedHeatSubtitle.Text = "Click the 'New Heat' button to add a new heat.";
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
                    txtSelectedHeatTitle.Text = "No events listed.";
                    txtSelectedHeatSubtitle.Text = "Click the 'New Event' button to create a new competition event.";
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
                if (_timingMode == TimingMode.OpenWater)
                {
                    txtSelectedHeatSubtitle.Text = $"{swimmerCount} Active athlete from {_selectedHeat.Lanes.Count} Registered participants";
                }
                else
                {
                    txtSelectedHeatSubtitle.Text = $"{swimmerCount} Active athletes from 10 tracks";
                }
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
            var newEvent = Meet.AddEvent(nextNum, $"New Event #{nextNum:D2}");
            if (_timingMode == TimingMode.OpenWater && newEvent.Heats.Count > 0)
            {
                newEvent.Heats[0].Lanes.Clear();
            }
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
                    MessageBox.Show("Please create an event first using the 'New Event' button.", "Informasi", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }

            var newHeat = _selectedEvent.AddHeat();
            if (_timingMode == TimingMode.OpenWater)
            {
                newHeat.Lanes.Clear();
            }
            SelectHeat(newHeat, _selectedEvent);
        }

        private void BtnAddNewOwsSwimmer_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedHeat == null)
            {
                MessageBox.Show("Please select Event and Heat first before adding participants.", 
                    "Select Heat", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int nextNum = _selectedHeat.Lanes.Count + 1;
            int suggestedBib = nextNum;
            var validBibs = _selectedHeat.Lanes
                .Select(l => int.TryParse(l.BibNumber, out int b) ? b : 0)
                .Where(b => b > 0)
                .ToList();
            if (validBibs.Count > 0)
            {
                suggestedBib = validBibs.Max() + 1;
            }

            var newLane = new LaneModel
            {
                LaneNumber = nextNum,
                BibNumber = suggestedBib.ToString(),
                SwimmerName = $"Swimmer {suggestedBib}",
                Status = LaneStatus.Ready
            };

            _selectedHeat.Lanes.Add(newLane);
            UpdateSelectedHeatSubtitle();
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
                            $"This heat is the only heat on {parentEvent.DisplayTitle}.\nDeleting it will delete the entire event.\n\nProceed to delete the event?",
                            "Confirm Deletion",
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
                            $"Delete {heat.DisplayTitle} from {parentEvent.DisplayTitle}?",
                            "Confirm Heat Deletion",
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
                    $"Delete {raceEvent.DisplayTitle} along with all the heats and athletes in them?",
                    "Confirm Event Deletion",
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
                MessageBox.Show("Please select an Event or Heat from the list on the left to delete.", "Pilih Item", MessageBoxButton.OK, MessageBoxImage.Information);
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
                    txtSelectedHeatTitle.Text = $"{_selectedHeat.DisplayTitle}";
                }
            }
        }

        private void TxtEventName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUi || _selectedEvent == null) return;

            _selectedEvent.EventName = txtEventName.Text.Trim();
            if (_selectedHeat != null)
            {
                txtSelectedHeatTitle.Text = $"{_selectedHeat.DisplayTitle}";
            }
        }

        private void BtnClearHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedHeat == null) return;

            string confirmMsg = _timingMode == TimingMode.OpenWater
                ? $"Remove all participants from {_selectedHeat.DisplayTitle}?"
                : $"Empty all athletes on {_selectedHeat.DisplayTitle}?";

            var result = MessageBox.Show(
                confirmMsg,
                "Konfirmasi",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (_timingMode == TimingMode.OpenWater)
                {
                    _selectedHeat.Lanes.Clear();
                }
                else
                {
                    _selectedHeat.ClearSwimmers();
                }
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
                if (_timingMode == TimingMode.OpenWater && _selectedHeat != null)
                {
                    _selectedHeat.Lanes.Remove(lane);
                    UpdateSelectedHeatSubtitle();
                    return;
                }

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
            string modeLabel = _timingMode == TimingMode.OpenWater ? "Open Water (OWS)" : "Pool Swimming";
            var dialog = new OpenFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                Title = _timingMode == TimingMode.OpenWater
                    ? "Import Open Water Swimming Start List"
                    : "Import Swimming Meet Start List"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string filePath = dialog.FileName;
            string fileName = System.IO.Path.GetFileName(filePath);

            btnImportExcel.IsEnabled = false;
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

            try
            {
                // --- Step 1: Detect the file's timing mode before doing a full import ---
                TimingMode? detectedMode = await System.Threading.Tasks.Task.Run(
                    () => _excelService.DetectTimingMode(filePath));

                if (detectedMode == null)
                {
                    // File has no recognizable header — unknown format
                    MessageBox.Show(
                        $"File \"{fileName}\" the format cannot be recognized.\n\n" +
                        $"Use the 'Download Template' button to get the correct template.",
                        "File format not recognized",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (detectedMode != _timingMode)
                {
                    // Format file tidak cocok dengan mode aktif
                    string fileModeName  = detectedMode == TimingMode.OpenWater ? "Open Water (OWS)" : "Pool Swimming";
                    string requiredCols  = _timingMode == TimingMode.OpenWater
                        ? "Event No, No Bib, Athlete"
                        : "Event No, Heat, Lane, Athlete";
                    string detectedCols  = detectedMode == TimingMode.OpenWater
                        ? "Event No, No Bib, Athlete"
                        : "Event No, Heat, Lane, Athlete";

                    MessageBox.Show(
                        $"File \"{fileName}\" cannot be imported.\n\n" +
                        $"Select the Excel file that corresponds to the mode. {modeLabel}, or change the timing mode on the main screen before importing this file.",
                        "File format does not match the mode",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // --- Step 2: Mode matched — proceed with full import ---
                var importedMeet = await System.Threading.Tasks.Task.Run(
                    () => _excelService.ImportMeetFromExcel(filePath, _timingMode));
                Meet = importedMeet;
                BindMeetData();

                int totalParticipants = Meet.Events
                    .SelectMany(ev => ev.Heats)
                    .SelectMany(h => h.Lanes)
                    .Count(l => !string.IsNullOrWhiteSpace(l.SwimmerName));
                string participantLabel = _timingMode == TimingMode.OpenWater ? "BIB Participants" : "Athlete";

                MessageBox.Show(
                    $"Start list imported successfully!",
                    "Import Successful",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (IsFileLockedException(ex))
                {
                    MessageBox.Show(
                        $"File '{fileName}' currently open in another program.\n\n" +
                        $"Close the file first, then try again.",
                        "The Excel file is currently open",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(
                        $"Failed to import Excel file:\n\n{ex.Message}",
                        "Import Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
                btnImportExcel.IsEnabled = true;
            }
        }


        private async void BtnDownloadTemplate_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = _timingMode == TimingMode.OpenWater 
                    ? "OWS_StartList_Template.xlsx" 
                    : "Swimming_StartList_Template.xlsx",
                Title = _timingMode == TimingMode.OpenWater
                    ? "Download Template Start List Open Water Swimming (BIB)"
                    : "Download Start List Template"
            };

            if (dialog.ShowDialog() == true)
            {
                btnDownloadTemplate.IsEnabled = false;
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    await System.Threading.Tasks.Task.Run(() => _excelService.GenerateSampleTemplate(dialog.FileName, _timingMode));
                    MessageBox.Show($"Excel template saved successfully to:\n{dialog.FileName}\n\nYou can fill in your meet data and import it anytime.", 
                        "Template Created", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    if (IsFileLockedException(ex))
                    {
                        MessageBox.Show(
                            $"File '{System.IO.Path.GetFileName(dialog.FileName)}' It is currently open in another program.\n\nPlease close the file in the other program first, or save it with a different filename.",
                            "The Excel file is currently open",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show($"Failed to create template:\n{ex.Message}", "Template Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    btnDownloadTemplate.IsEnabled = true;
                }
            }
        }

        private static bool IsFileLockedException(Exception ex)
        {
            if (ex is IOException ioEx)
            {
                int hr = ioEx.HResult & 0xFFFF;
                return hr == 32 || hr == 33 
                    || ioEx.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
                    || ioEx.Message.Contains("The Excel file is currently open", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private void BtnExportResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportDialog = new ExportSelectionDialog(Meet, _excelService, _timingMode)
                {
                    Owner = this
                };

                exportDialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open the result export dialog:\n\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private async void BtnSaveJson_Click(object sender, RoutedEventArgs e)
        {
            Meet.MeetName = txtMeetName.Text.Trim();

            var dialog = new SaveFileDialog
            {
                Filter = "Boston Timing Project (*.bts;*.json)|*.bts;*.json|JSON File (*.json)|*.json|All Files (*.*)|*.*",
                FileName = $"{Meet.MeetName.Replace(" ", "_")}_{DateTime.Now:yyyyMMdd}.bts",
                Title = "Save Competition Meet Project"
            };

            if (dialog.ShowDialog() == true)
            {
                btnSaveJson.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    await _persistenceService.SaveMeetToFileAsync(Meet, _timingMode, dialog.FileName);
                    MessageBox.Show(
                        $"Meet '{Meet.MeetName}' berhasil disimpan ke:\n{dialog.FileName}",
                        "Simpan Meet Berhasil",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Gagal menyimpan file meet:\n{ex.Message}",
                        "Simpan Meet Gagal",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    btnSaveJson.IsEnabled = true;
                }
            }
        }

        private async void BtnOpenJson_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Boston Timing Project (*.bts;*.json)|*.bts;*.json|JSON File (*.json)|*.json|All Files (*.*)|*.*",
                Title = "Open Competition Meet Project"
            };

            if (dialog.ShowDialog() == true)
            {
                btnOpenJson.IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;
                try
                {
                    var project = await _persistenceService.LoadMeetFromFileAsync(dialog.FileName);
                    if (project?.Meet != null)
                    {
                        Meet = project.Meet;
                        txtMeetName.Text = Meet.MeetName;
                        BindMeetData();
                        MessageBox.Show(
                            $"Meet '{Meet.MeetName}' berhasil dimuat ({Meet.Events.Count} Events).",
                            "Buka Meet Berhasil",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show(
                            "Format file tidak valid atau data meet kosong.",
                            "Gagal Memuat Meet",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Gagal membuka file meet:\n{ex.Message}",
                        "Error Buka Meet",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                finally
                {
                    Mouse.OverrideCursor = null;
                    btnOpenJson.IsEnabled = true;
                }
            }
        }

        private void dgHeatLanes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }
    }
}
