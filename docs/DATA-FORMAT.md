# DATA-FORMAT

Persisted format: `state.json`, schema version **1** (`"v": 1`). A version mismatch
makes the server **refuse to start** rather than migrate (bookmarks are
irreplaceable; silent rewrites are riskier). Types:
`bmsync/src/BookmarkSync.Domain/State.cs`; JSON key order/omitempty rules:
`bmsync/src/BookmarkSync.Domain/StateJsonConverters.cs`,
`bmsync/src/BookmarkSync.Domain/BmsyncJson.cs`.

## State

```jsonc
{
  "v": 1,
  "hlc": "1790000000000-00123",   // authoritative server clock
  "items": { "<key>": { /* item */ }, "...": {} }
}
```

Top-level keys are always written in order `v`, `hlc`, `items`.

## Item fields

Single-letter names save bytes (every sync ships the full state). Empty fields
are omitted on write; unknown fields are ignored on read.

| Field | Name | Meaning |
|---|---|---|
| `p` | parent | Parent key; top-level items use a Firefox root id (`toolbar_____`, `menu________`, `unfiled_____`, `mobile______`) |
| `t` | type | `"b"` bookmark (has URL) or `"f"` folder |
| `n` | name | Title / folder name |
| `u` | url | Bookmarks only |
| `a` | added-at | HLC creation time; display only, **never** used for merge decisions |
| `m` | modified | HLC last-modified time; the **sole** input to LWW comparison |
| `d` | deleted | Tombstone marker |
| `x` | deleted-at | Wall-clock millis of deletion; GC decisions only |

Keys are content-derived (no metadata stored anywhere): bookmarks
`SHA-256("u:" + url)` truncated to 16 bytes; folders
`SHA-256("f:" + parentKey + NUL + title)`. Consequences: changing a URL or a
folder name shows up as delete + add; the same URL in two folders collapses to
one item. Sort order is intentionally **not** synced.

## HLC encoding

`encode(physicalMs, counter)` = 13-digit physical millis + `"-"` + 5-digit
counter, e.g. `"1790000000000-00042"`. Fixed width, so **string lexicographic
order == timestamp order**; valid until year 2286. Counter overflow carries into
`physicalMs + 1`. Both sides (`bmsync/src/BookmarkSync.Domain/Hlc.cs`,
`extension/lib/hlc.js`) must stay behavior-equivalent; contract:
`test/hlc_vectors.json`.

## Tombstones and GC

Deletion writes a tombstone (`d: true`) whose `m` competes in the normal LWW
race — this is an LWW-Element-Set, which is what distinguishes "deleted" from
"never seen". `x` records wall-clock deletion time.

- GC runs after each successful sync: tombstones with `x` older than
  `BMSYNC_TOMBSTONE_TTL_DAYS` (default 90) are removed; `x = 0` tombstones are
  never removed. Only tombstones are ever deleted — never live items.
- Clients need no GC logic: they replace their cache wholesale with the
  server-returned state.
- Offline > 90 days risk: a tombstone may be GC'd before an offline device sees
  it, so a still-present local copy can "resurrect" as an add. Accepted
  trade-off ([architecture](architecture.md) §11).
