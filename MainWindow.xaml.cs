using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using boston_timing_system.Core;
using boston_timing_system.Models;
using boston_timing_system.Services;
using boston_timing_system.Views;

namespace boston_timing_system
{
    public partial class MainWindow : Window
    {
        private readonly RaceTimingEngine _engine;
        private readonly TimingWebSocketServer _wsServer;
        private readonly DispatcherTimer _displayTimer;
        private readonly DispatcherTimer _clockTimer;
        private readonly ExcelMeetDataService _excelService = new();
        private readonly Services.ThermalPrintService _thermalService = new();

        private CompetitionMeetModel _poolMeet = new();
        private CompetitionMeetModel _owsMeet = new();

        private CompetitionMeetModel _currentMeet
        {
            get => _engine.CurrentMode == TimingMode.Pool ? _poolMeet : _owsMeet;
            set
            {
                if (_engine.CurrentMode == TimingMode.Pool)
                {
                    _poolMeet = value;
                }
                else
                {
                    _owsMeet = value;
                }
            }
        }

        public System.Collections.ObjectModel.ObservableCollection<string> MessageLogs { get; } = new();

        public MainWindow()
        {
            InitializeComponent();

            // 1. Initialize timing engine with 10 lanes
            _engine = new RaceTimingEngine(defaultLaneCount: 10);
            icLanes.ItemsSource = _engine.Lanes;
            icOwsRecords.ItemsSource = _engine.OwsRecords;

            // Enable thread-safe collection synchronization for WPF UI
            BindingOperations.EnableCollectionSynchronization(_engine.Lanes, _engine.SyncRoot);
            BindingOperations.EnableCollectionSynchronization(_engine.OwsRecords, _engine.SyncRoot);

            _engine.OwsRecords.CollectionChanged += (s, e) => RunOnUi(UpdateOwsSummaryUi);
            _engine.ModeChanged += (m) => RunOnUi(() => ApplyTimingModeUi(m));
            _engine.OwsFinishRecorded += (rec) => RunOnUi(() =>
            {
                UpdateOwsSummaryUi();
                AddLogMessage($"[OWS FINISH] #{rec.Rank} {rec.FormattedTime} (Bib {rec.BibNumber} {rec.SwimmerName})");
            });

            // 2. Initialize and start WebSocket server
            _wsServer = new TimingWebSocketServer(_engine, port: 8181);
            _wsServer.LogReceived += HandleServerLogReceived;

            try
            {
                _wsServer.Start();
            }
            catch (Exception ex)
            {
                AddLogMessage($"Failed to start WebSocket server: {ex.Message}");
            }

            // Footer access info
            txtAccessIp.Text = _wsServer.LocalIpAddress;
            txtAccessCode.Text = _wsServer.AccessCode;
            txtAccessScoreboard.Text = $"{_wsServer.LocalIpAddress}:3000/scoreboard.html";

            AddLogMessage("Mode initialized: POOL SWIMMING");
            AddLogMessage("System ready. ClosedXML Meet Manager active.");
            AddLogMessage($"WebSocket Server listening on {_wsServer.ServerUri}");
            _wsServer.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TimingWebSocketServer.StartersCount) ||
                    e.PropertyName == nameof(TimingWebSocketServer.ChiefsCount) ||
                    e.PropertyName == nameof(TimingWebSocketServer.RefereesCount))
                {
                    RunOnUi(UpdateMobileConnectIndicators);
                }
            };
            UpdateMobileConnectIndicators();

            // 3. Display timer for UI rendering (~30 FPS)
            _displayTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _displayTimer.Tick += HandleDisplayTimerTick;

            // Live digital wall clock & date (1-second tick matching reference header)
            txtHeaderClock.Text = DateTime.Now.ToString("HH:mm:ss");
            txtCurrentDate.Text = DateTime.Now.ToString("dd MMM yyyy");
            txtCurrentDayOfWeek.Text = DateTime.Now.ToString("dddd");
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (s, e) =>
            {
                txtHeaderClock.Text = DateTime.Now.ToString("HH:mm:ss");
                txtCurrentDate.Text = DateTime.Now.ToString("dd MMM yyyy");
                txtCurrentDayOfWeek.Text = DateTime.Now.ToString("dddd");
            };
            _clockTimer.Start();

            // 4. Hook up engine events
            _engine.RaceStarted += OnRaceStarted;
            _engine.RaceStopped += OnRaceStopped;
            _engine.RaceReset += OnRaceReset;
            _engine.LaneFinished += OnLaneFinished;
            _engine.LaneStatusChanged += OnLaneStatusChanged;

            // 5. Initialize default meet structure
            InitializeDefaultMeet();

            UpdateUiState();
        }

        private void InitializeDefaultMeet()
        {
            // 1. Initialize clean Pool Swimming Meet (No dummy events)
            _poolMeet = new CompetitionMeetModel
            {
                MeetName = "Swimming Competition Meet"
            };

            // 2. Initialize clean Open Water Swimming (OWS) Meet (No dummy events)
            _owsMeet = new CompetitionMeetModel
            {
                MeetName = "Open Water Swimming Meet"
            };

            // 3. Clear engine lanes (clean empty slots ready for import/data entry)
            _engine.InitializeLanes(10);

            // 4. Bind initially active meet to UI
            txtMeetTitle.Text = _currentMeet.MeetName;
            _currentMeet.SelectedEvent = null;
            _currentMeet.SelectedHeat = null;

            UpdateEventDisplay();
            UpdateHeatDisplay();
            UpdateNavigationButtonStates();
            UpdateOwsSummaryUi();
        }

        private void BtnOpenMeetManager_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished && _currentMeet.SelectedHeat.HasResults)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            var meetManagerWin = new MeetManagerWindow(_currentMeet, _excelService, _engine.CurrentMode)
            {
                Owner = this
            };

            if (meetManagerWin.ShowDialog() == true)
            {
                _currentMeet = meetManagerWin.Meet;
                txtMeetTitle.Text = _currentMeet.MeetName;

                if (_currentMeet.Events.Count > 0)
                {
                    var targetEvent = _currentMeet.SelectedEvent ?? _currentMeet.Events[0];
                    _currentMeet.SelectedEvent = targetEvent;

                    var targetHeat = (_currentMeet.SelectedHeat != null && targetEvent.Heats.Contains(_currentMeet.SelectedHeat))
                        ? _currentMeet.SelectedHeat
                        : targetEvent.Heats.FirstOrDefault();

                    if (targetHeat != null)
                    {
                        LoadHeat(targetHeat);
                    }
                    else
                    {
                        _currentMeet.SelectedHeat = null;
                        UpdateEventDisplay();
                        UpdateHeatDisplay();
                        UpdateNavigationButtonStates();
                    }
                }
                else
                {
                    _currentMeet.SelectedEvent = null;
                    _currentMeet.SelectedHeat = null;
                    UpdateEventDisplay();
                    UpdateHeatDisplay();
                    UpdateNavigationButtonStates();
                }

                txtServerLog.Text = $"Meet '{_currentMeet.MeetName}' active ({_currentMeet.Events.Count} Events).";
            }
        }

        private void BtnPrevEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            if (_currentMeet.PreviousEvent())
            {
                if (_currentMeet.SelectedHeat != null)
                {
                    LoadHeat(_currentMeet.SelectedHeat);
                }
                else
                {
                    UpdateEventDisplay();
                    UpdateHeatDisplay();
                    UpdateNavigationButtonStates();
                }
            }
        }

        private void BtnNextEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            if (_currentMeet.NextEvent())
            {
                if (_currentMeet.SelectedHeat != null)
                {
                    LoadHeat(_currentMeet.SelectedHeat);
                }
                else
                {
                    UpdateEventDisplay();
                    UpdateHeatDisplay();
                    UpdateNavigationButtonStates();
                }
            }
        }

        private void UpdateEventDisplay()
        {
            if (_currentMeet.SelectedEvent != null)
            {
                txtCurrentEventDisplay.Text = $"{_currentMeet.SelectedEvent.EventNumber:D2}";
                txtCurrentEventName.Text = _currentMeet.SelectedEvent.EventName;
            }
            else
            {
                txtCurrentEventDisplay.Text = "No Event";
                txtCurrentEventName.Text = string.Empty;
            }
        }

        private void LoadHeat(HeatModel selectedHeat)
        {
            // Save previous heat results if finished
            if (_currentMeet.SelectedHeat != null && _currentMeet.SelectedHeat != selectedHeat && _engine.Status == RaceStatus.Finished)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            _currentMeet.SelectedHeat = selectedHeat;
            _engine.LoadHeat(selectedHeat);

            UpdateEventDisplay();
            UpdateHeatDisplay();

            // Check if loaded heat already has results
            if (_engine.Status == RaceStatus.Finished)
            {
                _displayTimer.Stop();
                txtMasterTimer.Text = _engine.FormattedElapsedTime;
                txtRaceStatus.Text = "STATUS: FINISHED";
                txtRaceStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A3412"));
                bdRaceStatus.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEDD5"));
                bdRaceStatus.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FED7AA"));
                AddLogMessage($"Loaded {selectedHeat.DisplayTitle} [FINISHED - Results Recorded]");
            }
            else
            {
                _displayTimer.Stop();
                txtMasterTimer.Text = "00.00.00";
                txtRaceStatus.Text = "STATUS: READY";
                txtRaceStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));
                bdRaceStatus.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"));
                bdRaceStatus.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                AddLogMessage($"Loaded {selectedHeat.DisplayTitle} [READY]");
            }

            UpdateUiState();

            // Notify all connected React Native devices with new event & heat context
            if (_currentMeet.SelectedEvent != null)
            {
                _wsServer.UpdateCurrentMeetContext(
                    _currentMeet.MeetName,
                    _currentMeet.SelectedEvent.EventNumber,
                    _currentMeet.SelectedEvent.EventName,
                    selectedHeat.HeatNumber);
            }

            UpdateNavigationButtonStates();
        }

        private void BtnPrevHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            if (_currentMeet.PreviousHeat())
            {
                if (_currentMeet.SelectedHeat != null)
                {
                    LoadHeat(_currentMeet.SelectedHeat);
                }
            }
        }

        private void BtnNextHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            if (_currentMeet.NextHeat())
            {
                if (_currentMeet.SelectedHeat != null)
                {
                    LoadHeat(_currentMeet.SelectedHeat);
                }
            }
        }

        private void UpdateHeatDisplay()
        {
            if (_currentMeet.SelectedHeat != null && _currentMeet.SelectedEvent != null)
            {
                int totalHeats = _currentMeet.SelectedEvent.Heats.Count;
                txtCurrentHeatDisplay.Text = totalHeats > 1
                    ? $"{_currentMeet.SelectedHeat.HeatNumber} / {totalHeats}"
                    : $"{_currentMeet.SelectedHeat.HeatNumber}";

                int swimmerCount = _currentMeet.SelectedHeat.Lanes.Count(
                    l => l.Status != LaneStatus.OFF && l.Status != LaneStatus.Empty && !string.IsNullOrWhiteSpace(l.SwimmerName));
                txtHeatSwimmerCount.Text = $"{swimmerCount} Swimmers on Board";
            }
            else
            {
                txtCurrentHeatDisplay.Text = "No Heat";
                txtHeatSwimmerCount.Text = "0 Swimmers on Board";
            }
        }

        private void UpdateNavigationButtonStates()
        {
            btnPrevEvent.IsEnabled = _currentMeet.CanGoPrevEvent && !_engine.IsRunning;
            btnNextEvent.IsEnabled = _currentMeet.CanGoNextEvent && !_engine.IsRunning;
            btnPrevHeat.IsEnabled = _currentMeet.CanGoPrevHeat && !_engine.IsRunning;
            btnNextHeat.IsEnabled = _currentMeet.CanGoNextHeat && !_engine.IsRunning;
        }

        private void HandleDisplayTimerTick(object? sender, EventArgs e)
        {
            txtMasterTimer.Text = _engine.FormattedElapsedTime;

            // Mirror running time to all currently running lanes
            string currentRunningTime = _engine.FormattedElapsedTime;
            foreach (var lane in _engine.Lanes)
            {
                if (lane.Status == LaneStatus.Running)
                {
                    lane.FormattedTime = currentRunningTime;
                }
            }
        }

        private void BtnStartRace_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.StartRace())
            {
                _displayTimer.Start();
                UpdateUiState();
                UpdateNavigationButtonStates();
            }
        }

        private void BtnStopRace_Click(object sender, RoutedEventArgs e)
        {
            _engine.StopRace();
            _displayTimer.Stop();
            txtMasterTimer.Text = _engine.FormattedElapsedTime;

            if (_currentMeet.SelectedHeat != null)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            UpdateUiState();
            UpdateNavigationButtonStates();
        }

        private void BtnResetRace_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && 
                (_currentMeet.SelectedHeat.IsCompleted || _currentMeet.SelectedHeat.Lanes.Any(MeetExportOptions.HasResult)))
            {
                var confirm = MessageBox.Show(
                    $"Heat {_currentMeet.SelectedHeat.HeatNumber} sudah memiliki hasil balapan resmi.\n\nApakah Anda yakin ingin mereset dan mengulang lomba untuk heat ini? Catatan waktu heat ini akan dihapus.",
                    "Konfirmasi Reset Heat Selesai",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }

                // Clear results for current heat
                _currentMeet.SelectedHeat.IsCompleted = false;
                foreach (var lane in _currentMeet.SelectedHeat.Lanes)
                {
                    lane.FinishTime = null;
                    lane.FormattedTime = "00.00.00";
                    lane.Rank = null;
                    if (string.IsNullOrWhiteSpace(lane.SwimmerName))
                    {
                        lane.Status = LaneStatus.OFF;
                    }
                    else if (lane.Status != LaneStatus.OFF && lane.Status != LaneStatus.Empty)
                    {
                        lane.Status = LaneStatus.Ready;
                    }
                }
            }

            _engine.ResetRace();
            _displayTimer.Stop();
            txtMasterTimer.Text = "00.00.00";

            // Reload current heat fresh
            if (_currentMeet.SelectedHeat != null)
            {
                _engine.LoadHeat(_currentMeet.SelectedHeat);
            }

            txtRaceStatus.Text = "STATUS: READY";
            txtRaceStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));
            bdRaceStatus.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"));
            bdRaceStatus.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));

            UpdateUiState();
            UpdateNavigationButtonStates();
            AddLogMessage("Race timer reset [READY]");
        }

        private void BorderResultTime_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: LaneModel lane })
            {
                if (_engine.IsRunning && lane.Status == LaneStatus.Running)
                {
                    _engine.StopLane(lane.LaneNumber);
                }
            }
        }


        private void BtnLaneStop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: LaneModel lane })
            {
                _engine.StopLane(lane.LaneNumber);
            }
        }

        private void BtnCopyUri_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_wsServer.ServerUri);
                txtServerLog.Text = $"Copied to clipboard: {_wsServer.ServerUri}";
            }
            catch
            {
                // Clipboard access might occasionally be locked by other apps
            }
        }

        private void HandleServerLogReceived(string message)
        {
            RunOnUi(() =>
            {
                AddLogMessage(message);
                UpdateMobileConnectIndicators();
            });
        }

        private void AddLogMessage(string message)
        {
            RunOnUi(() =>
            {
                string timestamp = DateTime.Now.ToString("HH.mm.ss");
                MessageLogs.Insert(0, $"{timestamp}  {message}");
                while (MessageLogs.Count > 100)
                {
                    MessageLogs.RemoveAt(MessageLogs.Count - 1);
                }
                if (txtServerLog != null)
                {
                    txtServerLog.Text = message;
                }
            });
        }

        private static readonly SolidColorBrush ActiveSignalBrush = CreateFrozenBrush("#16A34A");
        private static readonly SolidColorBrush IdleSignalBrush = CreateFrozenBrush("#94A3B8");

        private static SolidColorBrush CreateFrozenBrush(string colorHex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            brush.Freeze();
            return brush;
        }

        private void UpdateMobileConnectIndicators()
        {
            bool hasStarter = _wsServer.StartersCount > 0;
            barStarter1.Fill = hasStarter ? ActiveSignalBrush : IdleSignalBrush;
            barStarter2.Fill = hasStarter ? ActiveSignalBrush : IdleSignalBrush;
            barStarter3.Fill = hasStarter ? ActiveSignalBrush : IdleSignalBrush;
            barStarter4.Fill = hasStarter ? ActiveSignalBrush : IdleSignalBrush;

            if (_engine.IsOpenWaterMode)
            {
                txtMobileRole2.Text = "WASIT FINIS";
                bool hasReferee = _wsServer.IsOwsRefereeConnected || _wsServer.RefereesCount > 0;
                barChief1.Fill = hasReferee ? ActiveSignalBrush : IdleSignalBrush;
                barChief2.Fill = hasReferee ? ActiveSignalBrush : IdleSignalBrush;
                barChief3.Fill = hasReferee ? ActiveSignalBrush : IdleSignalBrush;
                barChief4.Fill = hasReferee ? ActiveSignalBrush : IdleSignalBrush;
            }
            else
            {
                txtMobileRole2.Text = "CHIEF";
                bool hasChief = _wsServer.ChiefsCount > 0;
                barChief1.Fill = hasChief ? ActiveSignalBrush : IdleSignalBrush;
                barChief2.Fill = hasChief ? ActiveSignalBrush : IdleSignalBrush;
                barChief3.Fill = hasChief ? ActiveSignalBrush : IdleSignalBrush;
                barChief4.Fill = hasChief ? ActiveSignalBrush : IdleSignalBrush;
            }
        }

        private void BtnToggleTimingMode_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsRunning)
            {
                MessageBox.Show(
                    "Balapan sedang berjalan! Hentikan atau reset balapan terlebih dahulu sebelum mengganti mode pencatatan waktu.",
                    "Peringatan Mode Balapan",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Save results of currently loaded heat before switching
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished && _currentMeet.SelectedHeat.HasResults)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            var targetMode = _engine.CurrentMode == TimingMode.Pool ? TimingMode.OpenWater : TimingMode.Pool;
            _engine.SetMode(targetMode);
            ApplyTimingModeUi(targetMode);

            // Switch to the separate meet data for the target mode
            txtMeetTitle.Text = _currentMeet.MeetName;

            if (_currentMeet.Events.Count > 0)
            {
                var targetEvent = _currentMeet.SelectedEvent ?? _currentMeet.Events[0];
                _currentMeet.SelectedEvent = targetEvent;

                var targetHeat = (_currentMeet.SelectedHeat != null && targetEvent.Heats.Contains(_currentMeet.SelectedHeat))
                    ? _currentMeet.SelectedHeat
                    : targetEvent.Heats.FirstOrDefault();

                if (targetHeat != null)
                {
                    LoadHeat(targetHeat);
                }
                else
                {
                    _currentMeet.SelectedHeat = null;
                    UpdateEventDisplay();
                    UpdateHeatDisplay();
                    UpdateNavigationButtonStates();
                }
            }
            else
            {
                _currentMeet.SelectedEvent = null;
                _currentMeet.SelectedHeat = null;
                UpdateEventDisplay();
                UpdateHeatDisplay();
                UpdateNavigationButtonStates();
            }

            if (_currentMeet.SelectedEvent != null && _currentMeet.SelectedHeat != null)
            {
                _wsServer.UpdateCurrentMeetContext(
                    _currentMeet.MeetName,
                    _currentMeet.SelectedEvent.EventNumber,
                    _currentMeet.SelectedEvent.EventName,
                    _currentMeet.SelectedHeat.HeatNumber);
            }
            else
            {
                _wsServer.UpdateCurrentMeetContext(
                    _currentMeet.MeetName,
                    0,
                    "No Event",
                    0);
            }

            AddLogMessage($"Switched to {targetMode} Meet: '{_currentMeet.MeetName}' ({_currentMeet.Events.Count} Events)");
        }

        private void ApplyTimingModeUi(TimingMode mode)
        {
            if (mode == TimingMode.Pool)
            {
                txtTimingModeText.Text = "POOL SWIMMING";
                txtTimingModeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
                btnToggleTimingMode.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
                btnToggleTimingMode.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));

                bdrPoolLanesView.Visibility = Visibility.Visible;
                bdrOwsView.Visibility = Visibility.Collapsed;

                AddLogMessage("Mode changed to POOL SWIMMING (10 Lanes)");
            }
            else
            {
                txtTimingModeText.Text = "OPEN WATER (OWS)";
                txtTimingModeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F766E"));
                btnToggleTimingMode.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0FDFA"));
                btnToggleTimingMode.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D9488"));

                bdrPoolLanesView.Visibility = Visibility.Collapsed;
                bdrOwsView.Visibility = Visibility.Visible;

                UpdateOwsSummaryUi();
                AddLogMessage("Mode changed to OPEN WATER SWIMMING (OWS - 1 Starter & 1 Wasit)");
            }

            UpdateMobileConnectIndicators();
        }

        private void BtnOwsManualTap_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.Status != RaceStatus.Running)
            {
                MessageBox.Show(
                    "Lomba belum dimulai! Tekan 'START' terlebih dahulu sebelum mencatat waktu finis perenang.",
                    "Info OWS",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var record = _engine.RecordOwsFinish();
            if (record != null)
            {
                UpdateOwsSummaryUi();
                AddLogMessage($"[DESKTOP OWS TAP] Rank #{record.Rank} ({record.FormattedTime}) dicatat.");
            }
        }

        private void BtnDeleteOwsRecord_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is OwsRecordModel record)
            {
                var confirm = MessageBox.Show(
                    $"Hapus catatan finis Rank #{record.Rank} ({record.FormattedTime}) [Bib: {record.BibNumber}]?",
                    "Konfirmasi Hapus Finisher OWS",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    _engine.RemoveOwsRecord(record);
                    UpdateOwsSummaryUi();
                    AddLogMessage($"[OWS DELETE] Rank #{record.Rank} dihapus.");
                }
            }
        }

        private void UpdateOwsSummaryUi()
        {
            RunOnUi(() =>
            {
                int count = _engine.OwsRecords.Count;
                pnlOwsEmptyState.Visibility = count > 0 ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        private void BtnPrintResults_Click(object sender, RoutedEventArgs e)
        {
            var heatToPrint = _currentMeet.SelectedHeat;
            if (heatToPrint == null)
            {
                heatToPrint = new HeatModel(1, 1, _currentMeet.SelectedEvent?.EventName ?? "Race Event");
                _engine.SaveResultsToHeat(heatToPrint);
            }
            else
            {
                _engine.SaveResultsToHeat(heatToPrint);
            }

            heatToPrint.CalculateRanks();

            try
            {
                var printWindow = new Views.ThermalPrintWindow(_currentMeet, heatToPrint, _thermalService)
                {
                    Owner = this
                };

                printWindow.ShowDialog();

                if (printWindow.PrintSuccessful)
                {
                    AddLogMessage($"Race results printed to 58mm thermal printer [Heat {heatToPrint.HeatNumber}]");
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"Thermal print error: {ex.Message}");
                MessageBox.Show($"Failed to open 58mm thermal print dialog: {ex.Message}", "Thermal Print Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRefreshAccessCode_Click(object sender, RoutedEventArgs e)
        {
            if (_wsServer != null)
            {
                string newCode = _wsServer.RegenerateAccessCode();
                txtAccessCode.Text = newCode;
                AddLogMessage($"Access code baru digenerate: {newCode}");
            }
        }

        private void BtnShowQrCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string ip = _wsServer?.LocalIpAddress ?? txtAccessIp.Text;
                int port = _wsServer?.Port ?? 8181;
                string code = _wsServer?.AccessCode ?? txtAccessCode?.Text ?? "1000";

                var qrWindow = new Views.QrCodeConnectionWindow(ip, port, code, webPort: 3000)
                {
                    Owner = this
                };
                qrWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                AddLogMessage($"QR Code error: {ex.Message}");
                MessageBox.Show($"Failed to display QR Code: {ex.Message}", "QR Code Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.BeginInvoke(action);
            }
        }

        private void OnRaceStarted()
        {
            RunOnUi(() =>
            {
                txtRaceStatus.Text = "STATUS: RUNNING";
                txtRaceStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534"));
                bdRaceStatus.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
                bdRaceStatus.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
                _displayTimer.Start();
                UpdateUiState();
                UpdateNavigationButtonStates();
                AddLogMessage("Race started");
            });
        }

        private void OnRaceStopped()
        {
            RunOnUi(() =>
            {
                _displayTimer.Stop();
                txtMasterTimer.Text = _engine.FormattedElapsedTime;
                txtRaceStatus.Text = "STATUS: FINISHED";
                txtRaceStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9A3412"));
                bdRaceStatus.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEDD5"));
                bdRaceStatus.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FED7AA"));

                if (_currentMeet.SelectedHeat != null)
                {
                    _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
                }

                UpdateUiState();
                UpdateNavigationButtonStates();
                AddLogMessage($"Race stopped. Final clock: {_engine.FormattedElapsedTime}");
            });
        }

        private void OnRaceReset()
        {
            RunOnUi(() =>
            {
                _displayTimer.Stop();
                txtMasterTimer.Text = "00.00.00";
                txtRaceStatus.Text = "STATUS: READY";
                txtRaceStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0369A1"));
                bdRaceStatus.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0F2FE"));
                bdRaceStatus.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BAE6FD"));
                UpdateOwsSummaryUi();
                UpdateUiState();
                UpdateNavigationButtonStates();
                AddLogMessage("Race timer reset [READY]");
            });
        }

        private void OnLaneFinished(LaneModel lane, TimeSpan finishTime)
        {
            RunOnUi(() =>
            {
                // If all finished, auto-save results to current heat
                if (_engine.Status == RaceStatus.Finished && _currentMeet.SelectedHeat != null)
                {
                    _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
                }
            });
        }

        private void OnLaneStatusChanged(LaneModel lane, LaneStatus status)
        {
            RunOnUi(() =>
            {
                if (_currentMeet.SelectedHeat != null)
                {
                    _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
                }
                AddLogMessage($"Lane {lane.LaneNumber} status set to {status}");
            });
        }

        private void UpdateUiState()
        {
            btnStartRace.IsEnabled = _engine.CanStart;
            btnStopRace.IsEnabled = _engine.CanStop;
            btnResetRace.IsEnabled = _engine.CanReset;
        }

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            _displayTimer.Stop();
            _wsServer.Dispose();
            _engine.Dispose();
            base.OnClosed(e);
        }
    }
}