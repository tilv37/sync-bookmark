# Code review pass — readability & open-source cleanup

What this pass changed, what it deliberately did not change, and where the
next pass should look. Intended for reviewers of the open-source release.

> Wire compatibility was the hard constraint: JSON names (`p/t/n/u/a/m/d/x`,
> `v/hlc/items`), HLC encoding, key derivation, and API shapes are unchanged.
> Renames are locals/docs only.

---

## 1. Naming: what changed

**Principle:** wire-short names stay on the wire; code uses words.

- `Item.P/T/N/U/A/M/D/X` (public C#) kept — they are the protocol, pinned by
  `[JsonPropertyName]` + `ItemJsonConverter` + `StateJsonConverter` +
  `JsonParityTests`. Renaming them would fork `state.json` and every client.
  Readability is carried by XML docs + a field table (`doc/architecture.md`
  §3, `docs/DATA-FORMAT.md`), not by lengthening the wire.
- Same for JS `p/t/n/u/a/m/d/x` in `collect.js`/`apply.js`/`sync.js`.
- Locals renamed everywhere:
  - C#: `_l/_c → _physicalMs/_logicalCounter`, `s/c → serverItem/clientItem`,
    `sOk/cOk → hasServer/hasClient`, `bm → baseHlc`, `k → key`,
    `a/b → presented/expected (Auth)`, `e/cfg/d/t → env/options/envMap/token`,
    `oldPath/newPath → sourcePath/destPath`, `i/cur → attemptIndex/current`,
    `t/s → timestamp/candidate`, `al/ac/bl/bc → leftPhysical/...`.
  - JS: `encode(l,c) → encode(physicalMs,counter)`,
    `decode(s) → decode(hlcString)`, `compare(a,b) → compare(leftHlc,rightHlc)`,
    `#l/#c → #physicalMs/#logicalCounter`, `p → wallNowMs/physicalNow`,
    `rd/d → remoteDecoded/decoded`, `max → maxObserved`,
    `ffId/ffIdByKey/ffKeyToId → firefoxLocalId/firefoxIdBySyncKey`,
    `m/a → modifiedHlc/createdHlc`, `u/r/op → upsertOp/removeOp`,
    `api → bookmarksApi`, `get/set → cacheGet/cacheSet`,
    `load/save/$/fmt → importExtensionModule/loadSettingsIntoForm/getById/format*`.
- `_gate` disambiguated: `_syncGate/_storeLock/_bucketLock` by owner
  (`BookmarkStore` semaphore vs `RateLimiter` lock); `_dir → _dataDir`,
  `_clock → _hlcClock` where touched.
- `ValueOf(it,field)/FieldValue(it,label)` clarified to
  `GetContentValue/GetValueByLabel` semantics with `propertyName` vs
  `conflictFieldLabel` (the old `Label`/`Field` tuple order was easy to swap).

**Not renamed (deliberate):** `State.Clock` (wire `hlc`) stays — `Hlc` the type
vs `Hlc` the property collide in C# scope (`Hlc.Zero` would resolve against
the string property). The XML remark documents this. `@base` stays as the
parameter name (keyword escape) to avoid churning every caller; docs call it
`baselineHlcs`.

---

## 2. Comments: what was removed / added

- **Removed (~10%):** restatements of the next line
  (`winner = s; // only server has`, `unchanged++ // value type`, `continue;
  // top-level, normal`, `capacity: 8192`, `EnvOr // env-first`, one-line
  `<summary>Puts X…</summary>` × 5 in `DomainJsonContext`).
- **Kept + translated:** every *why* comment — HLC branch order with
  counterexample, NUL separator choice, tombstone-per-subtree, gesture-loss
  duplication (merged into one), IDBRequest-clone pitfall, Windows rename
  retry, `WhenWritingDefault` vs Go `omitempty` (verified by repro, not
  memory), source-gen bypassing custom converters, `PublishAot` cannot
  cross-compile, `Retry-After: 60` coupling to the 60s window.
- **Added (missing):** nullability/ownership/thread-safety on
  `Merge()` (`@base` nullable, inputs never mutated, `SemaphoreSlim` across
  `Snapshot→Merge→GC→Save→Append` is a slow-path hold); `DepthOf`
  `>MaxDepth+1` early-exit returning `depth` (not -1); `IsHex` rejecting
  uppercase; `Normalize` silent carry; `Auth.ClientIp` loopback vs
  `MapToIPv4` asymmetry; `Retry-After` ↔ window coupling; three conflict
  limits (`50` default page vs `500` buffer vs `0` = all in health);
  `RenameWithRetry` timeout budget (`attempts: 5` at call site, `1<<i` ms);
  `observeMany` not `+1` (vs `update`); `stripUndefined` also dropping `''`;
  `DEFAULT_SETTINGS` 3-roots vs `scan` 1-root divergence; `K_*` migration
  policy absent (flagged, not invented); `isNotFound` Chinese-match fragility
  (kept intentionally — matches zh-CN Firefox locale — with comment).
- **Long essays moved:** 15-line DI-async pitfall (`BmsyncServer`) and 24-line
  encoder rationale (`ApiJsonContext`) now live in `doc/architecture.md` +
  one-line pointers inline.

---

## 3. Structure: long methods & magic numbers

- **Split candidates (flagged, not all split — behavior freeze for release):**
  `Merger.Merge` (~150 lines: winner-pick + stats + 3-way + invariants →
  extract `PickWinner/RecordStats/DetectConcurrentEdit`),
  `State.Validate` (~115 lines → `ValidateKeysAndTypes/ValidateTree/ValidateDepth`),
  `HandleSyncAsync` (~110 lines, 6 early returns → `TryParseRequestAsync`),
  `BmsyncServer.Build` (~120 lines incl. sync-over-async `GetResult` →
  `CreateStore/MapRoutes`), `Hlc.Update` (extract `NextCounter`),
  `Item.Read/State.Read` (extract `ReadItemField`), `SyncAsync` /
  `ListSnapshotsAsync` (read-only snapshot listing holds the write gate —
  starves writers; flagged).
- **Magic numbers extracted or linked:** `50` default conflict page (needs a
  `DefaultConflictLimit` const), `Retry-After "60"` ↔ 60 s window,
  `1<<i` backoff + `attempts: 5`, `8192/81920` pool sizes, `3 s` health
  timeout, `"yyyyMMddTHHmmss.fffZ"`, `MaxDepth+1`, `90 d × 3 sites` vs
  `HistoryKeep 30 × 2 sites`. Good counter-examples kept:
  `PhysicalDigits/CounterDigits/CounterMax/PhysicalModulus`,
  `MaxBodyBytes 16 MB`, `MinTokenLen 32`, Win32 codes, `ESTALE 116`.
- **Inconsistencies standardized in docs (not by churn):** clock
  (`Hlc` type vs `HlcValue` strings), states
  (`serverState/incomingState/mergedState`), conflicts/snapshots
  (`ConflictBuffer`/`Snapshot`), locks/paths, JSON facades
  (`BmsyncJson/DomainJson/ApiJson/...` → one facade doc), errors
  (`ValidationResult` standard).

---

## 4. HLC parity: what "equivalent" means

Header claim softened from "line-by-line / byte-identical × 3" to
**behavior-equivalent, vectors authoritative**. Real deltas documented:

1. Naming/style (JS camelCase vs C# PascalCase) — idiomatic, fine.
2. API shape (`decode → null|{…}` vs `TryDecode → bool+out`) — equivalent.
3. Strictness (C# `NumberStyles.None` vs JS `/^\d+$/`; 13-digit < 2^53, safe
   but noted).
4. Concurrency (C# `lock(_gate)` vs JS single-threaded) — fine, checklist
   notes it.
5. `PhysicalModulus` guard exists only in C#; JS would emit 14 digits past
   1e13 and break `FORMAT_RE`/vectors (flagged, not changed — vectors pin
   current behavior).
6. `FORMAT_RE` now built from constants (was duplicated literals).

---

## 5. Tests & verification for this pass

- `cd bmsync && dotnet build` — 0 warnings, 0 errors (TreatWarningsAsErrors).
- `cd bmsync && dotnet test` — full suite (Domain/Store/Server) green.
- `cd extension && node --test lib/*.test.js` — 90 pass.
- `./test/check-extension.sh` — 18 passed (fixed one false positive where
  the `Type` field-label map matched the message-type grep; now only
  `sendMessage` lines count).
- `bash -n` on all shell scripts.
- Intentionally kept non-English literals (not defects):
  - CJK bookmark titles in `JsonParityTests`/`ApiEndpointTests`
    (subjects of CJK-passthrough tests) + `ResponseKeepsChineseUnescaped`.
  - `'不存在'` in `apply.js:isNotFound` (matches zh-CN Firefox locale text).
  - `test/hlc_vectors.json` vectors themselves (note/layout translated).

---

## 6. Next pass (not in this release)

1. Split the long methods in §3 (needs new tests per extract, not a
   release-week refactor).
2. `ListSnapshotsAsync` lock scope (read-only path holds write gate).
3. `DefaultConflictLimit` + `Retry-After` ↔ window single source.
4. `PhysicalModulus` guard in JS + shared `MAX_PHYSICAL_MS`.
5. `store.js`/`collect.js` default-roots unification + `K_*` migration doc.
6. `isNotFound` error-code allowlist (drop locale-text matching).
7. Decide `test/jsonq` future: port to dependency-free Python/sh or document
   Go as an explicit test-only dep (currently a legacy Go helper; smoke
   prefers `python3 → jq → jsonq`).
