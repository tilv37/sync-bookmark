using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using BookmarkSync.Domain;

namespace BookmarkSync.Server;

/// <summary>HTTP 层的硬限额。</summary>
/// <remarks>
/// 领域层的限额（MaxItems / MaxDepth / KeyLen …）在 BookmarkSync.Domain/Limits.cs。
/// 分开放是因为它们归属不同：这里管的是"HTTP 请求能有多大、能多快"，
/// 那边管的是"书签数据本身能有多复杂"。放一起会让人搞不清某个数字
/// 改大之后影响的是网络还是数据结构。
/// </remarks>
public static class HttpLimits
{
    /// <summary>/api/sync 请求体上限。</summary>
    /// <remarks>
    /// 注意：这只是应用层上限。nginx 侧还有一层<b>更小</b>的限制
    /// （client_max_body_size 默认 1m），必须由用户在 NPM 中放开，
    /// 见 docs/design.md §10.4。书签超过约 6000 条时就会撞上后者。
    /// </remarks>
    public const long MaxBodyBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 按来源的同步请求限流。
    /// </summary>
    /// <remarks>
    /// 为什么是 60 而不是个位数：这是<b>手动触发</b>的工具，用户的正常操作是
    /// "点一下同步按钮"。一次典型会话包括：首次同步、公司电脑首次拉取、
    /// 几次补同步、失败后重试、顺便拉一次冲突列表。10 次/分钟会让这些正当
    /// 操作在开始几次之后全部 429，然后用户什么都不用做也得等一分钟 ——
    /// 这既挡不住真正的攻击者，又让正常用起来很难受。
    /// <para>
    /// 真正的防护目标是"挡住脚本化的高频调用"，而 60 次/分钟（每秒 1 次）
    /// 已经远远超过人的点击速度。数据量也小：60 次 × 800KB 的上传，
    /// 对任何一台 VPS 都不构成压力。
    /// </para>
    /// </remarks>
    public const int RateLimitPerMinute = 60;
}

/// <summary>JSON 编解码与请求体读取的辅助。</summary>
public static class HttpJson
{
    /// <summary>
    /// 序列化选项：与 <see cref="BmsyncJson.Storage"/> 同一套（key 有序、
    /// 空字段省略、中文不转义），但<b>不</b>拒绝未知字段 ——
    /// 响应是我们自己写出的结构，没有"未知字段"可言。
    /// </summary>
    // 实际定义在 ApiJsonContext.cs —— 那里额外说明了为什么必须把
    // ItemJsonConverter / StateJsonConverter 显式加回来（源生成会绕过它们）。
    public static readonly JsonSerializerOptions Response = ApiJson.Response;

    /// <summary>写一个 JSON 响应。</summary>
    /// <remarks>
    /// 显式设置 Content-Type 而不是依赖框架的 ObjectResult：框架会写
    /// <c>application/json; charset=utf-8</c>，但如果哪次改成返回一个
    /// 已经序列化好的 string，charset 就会静默消失，而扩展是按
    /// <c>response.json()</c> 读的 —— 那会给出"Unexpected token"这种
    /// 指向错误方向的报错。
    /// </remarks>
    public static async Task WriteJsonAsync<T>(HttpContext ctx, int status, T value, JsonTypeInfo<T> info)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";

        // 走 JsonTypeInfo 重载而不是 JsonSerializer.SerializeAsync(stream, v, options)：
        // 后者带 [RequiresDynamicCode]，PublishAot 下报 IL3050。理由与
        // ApiJson 那一段相同。
        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, info, ctx.RequestAborted)
            .ConfigureAwait(false);
    }

    public static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message) =>
        WriteJsonAsync(
            ctx, status,
            new ErrorResponse { Code = code, Message = message },
            ApiJson.TypeInfoOf<ErrorResponse>());

    /// <summary>
    /// 带大小上限地读取请求体。
    /// </summary>
    /// <returns>
    /// 读取成功返回 true 并把字节写进 <paramref name="buffer"/>；
    /// 超过上限返回 false（此时 <paramref name="tooLarge"/> 为 true）。
    /// </returns>
    /// <remarks>
    /// 刻意自己读而不是交给框架的模型绑定：要精确控制 413 的时机与错误体，
    /// 并且在<b>开始解析之前</b>就知道体积 —— 否则一个 100MB 的坏上传会先被
    /// 读进内存、再被拒绝，而那一刻进程已经吃掉了 100MB。
    /// </remarks>
    public static async Task<(bool Ok, bool TooLarge, byte[]? Body)> ReadBodyAsync(
        HttpContext ctx, long limit)
    {
        var buffer = new MemoryStream(capacity: 8192);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);

        try
        {
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return (false, true, null);
                }

                buffer.Write(chunk, 0, read);
            }

            return (true, false, buffer.ToArray());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
