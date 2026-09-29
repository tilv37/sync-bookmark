using System.Text.Json;
using BookmarkSync.Domain;
using BookmarkSync.Store;

namespace BookmarkSync.Server;

/// <summary>
/// 四个端点的处理器。
/// </summary>
/// <remarks>
/// 每个处理器都很薄：解析请求 → 调 store → 写响应。真正的决策都在
/// Domain（合并）和 Store（落盘）里，这里不做业务判断。
/// </remarks>
public sealed class ApiEndpoints
{
    private readonly BookmarkStore _store;
    private readonly ILogger<ApiEndpoints> _log;
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;

    public ApiEndpoints(BookmarkStore store, ILogger<ApiEndpoints> log)
    {
        _store = store;
        _log = log;
    }

    /// <summary>
    /// POST /api/sync —— 核心端点：客户端上报本地状态，服务端合并后返回权威状态。
    /// </summary>
    /// <remarks>
    /// ★ 关键不变量：任何非 200 响应都不得让客户端修改本地书签。
    ///   这里的错误路径只写 HTTP 响应，不碰任何状态 —— 客户端据此保证
    ///   "同步失败时本地书签零变化"。见 docs/design.md §9.6。
    /// </remarks>
    public async Task HandleSyncAsync(HttpContext ctx)
    {
        (bool ok, bool tooLarge, byte[]? body) =
            await HttpJson.ReadBodyAsync(ctx, HttpLimits.MaxBodyBytes).ConfigureAwait(false);

        if (tooLarge)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge,
                ErrorCodes.PayloadTooBig, "书签数据超过上限，请检查是否有异常条目").ConfigureAwait(false);
            return;
        }

        if (!ok)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest, "读取请求体失败").ConfigureAwait(false);
            return;
        }

        if (body is null || body.Length == 0)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest, "请求体为空").ConfigureAwait(false);
            return;
        }

        SyncRequest req;
        try
        {
            // 走 JsonTypeInfo 重载（ApiJson.ParseRequest）而不是
            // JsonSerializer.Deserialize<SyncRequest>(body, options)：
            // 后者带 [RequiresDynamicCode]，PublishAot 下报 IL3050。
            //
            // 未知字段的严格性由 ApiJsonContext 的生成配置保证 ——
            // 见 ApiJsonContext.cs 里关于 Converters 优先级的说明。
            req = ApiJson.ParseRequest(body) ?? throw new JsonException("解析结果为 null");
        }
        catch (JsonException ex)
        {
            // 严格模式下未知字段会走到这里。错误信息里带上字段名，
            // 因为"字段名拼错"是最常见也最容易自行修复的一类问题。
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest, "请求体解析失败: " + ex.Message).ConfigureAwait(false);
            return;
        }

        State incoming = req.State;
        if (incoming.V != Schema.Version)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "state 的 schema 版本不匹配，请确认扩展与服务端版本配套").ConfigureAwait(false);
            return;
        }

        // 老版本扩展可能漏发 items 字段
        if (incoming.Items is null)
        {
            incoming = new State { V = incoming.V, Clock = incoming.Clock, Items = State.NewItemsDictionary() };
        }

        // 硬错误（key 格式、类型、HLC、超限额）直接拒绝 —— 这类输入说明数据
        // 损坏或格式不兼容，放行只会把问题带进合并。
        // 结构性问题（父节点缺失、父链成环）只记日志不拒绝：为一个孤立节点
        // 拒掉整份上传，对用户来说比跳过那个节点糟糕得多。
        ValidationResult validation = incoming.Validate();
        if (!validation.IsValid)
        {
            _log.LogWarning("拒绝不合法的上传 {Device} {Err}", req.Device, validation.Error);
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed, "书签数据校验失败: " + validation.Error).ConfigureAwait(false);
            return;
        }

        foreach (string warn in validation.Warnings)
        {
            _log.LogWarning("上传数据存在结构问题 {Device} {Warn}", req.Device, warn);
        }

        MergeResult res;
        try
        {
            res = await _store.SyncAsync(incoming, req.Base, req.Device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "同步失败 {Device}", req.Device);
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError,
                ErrorCodes.Internal, "服务端处理失败，本次未修改任何数据").ConfigureAwait(false);
            return;
        }

        _log.LogInformation(
            "同步完成 {Device} created={Created} updated={Updated} deleted={Deleted} " +
            "unchanged={Unchanged} conflicts={Conflicts} items={Items}",
            req.Device, res.Summary.Created, res.Summary.Updated, res.Summary.Deleted,
            res.Summary.Unchanged, res.Conflicts.Count, res.State.Items.Count);

        await HttpJson.WriteJsonAsync(
            ctx, StatusCodes.Status200OK,
            new SyncResponse
            {
                State = res.State,
                Summary = res.Summary,
                // 编码成 [] 而不是 null，省得客户端每次都判空
                Conflicts = res.Conflicts.Count == 0 ? [] : res.Conflicts,
                Hlc = res.State.Clock,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ApiJson.TypeInfoOf<SyncResponse>()).ConfigureAwait(false);
    }

    /// <summary>GET /api/health —— 连通性与基本统计。<b>免鉴权</b>，且不返回任何书签内容。</summary>
    public async Task HandleHealthAsync(HttpContext ctx)
    {
        await HttpJson.WriteJsonAsync(
            ctx, StatusCodes.Status200OK,
            new HealthResponse
            {
                Ok = true,
                Service = ServerOptions.ServiceName,
                Schema = Schema.Version,
                Items = await _store.GetItemCountAsync().ConfigureAwait(false),
                Active = await _store.GetActiveCountAsync().ConfigureAwait(false),
                Conflicts = (await _store.GetConflictsAsync(0).ConfigureAwait(false)).Count,
                Uptime = (long)(DateTimeOffset.UtcNow - _start).TotalSeconds,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ApiJson.TypeInfoOf<HealthResponse>()).ConfigureAwait(false);
    }

    /// <summary>GET /api/conflicts —— 冲突环形缓冲。默认 50 条。</summary>
    public async Task HandleConflictsAsync(HttpContext ctx)
    {
        int limit = 50;
        string? raw = ctx.Request.Query["limit"];
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out int n) && n > 0)
        {
            limit = n;
        }

        IReadOnlyList<Conflict> all = await _store.GetConflictsAsync(limit).ConfigureAwait(false);
        await HttpJson.WriteJsonAsync(
            ctx, StatusCodes.Status200OK,
            new ConflictsResponse
            {
                Conflicts = all,
                Total = all.Count,
            },
            ApiJson.TypeInfoOf<ConflictsResponse>()).ConfigureAwait(false);
    }

    /// <summary>GET /api/history —— 历史快照列表（只读，不提供回滚）。</summary>
    public async Task HandleHistoryAsync(HttpContext ctx)
    {
        IReadOnlyList<SnapshotInfo> snaps = await _store.ListSnapshotsAsync().ConfigureAwait(false);
        await HttpJson.WriteJsonAsync(
            ctx, StatusCodes.Status200OK,
            new HistoryResponse { Snapshots = snaps },
            ApiJson.TypeInfoOf<HistoryResponse>()).ConfigureAwait(false);
    }
}
