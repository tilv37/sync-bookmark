using BookmarkSync.Domain;

namespace BookmarkSync.Store;

/// <summary>
/// Configuration required by <see cref="BookmarkStore"/>.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> reusing the HTTP-layer <c>ServerOptions</c>: the
/// persistence layer must not depend on the HTTP layer (the arrow points
/// Server → Store). Store needs only three fields; the extra Token / Addr on
/// the HTTP side are meaningless here, so a minimal dedicated type is honest.
/// </remarks>
public sealed class StoreOptions
{
    /// <summary>Directory holding state.json / conflicts.json / history/.</summary>
    public required string DataDir { get; init; }

    /// <summary>Number of history snapshots to keep.</summary>
    public int HistoryKeep { get; init; } = 30;

    /// <summary>Tombstone retention; expired entries are removed by GC.</summary>
    public TimeSpan TombstoneTtl { get; init; } = TimeSpan.FromDays(90);
}

/// <summary>Metadata for one history snapshot.</summary>
/// <remarks>
/// Lives in Store, not the HTTP layer: it describes "what that file on disk
/// is", a storage-layer fact. HTTP only serializes it.
/// </remarks>
public sealed record SnapshotInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    [System.Text.Json.Serialization.JsonPropertyOrder(0)]
    public required string Id { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("at")]
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public long At { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("items")]
    [System.Text.Json.Serialization.JsonPropertyOrder(2)]
    public int Items { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("size")]
    [System.Text.Json.Serialization.JsonPropertyOrder(3)]
    public long Size { get; init; }
}
