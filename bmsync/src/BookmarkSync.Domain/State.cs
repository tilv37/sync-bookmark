using System.Text.Json.Serialization;

namespace BookmarkSync.Domain;

/// <summary>item 的类型。</summary>
public static class ItemTypes
{
    /// <summary>书签：有 URL。</summary>
    public const string Bookmark = "b";

    /// <summary>文件夹：无 URL。</summary>
    public const string Folder = "f";
}

/// <summary>
/// 同步的最小单位，对应一个书签或一个文件夹。
/// </summary>
/// <remarks>
/// <para>
/// 字段名用单字母是因为体积：5000 个书签的 JSON 用全字段名约 1.1 MB，
/// 用短名约 800 KB，而每次同步都是全量传输。
/// </para>
/// <para>
/// 用 <c>readonly record struct</c> 而不是 class：合并算法里有
/// <c>s == winner</c> 这种判断，依赖 Go 原实现里"值类型、== 即全字段相等"
/// 的语义。record struct 自动提供值相等与 <c>==</c>，换成 class 的话所有
/// 引用相等判断都会静默变成"永远相等"，统计与冲突判定会一起错掉。
/// </para>
/// </remarks>
public readonly record struct Item
{
    /// <summary>parent key（父目录）；顶层 item 为四个根目录 id 之一。</summary>
    [JsonPropertyName("p")]
    [JsonPropertyOrder(0)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string P { get; init; }

    /// <summary>类型，见 <see cref="ItemTypes"/>。</summary>
    [JsonPropertyName("t")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string T { get; init; }

    /// <summary>标题 / 文件夹名。</summary>
    [JsonPropertyName("n")]
    [JsonPropertyOrder(2)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string N { get; init; }

    /// <summary>URL，仅书签有。</summary>
    [JsonPropertyName("u")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string U { get; init; }

    /// <summary>HLC：创建时间，不参与合并。</summary>
    [JsonPropertyName("a")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string A { get; init; }

    /// <summary>HLC：最后修改时间 —— LWW 的唯一比较依据。</summary>
    [JsonPropertyName("m")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string M { get; init; }

    /// <summary>deleted：墓碑标记。</summary>
    [JsonPropertyName("d")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool D { get; init; }

    /// <summary>deletedAt：删除的墙钟毫秒，仅用于 GC 判定。</summary>
    [JsonPropertyName("x")]
    [JsonPropertyOrder(7)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long X { get; init; }
}

/// <summary>
/// 四个 Firefox 系统根目录的固定 id。
/// </summary>
/// <remarks>
/// 这些 id 在 Firefox 各版本与各语言界面下都是稳定的，因此可以安全地
/// 直接当作顶层 item 的 parent key 使用 —— 跨设备、跨语言都不需要映射表。
/// 见 docs/design.md §3.4。
/// </remarks>
public static class RootFolders
{
    /// <summary>书签栏。</summary>
    public const string Toolbar = "toolbar_____";

    /// <summary>菜单。</summary>
    public const string Menu = "menu________";

    /// <summary>其他书签。</summary>
    public const string Unfiled = "unfiled_____";

    /// <summary>移动设备书签（默认不同步）。</summary>
    public const string Mobile = "mobile______";

    private static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        Toolbar, Menu, Unfiled, Mobile,
    };

    public static bool IsRootFolder(string? key) => key is not null && All.Contains(key);
}

/// <summary>持久化格式的版本常量。</summary>
/// <remarks>
/// 版本不匹配时服务<b>拒绝启动</b>而非自动迁移：书签是用户不可再生的数据，
/// 静默改写它的风险远大于「升级后手工处理一次」的麻烦。见 docs/design.md §8.7。
/// <para>
/// 放在静态类里而不是命名空间层级：C# 不允许命名空间层级出现
/// <c>public const</c> 成员，只能是类型。
/// </para>
/// </remarks>
public static class Schema
{
    /// <summary>state.json / /api/sync 请求体的 schema 版本。</summary>
    public const int Version = 1;
}

/// <summary>一份 state 的校验结果。</summary>
/// <remarks>
/// 对应 Go 版返回的 <c>([]string, error)</c>。分成"硬错误"与"软警告"两级的
/// 理由见 <see cref="State.Validate"/>。
/// </remarks>
public readonly record struct ValidationResult(IReadOnlyList<string> Warnings, string? Error)
{
    /// <summary>是否通过硬校验。</summary>
    public bool IsValid => Error is null;

    public static ValidationResult Ok(IReadOnlyList<string> warnings) => new(warnings, null);

    /// <summary>硬错误：直接拒绝这份上传，不给任何软警告。</summary>
    public static ValidationResult Fail(string error) =>
        new(Array.Empty<string>(), error);
}

/// <summary>完整的同步状态，即 state.json 的内存表示。</summary>
public sealed class State
{
    /// <summary>schema 版本。</summary>
    [JsonPropertyName("v")]
    [JsonPropertyOrder(0)]
    public int V { get; init; } = Schema.Version;

    /// <summary>服务端权威 HLC，客户端用它校准本地时钟。</summary>
    /// <remarks>
    /// C# 属性名是 <c>Clock</c> 而 wire 名是 <c>hlc</c>：若把属性也叫
    /// <c>Hlc</c>，它会和 <see cref="Hlc"/> 类在 State 的作用域内打架 ——
    /// 成员查找优先于类型查找，<c>Hlc.Zero</c> 会被解析成"访问这个字符串
    /// 属性的 Zero"，而字符串没有 Zero，得到的是一个让人一头雾水的编译错误。
    /// wire 名保持 <c>hlc</c> 不变，协议完全不受影响。
    /// </remarks>
    [JsonPropertyName("hlc")]
    [JsonPropertyOrder(1)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Clock { get; init; } = string.Empty;

    /// <summary>key → item。</summary>
    [JsonPropertyName("items")]
    [JsonPropertyOrder(2)]
    public Dictionary<string, Item> Items { get; init; } = NewItemsDictionary();

    /// <summary>
    /// 创建一个空字典，比较器固定为 <see cref="StringComparer.Ordinal"/>。
    /// </summary>
    /// <remarks>
    /// 必须显式指定：Go 的 map 按字节比较字符串键，.NET 的默认字符串比较器
    /// 虽然也是序数比较，但依赖默认值意味着"哪天有人改了 CultureInfo
    /// 就会悄悄改变排序"，而合并算法的确定性依赖 key 序。
    /// </remarks>
    public static Dictionary<string, Item> NewItemsDictionary() =>
        new(StringComparer.Ordinal);

    public static State New() => new();

    /// <summary>
    /// 返回一份只有 <see cref="Clock"/> 不同、Items 字典<b>共享</b>的副本。
    /// </summary>
    /// <remarks>
    /// 为什么不是 <c>with</c> 表达式：<c>State</c> 是普通 class 而不是 record。
    /// 改成 record 看起来更"现代"，但 record 会自动生成基于字段的 Equals，
    /// 而 Items 是 <c>Dictionary</c> —— 默认比较器对它做的是<b>引用</b>比较，
    /// 于是两个内容完全相同、字典却是不同对象的 State 会判为不等。
    /// 那正是合并测试里 <c>sameStates</c> 一直在做的事，踩中了这个坑会
    /// 让"幂等性"这类断言莫名其妙地失败，且原因极难定位。
    /// <para>
    /// Items 共享在这里是安全的：调用方（store.SyncAsync）拿到副本后不会
    /// 再往字典里写，它只是换一个 Clock 就要落盘。
    /// </para>
    /// </remarks>
    public State WithClock(string clock) => new() { V = V, Clock = clock, Items = Items };

    /// <summary>返回一份深拷贝。</summary>
    /// <remarks>
    /// 为什么必须深拷贝：合并会把 server 与 incoming 的 item 放进结果字典。
    /// <see cref="Item"/> 是值类型，拷贝本身安全；真正危险的是<b>字典本身</b>
    /// 被共享 —— 后续若有人就地修改结果，会连带污染服务端状态。所以连字典一起复制。
    /// </remarks>
    public State Clone()
    {
        var copy = NewItemsDictionary();
        foreach ((string k, Item v) in Items)
        {
            copy[k] = v;
        }

        return new State { V = V, Clock = Clock, Items = copy };
    }

    /// <summary>本 state 中最大的 HLC（扫描所有 a 与 m 字段）。</summary>
    /// <remarks>
    /// 服务端在处理一次同步后用它推进自己的 HLC，从而保证服务端发出的
    /// 时间戳一定大于它见过的所有客户端时间戳。见 docs/design.md §6.4。
    /// </remarks>
    public string MaxHlc()
    {
        string output = Hlc.Zero;
        foreach (Item it in Items.Values)
        {
            if (Hlc.Compare(it.M, output) > 0)
            {
                output = it.M;
            }

            if (Hlc.Compare(it.A, output) > 0)
            {
                output = it.A;
            }
        }

        return output;
    }

    /// <summary>活跃（非墓碑）item 数。</summary>
    public int CountActive()
    {
        int n = 0;
        foreach (Item it in Items.Values)
        {
            if (!it.D)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>全部 key 的升序切片。</summary>
    /// <remarks>
    /// 确定性很重要：合并遍历字典的顺序取决于插入顺序，而冲突列表、
    /// HLC 碰撞时的兜底决胜都依赖顺序稳定，否则同样的输入会产生不同的输出。
    /// </remarks>
    public string[] KeysSorted()
    {
        var keys = new string[Items.Count];
        Items.Keys.CopyTo(keys, 0);
        Array.Sort(keys, StringComparer.Ordinal);
        return keys;
    }

    /// <summary>key 所在位置的深度（根目录的直接子项深度为 1）。</summary>
    /// <returns>深度；父链异常时返回 -1 并通过 <paramref name="error"/> 给出原因。</returns>
    public int DepthOf(string key, out string? error)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;
        string cur = key;

        while (true)
        {
            if (!Items.TryGetValue(cur, out Item it))
            {
                error = $"父节点 {cur} 缺失";
                return -1;
            }

            if (RootFolders.IsRootFolder(it.P))
            {
                error = null;
                return depth + 1;
            }

            if (!seen.Add(cur))
            {
                error = "父链成环";
                return -1;
            }

            cur = it.P;
            depth++;

            if (depth > Limits.MaxDepth + 1)
            {
                // 提前退出，避免构造出的长链让 DepthOf 跑很久
                error = $"父链超过 {Limits.MaxDepth + 1} 层";
                return depth;
            }
        }
    }

    /// <summary>校验一份 state 是否可以安全参与合并。</summary>
    /// <remarks>
    /// 严格程度是刻意分级的：
    /// <list type="bullet">
    /// <item>
    /// <b>硬错误</b>（<see cref="ValidationResult.Error"/> 非空）：会导致解析出错
    /// 或资源失控的问题 —— 版本不符、key 格式非法、类型非法、HLC 格式非法、
    /// 超出数量/深度/长度上限。这些情况说明输入损坏或恶意，必须拒绝。
    /// </item>
    /// <item>
    /// <b>软警告</b>（<see cref="ValidationResult.Warnings"/>，不阻断）：树结构层面的
    /// 问题 —— 父节点缺失、父节点不是文件夹、存在环。这些理论上不该出现
    /// （采集端总是从真实的 Firefox 树生成，父节点必然存在），但一旦出现，
    /// 让整份上传被拒会非常挫伤用户。改为记录警告，由 apply 阶段做兜底
    /// （父节点找不到就跳过该 item）。
    /// </item>
    /// </list>
    /// </remarks>
    public ValidationResult Validate()
    {
        var warnings = new List<string>();

        if (V != Schema.Version)
        {
            return ValidationResult.Fail($"schema 版本不符：期望 {Schema.Version}，收到 {V}");
        }

        if (Items.Count > Limits.MaxItems)
        {
            return ValidationResult.Fail($"item 数量 {Items.Count} 超过上限 {Limits.MaxItems}");
        }

        foreach ((string key, Item it) in Items)
        {
            if (key.Length != Limits.KeyLen || !IsHex(key))
            {
                return ValidationResult.Fail($"item key 非法：\"{key}\"（期望 {Limits.KeyLen} 位十六进制）");
            }

            switch (it.T)
            {
                case ItemTypes.Bookmark:
                    if (string.IsNullOrEmpty(it.U))
                    {
                        return ValidationResult.Fail($"item {key}：书签必须有 url");
                    }

                    if (it.U.Length > Limits.MaxUrlLen)
                    {
                        return ValidationResult.Fail(
                            $"item {key}：url 长度 {it.U.Length} 超过上限 {Limits.MaxUrlLen}");
                    }

                    break;

                case ItemTypes.Folder:
                    if (!string.IsNullOrEmpty(it.U))
                    {
                        return ValidationResult.Fail($"item {key}：文件夹不应有 url");
                    }

                    break;

                default:
                    return ValidationResult.Fail($"item {key}：类型非法 \"{it.T}\"");
            }

            if (it.N is not null && it.N.Length > Limits.MaxTitleLen)
            {
                return ValidationResult.Fail(
                    $"item {key}：标题长度 {it.N.Length} 超过上限 {Limits.MaxTitleLen}");
            }

            if (!Hlc.TryDecode(it.M, out _, out _))
            {
                return ValidationResult.Fail($"item {key}：m 字段不是合法 HLC：\"{it.M}\"");
            }

            if (!string.IsNullOrEmpty(it.A) && !Hlc.TryDecode(it.A, out _, out _))
            {
                return ValidationResult.Fail($"item {key}：a 字段不是合法 HLC：\"{it.A}\"");
            }

            if (it.D && it.X == 0)
            {
                warnings.Add($"item {key} 是墓碑但缺少删除时间 x");
            }
        }

        // 树结构检查
        foreach ((string key, Item it) in Items)
        {
            if (RootFolders.IsRootFolder(it.P))
            {
                continue; // 顶层 item，父为系统根目录，正常
            }

            if (!Items.TryGetValue(it.P, out Item parent))
            {
                warnings.Add($"item {key} 的父节点 {it.P} 不存在");
                continue;
            }

            if (parent.T != ItemTypes.Folder)
            {
                warnings.Add($"item {key} 的父节点 {it.P} 不是文件夹");
                continue;
            }

            // 父节点是墓碑而子节点存活，一定是状态不一致：apply 阶段无法把它
            // 挂到任何地方。记录下来但继续（该 item 在 apply 时会被安全跳过）。
            if (parent.D && !it.D)
            {
                warnings.Add($"item {key} 存活但父节点 {it.P} 已删除");
            }
        }

        // 深度检查（带环检测，防御性：损坏数据可能构造出环导致死循环）
        foreach (string key in Items.Keys)
        {
            int d = DepthOf(key, out string? err);
            if (err is not null)
            {
                warnings.Add($"item {key} 的父链异常：{err}");
                continue;
            }

            if (d > Limits.MaxDepth)
            {
                return ValidationResult.Fail($"item {key} 深度 {d} 超过上限 {Limits.MaxDepth}");
            }
        }

        return ValidationResult.Ok(warnings);
    }

    private static bool IsHex(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char ch in s)
        {
            bool digit = ch is >= '0' and <= '9';
            bool lower = ch is >= 'a' and <= 'f';
            if (!digit && !lower)
            {
                return false;
            }
        }

        return true;
    }
}
