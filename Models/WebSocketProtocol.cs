using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace boston_timing_system.Models
{
    public enum ClientRole
    {
        Unknown,
        Starter,
        Referee,
        Chief,
        Spectator,
        Admin
    }

    public class ClientCommand
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("laneNumber")]
        public int? LaneNumber { get; set; }

        [JsonPropertyName("deviceName")]
        public string? DeviceName { get; set; }

        /// <summary>
        /// Local Unix epoch timestamp (ms) sent by React Native client
        /// </summary>
        [JsonPropertyName("clientTimestamp")]
        public long? ClientTimestamp { get; set; }

        /// <summary>
        /// Exact race elapsed time in milliseconds captured locally by client stopwatch when button was pressed (crucial for offline queue)
        /// </summary>
        [JsonPropertyName("elapsedTimeMs")]
        public long? ElapsedTimeMs { get; set; }

        /// <summary>
        /// One-way latency in milliseconds estimated by client from ping-pong RTT
        /// </summary>
        [JsonPropertyName("estimatedLatencyMs")]
        public double? EstimatedLatencyMs { get; set; }

        /// <summary>
        /// 4-digit access code for authentication
        /// </summary>
        [JsonPropertyName("accessCode")]
        public string? AccessCode { get; set; }

        [JsonPropertyName("bibNumber")]
        public string? BibNumber { get; set; }

        [JsonPropertyName("swimmerName")]
        public string? SwimmerName { get; set; }

        [JsonPropertyName("club")]
        public string? Club { get; set; }

        [JsonPropertyName("rank")]
        public int? Rank { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("previousBibNumber")]
        public string? PreviousBibNumber { get; set; }

        [JsonPropertyName("formattedTime")]
        public string? FormattedTime { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }
    }

    public class ServerEvent
    {
        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("timingMode")]
        public string? TimingMode { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("elapsedTime")]
        public string? ElapsedTime { get; set; }

        [JsonPropertyName("laneNumber")]
        public int? LaneNumber { get; set; }

        [JsonPropertyName("finishTime")]
        public string? FinishTime { get; set; }

        [JsonPropertyName("splitTime")]
        public string? SplitTime { get; set; }

        [JsonPropertyName("timestamp")]
        public long? Timestamp { get; set; }

        /// <summary>
        /// Echoed client timestamp from PING
        /// </summary>
        [JsonPropertyName("clientTimestamp")]
        public long? ClientTimestamp { get; set; }

        /// <summary>
        /// Server Unix timestamp (ms) when PONG is emitted
        /// </summary>
        [JsonPropertyName("serverTimestamp")]
        public long? ServerTimestamp { get; set; }

        /// <summary>
        /// Compensated latency applied to the action in milliseconds
        /// </summary>
        [JsonPropertyName("compensatedLatencyMs")]
        public double? CompensatedLatencyMs { get; set; }

        [JsonPropertyName("lanes")]
        public List<LaneStateDto>? Lanes { get; set; }

        [JsonPropertyName("owsRecords")]
        public List<OwsRecordDto>? OwsRecords { get; set; }

        [JsonPropertyName("owsFinisherCount")]
        public int? OwsFinisherCount { get; set; }

        [JsonPropertyName("meetName")]
        public string? MeetName { get; set; }

        [JsonPropertyName("eventNumber")]
        public int? EventNumber { get; set; }

        [JsonPropertyName("eventName")]
        public string? EventName { get; set; }

        [JsonPropertyName("heatNumber")]
        public int? HeatNumber { get; set; }

        [JsonPropertyName("connectedClients")]
        public ClientSummaryDto? ConnectedClients { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("accessCode")]
        public string? AccessCode { get; set; }
    }

    public class LaneStateDto
    {
        [JsonPropertyName("laneNumber")]
        public int LaneNumber { get; set; }

        [JsonPropertyName("swimmerName")]
        public string SwimmerName { get; set; } = string.Empty;

        [JsonPropertyName("club")]
        public string Club { get; set; } = string.Empty;

        [JsonPropertyName("seedTime")]
        public string SeedTime { get; set; } = "--:--.--";

        [JsonPropertyName("rank")]
        public int? Rank { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("formattedTime")]
        public string FormattedTime { get; set; } = "00:00.00";

        [JsonPropertyName("isRefereeConnected")]
        public bool IsRefereeConnected { get; set; }

        [JsonPropertyName("isChiefConnected")]
        public bool IsChiefConnected { get; set; }

        [JsonPropertyName("latencyMs")]
        public double LatencyMs { get; set; }
    }

    public class ClientSummaryDto
    {
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }

        [JsonPropertyName("startersCount")]
        public int StartersCount { get; set; }

        [JsonPropertyName("refereesCount")]
        public int RefereesCount { get; set; }

        [JsonPropertyName("chiefsCount")]
        public int ChiefsCount { get; set; }

        [JsonPropertyName("spectatorsCount")]
        public int SpectatorsCount { get; set; }

        [JsonPropertyName("averageLatencyMs")]
        public double AverageLatencyMs { get; set; }
    }

    public class OwsRecordDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("rank")]
        public int Rank { get; set; }

        [JsonPropertyName("bibNumber")]
        public string BibNumber { get; set; } = string.Empty;

        [JsonPropertyName("swimmerName")]
        public string SwimmerName { get; set; } = string.Empty;

        [JsonPropertyName("club")]
        public string Club { get; set; } = string.Empty;

        [JsonPropertyName("formattedTime")]
        public string FormattedTime { get; set; } = "00:00.00";

        [JsonPropertyName("gapTime")]
        public string GapTime { get; set; } = "+00:00.00";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "Finished";
    }
}
