# TESTING

Automated layers are covered in [DEVELOPMENT](DEVELOPMENT.md). This page is the
manual two-device checklist. It covers what automated tests cannot: real Firefox
behavior on two profiles sharing one server.

## Setup

Use two Firefox profiles on one machine before using two machines — it is faster
and exercises the same server path. The two profiles share one server instance;
that is the scenario under test.

Load the extension via `about:debugging#/runtime/this-firefox` → temporary load
→ select `extension/manifest.json`. After code changes, hit Reload there.

## 12-item checklist

Profile A simulates the personal PC, B the work PC.

| # | Steps | Expected |
|---|---|---|
| 1 | B: fill in settings → "Test connection" | Shows server item count (`GET /api/health`) |
| 2 | B: "Sync now" → grant the site permission when prompted | B shows all of A's bookmarks (first sync uploads when the cloud is empty, else merges) |
| 3 | A: add 3 bookmarks → A syncs → B syncs | B shows the 3 |
| 4 | A: delete 1 → A syncs → B syncs | Gone on B (**core feature**: delete propagation) |
| 5 | A: delete a whole folder incl. children → sync both | Whole folder gone on B (every subtree item gets a tombstone; see `extension/lib/collect.js`) |
| 6 | A: rename a title → sync both | B title updated |
| 7 | A: drag a bookmark into an **already existing** folder → sync both | Same location on B (regression-prone: `extension/lib/apply.js` once failed to resolve unchanged parent dirs) |
| 8 | A: sync 3 times in a row | Bookmark count unchanged (idempotency) |
| 9 | A adds 5 while offline, B adds 3 online → A syncs → B syncs | Both end with 8 (concurrent merge) |
| 10 | Break the token → sync | Clear error, **local bookmarks untouched** |
| 11 | Go offline → sync | Clear error, **local bookmarks untouched** |
| 12 | Both edit the same title concurrently → sync | Silent LWW win by timestamp; entry visible under conflicts (settings → conflict list, `GET /api/conflicts`) |

Rules: content equality passes — **folder order is not synced** (new items append
at the end; see [DATA-FORMAT](DATA-FORMAT.md)). Any non-200 response must never
modify local bookmarks (`extension/lib/sync.js`, [architecture](architecture.md) §4).

## Clock-skew test (recommended)

LWW depends on timestamps; HLC (`bmsync/src/BookmarkSync.Domain/Hlc.cs` ↔
`extension/lib/hlc.js`) preserves causal order:

1. Set A's system clock 1 hour ahead.
2. A edits/adds bookmarks → A syncs.
3. Restore the clock.
4. B syncs → B must receive A's changes; repeat in reverse.

If edits get overwritten here, HLC is broken — check `update()`. See also the
CJK/emoji and >6000-item cases in [architecture](architecture.md) §10 when running the full pass.
