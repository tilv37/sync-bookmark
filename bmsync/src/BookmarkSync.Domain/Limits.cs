namespace BookmarkSync.Domain;

/// <summary>
/// 领域层的硬限额。
///
/// 这些数字定义在 docs/design.md §7.7，会与文档反复核对，所以集中放置，
/// 避免散落成魔法数字。HTTP 层的限额（请求体大小、限流）在
/// BookmarkSync.Server/Limits.cs。
/// </summary>
public static class Limits
{
    /// <summary>单份 state 允许的最大 item 数。</summary>
    public const int MaxItems = 50_000;

    /// <summary>
    /// 书签树最大层数（不含根目录本身）。
    /// <para>
    /// 为什么需要：客户端计算 key 时文件夹 key 依赖父级 key，理论上可以构造
    /// 极深的链。限制层数既防止病态数据，也给 apply 阶段的递归留出上界。
    /// </para>
    /// </summary>
    public const int MaxDepth = 32;

    /// <summary>item key 的十六进制字符数（SHA-256 取前 16 字节）。</summary>
    /// <remarks>
    /// 128 bit 对个人书签规模而言碰撞概率可忽略。
    /// </remarks>
    public const int KeyLen = 32;

    /// <summary>URL 字段长度上限，防御异常超长输入。</summary>
    /// <remarks>
    /// 真实书签标题经常超过 1KB（网页 title 原样存进来，真机上见过 2762 字节的），
    /// 1024 会误伤正常数据。8KB 仍远小于 16MB 请求体上限，极端病态输入由
    /// HTTP 层的 MaxBodyBytes 兜底。
    /// </remarks>
    public const int MaxUrlLen = 4096;

    /// <summary>标题字段长度上限。</summary>
    public const int MaxTitleLen = 8192;
}
