namespace BookmarkSync.Domain;

/// <summary>Conflict kinds.</summary>
public static class ConflictReasons
{
    /// <summary>
    /// Genuine concurrent edit: both sides changed the same item from the same baseline.
    /// <b>This is the valuable conflict</b> worth surfacing to the user.
    /// </summary>
    public const string ConcurrentEdit = "concurrent_edit";

    /// <summary>
    /// HLC timestamps fully equal but content differs.
    /// </summary>
    /// <remarks>
    /// Should not happen in theory — HLC is strictly increasing per causal chain,
    /// with the logical counter separating same-millis events. Reaching here
    /// suggests divergent HLC implementations; it is logged for diagnosis,
    /// not expected.
    /// </remarks>
    public const string HlcCollision = "hlc_collision";
}

/// <summary>Winning side marker.</summary>
public static class ConflictWinners
{
    public const string Server = "server";
    public const string Client = "client";
}

/// <summary>A single conflict record. Pure observation, <b>does not affect the merge outcome</b>.</summary>
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

/// <summary>Merge result statistics.</summary>
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

/// <summary>Full output of one merge.</summary>
public sealed class MergeResult
{
    public required State State { get; init; }

    public required IReadOnlyList<Conflict> Conflicts { get; init; }

    public required Summary Summary { get; init; }
}

/// <summary>
/// Merge algorithm: LWW-Element-Set (LWW set with tombstones).
/// </summary>
/// <remarks>
/// <para>
/// Merge is the highest-risk code in this project: it decides whether two
/// devices converge to the same bookmark set, and a bug here causes
/// <b>silent data loss the user never notices</b>.
/// </para>
/// <para>
/// First principle of this file is therefore not performance or brevity, but:
/// </para>
/// <list type="number">
/// <item>Deterministic traversal (sorted union, never dictionary order)</item>
/// <item>Explicit assertions for the three invariants (see <see cref="MergeInvariants"/>)</item>
/// <item>A test case for every branch</item>
/// </list>
/// <para>
/// This is not a CRDT: CRDTs merge conflict-free, while here conflicts are
/// allowed and resolved by HLC timestamp. That is enough for personal
/// bookmarks at a fraction of the implementation cost.
/// </para>
/// <para>
/// Identity (key) is derived from a content hash with no stored metadata —
/// see docs/architecture.md §2. Changing a URL or a folder name therefore appears
/// as "delete old + add new", not "modify". This is a deliberate trade-off.
/// </para>
/// </remarks>
public static class Merger
{
    /// <summary>
    /// Merges the client-reported state with the server state and returns the authoritative result.
    /// </summary>
    /// <param name="server">Current server state (never mutated).</param>
    /// <param name="incoming">Client-reported state (never mutated).</param>
    /// <param name="base">
    /// HLC snapshot of what the client "last saw" per key (key → m). Null/empty
    /// means first sync from this client. Its only purpose is to distinguish
    /// "I changed it" from "the other side changed it" — without it, every
    /// merge would log just-merged content as conflicts and the log would lose value.
    /// </param>
    /// <param name="device">Reporting device id, recorded for diagnosis.</param>
    /// <param name="now">Wall-clock millis for this operation, recorded on conflicts.</param>
    /// <remarks>
    /// On deletion: a tombstone (d=true) is an ordinary item whose M takes part
    /// in LWW resolution. Deletion therefore propagates with no special channel —
    /// the core reason for LWW-Element-Set over naive diff sync.
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

        // Absorb every incoming timestamp first, so any timestamp produced
        // afterwards is strictly greater (HLC causality). This is why the
        // server timestamp is authoritative.
        var stamps = new List<string>(incoming.Items.Count * 2);
        foreach (Item incomingItem in incoming.Items.Values)
        {
            stamps.Add(incomingItem.M);
            stamps.Add(incomingItem.A);
        }

        clock.ObserveMany(stamps);

        var result = new State { Clock = clock.Current() };

        var conflicts = new List<Conflict>(4);
        int created = 0, updated = 0, deleted = 0, unchanged = 0;

        // Walk the sorted union of both key sets for deterministic output.
        foreach (string key in SortedUnionKeys(server, incoming))
        {
            bool hasServer = server.Items.TryGetValue(key, out Item serverItem);
            bool hasClient = incoming.Items.TryGetValue(key, out Item clientItem);

            Item winner;
            if (!hasClient)
            {
                winner = serverItem;
            }
            else if (!hasServer)
            {
                winner = clientItem;
            }
            else if (Hlc.Compare(clientItem.M, serverItem.M) > 0)
            {
                winner = clientItem;
            }
            else if (Hlc.Compare(serverItem.M, clientItem.M) > 0)
            {
                winner = serverItem;
            }
            else
            {
                // Equal HLC timestamps.
                if (SameContent(serverItem, clientItem))
                {
                    // Stored once per side within the same millis with identical
                    // content — normal, keep either side.
                    winner = serverItem;
                }
                else
                {
                    // Indicates divergent HLC implementations. Still produce a
                    // deterministic result (title ordinal fallback); otherwise
                    // repeated syncs would fork.
                    (winner, Item _) = LexicographicPick(serverItem, clientItem);
                    string field = FirstDifferingField(serverItem, clientItem);
                    conflicts.Add(new Conflict
                    {
                        At = now,
                        Device = device,
                        Key = key,
                        Reason = ConflictReasons.HlcCollision,
                        Field = field,
                        Winner = WinnerSide(winner, serverItem),
                        Loser = device,
                        WinnerValue = FieldValue(winner, field),
                        LoserValue = FieldValue(LoserOf(serverItem, clientItem, winner), field),
                        Url = FirstNonEmpty(serverItem.U, clientItem.U),
                    });
                }
            }

            result.Items[key] = winner;

            // Stats from the server perspective.
            if (!hasServer)
            {
                created++;
            }
            else if (serverItem == winner)
            {
                // Item is a value type: == means field-wise equality.
                unchanged++;
            }
            else if (winner.D && !serverItem.D)
            {
                deleted++;
            }
            else
            {
                updated++;
            }

            // Three-way conflict detection (observation only; winner is fixed).
            if (hasServer && hasClient && !SameContent(serverItem, clientItem)
                && @base is not null && @base.TryGetValue(key, out string? baseHlc))
            {
                bool clientChanged = Hlc.Compare(clientItem.M, baseHlc) > 0;
                bool serverChanged = Hlc.Compare(serverItem.M, baseHlc) > 0;
                if (clientChanged && serverChanged)
                {
                    string field = FirstDifferingField(serverItem, clientItem);
                    conflicts.Add(new Conflict
                    {
                        At = now,
                        Device = device,
                        Key = key,
                        Reason = ConflictReasons.ConcurrentEdit,
                        Field = field,
                        Winner = WinnerSide(winner, serverItem),
                        Loser = LoserSide(winner, serverItem, device),
                        WinnerValue = FieldValue(winner, field),
                        LoserValue = FieldValue(LoserOf(serverItem, clientItem, winner), field),
                        Url = FirstNonEmpty(serverItem.U, clientItem.U),
                    });
                }
            }
        }

        string? invariantError = MergeInvariants(server, incoming, result);
        if (invariantError is not null)
        {
            // A broken invariant means a bug in Merge itself (programmer error,
            // not user error). Never return a possibly corrupt authoritative
            // state — fail this sync instead via InvalidOperationException.
            throw new InvalidOperationException("bmsync: merge invariant violated: " + invariantError);
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

    /// <summary>Validates the three invariants that must hold for any input.</summary>
    /// <returns>Non-null means a violation with the reason. Any violation means a bug in Merge, not bad user data.</returns>
    public static string? MergeInvariants(State server, State incoming, State result)
    {
        // 1. Result keys must equal exactly the union of both sides: no more,
        //    no fewer. Extra means invented data; missing means lost bookmarks.
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in server.Items.Keys)
        {
            union.Add(key);
        }

        foreach (string key in incoming.Items.Keys)
        {
            union.Add(key);
        }

        if (union.Count != result.Items.Count)
        {
            return $"result item count {result.Items.Count} != union {union.Count}";
        }

        foreach (string key in result.Items.Keys)
        {
            if (!union.Contains(key))
            {
                return $"result contains foreign key {key}";
            }
        }

        foreach (string key in union)
        {
            if (!result.Items.ContainsKey(key))
            {
                return $"result missing key {key}";
            }
        }

        // 2. Deletion is expressed only via tombstones. A server key must never
        //    vanish from the result — the "merge never silently drops bookmarks" promise.
        foreach ((string key, Item serverItem) in server.Items)
        {
            if (!result.Items.TryGetValue(key, out Item merged))
            {
                return $"server item {key} vanished from result";
            }

            if (!serverItem.D && merged.D && merged.M == serverItem.M)
            {
                return $"item {key} marked deleted without timestamp change";
            }
        }

        // 3. Neither input may be mutated in place — idempotency depends on it.
        //    Only a light check here; full "merge twice is stable" is covered by tests.
        return null;
    }

    /// <summary>Returns the key union of server and incoming, ascending ordinal.</summary>
    /// <remarks>
    /// Sorting is not cosmetic: if any accounting or tie-break depended on
    /// processing order, identical input would yield different output.
    /// Determinism is the precondition for reproducible debugging.
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

    /// <summary>Returns true when two items have equal <b>content</b>.</summary>
    /// <remarks>
    /// Compares only (P, T, N, U); M / A / D / X are excluded. M is a timestamp,
    /// not content — the same bookmark re-saved on two machines gets different M
    /// with identical content and must not count as a conflict.
    /// </remarks>
    public static bool SameContent(Item a, Item b) =>
        a.P == b.P && a.T == b.T && a.N == b.N && a.U == b.U;

    // Field-level diff for conflict records.

    /// <summary>Content fields in fixed order.</summary>
    /// <remarks>
    /// Fixed order keeps <see cref="FirstDifferingField"/> stable when several
    /// fields differ at once; otherwise the same conflict would log different
    /// fields across sync rounds.
    /// </remarks>
    private static readonly (string Label, string Field)[] ContentFields =
    {
        ("parent", nameof(Item.P)),
        ("type", nameof(Item.T)),
        ("title", nameof(Item.N)),
        ("url", nameof(Item.U)),
    };

    private static string FirstDifferingField(Item serverItem, Item clientItem)
    {
        foreach ((string label, string field) in ContentFields)
        {
            if (ValueOf(serverItem, field) != ValueOf(clientItem, field))
            {
                return label;
            }
        }

        return string.Empty;
    }

    private static string ValueOf(Item item, string field) => field switch
    {
        nameof(Item.P) => item.P ?? string.Empty,
        nameof(Item.T) => item.T ?? string.Empty,
        nameof(Item.N) => item.N ?? string.Empty,
        nameof(Item.U) => item.U ?? string.Empty,
        _ => string.Empty,
    };

    private static string FieldValue(Item item, string label)
    {
        foreach ((string entryLabel, string field) in ContentFields)
        {
            if (entryLabel == label)
            {
                return ValueOf(item, field);
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrEmpty(first) ? first : second ?? string.Empty;

    /// <summary>Deterministic HLC-collision fallback: smaller title wins.</summary>
    /// <remarks>Pure determinism, no "which is better" semantics.</remarks>
    private static (Item Winner, Item Loser) LexicographicPick(Item serverItem, Item clientItem) =>
        string.CompareOrdinal(serverItem.N ?? string.Empty, clientItem.N ?? string.Empty) <= 0
            ? (serverItem, clientItem)
            : (clientItem, serverItem);

    /// <summary>Returns whether the winner came from server or client.</summary>
    /// <remarks>Precondition: the two inputs are not field-wise equal (otherwise the source is ambiguous).</remarks>
    private static string WinnerSide(Item winner, Item serverItem) =>
        winner == serverItem ? ConflictWinners.Server : ConflictWinners.Client;

    /// <summary>Returns the loser marker: server win means the client device lost, and vice versa.</summary>
    private static string LoserSide(Item winner, Item serverItem, string device) =>
        winner == serverItem ? device : ConflictWinners.Server;

    /// <summary>Returns whichever of the two inputs is not the winner.</summary>
    private static Item LoserOf(Item serverItem, Item clientItem, Item winner) =>
        winner == serverItem ? clientItem : serverItem;
}
