using System.Text.Json;
using BookmarkSync.Domain;
using BookmarkSync.Store;

namespace BookmarkSync.Server;

/// <summary>
/// Handlers for the four endpoints.
/// </summary>
/// <remarks>
/// Each handler is thin: parse request → call store → write response. All real
/// decisions live in Domain (merge) and Store (persistence); no business logic here.
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
    /// POST /api/sync — core endpoint: the client uploads its local state and the server merges and returns the authoritative state.
    /// </summary>
    /// <remarks>
    /// Key invariant: no non-200 response may cause the client to modify local bookmarks.
    /// Error paths here only write the HTTP response and touch no state — clients rely
    /// on that to guarantee "zero local bookmark changes on failed sync". See docs/architecture.md §4.
    /// </remarks>
    public async Task HandleSyncAsync(HttpContext ctx)
    {
        (bool ok, bool tooLarge, byte[]? body) =
            await HttpJson.ReadBodyAsync(ctx, HttpLimits.MaxBodyBytes).ConfigureAwait(false);

        if (tooLarge)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge,
                ErrorCodes.PayloadTooBig, "Bookmark data exceeds the limit, check for abnormal entries").ConfigureAwait(false);
            return;
        }

        if (!ok)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest, "Failed to read request body").ConfigureAwait(false);
            return;
        }

        if (body is null || body.Length == 0)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest, "Request body is empty").ConfigureAwait(false);
            return;
        }

        SyncRequest req;
        try
        {
            // Use the JsonTypeInfo overload (ApiJson.ParseRequest) instead of
            // JsonSerializer.Deserialize<SyncRequest>(body, options): the latter carries
            // [RequiresDynamicCode] and fails PublishAot with IL3050.
            //
            // Strictness for unknown fields comes from ApiJsonContext's generation config —
            // see the Converters-priority note in ApiJsonContext.cs.
            req = ApiJson.ParseRequest(body) ?? throw new JsonException("parse result was null");
        }
        catch (JsonException ex)
        {
            // Unknown fields land here in strict mode. Include the field name in the
            // message: a misspelled field name is the most common and most easily
            // self-fixable failure class.
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.BadRequest, "Failed to parse request body: " + ex.Message).ConfigureAwait(false);
            return;
        }

        State incoming = req.State;
        if (incoming.V != Schema.Version)
        {
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ErrorCodes.BadRequest,
                "State schema version mismatch, check that the extension and server versions match").ConfigureAwait(false);
            return;
        }

        // Older extensions may omit the items field.
        if (incoming.Items is null)
        {
            incoming = new State { V = incoming.V, Clock = incoming.Clock, Items = State.NewItemsDictionary() };
        }

        // Hard errors (key format, type, HLC, over-limit) are rejected outright — such
        // input is corrupt or incompatible, and letting it into the merge only spreads
        // the damage. Structural issues (missing parents, parent cycles) are only
        // logged: rejecting a whole upload over one orphaned item is far worse for
        // users than skipping that item.
        ValidationResult validation = incoming.Validate();
        if (!validation.IsValid)
        {
            _log.LogWarning("Rejecting invalid upload {Device} {Err}", req.Device, validation.Error);
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed, "Bookmark validation failed: " + validation.Error).ConfigureAwait(false);
            return;
        }

        foreach (string warn in validation.Warnings)
        {
            _log.LogWarning("Upload has structural issues {Device} {Warn}", req.Device, warn);
        }

        MergeResult res;
        try
        {
            res = await _store.SyncAsync(incoming, req.Base, req.Device).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync failed {Device}", req.Device);
            await HttpJson.WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError,
                ErrorCodes.Internal, "Server failed to process the request, no data was modified").ConfigureAwait(false);
            return;
        }

        _log.LogInformation(
            "Sync completed {Device} created={Created} updated={Updated} deleted={Deleted} " +
            "unchanged={Unchanged} conflicts={Conflicts} items={Items}",
            req.Device, res.Summary.Created, res.Summary.Updated, res.Summary.Deleted,
            res.Summary.Unchanged, res.Conflicts.Count, res.State.Items.Count);

        await HttpJson.WriteJsonAsync(
            ctx, StatusCodes.Status200OK,
            new SyncResponse
            {
                State = res.State,
                Summary = res.Summary,
                // Encode as [] rather than null so clients never need a null check.
                Conflicts = res.Conflicts.Count == 0 ? [] : res.Conflicts,
                Hlc = res.State.Clock,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            ApiJson.TypeInfoOf<SyncResponse>()).ConfigureAwait(false);
    }

    /// <summary>GET /api/health — connectivity and basic stats. <b>Unauthenticated</b>; returns no bookmark content.</summary>
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

    /// <summary>GET /api/conflicts — conflict ring buffer. Defaults to 50 entries.</summary>
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

    /// <summary>GET /api/history — history snapshot list (read-only, no rollback).</summary>
    public async Task HandleHistoryAsync(HttpContext ctx)
    {
        IReadOnlyList<SnapshotInfo> snaps = await _store.ListSnapshotsAsync().ConfigureAwait(false);
        await HttpJson.WriteJsonAsync(
            ctx, StatusCodes.Status200OK,
            new HistoryResponse { Snapshots = snaps },
            ApiJson.TypeInfoOf<HistoryResponse>()).ConfigureAwait(false);
    }
}
