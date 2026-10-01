# bmsync — Technical reference

Audience: contributors and reviewers who need to understand the system deeply
enough to change it safely. For setup and operations, start with
`index.md`. For the wire format, see `DATA-FORMAT.md`. This file is the
authoritative English design reference for this project.

---

## 1. System overview

```text
+----------------+                          +----------------+
| Firefox A      |                          | Firefox B      |
|  extension     |                          |  extension     |
|   collect.js   |                          |   collect.js   |
|   apply.js     |                          |   apply.js     |
|   hlc.js       |                          |   hlc.js       |
+-------+--------+                          +--------+-------+
        | POST /api/sync (state + base)              |
        +--------------------------------------------+
        |                                            |
        v                                            v
+------------------------------------------------------------+
| VPS                                                        |
|  bmsync (.NET 10 NativeAOT, FROM scratch)                  |
|   auth (Bearer + rate limit)                               |
|   merge (sole implementation)                              |
|   store (atomic write + mutex)                             |
|   snapshot (last 30) + GC (90-day tombstones)              |
|  data/: state.json, conflicts.json (500-ring), history/    |
+------------------------------------------------------------+
        ^  http://bmsync:8080 (LAN plaintext only)
        |
+------------------------------------------------------------+
| Nginx Proxy Manager: certs, HTTPS termination, proxy       |
+------------------------------------------------------------+
```

Design decisions:

1. **Server is the only merge point.** The extension never merges; it only
   collects (tree → state) and applies (state → tree). This removes a whole
   class of fork bugs and makes sync idempotent.
2. **Full-state transfer.** A typical library (500–5000 bookmarks) is
   80–800 KB JSON — small enough that delta encoding is not worth the
   complexity. gzip in NPM compresses it ~5x.
3. **Client cache is a mirror, replaced wholesale.** After every sync the
   client IndexedDB copy is replaced by the returned server state. GC
   therefore exists in exactly one place (the server).
4. **Failures never touch local bookmarks.** Any non-200 response leaves the
   Firefox tree unchanged. Partial apply is reported as "partial, retry is
   safe" with no rollback (the merge direction guarantees retry converges).

---

## 2. Identity (the core of the design)

Firefox gives only local integer ids (recycled, non-portable) and exposes no
custom-metadata store. Keys are therefore derived from content, with no
stored metadata:

```text
Bookmark: key = SHA-256("u:" + url)[0..16]  → 32 lowercase hex
Folder:   key = SHA-256("f:" + parentKey + NUL + title)[0..16]
```

- `NUL` (`String.fromCharCode(0)`, never a literal) separates folder fields:
  URLs and titles can contain `|`/`:` but never NUL, so the join is
  unambiguous.
- Bookmark keys exclude the parent: moving a bookmark changes only `p`
  (correct "move"); including it would turn every folder rename into
  delete-all + add-all.
- Folder keys include the parent: renaming/moving a folder rebuilds the
  subtree (content preserved via LWW, but many tombstones).
- Same URL in two folders merges into one; same URL twice in one folder
  dedupes. Both accepted by design.
- Four Firefox roots are fixed sentinels (`toolbar_____`, `menu________`,
  `unfiled_____`, `mobile______`), used only as `p` values, never as items.
  `mobile______` is excluded by default. Matching is by id, never by
  localized title, with a positional fallback + one-time UI warning.

Implementation: `extension/lib/keys.js` (+ tests in `keys.test.js`).
Server only validates key shape (`Limits.KeyLen = 32`, lowercase hex).

---

## 3. Data model

```jsonc
{
  "v": 1,
  "hlc": "1790000000000-00042",
  "items": {
    "<32-hex-key>": {
      "p": "toolbar_____", "t": "b", "n": "Example",
      "u": "https://example.com",
      "a": "1790000000000-00000",
      "m": "1790000000000-00007",
      "d": false, "x": 0
    }
  }
}
```

| Field | Meaning | Merge role |
|---|---|---|
| `p` | Parent key or root sentinel | Content (compared) |
| `t` | `b` bookmark / `f` folder | Content |
| `n` | Title / folder name | Content |
| `u` | URL (bookmarks only) | Content |
| `a` | Created HLC | Display only |
| `m` | Modified HLC | **Sole LWW comparison** |
| `d` | Tombstone flag | Content (a tombstone is an ordinary item) |
| `x` | Deletion wall-clock ms | GC only |

Invariants (must always hold):

1. Keys are immutable identities, never positions.
2. Merge compares only `m`; `a`/`x` never decide.
3. HLC strings are strictly monotonic; lexicographic order == time order.
4. Non-200 never mutates local bookmarks.
5. Server deletes items only via GC on tombstones.
6. Client cache always equals the last returned server state.

Short wire names (`p/t/n/u/a/m/d/x`) are intentional compactness (5000 items:
~1.1 MB long names vs ~800 KB short names over full-state POST). C# property
names are `Item.P/T/N/U/A/M/D/X` with `[JsonPropertyName("p")]` etc.;
readability comes from docs and locals, not by lengthening the wire.

---

## 4. Sync pipeline

### 4.1 Collect (extension → outbound state)

```text
1. tree = browser.bookmarks.getTree()
2. Flatten preorder: firefoxLocalId → syncKey, syncKey → {type,title,url,parentSyncKey}
3. Diff vs cached.items on (p,t,n,u) only:
   live+!cached      → NEW (m = HLC.now())
   live+cached       → changed? MODIFIED (m = now) : keep cached.m (no re-stamp)
   live+cached(d)    → RESURRECT (d=false, m = now)
   !live+cached      → DELETED (d=true, x=nowMs, m=now)
   !live+!cached     → ignore (already GC'd)
4. base = { key: cached[key].m } (slim key→m map for 3-way detection)
5. POST /api/sync { device, state, base }
```

Roots never enter `items`; `mobile______` excluded unless enabled.
Unchanged items keep `cached.m` — re-stamping everything would fake
conflicts every round.

### 4.2 Merge (server, sole implementation)

```text
for key in union(server.items, incoming.items) sorted:
  S = server[key]; C = incoming[key]; B = base[key] (-inf if absent)
  winner = missing-side ?? newer-m ?? (equal-m ?
             sameContent ? S : lexicographic-pick + hlc_collision : …)
  stats (server view): created / updated / deleted / unchanged
  if B present and C.m>B and S.m>B and content differs:
    log concurrent_edit (observation only; winner unchanged)
```

- Sorted union → deterministic output for identical input.
- Equal-`m` + differing content → title-ordinal fallback + `hlc_collision`
  (signals HLC divergence; should not happen).
- `base` suppresses noise: without it, just-merged content would log as a
  conflict every round. First sync (`base` empty) logs zero conflicts.
- `MergeInvariants` asserts: result keys == union; no server key vanishes;
  no deletion without timestamp change. Violation throws (programmer bug,
  never a corrupt authoritative state).

Code: `bmsync/src/BookmarkSync.Domain/Merge.cs`. Tests: `MergeTests.cs`
(idempotent, order-independent, randomized convergence, GC boundaries).

### 4.3 Apply (inbound state → Firefox)

```text
1. desired = incoming minus tombstones; re-scan live tree
2. Deletes top-down: live − desired, shallowest first
   (Firefox remove() is recursive for folders)
3. Creates/updates by ascending depth (parents first):
   missing → create; parentKey differs → move; title/url differ → update
4. Missing parents are skipped (never silently relocated to root)
5. Counts → { created, updated, deleted }; cache = returned state
```

Order matters: deletes first (else creates look pre-existing), parents
before children (else `create` fails on invalid parentId). No rollback on
mid-apply failure — retry converges by LWW direction.

Code: `extension/lib/apply.js` (`planApply` pure + `executePlan` effects).

### 4.4 No ordering

No `sortKey` is carried; `create` omits `index` so new items append.
Content converges; intra-folder order does not. Fractional indexing is the
documented future path (requires `onMoved` tracking).

---

## 5. Clock: HLC

State: `(physicalMs: uint64 millis, counter: uint32)`.

```text
now():                                  # local event
  wall = clock()
  if wall > physicalMs: physicalMs = wall; counter = 0
  else:                 counter += 1
  normalize(); return encode()

update(remote):                         # after observing (rPhysical, rCounter)
  wall = clock(); maxObserved = max(physicalMs, wall, rPhysical)
  if   maxObserved == physicalMs && maxObserved == wall: counter = max(counter, rCounter) + 1
  elif maxObserved == physicalMs:  counter += 1
  elif maxObserved == rPhysical:    counter = rCounter + 1
  else:                            counter = 0
  physicalMs = maxObserved; normalize(); return encode()
```

**Branch order is load-bearing.** The third arm must be
`max == remote`, not `max == wall`: when the wall clock catches up to a
remote millis, `counter = 0` would sort *before* the observed remote stamp,
breaking causality into alternating wins and silent loss. Regression tests
pin both remote-ahead shapes.

- Encoding: `encode = 13-digit millis + '-' + 5-digit counter`
  (good until year 2286); lexicographic == temporal.
- Overflow: `counter > 99999 → physicalMs+1, counter=0`.
- Invalid stamps sort below every valid one (corrupted side always loses);
  two invalid sort ordinally for determinism.
- Calibration happens every sync: server `ObserveMany(all a/m)` before
  merge; client `update(response.hlc)` after. Wall-clock edits to the past
  cannot move `physicalMs` backwards.

Parity contract: `test/hlc_vectors.json` (15 vectors). C# replays +
independently recomputes; JS replays. Implementations:
`bmsync/src/BookmarkSync.Domain/Hlc.cs` ↔ `extension/lib/hlc.js`
(behavior-equivalent; JS camelCase vs C# PascalCase is idiomatic, vectors
are the authority).

---

## 6. Server (.NET 10)

```text
Cli → Server → Store → Domain
```

- **Domain** (`BookmarkSync.Domain`): model + merge + HLC + GC. References
  nothing else (not even logging). Value types in/out; fully testable
  without HTTP or disk.
- **Store** (`BookmarkSync.Store`): `BookmarkStore` holds state + a
  `SemaphoreSlim` across read → merge → GC → write (single-process, no file
  locks; snapshots happen outside the hottest path where possible).
  Atomic write = tmp → fsync → rename → dir-sync, with Windows
  ACCESS_DENIED/SHARING_VIOLATION backoff (`FileOps`). Own minimal
  `StoreOptions` (never reuses HTTP options — dependency direction matters).
- **Server** (`BookmarkSync.Server`): Minimal API, four routes. `Auth`
  (constant-time Bearer + per-IP 60/min token bucket, limit-before-auth
  ordering so 429/401 cannot oracle token existence), `HttpJson` (body cap
  16 MB, `Retry-After: 60`), source-generated JSON (AOT requirement).
- **Cli** (`BookmarkSync.Cli`, assembly `bmsync`): startup, `--config-check`
  (validates without binding), graceful shutdown, `healthcheck` subcommand
  (scratch has no shell/wget, so Docker exec-calls the binary and checks the
  `service` field, not just 200).

Zero third-party NuGet in production (only
`Microsoft.Extensions.Logging.Abstractions` for `ILogger`).

Storage (`/data`):

```text
state.json  conflicts.json (500-ring)  meta in memory
history/<UTC-stamp>.json (last 30; failures never block sync)
```

Startup refuses on schema mismatch or bad token (never silently migrates
user-irreplaceable data).

---

## 7. Extension (Firefox MV3)

Pure ESM, zero deps, no build. `about:debugging` → load
`extension/manifest.json`.

- `background.js`: message routing (`sync`/`getState`/`testConnection`/
  `getConflicts`), badge state machine, in-flight guard (idempotent merge
  makes double-clicks safe; the guard only avoids UI jitter).
- `lib/collect.js` / `apply.js` / `hlc.js` / `keys.js`: pure + injectable
  (`api` param, never bare `browser.*`) so Node tests run without Firefox.
  `mock-bookmarks.js` mimics real quirks (url-presence typing, four fixed
  root ids, recursive folder remove).
- `lib/client.js`: timeout, error mapping (non-200 never mutates), host
  permission via user gesture (MV3 requirement — first sync always shows a
  prompt; this is expected, not a bug). Firefox MV3 uses
  `background.scripts`, not `service_worker`.
- `lib/store.js`: IndexedDB thin wrapper (`K_STATE` replaced wholesale).
- `lib/sync.js`: one full sync orchestration (collect → POST → calibrate →
  apply → replace cache).
- `popup/` + `options/`: sync button, last-sync time, summary (cloud totals +
  delta), conflicts entry, settings (URL/token/test/roots/clear-cache).

---

## 8. HTTP API

Base `/api`, HTTPS only (terminated at NPM). `GET /api/health` is
unauthenticated (used by the settings "Test connection" button).

| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/sync` | Bearer | Merge + return authoritative state |
| GET | `/api/health` | none | Liveness + item counts |
| GET | `/api/conflicts?limit=50` | Bearer | 500-ring conflict log |
| GET | `/api/history` | Bearer | Snapshot list (read-only; rollback is manual) |

`POST /api/sync` request: `{ device, state, base }`.
Response: `{ state, summary{created,updated,deleted,unchanged}, conflicts, hlc, serverTime }`.
Summary is server-view; UI copy adjusts accordingly.

Errors: `400 bad_request`, `401 unauthorized`, `413 payload_too_large`,
`422 validation_failed`, `429 rate_limited` (+ `Retry-After`), `500
internal_error`. Protections: 16 MB body cap, 50k items, depth 32, 10 s
per-request budget, 60/min/IP, no CORS (extension fetch), method/path/status
logs only (never body/token).

---

## 9. Deploy & operations

- Compose binds `127.0.0.1:18081:8080` (loopback only; NPM on the same host
  proxies it). Never expose bmsync directly.
- NPM must set `client_max_body_size 32m;` (default 1 MB 413s libraries over
  ~6000 items) and keep `gzip` on for `application/json`.
- Dockerfile: SDK test → NativeAOT publish → `FROM scratch` (+ 3 glibc files
  AOT still needs). Healthcheck execs `/bmsync healthcheck`.
- Backups: `deploy/data/` daily (`backup.sh`, keep 14) + `history/` snapshots
  (30, pre-write). Rollback = stop, copy snapshot over `state.json`, start,
  re-sync both sides. Token rotation = new `BMSYNC_TOKEN`, restart, update
  every device. See `../docs/OPERATIONS.md`, `../docs/DEPLOYMENT.md`.

Ports (three contexts, often confused):

| Context | Value | Meaning |
|---|---|---|
| Compose host bind | `127.0.0.1:18081` | NPM dials this |
| Container | `:8080` | bmsync listens here |
| Local `dev.sh` | `127.0.0.1:18099` | Extension debugging |

---

## 10. Testing

`./test/all.sh`, five layers fastest-first: shell/C# syntax+build →
extension consistency (no Node) → extension unit (Node 18+) → .NET unit +
coverage + Linux publish → smoke (real HTTP + real files).

- Domain/Store/Server tri-project xunit; HTTP tests run real Kestrel
  (middleware order, auth-before-limit, content types — exactly what unit
  fakes miss).
- Extension `node --test` (90 cases: HLC vectors, keys, collect 6 scenarios,
  apply ordering + round-trip collect→apply→collect equivalence).
- `check-extension.sh` (handler/HTML-id/import/cross-port constants;
  mutation-tested with 5 fake ids).
- `smoke.sh` (28 assertions, 9 groups: auth, convergence, delete+TDD,
  idempotency, conflicts+fields, malformed, snapshots, limits, on-disk
  parseability). `jsonq/` is the dependency-free JSON reader fallback
  (`python3 → jq → jsonq`); it is a legacy Go helper, not a server dep.

Manual gate before release: two-profile sync (12 items: add/delete/folder
delete/rename/move/idempotent/offline-concurrent/auth-fail/offline-fail/
concurrent-title), clock-skew ±1h both directions, 6000+ item 413 check,
token-mismatch zero-local-change. See `../docs/TESTING.md`.

---

## 11. Risks & trade-offs (accepted)

- Merge bug → silent loss. Mitigated by pre-write snapshots, table-driven +
  randomized convergence tests, tombstone-only deletion, apply-plan logs,
  fake-data-first rollout.
- Full-state size (>20k items ≈ 3 MB). Mitigated by NPM gzip + raised body
  cap; sharding explicitly deferred until measured.
- >90-day offline resurrection. Accepted + documented; per-device
  `lastSeenAt` refusal is the recorded alternative.
- Far-future wall clock (e.g. year 2035) suppresses others until real time
  catches up. v1.1: local-clock anomaly warning (>1y vs serverTime).
- No tags/annotations/ordering/mobile-roots/manual conflict UI — explicit
  non-goals (§1.3, §13 Q1–Q5 defaults: field-level diffs, server-visible
  log, cloud-total + delta summary, pure LWW, mobile excluded).

---

## 12. File map (where to look)

```text
Merge:      bmsync/src/BookmarkSync.Domain/Merge.cs
HLC:        bmsync/src/BookmarkSync.Domain/Hlc.cs ↔ extension/lib/hlc.js
Identity:   extension/lib/keys.js
Collect:    extension/lib/collect.js    Apply: extension/lib/apply.js
Protocol:   bmsync/src/BookmarkSync.Server/ApiTypes.cs
Trust:      bmsync/src/BookmarkSync.Server/Auth.cs
Store:      bmsync/src/BookmarkSync.Store/BookmarkStore.cs
Entry:      bmsync/src/BookmarkSync.Cli/Program.cs
Vectors:    test/hlc_vectors.json
Checks:     test/all.sh  smoke.sh  check-extension.sh  dev.sh
Deploy:     deploy/docker-compose.yml  bmsync/Dockerfile
Diagrams:   docs/c4/*.puml
```

Change the protocol? Read `ApiTypes.cs` + `collect.js` together, bump schema,
note it in `docs/DATA-FORMAT.md`, and replay vectors on both HLC sides.
