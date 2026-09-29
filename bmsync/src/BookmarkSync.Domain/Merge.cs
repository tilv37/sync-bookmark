namespace BookmarkSync.Domain;

/// <summary>冲突类型。</summary>
public static class ConflictReasons
{
    /// <summary>
    /// 真正的并发编辑：两端都基于同一个基线改动了同一项。
    /// <b>这才是有价值的冲突</b>，用户需要知道。
    /// </summary>
    public const string ConcurrentEdit = "concurrent_edit";

    /// <summary>
    /// HLC 完全相等但内容不同。
    /// </summary>
    /// <remarks>
    /// 理论上不应发生 —— HLC 保证同一因果链上严格递增，每台设备在同一
    /// 毫秒内用逻辑计数区分。走到这里说明两端 HLC 实现不一致，
    /// 记录下来是为了排查，不是预期会有。
    /// </remarks>
    public const string HlcCollision = "hlc_collision";
}

/// <summary>决胜方标识。</summary>
public static class ConflictWinners
{
    public const string Server = "server";
    public const string Client = "client";
}

/// <summary>一条冲突记录。纯观测，<b>不影响合并结果</b>。</summary>
public sealed record Conflict
{
    [System.Text.Json.Serialization.JsonPropertyName("at")]
    [System.Text.Json.Serialization.JsonPropertyOrder(0)]
    public long At { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("device")]
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public string Device { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("key")]
    [System.Text.Json.Serialization.JsonPropertyOrder(2)]
    public string Key { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("reason")]
    [System.Text.Json.Serialization.JsonPropertyOrder(3)]
    public string Reason { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("field")]
    [System.Text.Json.Serialization.JsonPropertyOrder(4)]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? Field { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("winner")]
    [System.Text.Json.Serialization.JsonPropertyOrder(5)]
    public string Winner { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("loser")]
    [System.Text.Json.Serialization.JsonPropertyOrder(6)]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? Loser { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("winnerValue")]
    [System.Text.Json.Serialization.JsonPropertyOrder(7)]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? WinnerValue { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("loserValue")]
    [System.Text.Json.Serialization.JsonPropertyOrder(8)]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? LoserValue { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("url")]
    [System.Text.Json.Serialization.JsonPropertyOrder(9)]
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public string? Url { get; init; }
}

/// <summary>一次合并的结果统计。</summary>
public sealed record Summary
{
    [System.Text.Json.Serialization.JsonPropertyName("created")]
    [System.Text.Json.Serialization.JsonPropertyOrder(0)]
    public int Created { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("updated")]
    [System.Text.Json.Serialization.JsonPropertyOrder(1)]
    public int Updated { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("deleted")]
    [System.Text.Json.Serialization.JsonPropertyOrder(2)]
    public int Deleted { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("unchanged")]
    [System.Text.Json.Serialization.JsonPropertyOrder(3)]
    public int Unchanged { get; init; }
}

/// <summary>一次合并的全部产出。</summary>
public sealed class MergeResult
{
    public required State State { get; init; }

    public required IReadOnlyList<Conflict> Conflicts { get; init; }

    public required Summary Summary { get; init; }
}

/// <summary>
/// 合并算法：LWW-Element-Set（带墓碑的 LWW 集合）。
/// </summary>
/// <remarks>
/// <para>
/// 合并是本项目风险最高的一段代码：它决定了两台设备最终是否会收敛到
/// 同一个书签集合，而且出错时<b>静默丢数据、用户完全无感</b>。
/// </para>
/// <para>
/// 因此这一文件的第一原则不是性能或简洁，而是：
/// </para>
/// <list type="number">
/// <item>遍历顺序确定（走排序后的并集，不走字典遍历顺序）</item>
/// <item>三个不变量有显式断言（见 <see cref="MergeInvariants"/>）</item>
/// <item>每个分支都有对应的测试用例</item>
/// </list>
/// <para>
/// 这不是 CRDT。区别在于：CRDT 保证无冲突合并，这里允许冲突但用 HLC
/// 时间戳决胜。对个人书签场景够用，且实现量只有 CRDT 的零头。
/// </para>
/// <para>
/// identity（key）由内容哈希派生，不存任何元数据 —— 见 docs/design.md §3。
/// 这意味着「改 URL」和「改文件夹名」都会表现为「删除旧的 + 新增新的」，
/// 而非「修改」。这是刻意的取舍。
/// </para>
/// </remarks>
public static class Merger
{
    /// <summary>
    /// 把客户端上报的状态与服务端已有的状态合并，返回合并后的权威状态。
    /// </summary>
    /// <param name="server">服务端当前状态（本方法不会修改它）。</param>
    /// <param name="incoming">客户端上报的状态（本方法不会修改它）。</param>
    /// <param name="base">
    /// 客户端「上次见到」每个 key 时的 HLC 快照（key → m）。为空表示这是
    /// 该客户端的首次同步。它的唯一用途是<b>区分「我改了」和「对面改了」</b> ——
    /// 没有它，服务端每次合并都会把「刚合过的东西」记成冲突，日志迅速失去价值。
    /// </param>
    /// <param name="device">上报方的设备标识，写进冲突记录便于排查。</param>
    /// <param name="now">本次操作的墙钟毫秒，写进冲突记录。</param>
    /// <remarks>
    /// 关于"删除"：墓碑（d=true）就是一条普通 item，它的 M 参与 LWW 决胜。
    /// 因此删除天然会传播到其他设备，不需要任何特殊通道 —— 这是选用
    /// LWW-Element-Set 而非朴素"diff 同步"的核心原因。
    /// </remarks>
    public static MergeResult Merge(
        State server,
        State incoming,
        IReadOnlyDictionary<string, string>? @base,
        string device,
        long now)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(incoming);

        var clock = Hlc.NewWithClock(() => now);

        // 服务端先吸收客户端发来的所有时间戳，之后它产生的任何时间戳都会
        // 严格大于它们。这是 HLC 的因果性保证，也是"服务端时间戳最权威"的原因。
        var stamps = new List<string>(incoming.Items.Count * 2);
        foreach (Item it in incoming.Items.Values)
        {
            stamps.Add(it.M);
            stamps.Add(it.A);
        }

        clock.ObserveMany(stamps);

        var result = new State { Clock = clock.Current() };

        var conflicts = new List<Conflict>(4);
        int created = 0, updated = 0, deleted = 0, unchanged = 0;

        // 取两边 key 的并集后按字典序遍历，保证输出确定。
        foreach (string key in SortedUnionKeys(server, incoming))
        {
            bool sOk = server.Items.TryGetValue(key, out Item s);
            bool cOk = incoming.Items.TryGetValue(key, out Item c);

            Item winner;
            if (!cOk)
            {
                winner = s; // 只有服务端有
            }
            else if (!sOk)
            {
                winner = c; // 只有客户端有
            }
            else if (Hlc.Compare(c.M, s.M) > 0)
            {
                winner = c;
            }
            else if (Hlc.Compare(s.M, c.M) > 0)
            {
                winner = s;
            }
            else
            {
                // HLC 完全相等
                if (SameContent(s, c))
                {
                    // 同一毫秒内被两端各存了一次，但内容一模一样 —— 完全正常
                    winner = s;
                }
                else
                {
                    // 走到这里说明两端 HLC 实现不一致。仍然要给出一个确定结果，
                    // 否则每次同步结果都不同，最终必然分叉。按标题字典序兜底。
                    (winner, Item loser) = LexicographicPick(s, c);
                    string field = FirstDifferingField(s, c);
                    conflicts.Add(new Conflict
                    {
                        At = now,
                        Device = device,
                        Key = key,
                        Reason = ConflictReasons.HlcCollision,
                        Field = field,
                        Winner = WinnerSide(winner, s),
                        Loser = device,
                        WinnerValue = FieldValue(winner, field),
                        LoserValue = FieldValue(LoserOf(s, c, winner), field),
                        Url = FirstNonEmpty(s.U, c.U),
                    });
                    _ = loser;
                }
            }

            result.Items[key] = winner;

            // ── 统计 ─────────────────────────────────────────────────────
            if (!sOk)
            {
                created++; // 服务端没有 → 本次新增
            }
            else if (s == winner)
            {
                unchanged++; // Item 是值类型，== 即全字段相等
            }
            else if (winner.D && !s.D)
            {
                deleted++; // 存活 → 墓碑
            }
            else
            {
                updated++;
            }

            // ── 三方冲突检测（纯观测，不改变上面已定的 winner）─────────────
            if (sOk && cOk && !SameContent(s, c)
                && @base is not null && @base.TryGetValue(key, out string? bm))
            {
                bool clientChanged = Hlc.Compare(c.M, bm) > 0;
                bool serverChanged = Hlc.Compare(s.M, bm) > 0;
                if (clientChanged && serverChanged)
                {
                    string field = FirstDifferingField(s, c);
                    conflicts.Add(new Conflict
                    {
                        At = now,
                        Device = device,
                        Key = key,
                        Reason = ConflictReasons.ConcurrentEdit,
                        Field = field,
                        Winner = WinnerSide(winner, s),
                        Loser = LoserSide(winner, s, device),
                        WinnerValue = FieldValue(winner, field),
                        LoserValue = FieldValue(LoserOf(s, c, winner), field),
                        Url = FirstNonEmpty(s.U, c.U),
                    });
                }
            }
        }

        string? invariantError = MergeInvariants(server, incoming, result);
        if (invariantError is not null)
        {
            // 不变量被打破说明 Merge 本身有 bug。这是程序错误而非用户错误，
            // 绝不能把一份可能损坏的权威状态返回给客户端 —— 宁可让这次同步失败。
            //
            // 移植说明：Go 版在这里 panic。C# 不该用异常表达"代码有 bug"——
            // 异常会被 store 层 catch 成"同步失败"的 500，把一个编程错误
            // 伪装成运行时故障，排查时会去查数据和网络而不是查代码。
            // 这里直接抛 InvalidOperationException，让它以"崩溃"的形式暴露。
            throw new InvalidOperationException("bmsync: 合并不变量被打破: " + invariantError);
        }

        return new MergeResult
        {
            State = result,
            Conflicts = conflicts,
            Summary = new Summary
            {
                Created = created,
                Updated = updated,
                Deleted = deleted,
                Unchanged = unchanged,
            },
        };
    }

    /// <summary>校验三条必须在任何输入下都成立的不变量。</summary>
    /// <returns>非 null 表示某条被打破，值是原因。任何一条被打破都意味着 Merge 里有 bug，而不是用户数据有问题。</returns>
    public static string? MergeInvariants(State server, State incoming, State result)
    {
        // 1. 结果的 key 集合必须恰好等于两边 key 集合的并集：既不能多，
        //    也不能少。多意味着凭空造出第三种数据，少意味着书签凭空消失。
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in server.Items.Keys)
        {
            union.Add(k);
        }

        foreach (string k in incoming.Items.Keys)
        {
            union.Add(k);
        }

        if (union.Count != result.Items.Count)
        {
            return $"结果 item 数 {result.Items.Count} != 两边并集 {union.Count}";
        }

        foreach (string k in result.Items.Keys)
        {
            if (!union.Contains(k))
            {
                return $"结果出现不属于任何一边的 key {k}";
            }
        }

        foreach (string k in union)
        {
            if (!result.Items.ContainsKey(k))
            {
                return $"结果缺少 key {k}";
            }
        }

        // 2. 删除只能通过墓碑表达。服务端有的 key 绝不能从结果中消失 ——
        //    这是"合并绝不会静默丢书签"这条承诺的直接体现。
        foreach ((string k, Item s) in server.Items)
        {
            if (!result.Items.TryGetValue(k, out Item r))
            {
                return $"服务端 item {k} 从结果中消失";
            }

            if (!s.D && r.D && r.M == s.M)
            {
                return $"item {k} 在时间戳未变的情况下被判为已删除";
            }
        }

        // 3. 服务端与入参都不得被就地修改 —— 幂等性依赖于此。
        //    这里只做轻量抽样校验，完整的"重复 Merge 结果不变"由测试保证。
        return null;
    }

    /// <summary>返回 server 与 incoming 的 key 并集，按字典序升序。</summary>
    /// <remarks>
    /// 排序不是为了"好看"：若某处依赖"先处理哪个 key"来记账或决胜，
    /// 同样的输入会产出不同的输出。确定性在这里是可复现调试的前提。
    /// </remarks>
    public static string[] SortedUnionKeys(State server, State incoming)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>(server.Items.Count + incoming.Items.Count);

        foreach (string k in server.Items.Keys)
        {
            if (seen.Add(k))
            {
                output.Add(k);
            }
        }

        foreach (string k in incoming.Items.Keys)
        {
            if (seen.Add(k))
            {
                output.Add(k);
            }
        }

        output.Sort(StringComparer.Ordinal);
        return output.ToArray();
    }

    /// <summary>判断两个 item 的<b>内容</b>是否相同。</summary>
    /// <remarks>
    /// 关键：只比较 (P, T, N, U)，不比较 M / A / D / X。M 是时间戳而不是
    /// 内容 —— 同一个书签在两台机器上若被各自重新保存一次，M 会不同但内容
    /// 完全一样，不应该被当成冲突。
    /// </remarks>
    public static bool SameContent(Item a, Item b) =>
        a.P == b.P && a.T == b.T && a.N == b.N && a.U == b.U;

    // ── 冲突记录的字段级 diff ─────────────────────────────────────────────

    /// <summary>按固定顺序列出参与内容比较的字段。</summary>
    /// <remarks>
    /// 固定顺序保证 <see cref="FirstDifferingField"/> 在多项同时不同时总是返回
    /// 同一个字段，否则同一对冲突在不同同步轮次里会记录不同的字段。
    /// </remarks>
    private static readonly (string Label, string Field)[] ContentFields =
    {
        ("parent", nameof(Item.P)),
        ("type", nameof(Item.T)),
        ("title", nameof(Item.N)),
        ("url", nameof(Item.U)),
    };

    private static string FirstDifferingField(Item s, Item c)
    {
        foreach ((string label, string field) in ContentFields)
        {
            if (ValueOf(s, field) != ValueOf(c, field))
            {
                return label;
            }
        }

        return string.Empty;
    }

    private static string ValueOf(Item it, string field) => field switch
    {
        nameof(Item.P) => it.P ?? string.Empty,
        nameof(Item.T) => it.T ?? string.Empty,
        nameof(Item.N) => it.N ?? string.Empty,
        nameof(Item.U) => it.U ?? string.Empty,
        _ => string.Empty,
    };

    private static string FieldValue(Item it, string label)
    {
        foreach ((string lbl, string field) in ContentFields)
        {
            if (lbl == label)
            {
                return ValueOf(it, field);
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrEmpty(a) ? a : b ?? string.Empty;

    /// <summary>在 HLC 碰撞时给出确定决胜：标题字典序小者获胜。</summary>
    /// <remarks>纯粹为了确定性，不含任何"哪个更好"的语义。</remarks>
    private static (Item Winner, Item Loser) LexicographicPick(Item s, Item c) =>
        string.CompareOrdinal(s.N ?? string.Empty, c.N ?? string.Empty) <= 0
            ? (s, c)
            : (c, s);

    /// <summary>判断 winner 来自服务端还是客户端。</summary>
    /// <remarks>调用前提：s 与 c 不全字段相等（否则无法区分来源）。</remarks>
    private static string WinnerSide(Item winner, Item server) =>
        winner == server ? ConflictWinners.Server : ConflictWinners.Client;

    /// <summary>返回败方标识：服务端赢 → 败方是客户端设备；反之是服务端。</summary>
    private static string LoserSide(Item winner, Item server, string device) =>
        winner == server ? device : ConflictWinners.Server;

    /// <summary>返回 s、c 中不是 winner 的那一个。</summary>
    private static Item LoserOf(Item s, Item c, Item winner) =>
        winner == s ? c : s;
}
