using System;
using Fleck;
using boston_timing_system.Models;

namespace boston_timing_system.Services
{
    public class ConnectedClient
    {
        public IWebSocketConnection Socket { get; }
        public string ClientId { get; } = Guid.NewGuid().ToString("N");
        public string IpAddress { get; }
        public ClientRole Role { get; set; } = ClientRole.Unknown;
        public int? AssignedLane { get; set; }
        public string DeviceName { get; set; } = string.Empty;
        public bool IsAuthenticated { get; set; } = false;
        public DateTime ConnectedAt { get; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Measured Round-Trip Time in milliseconds
        /// </summary>
        public double RoundTripTimeMs { get; set; } = 0.0;

        /// <summary>
        /// Estimated one-way network latency (RTT / 2) in milliseconds
        /// </summary>
        public double OneWayLatencyMs => RoundTripTimeMs > 0 ? RoundTripTimeMs / 2.0 : 0.0;

        public ConnectedClient(IWebSocketConnection socket)
        {
            Socket = socket;
            IpAddress = socket.ConnectionInfo?.ClientIpAddress ?? "Unknown";
        }

        public void UpdateActivity()
        {
            LastSeenUtc = DateTime.UtcNow;
        }

        public void RecordLatencyMeasurement(double latencyMs)
        {
            // Exponential moving average to smooth out latency jitter
            if (RoundTripTimeMs <= 0)
            {
                RoundTripTimeMs = latencyMs * 2.0;
            }
            else
            {
                RoundTripTimeMs = (RoundTripTimeMs * 0.7) + ((latencyMs * 2.0) * 0.3);
            }
            UpdateActivity();
        }
    }
}
