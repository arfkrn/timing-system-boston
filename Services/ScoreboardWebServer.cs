using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace boston_timing_system.Services
{
    /// <summary>
    /// Lightweight, zero-configuration HTTP Web Server for the Live Scoreboard.
    /// Uses raw Socket/TcpListener bound to IPAddress.Any (0.0.0.0).
    /// This completely avoids Windows HTTP.SYS URL ACL permission requirements
    /// and eliminates the "400 Bad Request - Invalid Hostname" error across localhost and LAN IPs.
    /// Runs asynchronously in the background with zero interference to the main timing engine.
    /// </summary>
    public class ScoreboardWebServer : IDisposable
    {
        private TcpListener? _tcpListener;
        private CancellationTokenSource? _cts;
        private readonly int[] _candidatePorts;
        private readonly Func<int> _getWsPort;
        private readonly Func<string> _getTimingMode;
        private readonly string _assetsDirectory;

        public int Port { get; private set; }
        public string LocalIpAddress { get; }
        public bool IsRunning { get; private set; }
        public string ScoreboardUrl => $"http://{LocalIpAddress}:{Port}/scoreboard.html";

        public event Action<string>? LogReceived;

        public ScoreboardWebServer(Func<int> getWsPort, Func<string> getTimingMode, int[]? candidatePorts = null)
        {
            _getWsPort = getWsPort;
            _getTimingMode = getTimingMode;
            _candidatePorts = candidatePorts ?? new[] { 8088, 8090, 8080, 5000, 3000 };
            LocalIpAddress = NetworkHelper.GetLocalIpAddress(); 

            // Locate Assets/Scoreboard directory next to executable or in project folder
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string probePath = Path.Combine(baseDir, "Assets", "Scoreboard");
            if (!Directory.Exists(probePath))
            {
                // Development fallback
                probePath = Path.Combine(baseDir, "..", "..", "..", "Assets", "Scoreboard");
            }
            _assetsDirectory = Path.GetFullPath(probePath);
        }

        public bool Start()
        {
            if (IsRunning) return true;

            foreach (int port in _candidatePorts)
            {
                try
                {
                    var listener = new TcpListener(IPAddress.Any, port);
                    listener.Start();

                    _tcpListener = listener;
                    Port = port;
                    IsRunning = true;
                    _cts = new CancellationTokenSource();

                    Task.Run(() => AcceptLoopAsync(_tcpListener, _cts.Token));
                    Log($"Scoreboard Web Server started at {ScoreboardUrl}");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[HTTP PORT BUSY] Port {port} unavailable: {ex.Message}");
                }
            }

            Log("[WARNING] Could not bind Scoreboard Web Server to candidate ports.");
            return false;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        Log($"Scoreboard accept error: {ex.Message}");
                    }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true))
            {
                try
                {
                    // Read HTTP Request Line: "GET /scoreboard.html HTTP/1.1"
                    string? requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine)) return;

                    var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) return;

                    string method = parts[0].ToUpperInvariant();
                    string rawUrl = parts[1];

                    // Consume remaining HTTP request headers
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct).ConfigureAwait(false)))
                    {
                        // Drain headers
                    }

                    if (method != "GET" && method != "HEAD")
                    {
                        await SendResponseAsync(stream, 405, "Method Not Allowed", "text/plain", Encoding.UTF8.GetBytes("Method Not Allowed"), method == "HEAD", ct);
                        return;
                    }

                    // Parse path (ignore query string)
                    int queryIdx = rawUrl.IndexOf('?');
                    string path = (queryIdx >= 0 ? rawUrl.Substring(0, queryIdx) : rawUrl).Trim();
                    if (path == "/" || path == "/scoreboard" || path == "/scoreboard/")
                    {
                        path = "/scoreboard.html";
                    }

                    // Dynamic API endpoint
                    if (path.Equals("/api/info", StringComparison.OrdinalIgnoreCase))
                    {
                        var info = new
                        {
                            wsPort = _getWsPort(),
                            ip = LocalIpAddress,
                            mode = _getTimingMode(),
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info));
                        await SendResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", jsonBytes, method == "HEAD", ct, cors: true);
                        return;
                    }

                    // Serve static files from Assets/Scoreboard
                    string filename = Path.GetFileName(path);
                    string filePath = Path.Combine(_assetsDirectory, filename);

                    if (File.Exists(filePath))
                    {
                        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
                        string contentType = GetContentType(filename);
                        await SendResponseAsync(stream, 200, "OK", contentType, bytes, method == "HEAD", ct);
                    }
                    else
                    {
                        byte[] notFound = Encoding.UTF8.GetBytes("404 Not Found - Boston Timing Scoreboard");
                        await SendResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", notFound, method == "HEAD", ct);
                    }
                }
                catch (Exception ex)
                {
                    // Ignore client connection drops or socket reset
                    if (!ct.IsCancellationRequested && !(ex is SocketException) && !(ex is IOException))
                    {
                        Log($"Error serving scoreboard request: {ex.Message}");
                    }
                }
            }
        }

        private static async Task SendResponseAsync(
            Stream stream,
            int statusCode,
            string statusDescription,
            string contentType,
            byte[] body,
            bool isHeadOnly,
            CancellationToken ct,
            bool cors = false)
        {
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {statusDescription}\r\n");
            sb.Append($"Content-Type: {contentType}\r\n");
            sb.Append($"Content-Length: {body.Length}\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("Cache-Control: no-cache, no-store, must-revalidate\r\n");
            if (cors)
            {
                sb.Append("Access-Control-Allow-Origin: *\r\n");
            }
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct).ConfigureAwait(false);

            if (!isHeadOnly && body.Length > 0)
            {
                await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
            }
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        private static string GetContentType(string filename)
        {
            string ext = Path.GetExtension(filename).ToLowerInvariant();
            return ext switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8",
                ".css" => "text/css; charset=utf-8",
                ".js" => "application/javascript; charset=utf-8",
                ".json" => "application/json; charset=utf-8",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
        }

        private void Log(string msg)
        {
            LogReceived?.Invoke(msg);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _tcpListener?.Stop();
            }
            catch { }
            finally
            {
                IsRunning = false;
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}