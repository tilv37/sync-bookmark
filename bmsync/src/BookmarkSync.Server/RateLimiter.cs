namespace BookmarkSync.Server;

/// <summary>
/// 按来源计数的令牌桶。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要：这是一个暴露在公网（经由反代）的接口。虽然有 token 保护，
/// 但如果没有限流，一个拿着 token 的脚本（或者单纯的好奇者反复试 token）
/// 可以无限次打 /api/sync。每次请求都要做一次全量合并 + 落盘，
/// 足够把一台小 VPS 打满。
/// </para>
/// <para>
/// <b>为什么不用 ASP.NET Core 内置的限流中间件</b>：内置的
/// <c>AddRateLimiter</c> 按 endpoint/策略名分桶，且令牌桶算法是"匀速补充"，
/// 语义与本项目要的"每分钟固定 N 次"不同（前者允许突发，后者不允许）。
/// 端口径变化会让"60 次/分钟"这个已在文档里写明的承诺悄悄变样。
/// </para>
/// </remarks>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public int Count;
        public DateTimeOffset Start;
    }

    private readonly Lock _gate = new();
    private int _limit;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private DateTimeOffset _lastGc;

    public RateLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
        _lastGc = DateTimeOffset.UtcNow;
    }

    /// <summary>判断这次请求是否放行，并消耗一个配额。</summary>
    public bool Allow(string key)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            Gc(now);

            if (!_buckets.TryGetValue(key, out Bucket? b) || now - b.Start >= _window)
            {
                _buckets[key] = new Bucket { Count = 1, Start = now };
                return true;
            }

            if (b.Count >= _limit)
            {
                return false;
            }

            b.Count++;
            return true;
        }
    }

    /// <summary>
    /// 清理过期的桶。手工触发而非后台定时器：调用频率就是请求频率，
    /// 不值得为它开一个后台任务。
    /// </summary>
    private void Gc(DateTimeOffset now)
    {
        if (now - _lastGc < _window)
        {
            return;
        }

        _lastGc = now;
        List<string> expired = [];
        foreach ((string k, Bucket b) in _buckets)
        {
            if (now - b.Start >= _window)
            {
                expired.Add(k);
            }
        }

        foreach (string k in expired)
        {
            _buckets.Remove(k);
        }
    }

    /// <summary>仅供测试：直接写入一个桶，用来构造"窗口已滑过"的场景。</summary>
    internal void SeedBucket(string key, int count, DateTimeOffset start)
    {
        lock (_gate)
        {
            _buckets[key] = new Bucket { Count = count, Start = start };
        }
    }

    /// <summary>仅供测试：把上次 GC 时间推到过去。</summary>
    internal void ForceGcDue() => _lastGc = DateTimeOffset.UtcNow - _window;

    /// <summary>仅供测试：当前桶数量。</summary>
    internal int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>仅供测试：把额度改小，用来验证"第 N+1 次被拒"。</summary>
    /// <remarks>
    /// 生产额度是 60 次/分钟。要测"超限被拒"就得真的打 61 次请求并等一分钟 ——
    /// 那会让一个本该毫秒级的用例变成分钟级，久而久之大家就不写它了。
    /// <para>
    /// 所以额度做成可变（而不是 readonly）并只暴露给测试程序集：改额度这件事
    /// 本身是安全的，而"没人写这个测试"不是。
    /// </para>
    /// </remarks>
    internal void OverrideLimitForTest(int limit)
    {
        lock (_gate)
        {
            _limit = limit;
            _buckets.Clear();
        }
    }
}
