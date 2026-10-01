using System.Net;
using System.Text.Json;
using BookmarkSync.Domain;

namespace BookmarkSync.Server.Tests;

/// <summary>
/// API endpoint tests, mirroring the HTTP endpoint test cases.
/// </summary>
/// <remarks>
/// All go through the real pipeline, never handlers in isolation — see
/// <see cref="ServerFixture"/> for why.
/// </remarks>
public class ApiEndpointTests
{
    [Fact]
    public async Task HealthSkipsAuth()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        // The options page "test connection" must confirm liveness before the token is entered
        (HttpStatusCode status, string body) = await f.GetAsync("/api/health", withToken: false);

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("bmsync", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal(Schema.Version, doc.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public async Task HealthResponseContentTypeIncludesCharset()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        using HttpRequestMessage req = f.Request(HttpMethod.Get, "/api/health", withToken: false);
        using HttpResponseMessage resp = await f.Client.SendAsync(req);

        // The extension reads via response.json(); a missing charset fails with a misleading
        // "Unexpected token" error
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", resp.Content.Headers.ContentType?.CharSet);
    }

    [Fact]
    public async Task SyncRequiresValidToken()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();
        SyncRequest good = Fx.SyncBody(Fx.Items((Fx.Key1, Fx.Bookmark(RootFolders.Toolbar, "t", "https://x.example"))));

        (HttpStatusCode noToken, string _) = await f.PostSyncAsync(good, withToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken);

        (HttpStatusCode badToken, string body) = await f.PostSyncAsync(good, token: "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, badToken);
        Assert.Contains("unauthorized", body, StringComparison.Ordinal);

        (HttpStatusCode ok, _) = await f.PostSyncAsync(good);
        Assert.Equal(HttpStatusCode.OK, ok);
    }

    [Fact]
    public async Task BearerPrefixIsCaseInsensitive()
    {
        // RFC 7235 schemes are case-insensitive. Some HTTP clients lowercase it
        await using ServerFixture f = await ServerFixture.StartAsync();
        SyncRequest body = Fx.SyncBody(Fx.Items((Fx.Key1, Fx.Bookmark(RootFolders.Toolbar, "t", "https://x.example"))));

        using HttpRequestMessage req = new(HttpMethod.Post, "/api/sync")
        {
            // ApiJson.Response: SyncRequest is a Server type that DomainJsonContext behind BmsyncJson
            // does not know (see the Fx.SyncBody note)
            Content = new StringContent(
                JsonSerializer.Serialize(body, ApiJson.Response),
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"bearer {ServerFixture.Token}");

        using HttpResponseMessage resp = await f.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task SyncAcceptsAndReturnsState()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        SyncRequest body = Fx.SyncBody(Fx.Items(
            (Fx.Key1, Fx.Folder(RootFolders.Toolbar, "工作")),
            (Fx.Key2, Fx.Bookmark(Fx.Key1, "示例", "https://example.com", 100, 1))));

        (HttpStatusCode status, string raw) = await f.PostSyncAsync(body);
        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement items = doc.RootElement.GetProperty("state").GetProperty("items");

        // Both items must be present under exactly the uploaded keys.
        // Not written as an inline ternary like "items.GetProperty(Key1).ValueKind == Object ? Count() : 0"
        // — that silently yields 0 on a missing key, so the failure reads as the self-contradictory
        // "expected 0, actual 0" and costs an extra triage lap.
        Assert.Equal(2, items.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Object, items.GetProperty(Fx.Key1).ValueKind);
        Assert.Equal(JsonValueKind.Object, items.GetProperty(Fx.Key2).ValueKind);

        // Folders carry no url field (Go omitempty equivalent)
        Assert.False(items.GetProperty(Fx.Key1).TryGetProperty("u", out _));

        JsonElement summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("created").GetInt32());

        // Conflict counts live at the **response top level** (conflicts array + total), not in summary.
        // The Go Summary struct has only created/updated/deleted/unchanged.
        // Asserting summary.conflicts here was my earlier field-position mistake —
        // surfacing as KeyNotFoundException with no hint that the field position was wrong.
        Assert.Empty(doc.RootElement.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task SyncRejectsEmptyBody()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(null, rawBody: "");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Request body is empty", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncRejectsMalformedJson()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(null, rawBody: "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("bad_request", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncRejectsUnknownFields()
    {
        // A client with a misspelled field must get a clear error at once, not silent misbehavior
        // (e.g. device typed as devcie, forever treated as the default device with nobody suspecting a typo)
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(
            null, rawBody: """{"devcie":"typo","state":{"v":1,"items":{}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("devcie", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncRejectsTrailingContent()
    {
        // Reject a second JSON value: usually a client bug, possibly an attack probe
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, _) = await f.PostSyncAsync(
            null, rawBody: """{"device":"a","state":{"v":1,"items":{}}}{"extra":1}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task SyncRejectsSchemaVersionMismatch()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(
            null, rawBody: """{"device":"a","state":{"v":99,"items":{}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("schema", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncRejectsInvalidItem()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        // A bookmark missing url — a hard error, must be rejected.
        //
        // Deliberately plain string + Replace instead of an interpolated raw string:
        // "{{" and a trailing "}}}" parse as escaped braces plus interpolation end,
        // and the CS9007 error points nowhere near the real problem. JSON is full of braces,
        // and $$ does not save it — whether trailing }}} is two escapes plus content or
        // three contents depends on the $ count, which is brittle.
        string bad = """
            {"device":"a","state":{"v":1,"items":{
              "@KEY@":{"p":"toolbar_____","t":"b","n":"x","m":"0000000000100-00000","a":"0000000000100-00000"}
            }}}
            """.Replace("@KEY@", Fx.Key1, StringComparison.Ordinal);

        (HttpStatusCode status, string body) = await f.PostSyncAsync(null, rawBody: bad);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("validation_failed", body, StringComparison.Ordinal);
        Assert.Contains("must have url", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncAcceptsStructuralIssuesWithWarning()
    {
        // A missing parent is only a soft warning: rejecting a whole upload over one orphaned item
        // is far worse for users than skipping that item
        await using ServerFixture f = await ServerFixture.StartAsync();

        // The parent key points at a nonexistent 32-hex key — triggering the "parent does not exist" warning
        string orphan = """
            {"device":"a","state":{"v":1,"items":{
              "@KEY@":{"p":"99999999999999999999999999999999","t":"b","n":"x","u":"https://x.example","m":"0000000000100-00000","a":"0000000000100-00000"}
            }}}
            """.Replace("@KEY@", Fx.Key1, StringComparison.Ordinal);

        (HttpStatusCode status, _) = await f.PostSyncAsync(null, rawBody: orphan);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task ConflictsDefaultLimitFifty()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.GetAsync("/api/conflicts");

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("conflicts").ValueKind);
    }

    [Fact]
    public async Task HistoryReturnsEmptyList()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.GetAsync("/api/history");

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("snapshots").GetArrayLength());
    }

    [Fact]
    public async Task AllDataEndpointsRequireToken()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        foreach (string path in new[] { "/api/conflicts", "/api/history" })
        {
            (HttpStatusCode status, _) = await f.GetAsync(path, withToken: false);
            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }

        (HttpStatusCode sync, _) = await f.PostSyncAsync(
            Fx.SyncBody(Fx.Items()), withToken: false);
        Assert.Equal(HttpStatusCode.Unauthorized, sync);
    }

    [Fact]
    public async Task OverLimitReturns429WithRetryAfter()
    {
        await using ServerFixture f = await ServerFixture.StartAsync(rateLimitPerMinute: 3);

        for (int i = 0; i < 3; i++)
        {
            (HttpStatusCode status, _) = await f.PostSyncAsync(Fx.SyncBody(Fx.Items()));
            Assert.Equal(HttpStatusCode.OK, status);
        }

        using HttpRequestMessage req = f.Request(HttpMethod.Post, "/api/sync");
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await f.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal("60", resp.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
    }

    [Fact]
    public async Task RateLimitRunsBeforeAuth()
    {
        // Order matters: rate limit before auth. Otherwise someone with a wrong token could use
        // the 429/401 difference to tell whether a token exists.
        // Exhaust the quota here, then send a wrong token and expect 429, not 401.
        await using ServerFixture f = await ServerFixture.StartAsync(rateLimitPerMinute: 1);

        (HttpStatusCode first, _) = await f.GetAsync("/api/conflicts", token: ServerFixture.Token);
        Assert.Equal(HttpStatusCode.OK, first);

        (HttpStatusCode second, _) = await f.GetAsync("/api/conflicts", token: "wrong");
        Assert.Equal(HttpStatusCode.TooManyRequests, second);
    }

    [Fact]
    public async Task ResponseKeepsChineseUnescaped()
    {
        // The default JSON encoder writes CJK as \uXXXX, 6x the size.
        // 5000 bookmarks grow from ~800KB to 4MB and hit nginx client_max_body_size.
        await using ServerFixture f = await ServerFixture.StartAsync();

        SyncRequest body = Fx.SyncBody(Fx.Items(
            (Fx.Key1, Fx.Bookmark(RootFolders.Toolbar, "中文标题", "https://x.example"))));

        (_, string raw) = await f.PostSyncAsync(body);

        Assert.Contains("中文标题", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", raw, StringComparison.Ordinal);
    }
}
