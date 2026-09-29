using System.Net;
using System.Text.Json;
using BookmarkSync.Domain;

namespace BookmarkSync.Server.Tests;

/// <summary>
/// API 端点测试，对应 HTTP 端点的测试用例（同上）。
/// </summary>
/// <remarks>
/// 全部走真实管道（TestServer），不是单独调处理器 —— 理由见
/// <see cref="ServerFixture"/> 的说明。
/// </remarks>
public class ApiEndpointTests
{
    [Fact]
    public async Task health免鉴权()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        // options 页的"测试连接"要在用户填完令牌前就能确认服务活着
        (HttpStatusCode status, string body) = await f.GetAsync("/api/health", withToken: false);

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("bmsync", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal(Schema.Version, doc.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public async Task health响应ContentType带charset()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        using HttpRequestMessage req = f.Request(HttpMethod.Get, "/api/health", withToken: false);
        using HttpResponseMessage resp = await f.Client.SendAsync(req);

        // 扩展是按 response.json() 读的，charset 消失会给出
        // "Unexpected token" 这种指向错误方向的报错
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", resp.Content.Headers.ContentType?.CharSet);
    }

    [Fact]
    public async Task sync需要有效token()
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
    public async Task bearer前缀大小写不敏感()
    {
        // RFC 7235 的 scheme 是 case-insensitive。有些 HTTP 客户端会小写
        await using ServerFixture f = await ServerFixture.StartAsync();
        SyncRequest body = Fx.SyncBody(Fx.Items((Fx.Key1, Fx.Bookmark(RootFolders.Toolbar, "t", "https://x.example"))));

        using HttpRequestMessage req = new(HttpMethod.Post, "/api/sync")
        {
            // ApiJson.Response：SyncRequest 是 Server 的类型，BmsyncJson 挂的
            // DomainJsonContext 不认它（详见 Fx.SyncBody 的注释）
            Content = new StringContent(
                JsonSerializer.Serialize(body, ApiJson.Response),
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"bearer {ServerFixture.Token}");

        using HttpResponseMessage resp = await f.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task sync接受并返回状态()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        SyncRequest body = Fx.SyncBody(Fx.Items(
            (Fx.Key1, Fx.Folder(RootFolders.Toolbar, "工作")),
            (Fx.Key2, Fx.Bookmark(Fx.Key1, "示例", "https://example.com", 100, 1))));

        (HttpStatusCode status, string raw) = await f.PostSyncAsync(body);
        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(raw);
        JsonElement items = doc.RootElement.GetProperty("state").GetProperty("items");

        // 两个 item 都要在，且 key 就是我们上传的那两个。
        // 不写成 "items.GetProperty(Key1).ValueKind == Object ? Count() : 0" 这种
        // 内联三元 —— 它在 key 缺失时会静默返回 0，于是断言失败的原因是
        // "期望 0 实际 0" 这种自相矛盾的东西，排查要多绕一圈。
        Assert.Equal(2, items.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Object, items.GetProperty(Fx.Key1).ValueKind);
        Assert.Equal(JsonValueKind.Object, items.GetProperty(Fx.Key2).ValueKind);

        // 目录没有 url 字段（Go omitempty 的等价行为）
        Assert.False(items.GetProperty(Fx.Key1).TryGetProperty("u", out _));

        JsonElement summary = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("created").GetInt32());

        // 冲突数在**响应顶层**（conflicts 数组 + total），不在 summary 里。
        // Go 版 Summary 结构体只有 created/updated/deleted/unchanged 四项。
        // 早先在这里断言 summary.conflicts 是我记错了字段位置 ——
        // 症状是 KeyNotFoundException，完全看不出"是字段位置记错了"。
        Assert.Empty(doc.RootElement.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task sync拒绝空请求体()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(null, rawBody: "");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("请求体为空", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task sync拒绝畸形JSON()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(null, rawBody: "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("bad_request", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task sync拒绝未知字段()
    {
        // 字段名拼错的客户端应该立刻拿到明确错误，而不是静默忽略后行为诡异
        // （比如把 device 拼成 devcie，服务端一直当默认设备处理，没人想到是拼写问题）
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(
            null, rawBody: """{"devcie":"typo","state":{"v":1,"items":{}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("devcie", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task sync拒绝尾随内容()
    {
        // 拒绝第二个 JSON 值：通常是客户端 bug，也可能是攻击试探
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, _) = await f.PostSyncAsync(
            null, rawBody: """{"device":"a","state":{"v":1,"items":{}}}{"extra":1}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task sync拒绝schema版本不符()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.PostSyncAsync(
            null, rawBody: """{"device":"a","state":{"v":99,"items":{}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("schema", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task sync拒绝非法item()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        // 书签缺 url —— 属于硬错误，必须拒绝。
        //
        // 刻意用普通字符串 + Replace 而非插值原始字符串：原始字符串里
        // "{{" 与结尾的 "}}}" 会被解析器当成"转义花括号"与"插值结束"，
        // 报错信息（CS9007）完全指不到真正的问题。而 JSON 满屏都是花括号，
        // 用 $$ 也躲不掉 —— 结尾那三个 } 到底是两个转义加一个内容，还是
        // 三个内容，取决于 $ 的个数，脆得很。
        string bad = """
            {"device":"a","state":{"v":1,"items":{
              "@KEY@":{"p":"toolbar_____","t":"b","n":"x","m":"0000000000100-00000","a":"0000000000100-00000"}
            }}}
            """.Replace("@KEY@", Fx.Key1, StringComparison.Ordinal);

        (HttpStatusCode status, string body) = await f.PostSyncAsync(null, rawBody: bad);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("validation_failed", body, StringComparison.Ordinal);
        Assert.Contains("必须有 url", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task sync接受结构问题但记警告()
    {
        // 父节点缺失只是软警告：为一个孤立节点拒掉整份上传，
        // 对用户来说比跳过那个节点糟糕得多
        await using ServerFixture f = await ServerFixture.StartAsync();

        // 父 key 指向一个不存在的 32 位十六进制 key —— 触发"父节点不存在"警告
        string orphan = """
            {"device":"a","state":{"v":1,"items":{
              "@KEY@":{"p":"99999999999999999999999999999999","t":"b","n":"x","u":"https://x.example","m":"0000000000100-00000","a":"0000000000100-00000"}
            }}}
            """.Replace("@KEY@", Fx.Key1, StringComparison.Ordinal);

        (HttpStatusCode status, _) = await f.PostSyncAsync(null, rawBody: orphan);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task conflicts默认返回50条上限()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.GetAsync("/api/conflicts");

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("conflicts").ValueKind);
    }

    [Fact]
    public async Task history返回空列表()
    {
        await using ServerFixture f = await ServerFixture.StartAsync();

        (HttpStatusCode status, string body) = await f.GetAsync("/api/history");

        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("snapshots").GetArrayLength());
    }

    [Fact]
    public async Task 数据端点全部需要token()
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
    public async Task 超限返回429并带RetryAfter()
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
    public async Task 限流在鉴权之前()
    {
        // 顺序很重要：先限流再鉴权。否则一个拿错误 token 的人可以用
        // 429/401 的响应差异来区分"token 是否存在"。
        // 这里把额度耗光后再发错误 token，期待拿到 429 而不是 401。
        await using ServerFixture f = await ServerFixture.StartAsync(rateLimitPerMinute: 1);

        (HttpStatusCode first, _) = await f.GetAsync("/api/conflicts", token: ServerFixture.Token);
        Assert.Equal(HttpStatusCode.OK, first);

        (HttpStatusCode second, _) = await f.GetAsync("/api/conflicts", token: "wrong");
        Assert.Equal(HttpStatusCode.TooManyRequests, second);
    }

    [Fact]
    public async Task 响应里的中文不转义()
    {
        // 默认的 JSON 编码器会把中文写成 \uXXXX，体积涨 6 倍。
        // 5000 条书签会从约 800KB 涨到 4MB，撞上 nginx 的 client_max_body_size。
        await using ServerFixture f = await ServerFixture.StartAsync();

        SyncRequest body = Fx.SyncBody(Fx.Items(
            (Fx.Key1, Fx.Bookmark(RootFolders.Toolbar, "中文标题", "https://x.example"))));

        (_, string raw) = await f.PostSyncAsync(body);

        Assert.Contains("中文标题", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", raw, StringComparison.Ordinal);
    }
}
