using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private readonly Services.ScoreboardWebServer _scoreboardServer;
        private readonly MeetPersistenceService _persistenceService = new();
        private bool _isLoadingHeat;
        private bool _isHandlingOwsBibFocus;

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

            _persistenceService.AutoSaveStatusChanged += (msg) => RunOnUi(() => UpdateAutoSaveStatusUi(msg));

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
                TriggerAutoSave(immediate: false);
            });
            _engine.OwsRecordStatusChanged += (rec, status) => RunOnUi(() =>
            {
                UpdateOwsSummaryUi();
                AddLogMessage($"[OWS STATUS] BIB {rec.BibNumber} ({rec.SwimmerName}) status changed to {status}.");
                TriggerAutoSave(immediate: false);
            });

            // 2. Initialize WebSocket server with port fallback (tries 8181 → 8182 → 8183 → 8080)
            _wsServer = new TimingWebSocketServer(_engine, candidatePorts: new[] { 8181, 8182, 8183, 8080 });
            _wsServer.LogReceived += HandleServerLogReceived;

            try
            {
                _wsServer.Start();
            }
            catch (Exception ex)
            {
                AddLogMessage($"[CRITICAL] WebSocket server failed on all ports: {ex.Message}");
                MessageBox.Show(
                    $"WebSocket server could not be started on any port (8181, 8182, 8183, 8080).\n\n" +
                    $"Possible causes:\n" +
                    $"  • Ports are being used by another application\n" +
                    $"  • Firewall is blocking all candidate ports\n\n" +
                    $"Error details: {ex.Message}\n\n" +
                    $"Mobile devices will not be able to connect. " +
                    $"Please close any applications using these ports and restart this application.",
                    "WebSocket Server Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            // Footer access info — update after Start() so Port reflects the actual bound port
            txtAccessIp.Text = _wsServer.LocalIpAddress;
            txtAccessCode.Text = _wsServer.AccessCode;

            // 2b. Initialize embedded Scoreboard Web Server (default port 8088 with fallbacks)
            _scoreboardServer = new Services.ScoreboardWebServer(
                getWsPort: () => _wsServer.Port,
                getTimingMode: () => _engine.CurrentMode.ToString(),
                candidatePorts: new[] { 8088, 8090, 8080, 5000, 3000 });
            _scoreboardServer.LogReceived += HandleServerLogReceived;
            _scoreboardServer.Start();

            txtAccessScoreboard.Text = _scoreboardServer.IsRunning
                ? $"{_scoreboardServer.LocalIpAddress}:{_scoreboardServer.Port}/scoreboard.html"
                : $"{_wsServer.LocalIpAddress}:8080/scoreboard.html";

            AddLogMessage("Mode initialized: POOL SWIMMING");
            AddLogMessage("System ready. ClosedXML Meet Manager active.");
            AddLogMessage($"WebSocket Server listening on {_wsServer.ServerUri}");
            if (_scoreboardServer.IsRunning)
            {
                AddLogMessage($"Live Scoreboard Web Display active at {_scoreboardServer.ScoreboardUrl}");
            }
            _wsServer.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(TimingWebSocketServer.StartersCount) ||
                    e.PropertyName == nameof(TimingWebSocketServer.ChiefsCount) ||
                    e.PropertyName == nameof(TimingWebSocketServer.RefereesCount))
                {
                    RunOnUi(UpdateMobileConnectIndicators);
                }
            };
            // Refresh latency labels every time a PING measurement arrives from a mobile device
            _wsServer.LatencyUpdated += () => RunOnUi(UpdateMobileConnectIndicators);
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

            ApplyTimingModeUi(_engine.CurrentMode);
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
            if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
            }

            var originalEvent = _currentMeet.SelectedEvent;
            var originalHeat = _currentMeet.SelectedHeat;

            // Create an isolated deep clone snapshot so that changes in Meet Manager can be fully cancelled
            var meetSnapshot = _persistenceService.CloneMeet(_currentMeet);

            var meetManagerWin = new MeetManagerWindow(meetSnapshot, _excelService, _engine.CurrentMode, _persistenceService)
            {
                Owner = this
            };

            if (meetManagerWin.ShowDialog() == true)
            {
                _engine.ResetRace();
                _displayTimer.Stop();
                txtMasterTimer.Text = "00.00.00";

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
                        icLanes.Items.Refresh();
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

                if (_engine.CurrentMode == TimingMode.OpenWater)
                {
                    UpdateOwsSummaryUi();
                }

                txtServerLog.Text = $"Meet '{_currentMeet.MeetName}' active ({_currentMeet.Events.Count} Events).";
                AddLogMessage($"Meet '{_currentMeet.MeetName}' loaded ({_currentMeet.Events.Count} Events).");
                TriggerAutoSave(immediate: true);
            }
            else
            {
                _currentMeet.SelectedEvent = originalEvent;
                _currentMeet.SelectedHeat = originalHeat;
            }
        }

        private void BtnPrevEvent_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
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
            if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
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
            // Save previous heat results if finished or running
            if (_engine.CurrentHeat != null && _engine.CurrentHeat != selectedHeat && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
                TriggerAutoSave(immediate: false);
            }

            _isLoadingHeat = true;
            try
            {
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
            finally
            {
                _isLoadingHeat = false;
            }
        }

        private void BtnPrevHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
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
            if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
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
            if (_engine.IsOpenWaterMode && _engine.IsRunning)
            {
                var confirm = MessageBox.Show(
                    "Are you sure you want to finish and end the Open Water Swimming race?",
                    "Confirm Finish Race",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            _engine.StopRace();
            _displayTimer.Stop();
            txtMasterTimer.Text = _engine.FormattedElapsedTime;

            if (_engine.CurrentHeat != null)
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
            }

            UpdateUiState();
            UpdateNavigationButtonStates();
            TriggerAutoSave(immediate: true);
        }

        private void BtnResetRace_Click(object sender, RoutedEventArgs e)
        {
            if (_currentMeet.SelectedHeat != null && 
                (_currentMeet.SelectedHeat.IsCompleted || _currentMeet.SelectedHeat.Lanes.Any(MeetExportOptions.HasResult)))
            {
                var confirm = MessageBox.Show(
                    $"Heat {_currentMeet.SelectedHeat.HeatNumber} already has official race results.\n\nAre you sure you want to reset and restart this heat? The recorded times for this heat will be cleared.",
                    "Confirm Reset Completed Heat",
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
            TriggerAutoSave(immediate: false);
            AddLogMessage("Race timer reset [READY]");
        }

        private void BorderResultTime_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: LaneModel lane })
            {
                if (_engine.IsRunning && lane.Status == LaneStatus.Running)
                {
                    _engine.StopLane(lane.LaneNumber);
                    if (_engine.CurrentHeat != null)
                    {
                        _engine.SaveResultsToHeat(_engine.CurrentHeat);
                    }
                    TriggerAutoSave(immediate: false);
                }
            }
        }


        private void BtnLaneStop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: LaneModel lane })
            {
                _engine.StopLane(lane.LaneNumber);
                if (_engine.CurrentHeat != null)
                {
                    _engine.SaveResultsToHeat(_engine.CurrentHeat);
                }
                TriggerAutoSave(immediate: false);
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

        private void BtnOpenScoreboardWeb_Click(object sender, RoutedEventArgs e)
        {
            OpenScoreboardInBrowser();
        }

        private void TxtAccessScoreboard_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            OpenScoreboardInBrowser();
        }

        private void OpenScoreboardInBrowser()
        {
            try
            {
                string url = _scoreboardServer?.ScoreboardUrl ?? $"http://{_wsServer.LocalIpAddress}:8088/scoreboard.html";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                AddLogMessage($"Opened Scoreboard in browser: {url}");
            }
            catch (Exception ex)
            {
                AddLogMessage($"Failed to launch browser: {ex.Message}");
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

        private static readonly SolidColorBrush SignalGreenBrush = CreateFrozenBrush("#16A34A");
        private static readonly SolidColorBrush SignalAmberBrush = CreateFrozenBrush("#D97706");
        private static readonly SolidColorBrush SignalRedBrush   = CreateFrozenBrush("#DC2626");
        private static readonly SolidColorBrush SignalIdleBrush  = CreateFrozenBrush("#94A3B8");

        private static SolidColorBrush CreateFrozenBrush(string colorHex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            brush.Freeze();
            return brush;
        }

        private void UpdateSignalBars(
            System.Windows.Shapes.Rectangle b1,
            System.Windows.Shapes.Rectangle b2,
            System.Windows.Shapes.Rectangle b3,
            System.Windows.Shapes.Rectangle b4,
            bool isConnected,
            double latencyMs)
        {
            if (!isConnected)
            {
                b1.Fill = SignalIdleBrush;
                b2.Fill = SignalIdleBrush;
                b3.Fill = SignalIdleBrush;
                b4.Fill = SignalIdleBrush;
                return;
            }

            int bars;
            SolidColorBrush activeBrush;

            if (latencyMs <= 0 || latencyMs < 50.0)
            {
                bars = 4;
                activeBrush = SignalGreenBrush;
            }
            else if (latencyMs < 100.0)
            {
                bars = 3;
                activeBrush = SignalGreenBrush;
            }
            else if (latencyMs <= 150.0)
            {
                bars = 2;
                activeBrush = SignalAmberBrush;
            }
            else
            {
                bars = 1;
                activeBrush = SignalRedBrush;
            }

            b1.Fill = bars >= 1 ? activeBrush : SignalIdleBrush;
            b2.Fill = bars >= 2 ? activeBrush : SignalIdleBrush;
            b3.Fill = bars >= 3 ? activeBrush : SignalIdleBrush;
            b4.Fill = bars >= 4 ? activeBrush : SignalIdleBrush;
        }

        private void UpdateMobileConnectIndicators()
        {
            bool hasStarter = _wsServer.StartersCount > 0;
            UpdateSignalBars(barStarter1, barStarter2, barStarter3, barStarter4, hasStarter, _wsServer.StarterLatencyMs);
            UpdateFooterLatencyLabel(txtStarterLatency, hasStarter, _wsServer.StarterLatencyMs);

            if (_engine.IsOpenWaterMode)
            {
                txtMobileRole2.Text = "OWS REFEREE";
                bool hasReferee = _wsServer.IsOwsRefereeConnected || _wsServer.RefereesCount > 0;
                double latency = _wsServer.OwsRefereeLatencyMs > 0 ? _wsServer.OwsRefereeLatencyMs : _wsServer.ChiefLatencyMs;
                UpdateSignalBars(barChief1, barChief2, barChief3, barChief4, hasReferee, latency);
                UpdateFooterLatencyLabel(txtChiefLatency, hasReferee, latency);
            }
            else
            {
                txtMobileRole2.Text = "CHIEF";
                bool hasChief = _wsServer.ChiefsCount > 0;
                UpdateSignalBars(barChief1, barChief2, barChief3, barChief4, hasChief, _wsServer.ChiefLatencyMs);
                UpdateFooterLatencyLabel(txtChiefLatency, hasChief, _wsServer.ChiefLatencyMs);
            }
        }

        /// <summary>
        /// Sets the footer latency TextBlock text and foreground color.
        /// Shows "--" in grey when not connected or no latency data.
        /// Shows "X.Xms" color-coded: green &lt;50ms, amber 50–150ms, red &gt;150ms.
        /// </summary>
        private void UpdateFooterLatencyLabel(System.Windows.Controls.TextBlock label, bool isConnected, double latencyMs)
        {
            if (label == null) return;

            if (!isConnected || latencyMs <= 0)
            {
                label.Text = "--";
                label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
                return;
            }

            label.Text = $"{latencyMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}ms";
            string colorHex = latencyMs < 50.0  ? "#16A34A"   // green  — good
                            : latencyMs <= 150.0 ? "#D97706"   // amber  — moderate
                            :                      "#DC2626";  // red    — high
            label.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }

        private void BtnToggleTimingMode_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsRunning)
            {
                MessageBox.Show(
                    "Race is currently running! Please stop or reset the race before switching timing modes.",
                    "Race Mode Warning",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Save results of currently loaded heat before switching
            if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
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
            _wsServer.Broadcast(_wsServer.CreateStateSyncEvent());
            TriggerAutoSave(immediate: true);
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

                btnStopRace.Content = "STOP ALL";
                btnStopRace.ToolTip = "Stop all currently running lanes simultaneously";

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

                btnStopRace.Content = "FINISH RACE";
                btnStopRace.ToolTip = "End open water swimming race and stop master timer";

                UpdateOwsSummaryUi();
                AddLogMessage("Mode changed to OPEN WATER SWIMMING (OWS - 1 Starter & 1 Referee)");
            }

            UpdateMobileConnectIndicators();
        }

        private void BtnOwsManualTap_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.Status != RaceStatus.Running)
            {
                MessageBox.Show(
                    "Race has not started! Click 'START' first before recording a swimmer's finish time.",
                    "OWS Info",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var record = _engine.RecordOwsFinish();
            if (record != null)
            {
                UpdateOwsSummaryUi();
                AddLogMessage($"[DESKTOP OWS TAP] Rank #{record.Rank} ({record.FormattedTime}) recorded.");
            }
        }

        private void BtnDeleteOwsRecord_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is OwsRecordModel record)
            {
                var confirm = MessageBox.Show(
                    $"Delete finish record Rank #{record.Rank} ({record.FormattedTime}) [Bib: {record.BibNumber}]?",
                    "Confirm Delete OWS Finisher",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    _engine.RemoveOwsRecord(record);
                    UpdateOwsSummaryUi();
                    _wsServer.Broadcast(_wsServer.CreateStateSyncEvent());
                    AddLogMessage($"[OWS DELETE] Rank #{record.Rank} deleted.");
                }
            }
        }

        private void OwsBibNumber_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb && tb.Tag is OwsRecordModel record)
            {
                e.Handled = true;
                bool isValid = ProcessOwsBibInput(tb, record);
                if (isValid)
                {
                    tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
            }
        }

        /// <summary>
        /// Fired when the operator leaves the BIB TextBox in the OWS finisher table.
        /// Looks up the BIB in the registered participant list (current heat) and auto-fills
        /// SwimmerName + Club on the OwsRecordModel if a match is found, and marks the participant as Finished.
        /// Does NOT create new participants — only updates the existing OWS record and heat lane.
        /// </summary>
        private void OwsBibNumber_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.Tag is OwsRecordModel record)
            {
                ProcessOwsBibInput(tb, record);
            }
        }

        private bool ProcessOwsBibInput(TextBox tb, OwsRecordModel record)
        {
            if (_isHandlingOwsBibFocus)
            {
                return false;
            }

            try
            {
                _isHandlingOwsBibFocus = true;

                string enteredBib = record.BibNumber?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(enteredBib))
                {
                    record.SwimmerName = string.Empty;
                    record.Club = string.Empty;
                    record.Status = LaneStatus.Finished;

                    // Revert any lane previously associated with this rank
                    if (_engine.CurrentHeat != null)
                    {
                        var prevLane = _engine.CurrentHeat.Lanes.FirstOrDefault(l => l.Rank == record.Rank);
                        if (prevLane != null)
                        {
                            prevLane.Status = _engine.Status == RaceStatus.Running ? LaneStatus.Running : LaneStatus.Ready;
                            prevLane.FinishTime = null;
                            prevLane.FormattedTime = "00.00.00";
                            prevLane.Rank = null;

                            var prevEngineLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == prevLane.LaneNumber);
                            if (prevEngineLane != null)
                            {
                                prevEngineLane.Status = prevLane.Status;
                                prevEngineLane.FinishTime = null;
                                prevEngineLane.FormattedTime = "00.00.00";
                                prevEngineLane.Rank = null;
                            }
                        }
                    }
                    _wsServer.Broadcast(_wsServer.CreateStateSyncEvent());
                    return true;
                }

                // Check for duplicate BIB in other records (same finisher tapped twice)
                bool isDuplicate = _engine.OwsRecords
                    .Any(r => r != record && 
                         string.Equals(r.BibNumber, enteredBib, StringComparison.OrdinalIgnoreCase));

                if (isDuplicate)
                {
                    MessageBox.Show(
                        $"BIB \"{enteredBib}\" is already recorded for another finisher.\n\nEach BIB can only appear once. Please check and try again.",
                        "Duplicate BIB",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    record.BibNumber = string.Empty;
                    record.SwimmerName = string.Empty;
                    record.Club = string.Empty;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        tb.Focus();
                        tb.SelectAll();
                    }));
                    return false;
                }

                // Lookup participant from heat registration data
                var participant = _engine.LookupParticipantByBib(enteredBib);

                if (participant == null)
                {
                    record.BibNumber = string.Empty;
                    record.SwimmerName = string.Empty;
                    record.Club = string.Empty;
                    AddLogMessage($"[OWS BIB] #{record.Rank} BIB {enteredBib} not found in this heat's participant list.");

                    string heatInfo = _engine.CurrentHeat != null
                        ? $"Event {_engine.CurrentHeat.EventNumber} (Heat {_engine.CurrentHeat.HeatNumber})"
                        : "the current heat";

                    MessageBox.Show(
                        $"BIB number \"{enteredBib}\" is not registered in {heatInfo}.\n\nPlease check the participant list or the entered BIB number.",
                        "BIB Not Registered",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        tb.Focus();
                        tb.SelectAll();
                    }));
                    return false;
                }

                // If this finisher rank previously matched a different lane, revert the old lane
                if (_engine.CurrentHeat != null)
                {
                    var prevLane = _engine.CurrentHeat.Lanes.FirstOrDefault(l =>
                        l.Rank == record.Rank &&
                        (l.HasExplicitBibNumber ? !l.BibNumber.Equals(enteredBib, StringComparison.OrdinalIgnoreCase) : l.LaneNumber.ToString() != enteredBib));
                    if (prevLane != null)
                    {
                        prevLane.Status = _engine.Status == RaceStatus.Running ? LaneStatus.Running : LaneStatus.Ready;
                        prevLane.FinishTime = null;
                        prevLane.FormattedTime = "00.00.00";
                        prevLane.Rank = null;

                        var prevEngineLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == prevLane.LaneNumber);
                        if (prevEngineLane != null)
                        {
                            prevEngineLane.Status = prevLane.Status;
                            prevEngineLane.FinishTime = null;
                            prevEngineLane.FormattedTime = "00.00.00";
                            prevEngineLane.Rank = null;
                        }
                    }
                }

                // Update participant status and timing details in the heat
                participant.Status = LaneStatus.Finished;
                participant.FinishTime = record.FinishTime;
                participant.FormattedTime = record.FormattedTime;
                participant.Rank = record.Rank;

                // Sync with engine lane if exists
                var engineLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == participant.LaneNumber);
                if (engineLane != null)
                {
                    engineLane.Status = LaneStatus.Finished;
                    engineLane.FinishTime = record.FinishTime;
                    engineLane.FormattedTime = record.FormattedTime;
                    engineLane.Rank = record.Rank;
                }

                // Automatically display name and club from the matched registered participant
                record.SwimmerName = participant.SwimmerName ?? string.Empty;
                record.Club = participant.Club ?? string.Empty;

                AddLogMessage($"[OWS BIB] #{record.Rank} BIB {enteredBib} → {record.SwimmerName} ({record.Club}) [Finished]");

                // Broadcast updated state to mobile devices
                _wsServer.Broadcast(_wsServer.CreateStateSyncEvent());
                return true;
            }
            finally
            {
                _isHandlingOwsBibFocus = false;
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
            var currentMode = _engine.CurrentMode;
            var heatToPrint = _currentMeet.SelectedHeat ?? _engine.CurrentHeat;
            if (heatToPrint == null)
            {
                heatToPrint = new HeatModel(1, 1, _currentMeet.SelectedEvent?.EventName ?? (currentMode == TimingMode.OpenWater ? "OWS Race" : "Race Event"));
                _engine.SaveResultsToHeat(heatToPrint);
            }
            else if (_engine.CurrentHeat != null && ReferenceEquals(heatToPrint, _engine.CurrentHeat))
            {
                _engine.SaveResultsToHeat(_engine.CurrentHeat);
            }

            if (currentMode == TimingMode.OpenWater && _engine.OwsRecords.Count > 0)
            {
                foreach (var record in _engine.OwsRecords)
                {
                    var targetLane = heatToPrint.Lanes.FirstOrDefault(l =>
                        l.HasExplicitBibNumber
                            ? l.BibNumber.Equals(record.BibNumber, StringComparison.OrdinalIgnoreCase)
                            : (!string.IsNullOrWhiteSpace(record.BibNumber) && l.LaneNumber.ToString() == record.BibNumber));

                    if (targetLane == null)
                    {
                        int laneNum = int.TryParse(record.BibNumber, out int parsedNum) ? parsedNum : (heatToPrint.Lanes.Count + 1);
                        targetLane = new LaneModel
                        {
                            LaneNumber = laneNum,
                            BibNumber = record.BibNumber,
                            SwimmerName = record.SwimmerName,
                            Club = record.Club,
                            FinishTime = record.FinishTime,
                            FormattedTime = record.FormattedTime,
                            Rank = record.Rank > 0 ? record.Rank : null,
                            Status = record.Status
                        };
                        heatToPrint.Lanes.Add(targetLane);
                    }
                    else
                    {
                        targetLane.FinishTime = record.FinishTime;
                        targetLane.FormattedTime = record.FormattedTime;
                        targetLane.Rank = record.Rank > 0 ? record.Rank : null;
                        targetLane.Status = record.Status;
                        if (!string.IsNullOrWhiteSpace(record.SwimmerName)) targetLane.SwimmerName = record.SwimmerName;
                        if (!string.IsNullOrWhiteSpace(record.Club)) targetLane.Club = record.Club;
                    }
                }
            }

            heatToPrint.CalculateRanks();

            try
            {
                var printWindow = new Views.ThermalPrintWindow(_currentMeet, heatToPrint, _thermalService, currentMode)
                {
                    Owner = this
                };

                printWindow.ShowDialog();

                if (printWindow.PrintSuccessful)
                {
                    string modeDesc = currentMode == TimingMode.OpenWater ? "OWS" : $"Heat {heatToPrint.HeatNumber}";
                    AddLogMessage($"Race results printed to 58mm thermal printer [{modeDesc}]");
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
                AddLogMessage($"New access code generated: {newCode}");
            }
        }

        private void BtnShowQrCode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string ip = _wsServer?.LocalIpAddress ?? txtAccessIp.Text;
                int port = _wsServer?.Port ?? 8181;
                string code = _wsServer?.AccessCode ?? txtAccessCode?.Text ?? "1000";

                int webPort = _scoreboardServer?.Port ?? 8080;
                var qrWindow = new Views.QrCodeConnectionWindow(ip, port, code, webPort: webPort, timingMode: _engine.CurrentMode)
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

                if (_engine.CurrentHeat != null)
                {
                    _engine.SaveResultsToHeat(_engine.CurrentHeat);
                }

                UpdateUiState();
                UpdateNavigationButtonStates();
                TriggerAutoSave(immediate: true);
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
            if (_isLoadingHeat || _engine.IsLoadingHeat) return;

            RunOnUi(() =>
            {
                if (_isLoadingHeat || _engine.IsLoadingHeat) return;

                if (_engine.CurrentHeat != null)
                {
                    _engine.SaveResultsToHeat(_engine.CurrentHeat);
                }
                TriggerAutoSave(immediate: false);
            });
        }

        private void OnLaneStatusChanged(LaneModel lane, LaneStatus status)
        {
            if (_isLoadingHeat || _engine.IsLoadingHeat) return;

            RunOnUi(() =>
            {
                if (_isLoadingHeat || _engine.IsLoadingHeat) return;

                if (_engine.CurrentHeat != null)
                {
                    _engine.SaveResultsToHeat(_engine.CurrentHeat);
                }
                TriggerAutoSave(immediate: false);
                AddLogMessage($"Lane {lane.LaneNumber} status set to {status}");
            });
        }

        private void UpdateUiState()
        {
            btnStartRace.IsEnabled = _engine.CanStart;
            btnStopRace.IsEnabled = _engine.CanStop;
            btnResetRace.IsEnabled = _engine.CanReset;
        }

        #region Session Persistence & Auto-Save Lifecycle

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_persistenceService.HasAutoSaveSession())
            {
                var timestamp = _persistenceService.GetAutoSaveTimestamp();
                string timeStr = timestamp.HasValue ? timestamp.Value.ToString("dd MMM yyyy HH:mm:ss") : "previous";
                bool isCrash = _persistenceService.IsCrashDetected();

                string dialogTitle;
                string dialogMessage;

                if (isCrash)
                {
                    dialogTitle = "Race Session Recovery (Crash Recovery)";
                    dialogMessage = $"An unexpected shutdown was detected. A previous race session was auto-saved on:\n{timeStr}\n\n" +
                                    "Do you want to recover that session?\n\n" +
                                    "• Select [Yes] to recover your previous race session\n" +
                                    "• Select [No] to discard it and start a new meet";
                }
                else
                {
                    dialogTitle = "Continue Previous Session";
                    dialogMessage = $"A previously saved race session is available from:\n{timeStr}\n\n" +
                                    "Do you want to continue that session?\n\n" +
                                    "• Select [Yes] to load and continue the previous race session\n" +
                                    "• Select [No] to start a clean new meet session";
                }

                var result = MessageBox.Show(
                    dialogMessage,
                    dialogTitle,
                    MessageBoxButton.YesNo,
                    isCrash ? MessageBoxImage.Warning : MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    await RestoreSavedSessionAsync();
                }
                else
                {
                    _persistenceService.ClearAutoSaveSession();
                    _persistenceService.MarkSessionCleanExit();
                    AddLogMessage("Previous session discarded. Starting a new meet.");
                }
            }

            // Mark session as active so that if the app terminates abnormally, it will be detected as a crash
            _persistenceService.MarkSessionActive();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            try
            {
                if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
                {
                    _engine.SaveResultsToHeat(_engine.CurrentHeat);
                }
                TriggerAutoSave(immediate: true);

                // Normal close by user: remove lock marker so next launch is treated as clean exit
                _persistenceService.MarkSessionCleanExit();
            }
            catch
            {
                // Best effort on shutdown
            }
        }

        private void TriggerAutoSave(bool immediate = false)
        {
            if (_isLoadingHeat || _engine.IsLoadingHeat) return;

            try
            {
                if (_engine.CurrentHeat != null && (_engine.Status == RaceStatus.Finished || _engine.IsRunning))
                {
                    _engine.SaveResultsToHeat(_engine.CurrentHeat);
                }

                var dto = new MeetSessionDto
                {
                    ActiveTimingMode = _engine.CurrentMode,
                    PoolMeet = _poolMeet,
                    OwsMeet = _owsMeet,
                    ActiveOwsRecords = _engine.OwsRecords.ToList()
                };

                if (immediate)
                {
                    _persistenceService.SaveSessionImmediate(dto);
                }
                else
                {
                    _persistenceService.RequestDebouncedAutoSave(dto, delayMs: 600);
                }
            }
            catch (Exception ex)
            {
                AddLogMessage($"[AUTOSAVE ERROR] {ex.Message}");
            }
        }

        private void UpdateAutoSaveStatusUi(string message)
        {
            if (txtAutoSaveStatus != null)
            {
                txtAutoSaveStatus.Text = message;
            }
        }

        private async Task RestoreSavedSessionAsync()
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                var session = await _persistenceService.LoadAutoSaveSessionAsync();
                if (session == null)
                {
                    MessageBox.Show("Failed to read auto-save session file.", "Recovery Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _poolMeet = session.PoolMeet ?? new CompetitionMeetModel { MeetName = "Swimming Competition Meet" };
                _owsMeet = session.OwsMeet ?? new CompetitionMeetModel { MeetName = "Open Water Swimming Meet" };

                if (session.ActiveTimingMode != _engine.CurrentMode)
                {
                    _engine.SetMode(session.ActiveTimingMode);
                    ApplyTimingModeUi(session.ActiveTimingMode);
                }

                if (session.ActiveTimingMode == TimingMode.OpenWater && session.ActiveOwsRecords != null)
                {
                    _engine.OwsRecords.Clear();
                    foreach (var rec in session.ActiveOwsRecords)
                    {
                        _engine.OwsRecords.Add(rec);
                    }
                    UpdateOwsSummaryUi();
                }

                txtMeetTitle.Text = _currentMeet.MeetName;

                if (_currentMeet.Events.Count > 0)
                {
                    RaceEventModel? targetEvent = null;
                    if (_currentMeet.ActiveEventNumber.HasValue)
                    {
                        targetEvent = _currentMeet.Events.FirstOrDefault(ev => ev.EventNumber == _currentMeet.ActiveEventNumber.Value);
                    }
                    targetEvent ??= _currentMeet.Events[0];
                    _currentMeet.SelectedEvent = targetEvent;

                    HeatModel? targetHeat = null;
                    if (_currentMeet.ActiveHeatNumber.HasValue)
                    {
                        targetHeat = targetEvent.Heats.FirstOrDefault(h => h.HeatNumber == _currentMeet.ActiveHeatNumber.Value);
                    }
                    targetHeat ??= targetEvent.Heats.FirstOrDefault();

                    if (targetHeat != null)
                    {
                        LoadHeat(targetHeat);
                        icLanes.Items.Refresh();
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

                AddLogMessage($"Race session '{_currentMeet.MeetName}' ({_currentMeet.Events.Count} Events) successfully restored.");
            }
            catch (Exception ex)
            {
                AddLogMessage($"[RESTORE ERROR] {ex.Message}");
                MessageBox.Show($"An error occurred while restoring session:\n{ex.Message}", "Recovery Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            _clockTimer.Stop();
            _displayTimer.Stop();
            _scoreboardServer.Dispose();
            _wsServer.Dispose();
            _engine.Dispose();
            base.OnClosed(e);
        }
    }
}