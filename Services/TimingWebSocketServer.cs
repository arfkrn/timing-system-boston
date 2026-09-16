using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Fleck;
using boston_timing_system.Core;
using boston_timing_system.Helpers;
using boston_timing_system.Models;

namespace boston_timing_system.Services
{
    public class TimingWebSocketServer : ObservableObject, IDisposable
    {
        private readonly RaceTimingEngine _engine;
        private WebSocketServer? _server;
        private readonly ConcurrentDictionary<IWebSocketConnection, ConnectedClient> _clients = new();
        private readonly ConcurrentDictionary<string, CachedSession> _cachedSessions = new();
        private readonly object _syncLock = new();

        private bool _isRunning;
        private int _totalClients;
        private int _startersCount;
        private int _refereesCount;
        private int _chiefsCount;
        private int _spectatorsCount;
        private long _lastTickTimestamp;
        private Timer? _heartbeatWatchdogTimer;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public int Port { get; private set; }
        public string LocalIpAddress { get; }
        public string ServerUri => $"ws://{LocalIpAddress}:{Port}";
        public bool IsRunning
        {
            get => _isRunning;
            private set => SetProperty(ref _isRunning, value);
        }

        public string CurrentMeetName { get; set; } = "Swimming Meet 2026";
        public int CurrentEventNumber { get; set; } = 1;
        public string CurrentEventName { get; set; } = "50m Freestyle";
        public int CurrentHeatNumber { get; set; } = 1;

        public int TotalClients
        {
            get => _totalClients;
            private set => SetProperty(ref _totalClients, value);
        }

        public int StartersCount
        {
            get => _startersCount;
            private set => SetProperty(ref _startersCount, value);
        }

        public int RefereesCount
        {
            get => _refereesCount;
            private set => SetProperty(ref _refereesCount, value);
        }

        public int ChiefsCount
        {
            get => _chiefsCount;
            private set => SetProperty(ref _chiefsCount, value);
        }

        public int SpectatorsCount
        {
            get => _spectatorsCount;
            private set => SetProperty(ref _spectatorsCount, value);
        }

        public bool IsOwsRefereeConnected => _clients.Values.Any(c => c.Role == ClientRole.Referee);
        public double OwsRefereeLatencyMs => _clients.Values.FirstOrDefault(c => c.Role == ClientRole.Referee)?.OneWayLatencyMs ?? 0.0;

        /// <summary>Average one-way latency (ms) across all connected Starter devices. Returns 0 if none connected.</summary>
        public double StarterLatencyMs
        {
            get
            {
                var starters = _clients.Values.Where(c => c.Role == ClientRole.Starter && c.OneWayLatencyMs > 0).ToList();
                return starters.Count > 0 ? starters.Average(c => c.OneWayLatencyMs) : 0.0;
            }
        }

        /// <summary>Average one-way latency (ms) across all connected Chief/Referee devices. Returns 0 if none connected.</summary>
        public double ChiefLatencyMs
        {
            get
            {
                var chiefs = _clients.Values
                    .Where(c => (c.Role == ClientRole.Chief || c.Role == ClientRole.Referee) && c.OneWayLatencyMs > 0)
                    .ToList();
                return chiefs.Count > 0 ? chiefs.Average(c => c.OneWayLatencyMs) : 0.0;
            }
        }

        private string _accessCode = string.Empty;
        public string AccessCode
        {
            get => _accessCode;
            private set => SetProperty(ref _accessCode, value);
        }

        /// <summary>
        /// Generates a random 4-digit code (1000 to 9999) from the server
        /// </summary>
        public static string GenerateRandomAccessCode()
        {
            return Random.Shared.Next(1000, 10000).ToString("D4");
        }

        /// <summary>
        /// Regenerates a fresh random 4-digit access code and notifies connected clients
        /// </summary>
        public string RegenerateAccessCode()
        {
            AccessCode = GenerateRandomAccessCode();
            Broadcast(new ServerEvent
            {
                Event = "ACCESS_CODE_ROTATED",
                AccessCode = AccessCode,
                Message = $"Server access code updated: {AccessCode}"
            });
            Log($"[SECURITY] New 4-digit Access Code generated: {AccessCode}");
            return AccessCode;
        }

        public event Action<string>? LogReceived;
        /// <summary>Fired every time a PING/latency measurement is received from any client.</summary>
        public event Action? LatencyUpdated;

        private readonly int[] _candidatePorts;

        public TimingWebSocketServer(RaceTimingEngine engine, int port = 8181)
            : this(engine, new[] { port }) { }

        public TimingWebSocketServer(RaceTimingEngine engine, int[] candidatePorts)
        {
            _engine = engine;
            _candidatePorts = candidatePorts.Length > 0 ? candidatePorts : new[] { 8181 };
            Port = _candidatePorts[0]; // provisional; updated in Start() to the actual bound port
            LocalIpAddress = NetworkHelper.GetLocalIpAddress();
            AccessCode = GenerateRandomAccessCode();

            // Hook into engine events to broadcast to all connected phones
            _engine.RaceStarted += HandleEngineRaceStarted;
            _engine.RaceStopped += HandleEngineRaceStopped;
            _engine.RaceReset += HandleEngineRaceReset;
            _engine.LaneFinished += HandleEngineLaneFinished;
            _engine.LaneStatusChanged += HandleEngineLaneStatusChanged;
            _engine.LaneSplitRecorded += HandleEngineLaneSplitRecorded;
            _engine.Tick += HandleEngineTick;
            _engine.ModeChanged += HandleEngineModeChanged;
            _engine.OwsFinishRecorded += HandleEngineOwsFinishRecorded;
            _engine.OwsRecordStatusChanged += HandleEngineOwsRecordStatusChanged;
        }

        /// <summary>
        /// Tries to start the WebSocket server, cycling through <see cref="_candidatePorts"/> until
        /// one succeeds. Returns true on success, false if every candidate port is unavailable.
        /// </summary>
        public bool Start()
        {
            lock (_syncLock)
            {
                if (IsRunning)
                {
                    return true;
                }

                FleckLog.LogAction = (level, message, ex) =>
                {
                    // Suppress Fleck internal log noise
                };

                Exception? lastException = null;

                foreach (int candidatePort in _candidatePorts)
                {
                    try
                    {
                        var server = new WebSocketServer($"ws://0.0.0.0:{candidatePort}")
                        {
                            RestartAfterListenError = false
                        };

                        server.Start(socket =>
                        {
                            socket.OnOpen = () => HandleSocketOpen(socket);
                            socket.OnClose = () => HandleSocketClose(socket);
                            socket.OnMessage = message => HandleSocketMessage(socket, message);
                            socket.OnError = ex => HandleSocketError(socket, ex);
                        });

                        // Reached here — port is available and server is listening
                        _server = server;
                        Port = candidatePort;
                        IsRunning = true;
                        _heartbeatWatchdogTimer?.Dispose();
                        _heartbeatWatchdogTimer = new Timer(CheckHeartbeats, null, 1000, 1000);
                        Log($"WebSocket Server listening on {ServerUri}");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        Log($"[PORT UNAVAILABLE] Port {candidatePort} failed: {ex.Message}. Trying next port...");
                    }
                }

                // All ports exhausted
                Log($"[ERROR] All candidate ports failed. Last error: {lastException?.Message}");
                throw new InvalidOperationException(
                    $"WebSocket server could not bind to any of the candidate ports [{string.Join(", ", _candidatePorts)}]. " +
                    $"Last error: {lastException?.Message}", lastException);
            }
        }

        public void Stop()
        {
            lock (_syncLock)
            {
                if (!IsRunning)
                {
                    return;
                }

                _heartbeatWatchdogTimer?.Dispose();
                _heartbeatWatchdogTimer = null;

                foreach (var client in _clients.Keys.ToList())
                {
                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                        // Ignore close error
                    }
                }

                _clients.Clear();
                _server?.Dispose();
                _server = null;

                IsRunning = false;
                UpdateAllLaneRefereeStatuses();
                UpdateClientMetrics();
                Log("WebSocket Server stopped.");
            }
        }

        private void CheckHeartbeats(object? state)
        {
            var now = DateTime.UtcNow;

            // 1. Clean up expired reconnect sessions older than 60 seconds
            foreach (var key in _cachedSessions.Keys.ToList())
            {
                if (_cachedSessions.TryGetValue(key, out var cached) && (now - cached.DisconnectedAt).TotalSeconds > 60)
                {
                    _cachedSessions.TryRemove(key, out _);
                }
            }

            // 2. Heartbeat check with 8-second tolerance for pool Wi-Fi RF jitter
            foreach (var pair in _clients.ToList())
            {
                var socket = pair.Key;
                var client = pair.Value;

                if ((now - client.LastSeenUtc).TotalSeconds > 8)
                {
                    Log($"[HEARTBEAT TIMEOUT >8s] Device connection timed out: {client.IpAddress} ({client.Role}, Lane {client.AssignedLane})");
                    try
                    {
                        socket.Close();
                    }
                    catch
                    {
                        // Ignore socket close failure
                    }
                    // Do NOT call HandleSocketClose(socket) manually here; Fleck automatically triggers socket.OnClose
                }
            }
        }

        private void HandleSocketOpen(IWebSocketConnection socket)
        {
            var client = new ConnectedClient(socket);
            _clients.TryAdd(socket, client);

            UpdateClientMetrics();
            Log($"Device connected: {client.IpAddress} (ID: {client.ClientId})");

            // Send current state synchronization immediately upon connecting
            SendToSocket(socket, CreateStateSyncEvent());
        }

        private void HandleSocketClose(IWebSocketConnection socket)
        {
            if (_clients.TryRemove(socket, out var client))
            {
                Log($"Device disconnected: {client.IpAddress} (Role: {client.Role}, Lane: {client.AssignedLane})");

                // Cache active authenticated session for 60s grace period to allow instant reconnect
                if (client.IsAuthenticated && client.Role != ClientRole.Unknown && !string.IsNullOrWhiteSpace(client.IpAddress))
                {
                    _cachedSessions[client.IpAddress] = new CachedSession
                    {
                        Role = client.Role,
                        AssignedLane = client.AssignedLane,
                        DeviceName = client.DeviceName,
                        IsAuthenticated = client.IsAuthenticated,
                        DisconnectedAt = DateTime.UtcNow
                    };
                }

                // Immediately sync referee status on all lanes so indicators turn off
                UpdateAllLaneRefereeStatuses();

                if (client.Role == ClientRole.Chief)
                {
                    Log($"Chief disconnected: {client.IpAddress}");
                }

                UpdateClientMetrics();
            }
        }

        private void HandleSocketError(IWebSocketConnection socket, Exception ex)
        {
            Log($"Socket error on {socket.ConnectionInfo?.ClientIpAddress}: {ex.Message}");
            // Clean up client immediately on socket error so indicators do not get stuck
            HandleSocketClose(socket);
        }

        private void HandleSocketMessage(IWebSocketConnection socket, string rawMessage)
        {
            if (!_clients.TryGetValue(socket, out var client))
            {
                return;
            }

            client.UpdateActivity();

            try
            {
                var command = JsonSerializer.Deserialize<ClientCommand>(rawMessage, JsonOptions);
                if (command == null || string.IsNullOrWhiteSpace(command.Action))
                {
                    return;
                }

                string actionUpper = command.Action.Trim().ToUpperInvariant();

                // Gate operational actions: require valid authentication with 4-digit Access Code
                if (actionUpper != "PING" && actionUpper != "REGISTER" && actionUpper != "SYNC_REQUEST")
                {
                    if (!client.IsAuthenticated)
                    {
                        Log($"[AUTH REQUIRED] Rejected '{actionUpper}' from unauthenticated client {client.IpAddress}");
                        SendToSocket(socket, new ServerEvent
                        {
                            Event = "AUTH_REQUIRED",
                            Message = "Perangkat belum terotentikasi. Silakan hubungkan dengan 4 digit access code yang valid."
                        });
                        return;
                    }
                }

                switch (actionUpper)
                {
                    case "PING":
                        if (command.EstimatedLatencyMs.HasValue)
                        {
                            client.RecordLatencyMeasurement(command.EstimatedLatencyMs.Value);
                            LatencyUpdated?.Invoke(); // notify UI to refresh latency labels
                        }

                        if (client.Role == ClientRole.Referee && client.AssignedLane.HasValue)
                        {
                            var activeLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == client.AssignedLane.Value);
                            if (activeLane != null)
                            {
                                activeLane.RefereeLatencyMs = client.OneWayLatencyMs;
                            }
                        }

                        SendToSocket(socket, new ServerEvent
                        {
                            Event = "PONG",
                            ClientTimestamp = command.ClientTimestamp,
                            ServerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        });
                        break;

                    case "REGISTER":
                        Log($"Command '{actionUpper}' from {client.IpAddress} ({command.Role ?? "Unknown"})");
                        HandleRegisterCommand(client, command);
                        break;

                    case "UPDATE_OWS_BIB":
                    case "UPDATE_OWS_RECORD":
                        if (_engine.CurrentMode == TimingMode.OpenWater)
                        {
                            OwsRecordModel? rec = null;
                            if (!string.IsNullOrWhiteSpace(command.Id))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.Id == command.Id);
                            }
                            if (rec == null && command.Rank.HasValue && command.Rank.Value > 0)
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.Rank == command.Rank.Value);
                            }
                            if (rec == null && !string.IsNullOrWhiteSpace(command.PreviousBibNumber))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.BibNumber.Equals(command.PreviousBibNumber, StringComparison.OrdinalIgnoreCase));
                            }
                            if (rec == null && !string.IsNullOrWhiteSpace(command.SwimmerName))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.SwimmerName.Equals(command.SwimmerName, StringComparison.OrdinalIgnoreCase));
                            }
                            if (rec == null && !string.IsNullOrWhiteSpace(command.FormattedTime))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.FormattedTime == command.FormattedTime);
                            }
                            if (rec == null && command.Rank.HasValue)
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.Rank == command.Rank.Value);
                            }
                            if (rec != null)
                            {
                                string newBib = command.BibNumber?.Trim() ?? string.Empty;

                                // KASUS 1: Client mengirim BIB kosong -> Reset / Hapus BIB pada Rank tersebut
                                if (string.IsNullOrWhiteSpace(newBib))
                                {
                                    rec.BibNumber = string.Empty;
                                    rec.SwimmerName = string.Empty;
                                    rec.Club = string.Empty;
                                    rec.Status = LaneStatus.Finished;

                                    if (_engine.CurrentHeat != null)
                                    {
                                        var prevLane = _engine.CurrentHeat.Lanes.FirstOrDefault(l => l.Rank == rec.Rank);
                                        if (prevLane != null)
                                        {
                                            prevLane.Status = _engine.Status == RaceStatus.Running ? LaneStatus.Running : LaneStatus.Ready;
                                            prevLane.FinishTime = null;
                                            prevLane.FormattedTime = "00.00.00";
                                            prevLane.Rank = null;

                                            var prevEngLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == prevLane.LaneNumber);
                                            if (prevEngLane != null)
                                            {
                                                prevEngLane.Status = prevLane.Status;
                                                prevEngLane.FinishTime = null;
                                                prevEngLane.FormattedTime = "00.00.00";
                                                prevEngLane.Rank = null;
                                            }
                                        }
                                    }

                                    Broadcast(CreateStateSyncEvent());
                                    Log($"[OWS RESET BIB] Cleared BIB for Rank #{rec.Rank} by {client.Role} ({client.IpAddress})");
                                    break;
                                }

                                // KASUS 2: Client mengirim nomor BIB baru -> Update BIB dan sinkronisasi peserta
                                rec.BibNumber = newBib;

                                if (!string.IsNullOrWhiteSpace(command.Status) && rec.HasBib)
                                {
                                    rec.StatusOverride = command.Status;
                                }

                                var participant = _engine.LookupParticipantByBib(newBib);
                                if (participant != null)
                                {
                                    rec.SwimmerName = participant.SwimmerName ?? string.Empty;
                                    rec.Club = participant.Club ?? string.Empty;
                                }
                                else if (!string.IsNullOrWhiteSpace(command.SwimmerName))
                                {
                                    rec.SwimmerName = command.SwimmerName.Trim();
                                    rec.Club = command.Club?.Trim() ?? string.Empty;
                                }
                                else
                                {
                                    rec.SwimmerName = string.Empty;
                                    rec.Club = string.Empty;
                                }

                                if (_engine.CurrentHeat != null)
                                {
                                    // Revert peserta lama jika rank ini sebelumnya terhubung dengan bib berbeda
                                    var prevLane = _engine.CurrentHeat.Lanes.FirstOrDefault(l =>
                                        l.Rank == rec.Rank &&
                                        (l.HasExplicitBibNumber ? !l.BibNumber.Equals(newBib, StringComparison.OrdinalIgnoreCase) : l.LaneNumber.ToString() != newBib));
                                    if (prevLane != null)
                                    {
                                        prevLane.Status = _engine.Status == RaceStatus.Running ? LaneStatus.Running : LaneStatus.Ready;
                                        prevLane.FinishTime = null;
                                        prevLane.FormattedTime = "00.00.00";
                                        prevLane.Rank = null;

                                        var prevEngLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == prevLane.LaneNumber);
                                        if (prevEngLane != null)
                                        {
                                            prevEngLane.Status = prevLane.Status;
                                            prevEngLane.FinishTime = null;
                                            prevEngLane.FormattedTime = "00.00.00";
                                            prevEngLane.Rank = null;
                                        }
                                    }

                                    var matchedLane = _engine.CurrentHeat.Lanes.FirstOrDefault(l =>
                                        l.HasExplicitBibNumber
                                            ? l.BibNumber.Equals(newBib, StringComparison.OrdinalIgnoreCase)
                                            : l.LaneNumber.ToString() == newBib);
                                    if (matchedLane != null)
                                    {
                                        matchedLane.Status = rec.Status;
                                        matchedLane.FinishTime = rec.FinishTime;
                                        matchedLane.FormattedTime = rec.FormattedTime;
                                        matchedLane.Rank = rec.Status == LaneStatus.Finished ? rec.Rank : null;

                                        var engLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == matchedLane.LaneNumber);
                                        if (engLane != null)
                                        {
                                            engLane.Status = rec.Status;
                                            engLane.FinishTime = rec.FinishTime;
                                            engLane.FormattedTime = rec.FormattedTime;
                                            engLane.Rank = rec.Status == LaneStatus.Finished ? rec.Rank : null;
                                        }

                                        if (string.IsNullOrWhiteSpace(rec.SwimmerName) && !string.IsNullOrWhiteSpace(matchedLane.SwimmerName))
                                        {
                                            rec.SwimmerName = matchedLane.SwimmerName;
                                        }
                                        if (string.IsNullOrWhiteSpace(rec.Club) && !string.IsNullOrWhiteSpace(matchedLane.Club))
                                        {
                                            rec.Club = matchedLane.Club;
                                        }
                                    }
                                }

                                Broadcast(CreateStateSyncEvent());
                                Log($"[OWS UPDATE] Updated Rank #{rec.Rank} with BIB {rec.BibNumber} ({rec.SwimmerName})");
                            }
                        }
                        break;

                    case "DELETE_OWS_RECORD":
                        if (_engine.CurrentMode == TimingMode.OpenWater)
                        {
                            OwsRecordModel? rec = null;
                            if (!string.IsNullOrWhiteSpace(command.Id))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.Id == command.Id);
                            }
                            if (rec == null && command.Rank.HasValue && command.Rank.Value > 0)
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.Rank == command.Rank.Value);
                            }
                            if (rec == null && !string.IsNullOrWhiteSpace(command.BibNumber))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.BibNumber.Equals(command.BibNumber, StringComparison.OrdinalIgnoreCase));
                            }
                            if (rec == null && !string.IsNullOrWhiteSpace(command.SwimmerName))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.SwimmerName.Equals(command.SwimmerName, StringComparison.OrdinalIgnoreCase));
                            }
                            if (rec == null && !string.IsNullOrWhiteSpace(command.FormattedTime))
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.FormattedTime == command.FormattedTime);
                            }
                            if (rec == null && command.Rank.HasValue)
                            {
                                rec = _engine.OwsRecords.FirstOrDefault(r => r.Rank == command.Rank.Value);
                            }

                            if (rec != null)
                            {
                                int deletedRank = rec.Rank;
                                string deletedBib = rec.BibNumber;
                                _engine.RemoveOwsRecord(rec);

                                Broadcast(CreateStateSyncEvent());
                                Log($"[OWS DELETE] Rank #{deletedRank} (BIB: {deletedBib}, ID: {rec.Id}) deleted by {client.Role} ({client.IpAddress})");
                            }
                            else
                            {
                                Log($"[OWS DELETE REJECTED] Record not found (Rank: {command.Rank}, ID: {command.Id}).");
                            }
                        }
                        break;

                    case "START_RACE":
                        Log($"Command 'START_RACE' from {client.IpAddress} ({client.Role})");
                        _engine.StartRace();
                        break;

                    case "RECORD_OWS_FINISH":
                    case "STOP_LANE":
                        if (_engine.CurrentMode == TimingMode.OpenWater)
                        {
                            double owsLatencyMs = command.EstimatedLatencyMs ?? client.OneWayLatencyMs;
                            TimeSpan? compensatedOwsTime = null;

                            if (command.ElapsedTimeMs.HasValue && command.ElapsedTimeMs.Value > 0)
                            {
                                TimeSpan clientElapsed = TimeSpan.FromMilliseconds(command.ElapsedTimeMs.Value);
                                if (clientElapsed > _engine.Elapsed)
                                {
                                    clientElapsed = _engine.Elapsed;
                                }
                                compensatedOwsTime = clientElapsed;
                                Log($"Command '{actionUpper}' (OWS Finish) from {client.Role} ({client.IpAddress}) (Using Client Stopwatch Elapsed: {clientElapsed:mm\\:ss\\.ff})");
                            }
                            else if (owsLatencyMs > 0)
                            {
                                TimeSpan arrival = _engine.Elapsed;
                                compensatedOwsTime = arrival - TimeSpan.FromMilliseconds(owsLatencyMs);
                                Log($"Command '{actionUpper}' (OWS Finish) from {client.Role} ({client.IpAddress}) (Compensated: -{owsLatencyMs:F1}ms)");
                            }
                            else
                            {
                                Log($"Command '{actionUpper}' (OWS Finish) from {client.Role} ({client.IpAddress})");
                            }

                            var recorded = _engine.RecordOwsFinish(compensatedOwsTime, command.BibNumber, command.SwimmerName);
                            if (recorded != null)
                            {
                                Log($"[OWS FINISH SUCCESS] Rank #{recorded.Rank} recorded: {recorded.FormattedTime} ({recorded.BibNumber})");
                            }
                            else
                            {
                                Log($"[OWS FINISH REJECTED] Status perlombaan saat ini '{_engine.Status}'. Lomba harus di-START terlebih dahulu agar finis tercatat.");
                            }
                            break;
                        }

                        int laneToStop = command.LaneNumber ?? client.AssignedLane ?? -1;
                        if (laneToStop >= 0)
                        {
                            double latencyMs = command.EstimatedLatencyMs ?? client.OneWayLatencyMs;
                            TimeSpan? compensatedTime = null;

                            if (command.ElapsedTimeMs.HasValue && command.ElapsedTimeMs.Value > 0)
                            {
                                TimeSpan clientElapsed = TimeSpan.FromMilliseconds(command.ElapsedTimeMs.Value);
                                if (clientElapsed > _engine.Elapsed)
                                {
                                    clientElapsed = _engine.Elapsed;
                                }
                                compensatedTime = clientElapsed;
                                Log($"Command 'STOP_LANE' Lane {laneToStop} from {client.Role} ({client.IpAddress}) (Using Client Stopwatch Elapsed: {clientElapsed:mm\\:ss\\.ff})");
                            }
                            else if (latencyMs > 0)
                            {
                                TimeSpan arrival = _engine.Elapsed;
                                compensatedTime = arrival - TimeSpan.FromMilliseconds(latencyMs);
                                Log($"Command 'STOP_LANE' Lane {laneToStop} from {client.Role} ({client.IpAddress}) (Compensated: -{latencyMs:F1}ms)");
                            }
                            else
                            {
                                Log($"Command 'STOP_LANE' for Lane {laneToStop} from {client.Role} ({client.IpAddress})");
                            }

                            bool stopped = _engine.StopLane(laneToStop, compensatedTime);
                            if (stopped)
                            {
                                if (client.Role == ClientRole.Chief)
                                {
                                    Log($"[CHIEF BACKUP] Lane {laneToStop} stopped");
                                }
                            }
                            else
                            {
                                Log($"[STOP_LANE REJECTED] Lane {laneToStop} tidak dapat dihentikan. Status perlombaan: '{_engine.Status}'");
                            }

                            var targetLane = _engine.Lanes.FirstOrDefault(l => l.LaneNumber == laneToStop);
                            if (targetLane != null && latencyMs > 0)
                            {
                                targetLane.RefereeLatencyMs = latencyMs;
                            }
                        }
                        break;

                    case "STOP_ACTIVE_LANES":
                    case "STOP_ALL_ACTIVE":
                        double activeLatencyMs = command.EstimatedLatencyMs ?? client.OneWayLatencyMs;
                        TimeSpan? compActiveTime = null;
                        if (command.ElapsedTimeMs.HasValue && command.ElapsedTimeMs.Value > 0)
                        {
                            TimeSpan clientElapsed = TimeSpan.FromMilliseconds(command.ElapsedTimeMs.Value);
                            if (clientElapsed > _engine.Elapsed)
                            {
                                clientElapsed = _engine.Elapsed;
                            }
                            compActiveTime = clientElapsed;
                        }
                        else if (activeLatencyMs > 0)
                        {
                            TimeSpan arrival = _engine.Elapsed;
                            compActiveTime = arrival - TimeSpan.FromMilliseconds(activeLatencyMs);
                        }

                        int stoppedLanes = _engine.StopActiveLanes(compActiveTime);
                        Log($"Command '{actionUpper}' from {client.Role} ({client.IpAddress}) - stopped {stoppedLanes} active lane(s)");
                        break;

                    case "RECORD_SPLIT":
                        int laneForSplit = command.LaneNumber ?? client.AssignedLane ?? -1;
                        if (laneForSplit >= 0)
                        {
                            double latencyMs = command.EstimatedLatencyMs ?? client.OneWayLatencyMs;
                            TimeSpan? compensatedTime = null;

                            if (command.ElapsedTimeMs.HasValue && command.ElapsedTimeMs.Value > 0)
                            {
                                TimeSpan clientElapsed = TimeSpan.FromMilliseconds(command.ElapsedTimeMs.Value);
                                if (clientElapsed > _engine.Elapsed)
                                {
                                    clientElapsed = _engine.Elapsed;
                                }
                                compensatedTime = clientElapsed;
                                Log($"Command 'RECORD_SPLIT' Lane {laneForSplit} from {client.IpAddress} (Using Client Stopwatch Elapsed: {clientElapsed:mm\\:ss\\.ff})");
                            }
                            else if (latencyMs > 0)
                            {
                                TimeSpan arrival = _engine.Elapsed;
                                compensatedTime = arrival - TimeSpan.FromMilliseconds(latencyMs);
                                Log($"Command 'RECORD_SPLIT' Lane {laneForSplit} from {client.IpAddress} (Compensated: -{latencyMs:F1}ms)");
                            }
                            else
                            {
                                Log($"Command 'RECORD_SPLIT' for Lane {laneForSplit} from {client.IpAddress}");
                            }

                            _engine.RecordLaneSplit(laneForSplit, compensatedTime);
                        }
                        break;

                    case "RESET_RACE":
                        Log($"Command 'RESET_RACE' from {client.IpAddress} ({client.Role})");
                        _engine.ResetRace();
                        break;

                    case "SYNC_REQUEST":
                        SendToSocket(socket, CreateStateSyncEvent());
                        break;

                    case "DISCONNECT":
                    case "UNREGISTER":
                    case "LOGOUT":
                        Log($"Command '{actionUpper}' from {client.IpAddress} ({client.Role}, Lane {client.AssignedLane})");
                        client.Role = ClientRole.Unknown;
                        client.AssignedLane = null;
                        client.IsAuthenticated = false;
                        UpdateAllLaneRefereeStatuses();
                        UpdateClientMetrics();
                        try
                        {
                            socket.Close();
                        }
                        catch { }
                        HandleSocketClose(socket);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to process message: {ex.Message}");
            }
        }

        private void HandleRegisterCommand(ConnectedClient client, ClientCommand command)
        {
            ClientRole parsedRole = ClientRole.Unknown;
            if (!string.IsNullOrWhiteSpace(command.Role))
            {
                if (Enum.TryParse<ClientRole>(command.Role, ignoreCase: true, out var role))
                {
                    parsedRole = role;
                }
            }

            // Restore from 60s cached session if client reconnected without explicit role or after brief Wi-Fi drop
            if (_cachedSessions.TryRemove(client.IpAddress, out var cached) && (DateTime.UtcNow - cached.DisconnectedAt).TotalSeconds <= 60)
            {
                if (parsedRole == ClientRole.Unknown)
                {
                    parsedRole = cached.Role;
                }
                if (!command.LaneNumber.HasValue && cached.AssignedLane.HasValue)
                {
                    client.AssignedLane = cached.AssignedLane;
                }
                Log($"[SESSION RESTORED] Device {client.IpAddress} restored session as {cached.Role} (Lane {cached.AssignedLane})");
            }

            // Authentication verification using 4-digit server access code
            bool requiresAuth = parsedRole != ClientRole.Spectator;
            if (requiresAuth)
            {
                bool isCodeValid = !string.IsNullOrWhiteSpace(command.AccessCode) &&
                                   string.Equals(command.AccessCode.Trim(), AccessCode, StringComparison.Ordinal);

                if (!isCodeValid)
                {
                    client.IsAuthenticated = false;
                    Log($"[AUTH REJECTED] {client.IpAddress} ({command.DeviceName ?? "Device"}) sent invalid code '{command.AccessCode}' for role {parsedRole} (Expected: {AccessCode})");
                    SendToSocket(client.Socket, new ServerEvent
                    {
                        Event = "AUTH_FAILED",
                        Message = "Access Code salah! Masukkan 4 digit access code yang tertera pada server timing."
                    });
                    return;
                }
            }

            client.Role = parsedRole;
            client.IsAuthenticated = true;
            Log($"[AUTH SUCCESS] {client.IpAddress} ({command.DeviceName ?? "Device"}) authenticated with code {AccessCode} as {client.Role}");

            if (command.LaneNumber.HasValue)
            {
                int laneVal = command.LaneNumber.Value;
                if (laneVal == 10) laneVal = 0; // Normalize Lane 10 -> 0
                client.AssignedLane = laneVal;
            }

            if (!string.IsNullOrWhiteSpace(command.DeviceName))
            {
                client.DeviceName = command.DeviceName;
            }

            UpdateAllLaneRefereeStatuses();

            if (client.Role == ClientRole.Chief)
            {
                Log($"Chief registered from {client.IpAddress} ({client.DeviceName})");
            }

            UpdateClientMetrics();

            // Send registration confirmation and synced state
            SendToSocket(client.Socket, new ServerEvent
            {
                Event = "REGISTERED",
                AccessCode = AccessCode,
                Message = $"Registered successfully as {client.Role}" +
                          (client.AssignedLane.HasValue ? $" for Lane {client.AssignedLane.Value}" : "")
            });
            SendToSocket(client.Socket, CreateStateSyncEvent());
        }

        public void UpdateAllLaneRefereeStatuses()
        {
            var refereeClients = _clients.Values
                .Where(c => c.Role == ClientRole.Referee && c.AssignedLane.HasValue)
                .ToList();

            void Apply()
            {
                foreach (var lane in _engine.Lanes)
                {
                    var refereeClient = refereeClients.FirstOrDefault(c => c.AssignedLane == lane.LaneNumber);
                    lane.IsRefereeConnected = refereeClient != null;
                    lane.RefereeLatencyMs = refereeClient?.OneWayLatencyMs ?? 0;
                }
            }

            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke((Action)Apply);
            }
            else
            {
                Apply();
            }
        }

        private void UpdateLaneRefereeStatus(int laneNumber)
        {
            UpdateAllLaneRefereeStatuses();
        }

        private void UpdateClientMetrics()
        {
            var clientList = _clients.Values.ToList();
            TotalClients = clientList.Count;
            StartersCount = clientList.Count(c => c.Role == ClientRole.Starter);
            RefereesCount = clientList.Count(c => c.Role == ClientRole.Referee);
            ChiefsCount = clientList.Count(c => c.Role == ClientRole.Chief);
            SpectatorsCount = clientList.Count(c => c.Role == ClientRole.Spectator);
        }

        private void HandleEngineRaceStarted()
        {
            Broadcast(new ServerEvent
            {
                Event = "RACE_STARTED",
                Status = _engine.Status.ToString(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        private void HandleEngineRaceStopped()
        {
            Broadcast(CreateStateSyncEvent());
        }

        private void HandleEngineRaceReset()
        {
            Broadcast(new ServerEvent
            {
                Event = "RACE_RESET",
                Status = _engine.Status.ToString(),
                ElapsedTime = "00:00.00"
            });
        }

        private void HandleEngineLaneFinished(LaneModel lane, TimeSpan finishTime)
        {
            Broadcast(new ServerEvent
            {
                Event = "LANE_STOPPED",
                LaneNumber = lane.LaneNumber,
                FinishTime = lane.FormattedTime,
                Status = lane.Status.ToString(),
                CompensatedLatencyMs = lane.RefereeLatencyMs
            });
        }

        private void HandleEngineLaneStatusChanged(LaneModel lane, LaneStatus status)
        {
            Broadcast(new ServerEvent
            {
                Event = "LANE_STATUS_UPDATED",
                LaneNumber = lane.LaneNumber,
                Status = status.ToString(),
                FinishTime = lane.FormattedTime,
                Message = $"Lane {lane.LaneNumber} status updated to {status}"
            });

            Log($"Lane {lane.LaneNumber} status changed to {status}");

            if (_engine.Status == RaceStatus.Finished)
            {
                Broadcast(CreateStateSyncEvent());
            }
        }

        private void HandleEngineLaneSplitRecorded(LaneModel lane, TimeSpan splitTime)
        {
            Broadcast(new ServerEvent
            {
                Event = "LANE_SPLIT",
                LaneNumber = lane.LaneNumber,
                SplitTime = LaneModel.FormatTime(splitTime),
                CompensatedLatencyMs = lane.RefereeLatencyMs
            });
        }

        private void HandleEngineTick(TimeSpan elapsed)
        {
            // Throttle clock tick broadcast to every 80-100ms to preserve Wi-Fi bandwidth
            long nowMs = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastTickTimestamp).TotalMilliseconds < 80)
            {
                return;
            }

            _lastTickTimestamp = nowMs;

            Broadcast(new ServerEvent
            {
                Event = "CLOCK_TICK",
                ElapsedTime = _engine.FormattedElapsedTime
            });
        }

        public ServerEvent CreateStateSyncEvent()
        {
            var laneDtos = _engine.Lanes.Select(l => new LaneStateDto
            {
                LaneNumber = l.LaneNumber,
                SwimmerName = l.SwimmerName,
                Club = l.Club,
                SeedTime = l.SeedTime,
                Rank = l.Rank,
                Status = l.Status.ToString(),
                FormattedTime = l.FormattedTime,
                IsRefereeConnected = l.IsRefereeConnected,
                IsChiefConnected = ChiefsCount > 0,
                LatencyMs = l.RefereeLatencyMs
            }).ToList();

            double avgLatency = _clients.Values.Count > 0 ? _clients.Values.Average(c => c.OneWayLatencyMs) : 0.0;

            var owsDtos = _engine.OwsRecords.Select(r => new OwsRecordDto
            {
                Id = r.Id,
                Rank = r.Rank,
                BibNumber = r.BibNumber,
                SwimmerName = r.SwimmerName,
                Club = r.Club,
                FormattedTime = r.FormattedTime,
                GapTime = r.GapTime,
                Status = r.Status.ToString()
            }).ToList();

            return new ServerEvent
            {
                Event = "STATE_SYNC",
                TimingMode = _engine.CurrentMode == TimingMode.OpenWater ? "OPEN_WATER" : "POOL",
                Status = _engine.Status.ToString(),
                ElapsedTime = _engine.FormattedElapsedTime,
                MeetName = CurrentMeetName,
                EventNumber = CurrentEventNumber,
                EventName = CurrentEventName,
                HeatNumber = CurrentHeatNumber,
                AccessCode = AccessCode,
                Lanes = laneDtos,
                OwsRecords = owsDtos,
                OwsFinisherCount = _engine.OwsRecords.Count,
                ConnectedClients = new ClientSummaryDto
                {
                    TotalCount = TotalClients,
                    StartersCount = StartersCount,
                    RefereesCount = RefereesCount,
                    ChiefsCount = ChiefsCount,
                    SpectatorsCount = SpectatorsCount,
                    AverageLatencyMs = Math.Round(avgLatency, 1)
                }
            };
        }

        public void UpdateCurrentMeetContext(string meetName, int eventNumber, string eventName, int heatNumber)
        {
            CurrentMeetName = meetName;
            CurrentEventNumber = eventNumber;
            CurrentEventName = eventName;
            CurrentHeatNumber = heatNumber;
            Broadcast(CreateStateSyncEvent());
        }

        public void Broadcast(ServerEvent serverEvent)
        {
            string json = JsonSerializer.Serialize(serverEvent, JsonOptions);
            foreach (var client in _clients.Keys)
            {
                try
                {
                    if (client.IsAvailable)
                    {
                        lock (client)
                        {
                            client.Send(json);
                        }
                    }
                }
                catch
                {
                    // Ignore transient send failure
                }
            }
        }

        private static void SendToSocket(IWebSocketConnection socket, ServerEvent serverEvent)
        {
            try
            {
                if (socket.IsAvailable)
                {
                    string json = JsonSerializer.Serialize(serverEvent, JsonOptions);
                    lock (socket)
                    {
                        socket.Send(json);
                    }
                }
            }
            catch
            {
                // Ignore transient send failure
            }
        }

        private void Log(string message)
        {
            LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void HandleEngineModeChanged(TimingMode mode)
        {
            Broadcast(new ServerEvent
            {
                Event = "TIMING_MODE_CHANGED",
                TimingMode = mode == TimingMode.OpenWater ? "OPEN_WATER" : "POOL",
                Message = $"Timing mode switched to {mode}"
            });
            Broadcast(CreateStateSyncEvent());
            Log($"[MODE SWITCH] Timing system mode changed to {mode.ToString().ToUpperInvariant()}");
        }

        private void HandleEngineOwsFinishRecorded(OwsRecordModel record)
        {
            Broadcast(new ServerEvent
            {
                Event = "OWS_FINISH_RECORDED",
                TimingMode = "OPEN_WATER",
                OwsFinisherCount = _engine.OwsRecords.Count,
                OwsRecords = _engine.OwsRecords.Select(r => new OwsRecordDto
                {
                    Id = r.Id,
                    Rank = r.Rank,
                    BibNumber = r.BibNumber,
                    SwimmerName = r.SwimmerName,
                    Club = r.Club,
                    FormattedTime = r.FormattedTime,
                    GapTime = r.GapTime,
                    Status = r.Status.ToString()
                }).ToList(),
                Message = $"OWS Finish recorded: #{record.Rank} ({record.FormattedTime}) [Bib {record.BibNumber}]"
            });
        }

        private void HandleEngineOwsRecordStatusChanged(OwsRecordModel record, LaneStatus newStatus)
        {
            Broadcast(CreateStateSyncEvent());
            Log($"[OWS STATUS] Rank #{record.Rank} (BIB {record.BibNumber}) status changed to {newStatus}");
        }

        public void Dispose()
        {
            Stop();
            _engine.RaceStarted -= HandleEngineRaceStarted;
            _engine.RaceStopped -= HandleEngineRaceStopped;
            _engine.RaceReset -= HandleEngineRaceReset;
            _engine.LaneFinished -= HandleEngineLaneFinished;
            _engine.LaneStatusChanged -= HandleEngineLaneStatusChanged;
            _engine.LaneSplitRecorded -= HandleEngineLaneSplitRecorded;
            _engine.Tick -= HandleEngineTick;
            _engine.ModeChanged -= HandleEngineModeChanged;
            _engine.OwsFinishRecorded -= HandleEngineOwsFinishRecorded;
            _engine.OwsRecordStatusChanged -= HandleEngineOwsRecordStatusChanged;
        }
    }
    public class CachedSession
    {
        public ClientRole Role { get; set; } = ClientRole.Unknown;
        public int? AssignedLane { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public bool IsAuthenticated { get; set; }
        public DateTime DisconnectedAt { get; set; } = DateTime.UtcNow;
    }
}
