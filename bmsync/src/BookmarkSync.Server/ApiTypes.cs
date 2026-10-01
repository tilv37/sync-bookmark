using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkSync.Domain;
using BookmarkSync.Store;

namespace BookmarkSync.Server;

/// <summary>
/// API request / response shapes.
/// </summary>
/// <remarks>
/// Kept in its own file because these are <b>part of the live protocol</b>. Once JSON
/// field names are set they cannot change freely (extension and server upgrade together),
/// so protocol changes touch only this file.
/// <para>
/// Short field names (state/summary/hlc) with no renames: they mirror the
/// state.json structure, so logs need no mental translation between the two.
/// </para>
/// </remarks>
public sealed class SyncRequest
{
    /// <summary>Uploader identity, only for conflict records and log triage.</summary>
    [JsonPropertyName("device")]
    [JsonPropertyOrder(0)]
    public string Device { get; init; } = string.Empty;

    /// <summary>Full client-side local state (not a delta).</summary>
    [JsonPropertyName("state")]
    [JsonPropertyOrder(1)]
    public State State { get; init; } = State.New();

    /// <summary>
    /// Per-key HLC snapshot of the "last state received from the server".
    /// Its only purpose is telling "I changed it" apart from "the other side changed it",
    /// so only true concurrent conflicts are recorded. Empty on first sync. See docs/architecture.md §4.
    /// </summary>
    [JsonPropertyName("base")]
    [JsonPropertyOrder(2)]
    public Dictionary<string, string>? Base { get; init; }
}

/// <summary>POST /api/sync response body.</summary>
public sealed class SyncResponse
{
    /// <summary>Merged authoritative state. Clients should <b>fully replace</b> their local cache.</summary>
    [JsonPropertyName("state")]
    [JsonPropertyOrder(0)]
    public required State State { get; init; }

    /// <summary>Stats for this merge.</summary>
    [JsonPropertyName("summary")]
    [JsonPropertyOrder(1)]
    public required Summary Summary { get; init; }

    /// <summary>Conflicts newly produced by this merge. Full buffer at /api/conflicts.</summary>
    [JsonPropertyName("conflicts")]
    [JsonPropertyOrder(2)]
    public required IReadOnlyList<Conflict> Conflicts { get; init; }

    /// <summary>Current server timestamp; clients should call HLC.update() after applying it.</summary>
    [JsonPropertyName("hlc")]
    [JsonPropertyOrder(3)]
    public required string Hlc { get; init; }

    /// <summary>Server wall-clock millis, only for clients to spot badly skewed local clocks.</summary>
    [JsonPropertyName("serverTime")]
    [JsonPropertyOrder(4)]
    public long ServerTime { get; init; }
}

/// <summary>GET /api/health response.</summary>
public sealed class HealthResponse
{
    [JsonPropertyName("ok")]
    [JsonPropertyOrder(0)]
    public bool Ok { get; init; }

    [JsonPropertyName("service")]
    [JsonPropertyOrder(1)]
    public string Service { get; init; } = string.Empty;

    [JsonPropertyName("schema")]
    [JsonPropertyOrder(2)]
    public int Schema { get; init; }

    [JsonPropertyName("items")]
    [JsonPropertyOrder(3)]
    public int Items { get; init; }

    [JsonPropertyName("active")]
    [JsonPropertyOrder(4)]
    public int Active { get; init; }

    [JsonPropertyName("conflicts")]
    [JsonPropertyOrder(5)]
    public int Conflicts { get; init; }

    [JsonPropertyName("uptime")]
    [JsonPropertyOrder(6)]
    public long Uptime { get; init; }

    [JsonPropertyName("serverTime")]
    [JsonPropertyOrder(7)]
    public long ServerTime { get; init; }
}

/// <summary>GET /api/conflicts response.</summary>
public sealed class ConflictsResponse
{
    [JsonPropertyName("conflicts")]
    [JsonPropertyOrder(0)]
    public required IReadOnlyList<Conflict> Conflicts { get; init; }

    [JsonPropertyName("total")]
    [JsonPropertyOrder(1)]
    public int Total { get; init; }
}

/// <summary>GET /api/history response.</summary>
public sealed class HistoryResponse
{
    [JsonPropertyName("snapshots")]
    [JsonPropertyOrder(0)]
    public required IReadOnlyList<SnapshotInfo> Snapshots { get; init; }
}

/// <summary>Unified error shape for all non-2xx responses.</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("code")]
    [JsonPropertyOrder(0)]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(1)]
    public required string Message { get; init; }
}

/// <summary>Error codes (matching docs/architecture.md §8).</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string Unauthorized = "unauthorized";
    public const string PayloadTooBig = "payload_too_large";
    public const string ValidationFailed = "validation_failed";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal_error";
}
