namespace BookmarkSync.Domain;

/// <summary>墓碑清理。</summary>
/// <remarks>
/// <para>
/// ── 为什么需要墓碑 ────────────────────────────────────────────────────<br/>
/// 墓碑（d=true 的 item）存在的唯一理由是<b>把删除传播到其他设备</b>。
/// 一台设备删了书签，这个事实在 state 里必须留痕，否则另一台设备会
/// 认为自己本地那个书签是"服务端没有的新增"，从而重新上传它 ——
/// 书签会复活，而且用户会觉得莫名其妙。
/// </para>
/// <para>
/// ── 为什么要清理 ──────────────────────────────────────────────────────<br/>
/// 墓碑永远留着会让 state.json 无限膨胀。且它的信息价值有保质期：
/// 只要<b>所有</b>设备的本地书签树里都已经没有这个书签，墓碑就没有了。
/// </para>
/// <para>
/// 90 天这个数字的依据：它远大于任何"两台设备之间最长可能的不同步间隔"。
/// 超过 90 天没同步过的设备，其数据本身就已经不可靠了（见 design.md §12.3）。
/// </para>
/// <para>
/// ── 安全约束 ──────────────────────────────────────────────────────────<br/>
/// 1. <b>只删墓碑。</b>任何 d=false 的活跃 item 都不会被 GC 碰。
///    这是最重要的一条 —— GC 出 bug 的后果是静默丢书签。<br/>
/// 2. <b>只看 x（删除墙钟时间），不看 m。</b>m 是 HLC，时钟被改过时
///    它的含义会漂移；x 是普通墙钟，虽然也不可靠但语义明确。<br/>
/// 3. <b>x == 0 的墓碑不删。</b>删不掉的比删错好：它可能是刚产生的
///    墓碑但时间戳没写上，留着只是浪费几百字节。
/// </para>
/// </remarks>
public static class TombstoneGc
{
    /// <summary>清理过期墓碑，返回新字典与被清理的数量。</summary>
    public static (Dictionary<string, Item> Items, int Removed) Collect(
        Dictionary<string, Item> items,
        long now,
        TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(items);

        long cutoff = now - (long)ttl.TotalMilliseconds;
        var output = State.NewItemsDictionary();
        int removed = 0;

        foreach ((string k, Item it) in items)
        {
            if (it.D && it.X > 0 && it.X < cutoff)
            {
                removed++;
                continue;
            }

            output[k] = it;
        }

        return (output, removed);
    }

    /// <summary>
    /// 清理 <paramref name="state"/> 的过期墓碑，返回装好新字典的<b>新</b> State。
    /// </summary>
    /// <remarks>
    /// 返回新对象而不是就地修改：<c>State.Items</c> 是 <c>init</c> 属性，
    /// 这样"GC 有没有生效"从签名上一目了然 —— 一个 void 方法去改一个不可变的
    /// 对象，调用方只能靠"改完之后再读一遍"来确认，很容易漏掉。
    /// </remarks>
    public static (State State, int Removed) CollectFrom(
        State state,
        long now,
        TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(state);

        (Dictionary<string, Item> items, int removed) = Collect(state.Items, now, ttl);
        if (removed == 0)
        {
            return (state, 0);
        }

        return (new State { V = state.V, Clock = state.Clock, Items = items }, removed);
    }
}
