using System.Globalization;

namespace BookmarkSync.Domain;

/// <summary>
/// HLC —— 混合逻辑时钟（Hybrid Logical Clock）。
/// </summary>
/// <remarks>
/// <para>
/// 论文：Kulkarni et al., "Logical Physical Clocks and Consistent Snapshots
/// in Globally Distributed Databases", 2014
/// </para>
/// <para>
/// 解决的问题：合并用 LWW（Last-Write-Wins）决胜，而 LWW 的正确性完全依赖
/// 时间戳的可比性。若直接用 Date.now()，两台机器时钟偏差 5 分钟就会导致
/// 「我 10:00 加的书签」被「你 10:03 看到的旧版本」覆盖 —— 而且用户完全
/// 无感。这是同步工具最糟糕的失败方式。
/// </para>
/// <para>
/// HLC 保证<b>因果序</b>（causal order）：只要 A 端先发生的操作、B 端后看到了它，
/// B 端此后产生的任何时间戳都一定严格大于 A 端那个。这与两台机器的实际时钟
/// 差多少无关。
/// </para>
/// <para>
/// ── 编码格式 ──────────────────────────────────────────────────────────<br/>
/// encode(l, c) = 13 位物理毫秒 + '-' + 5 位逻辑计数<br/>
/// 13 位物理毫秒（可用至公元 2286 年）+ 5 位逻辑计数。定宽，因此
/// <b>字符串字典序 == 时间戳全序</b>，存储在 JSON 里也天然有序。
/// </para>
/// <para>
/// ⚠️ 本文件必须与 HLC 实现（服务端从 Go 迁移到 .NET 10 时逐行对照移植） 和
///    extension/lib/hlc.js <b>三者逐字节等价</b>。两端不一致会导致合并在
///    「m 恰好相等」时产生非确定行为。三方由 test/hlc_vectors.json
///    做交叉验证。
/// </para>
/// </remarks>
public sealed class Hlc
{
    /// <summary>物理部分的位数。</summary>
    internal const int PhysicalDigits = 13;

    /// <summary>逻辑计数部分的位数。</summary>
    internal const int CounterDigits = 5;

    /// <summary>
    /// 逻辑计数的上限。溢出时进位到物理部分（l+1, c=0），从而保持编码定宽。
    /// </summary>
    private const int CounterMax = 99_999;

    /// <summary>"最小"时间戳，用于比较起点。</summary>
    public const string Zero = "0000000000000-00000";

    /// <summary>编码格式说明，供文档与测试使用。</summary>
    public const string Layout = "13 位物理毫秒 + '-' + 5 位逻辑计数，例：1790000000000-00042";

    /// <summary>物理毫秒部分的上界，恰好是 10^13。</summary>
    private const long PhysicalModulus = 10_000_000_000_000L;

    private readonly object _gate = new();
    private readonly Func<long> _nowFn;

    private long _l;   // 最后一个已知的物理时间（毫秒）
    private int _c;    // 该毫秒内的逻辑计数

    /// <summary>用系统墙钟构造。生产环境用这个。</summary>
    public static Hlc New() => new(() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// 用注入的时钟构造。测试用它拿到确定性结果 —— 合并算法里传进来的
    /// <c>now</c> 就是要走这条路。
    /// </summary>
    public static Hlc NewWithClock(Func<long> nowFn) => new(nowFn);

    private Hlc(Func<long> nowFn)
    {
        _nowFn = nowFn ?? throw new ArgumentNullException(nameof(nowFn));
    }

    /// <summary>为一个<b>本地事件</b>产生时间戳。</summary>
    public string Now()
    {
        lock (_gate)
        {
            long p = _nowFn();
            if (p > _l)
            {
                _l = p;
                _c = 0;
            }
            else
            {
                _c++;
            }

            Normalize();
            return Encode(_l, _c);
        }
    }

    /// <summary>
    /// 收到一个远端时间戳后推进本地时钟，并返回一个新的本地时间戳。
    /// </summary>
    /// <remarks>
    /// 调用时机：客户端收到服务端返回的 hlc 之后；服务端收到请求里所有
    /// item 的最大 HLC 之后。见 docs/design.md §6.4。
    /// </remarks>
    public string Update(string remote)
    {
        lock (_gate)
        {
            long p = _nowFn();

            if (!TryDecode(remote, out long rl, out int rc))
            {
                // 远端时间戳非法：退化成纯本地推进。
                // 之所以不抛异常，是因为一次非法输入不应该让整个同步失败。
                if (p > _l)
                {
                    _l = p;
                    _c = 0;
                }
                else
                {
                    _c++;
                }

                Normalize();
                return Encode(_l, _c);
            }

            long max = _l;
            if (p > max)
            {
                max = p;
            }

            if (rl > max)
            {
                max = rl;
            }

            // ↓ 分支顺序不可交换。这不是风格问题，改动前先读 docs/plan.md 附录 D 第 1 条：
            //   曾经把「max == p」写成「max == rl」，结果物理时钟恰好追平远端毫秒时
            //   产生的时间戳**小于**已收到的远端时间戳，因果性被破坏，两端交替获胜，
            //   书签随机丢失且用户完全无感。
            if (max == _l && max == p)
            {
                // 本地时钟与物理时钟同时领先：逻辑计数取较大者再加一
                if (rc > _c)
                {
                    _c = rc;
                }

                _c++;
            }
            else if (max == _l)
            {
                // 本地时钟领先：只在已有计数上前进
                _c++;
            }
            else if (max == rl)
            {
                // 远端时间戳领先（含"物理时钟刚好追平远端"这一情形）：
                // 必须接在远端计数之后，否则下一次生成的本地事件会小于刚收到的
                // 远端事件，因果性被破坏。
                _c = rc + 1;
            }
            else
            {
                // max == p 且严格大于 _l 与 rl：进入了一个全新的毫秒
                _c = 0;
            }

            _l = max;
            Normalize();
            return Encode(_l, _c);
        }
    }

    /// <summary>
    /// 返回当前时间戳但**不**推进逻辑计数（语义上等价于 Now，但不改状态）。
    /// 用途：生成响应里给客户端看的 hlc 字段。
    /// </summary>
    public string Current()
    {
        lock (_gate)
        {
            return Encode(_l, _c);
        }
    }

    /// <summary>
    /// 批量吸收多个远端时间戳，把本地时钟推进到它们的最大值之后。
    /// </summary>
    /// <remarks>
    /// 服务端在合并前用它校准时钟：把请求里所有 item 的 a / m 都喂进来，
    /// 于是服务端之后发出的任何时间戳都严格大于它见过的所有客户端时间戳。
    /// 见 docs/design.md §6.4。
    /// </remarks>
    public string ObserveMany(IEnumerable<string> timestamps)
    {
        ArgumentNullException.ThrowIfNull(timestamps);

        lock (_gate)
        {
            foreach (string t in timestamps)
            {
                if (!TryDecode(t, out long l, out int c))
                {
                    continue;
                }

                if (CompareEncoded(l, c, _l, _c) > 0)
                {
                    _l = l;
                    _c = c;
                }
            }

            return Encode(_l, _c);
        }
    }

    private void Normalize()
    {
        if (_c > CounterMax)
        {
            _l++;
            _c = 0;
        }
    }

    /// <summary>把 (物理毫秒, 逻辑计数) 编码成定宽字符串。</summary>
    public static string Encode(long l, int c) =>
        string.Create(CultureInfo.InvariantCulture, $"{l:D13}-{c:D5}");

    /// <summary>
    /// 解析 HLC 字符串，失败返回 false。
    /// </summary>
    /// <remarks>
    /// 严格性与 Go 的 <c>strconv.ParseUint(s, 10, 64)</c> 对齐：
    /// 不接受正负号、不接受前后空白、不接受下划线分隔符。
    /// 用 <see cref="NumberStyles.None"/> 而不是默认样式，才能做到这一点 ——
    /// 默认样式会把 " 12" 和 "+12" 也解析成功，那是两套实现间的静默分歧。
    /// </remarks>
    public static bool TryDecode(string? s, out long l, out int c)
    {
        l = 0;
        c = 0;
        if (s is null || s.Length != PhysicalDigits + 1 + CounterDigits || s[PhysicalDigits] != '-')
        {
            return false;
        }

        if (!long.TryParse(s.AsSpan(0, PhysicalDigits), NumberStyles.None, CultureInfo.InvariantCulture, out l))
        {
            return false;
        }

        if (!int.TryParse(s.AsSpan(PhysicalDigits + 1), NumberStyles.None, CultureInfo.InvariantCulture, out c))
        {
            return false;
        }

        return true;
    }

    /// <summary>判断字符串是否为合法 HLC。</summary>
    public static bool IsValid(string? s) => TryDecode(s, out _, out _);

    /// <summary>比较两个 HLC：-1 / 0 / 1。</summary>
    /// <remarks>
    /// 非法时间戳被当作比任何合法值都<b>小</b>，这样合并时非法的一侧永远输，
    /// 不会污染权威状态。两侧都非法时退化为字典序比较以保证确定性。
    /// </remarks>
    public static int Compare(string? a, string? b)
    {
        bool aOk = TryDecode(a, out long al, out int ac);
        bool bOk = TryDecode(b, out long bl, out int bc);

        if (!aOk && !bOk)
        {
            return string.CompareOrdinal(a ?? string.Empty, b ?? string.Empty);
        }

        if (!aOk)
        {
            return -1;
        }

        if (!bOk)
        {
            return 1;
        }

        return CompareEncoded(al, ac, bl, bc);
    }

    /// <summary>比较已经解析好的 (l, c) 二元组，避免重复解析。</summary>
    public static int CompareEncoded(long al, int ac, long bl, int bc)
    {
        if (al < bl)
        {
            return -1;
        }

        if (al > bl)
        {
            return 1;
        }

        if (ac < bc)
        {
            return -1;
        }

        if (ac > bc)
        {
            return 1;
        }

        return 0;
    }

    /// <summary>返回序列中最大的时间戳；空序列返回 <see cref="Zero"/>。</summary>
    public static string MaxHlc(IEnumerable<string> list)
    {
        ArgumentNullException.ThrowIfNull(list);
        string output = Zero;
        foreach (string s in list)
        {
            if (Compare(s, output) > 0)
            {
                output = s;
            }
        }

        return output;
    }

    /// <summary>
    /// <see cref="MaxHlc(IEnumerable{string})"/> 的 params 版本，签名对齐 Go 的
    /// 可变参数 <c>MaxHLC(list ...string)</c>。
    /// </summary>
    public static string MaxHlc(params string[] list) => MaxHlc((IEnumerable<string>)list);

    /// <summary>物理毫秒的上界，用于测试与文档。</summary>
    internal static long PhysicalModulusInternal => PhysicalModulus;

    /// <summary>
    /// 仅供测试：直接设置内部状态。
    /// </summary>
    /// <remarks>
    /// 存在的理由：逻辑计数溢出（99999 → 进位）这个分支，从公共 API 走需要先
    /// 调用 99999 次 <see cref="Now"/> 才能抵达。让测试跑 10 万次只为抵达一个
    /// 边界，会让"测试很慢"变成掩盖真实问题的借口（"是不是因为它太慢了才
    /// 少写了个用例"）。直接设状态让边界测试保持毫秒级。
    /// <para>
    /// <c>internal</c> + <c>InternalsVisibleTo</c>：只有测试程序集看得见，
    /// 生产代码调不到。
    /// </para>
    /// </remarks>
    internal void SetStateForTest(long l, int c)
    {
        lock (_gate)
        {
            _l = l;
            _c = c;
        }
    }
}
