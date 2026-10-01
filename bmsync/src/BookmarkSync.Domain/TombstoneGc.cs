namespace BookmarkSync.Domain;

/// <summary>Tombstone garbage collection.</summary>
/// <remarks>
/// <para>
/// ── Why tombstones exist ─────────────────────────────────────────────<br/>
/// A tombstone (item with d=true) exists for one reason: <b>propagating
/// deletion to other devices</b>. Without a trace, the other side would treat
/// its still-present local bookmark as "a new item the server lacks" and
/// re-upload it — the bookmark resurrects for no apparent reason.
/// </para>
/// <para>
/// ── Why they are collected ───────────────────────────────────────────<br/>
/// Keeping them forever would grow state.json without bound, and their
/// information value expires once <b>every</b> device no longer has the
/// bookmark locally.
/// </para>
/// <para>
/// The 90-day basis: far longer than any plausible gap between syncs of two
/// devices. A device offline longer than that already has unreliable data
/// (see docs/architecture.md §11).
/// </para>
/// <para>
/// ── Safety constraints ───────────────────────────────────────────────<br/>
/// 1. <b>Delete tombstones only.</b> No live (d=false) item is ever touched —
///    a GC bug here means silent bookmark loss.<br/>
/// 2. <b>Use x (wall-clock deletion time), not m.</b> HLC drifts when clocks
///    are tampered with; x is a plain wall clock with clear semantics.<br/>
/// 3. <b>Never delete tombstones with x == 0.</b> Keeping an extra few hundred
///    bytes beats deleting the wrong thing.
/// </para>
/// </remarks>
public static class TombstoneGc
{
    /// <summary>Removes expired tombstones, returning the new map plus the removal count.</summary>
    public static (Dictionary<string, Item> Items, int Removed) Collect(
        Dictionary<string, Item> items,
        long now,
        TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(items);

        long cutoff = now - (long)ttl.TotalMilliseconds;
        var output = State.NewItemsDictionary();
        int removed = 0;

        foreach ((string key, Item item) in items)
        {
            if (item.D && item.X > 0 && item.X < cutoff)
            {
                removed++;
                continue;
            }

            output[key] = item;
        }

        return (output, removed);
    }

    /// <summary>
    /// Collects expired tombstones from <paramref name="state"/>, returning a
    /// <b>new</b> State.
    /// </summary>
    /// <remarks>
    /// Returns a new object instead of mutating: <c>State.Items</c> is an
    /// <c>init</c> property, so "did GC take effect" is visible in the
    /// signature. A void method mutating an immutable-looking object would
    /// force callers to re-read to confirm — easy to miss.
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
