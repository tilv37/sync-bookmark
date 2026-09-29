using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkSync.Domain;
using BookmarkSync.Store;

namespace BookmarkSync.Server;

/// <summary>
/// API 的请求 / 响应结构。
/// </summary>
/// <remarks>
/// 单独成文件的原因：这些是<b>线上协议的一部分</b>。JSON 字段名一旦定下
/// 就不能随便改（扩展与服务端要配套升级），所以它们值得有自己的位置，
/// 改协议时只动这一个文件。
/// <para>
/// 字段名用短名（state/summary/hlc）而不做重命名，因为它们直接对应
/// state.json 的结构 —— 两边字段名对得上，读日志时不用来回翻译。
/// </para>
/// </remarks>
public sealed class SyncRequest
{
    /// <summary>上报方的标识，仅用于冲突记录与日志排查。</summary>
    [JsonPropertyName("device")]
    [JsonPropertyOrder(0)]
    public string Device { get; init; } = string.Empty;

    /// <summary>客户端本地的完整状态（不是增量）。</summary>
    [JsonPropertyName("state")]
    [JsonPropertyOrder(1)]
    public State State { get; init; } = State.New();

    /// <summary>
    /// 客户端「上次从服务端收到的状态」里每个 key 的 HLC 快照。
    /// 它的唯一用途是区分「我改了」与「对面改了」，从而只记录真正的并发
    /// 冲突。首次同步时为空。见 docs/design.md §5.2。
    /// </summary>
    [JsonPropertyName("base")]
    [JsonPropertyOrder(2)]
    public Dictionary<string, string>? Base { get; init; }
}

/// <summary>POST /api/sync 的响应体。</summary>
public sealed class SyncResponse
{
    /// <summary>合并后的权威状态。客户端应<b>完整替换</b>自己的本地缓存。</summary>
    [JsonPropertyName("state")]
    [JsonPropertyOrder(0)]
    public required State State { get; init; }

    /// <summary>本次合并的统计。</summary>
    [JsonPropertyName("summary")]
    [JsonPropertyOrder(1)]
    public required Summary Summary { get; init; }

    /// <summary>本次新产生的冲突。全量缓冲在 /api/conflicts。</summary>
    [JsonPropertyName("conflicts")]
    [JsonPropertyOrder(2)]
    public required IReadOnlyList<Conflict> Conflicts { get; init; }

    /// <summary>服务端当前时间戳，客户端应用后应调用 HLC.update() 校准时钟。</summary>
    [JsonPropertyName("hlc")]
    [JsonPropertyOrder(3)]
    public required string Hlc { get; init; }

    /// <summary>服务端墙钟毫秒，仅供客户端检测明显异常的本地时钟。</summary>
    [JsonPropertyName("serverTime")]
    [JsonPropertyOrder(4)]
    public long ServerTime { get; init; }
}

/// <summary>GET /api/health 的响应。</summary>
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

/// <summary>GET /api/conflicts 的响应。</summary>
public sealed class ConflictsResponse
{
    [JsonPropertyName("conflicts")]
    [JsonPropertyOrder(0)]
    public required IReadOnlyList<Conflict> Conflicts { get; init; }

    [JsonPropertyName("total")]
    [JsonPropertyOrder(1)]
    public int Total { get; init; }
}

/// <summary>GET /api/history 的响应。</summary>
public sealed class HistoryResponse
{
    [JsonPropertyName("snapshots")]
    [JsonPropertyOrder(0)]
    public required IReadOnlyList<SnapshotInfo> Snapshots { get; init; }
}

/// <summary>所有非 2xx 的统一错误格式。</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("code")]
    [JsonPropertyOrder(0)]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    [JsonPropertyOrder(1)]
    public required string Message { get; init; }
}

/// <summary>错误码（与 docs/design.md §7.6 一致）。</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad_request";
    public const string Unauthorized = "unauthorized";
    public const string PayloadTooBig = "payload_too_large";
    public const string ValidationFailed = "validation_failed";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal_error";
}
