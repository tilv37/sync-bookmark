using BookmarkSync.Domain;

namespace BookmarkSync.Store;

/// <summary>
/// <see cref="BookmarkStore"/> 需要的配置。
/// </summary>
/// <remarks>
/// 刻意<b>不复用</b> HTTP 层的 <c>ServerOptions</c>：持久化层不该依赖 HTTP 层，
/// 那会让依赖方向反过来（Server → Store 才对）。而且 Store 其实只需要 3 个字段，
/// 声明一个自己的最小配置更诚实 —— HTTP 那边多出来的 Token / Addr
/// 对存储层毫无意义。
/// </remarks>
public sealed class StoreOptions
{
    /// <summary>state.json / conflicts.json / history/ 所在目录。</summary>
    public required string DataDir { get; init; }

    /// <summary>保留的历史快照份数。</summary>
    public int HistoryKeep { get; init; } = 30;

    /// <summary>墓碑（已删除项）的保留时长，超期后由 GC 清理。</summary>
    public TimeSpan TombstoneTtl { get; init; } = TimeSpan.FromDays(90);
}

/// <summary>一个历史快照的元信息。</summary>
/// <remarks>
/// 放在 Store 而不是 HTTP 层：它描述的是"磁盘上那个文件是什么"，
/// 属于存储层的事实。HTTP 层只是把它序列化出去。
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
