using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        private CompetitionMeetModel _currentMeet = new();

        public System.Collections.ObjectModel.ObservableCollection<string> MessageLogs { get; } = new();

        public MainWindow()
        {
            InitializeComponent();

            // 1. Initialize timing engine with 10 lanes
            _engine = new RaceTimingEngine(defaultLaneCount: 10);
            icLanes.ItemsSource = _engine.Lanes;

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

            AddLogMessage("Mode changed to POOL");
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
                Interval = TimeSpan.FromMilliseconds(30)
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
            _currentMeet = new CompetitionMeetModel
            {
                MeetName = "Jakarta Open Aquatic Championship 2026"
            };

            // Event 1: 50m Freestyle Men (2 Heats)
            var event1 = new RaceEventModel(1, "50m Freestyle Men");
            var heat1_1 = new HeatModel(1, 1, event1.EventName);
            var heat1_2 = new HeatModel(2, 1, event1.EventName);

            // Populate sample swimmers for Heat 1 (Seed times empty by default)
            ConfigureSampleLane(heat1_1, 1, "Rizky Pratama", "Tirta Jaya Aquatic", "");
            ConfigureSampleLane(heat1_1, 2, "Budi Santoso", "Millennium Aquatic", "");
            ConfigureSampleLane(heat1_1, 3, "Ahmad Fauzi", "Jaq Aquatic Club", "");
            ConfigureSampleLane(heat1_1, 4, "Kevin Wijaya", "Tirta Kencana", "");
            ConfigureSampleLane(heat1_1, 5, "Dimas Anggara", "Surabaya Aquatic Club", "");
            ConfigureSampleLane(heat1_1, 6, "Fajar Nugraha", "Bandung Swimming Club", "");
            ConfigureSampleLane(heat1_1, 7, "Bayu Permana", "Garuda SC", "");
            ConfigureSampleLane(heat1_1, 8, "Rian Hidayat", "Nusantara AC", "");

            // Populate sample swimmers for Heat 2
            ConfigureSampleLane(heat1_2, 2, "Gede Arya", "Bali Aquatic Club", "");
            ConfigureSampleLane(heat1_2, 3, "Jonathan Tan", "Medan Swimming Club", "");
            ConfigureSampleLane(heat1_2, 4, "Michael Setiawan", "Millennium Aquatic", "");
            ConfigureSampleLane(heat1_2, 5, "Hendro Kusumo", "Jaq Aquatic Club", "");
            ConfigureSampleLane(heat1_2, 6, "Aldo Saputra", "Tirta Kencana", "");
            ConfigureSampleLane(heat1_2, 7, "Farhan Akbar", "Semarang SC", "");

            event1.Heats.Add(heat1_1);
            event1.Heats.Add(heat1_2);
            _currentMeet.Events.Add(event1);

            // Event 2: 100m Breaststroke Women (1 Heat)
            var event2 = new RaceEventModel(2, "100m Breaststroke Women");
            var heat2_1 = new HeatModel(1, 2, event2.EventName);
            ConfigureSampleLane(heat2_1, 2, "Siti Rahma", "Millennium Aquatic", "");
            ConfigureSampleLane(heat2_1, 3, "Nadia Utami", "Jaq Aquatic Club", "");
            ConfigureSampleLane(heat2_1, 4, "Clara Anastasia", "Tirta Kencana", "");
            ConfigureSampleLane(heat2_1, 5, "Putri Anggraini", "Bandung SC", "");
            ConfigureSampleLane(heat2_1, 6, "Aisyah Bella", "Surabaya AC", "");

            event2.Heats.Add(heat2_1);
            _currentMeet.Events.Add(event2);

            // Bind to UI
            txtMeetTitle.Text = _currentMeet.MeetName;
            if (_currentMeet.Events.Count > 0)
            {
                _currentMeet.SelectedEvent = _currentMeet.Events[0];
                if (_currentMeet.SelectedEvent.Heats.Count > 0)
                {
                    LoadHeat(_currentMeet.SelectedEvent.Heats[0]);
                }
            }
            UpdateEventDisplay();
            UpdateHeatDisplay();
            UpdateNavigationButtonStates();
        }

        private static void ConfigureSampleLane(HeatModel heat, int laneNum, string name, string club, string seedTime = "")
        {
            var lane = heat.Lanes.FirstOrDefault(l => l.LaneNumber == laneNum);
            if (lane != null)
            {
                lane.SwimmerName = name;
                lane.Club = club;
                lane.SeedTime = seedTime;
                lane.Status = LaneStatus.Ready;
            }
        }

        private void BtnOpenMeetManager_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && _engine.Status == RaceStatus.Finished && _currentMeet.SelectedHeat.HasResults)
            {
                _engine.SaveResultsToHeat(_currentMeet.SelectedHeat);
            }

            var meetManagerWin = new MeetManagerWindow(_currentMeet, _excelService)
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
                txtCurrentEventDisplay.Text = $"Event #{_currentMeet.SelectedEvent.EventNumber:D2}";
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
                    ? $"Heat {_currentMeet.SelectedHeat.HeatNumber} / {totalHeats}"
                    : $"Heat {_currentMeet.SelectedHeat.HeatNumber}";

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
                    if (lane.Status != LaneStatus.OFF && lane.Status != LaneStatus.Empty)
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
        }

        private void UpdateMobileConnectIndicators()
        {
            var activeBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A34A"));
            var idleBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));

            bool hasStarter = _wsServer.StartersCount > 0;
            barStarter1.Fill = hasStarter ? activeBrush : idleBrush;
            barStarter2.Fill = hasStarter ? activeBrush : idleBrush;
            barStarter3.Fill = hasStarter ? activeBrush : idleBrush;
            barStarter4.Fill = hasStarter ? activeBrush : idleBrush;

            bool hasChief = _wsServer.ChiefsCount > 0;
            barChief1.Fill = hasChief ? activeBrush : idleBrush;
            barChief2.Fill = hasChief ? activeBrush : idleBrush;
            barChief3.Fill = hasChief ? activeBrush : idleBrush;
            barChief4.Fill = hasChief ? activeBrush : idleBrush;
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