# Contributing to bmsync

Thank you for considering a contribution. This project is intentionally small:
a Firefox extension plus a self-hosted .NET sync server with a single merge
implementation on the server side.

## Ground rules

1. **Merge logic first.** `bmsync/src/BookmarkSync.Domain/Merge.cs` is the
   highest-risk file. Any change there needs a dedicated unit test and must keep
   the three merge invariants green (see `MergeInvariants`).
2. **Server is the only merge point.** The extension only collects (`collect.js`)
   and applies (`apply.js`). Do not add merge logic to the extension.
3. **Wire compatibility.** `Item` field names (`p/t/n/u/a/m/d/x`), the HLC
   encoding (`13-digit ms + '-' + 5-digit counter`), and `state.json` schema
   version are part of the on-disk / on-wire protocol. Changing them requires a
   schema bump and a migration note in `docs/DATA-FORMAT.md`.
4. **HLC parity.** `bmsync/src/BookmarkSync.Domain/Hlc.cs` and
   `extension/lib/hlc.js` must stay behavior-equivalent. After any HLC change,
   regenerate and replay `test/hlc_vectors.json` on both sides.
5. **No silent data loss.** A failed sync must never modify local bookmarks.
   Snapshot before overwrite, keep tombstones for 90 days, GC only tombstones.

## Development setup

- .NET SDK 10 (`global.json` pins 10.0.401, `rollForward: latestFeature`)
- Node 18+ (extension unit tests only; optional)
- bash + curl (integration scripts)

```bash
./test/all.sh
```

This runs five layers, fastest first: syntax + C# build, extension
consistency checks, extension unit tests (Node), .NET unit tests + coverage +
Linux publish, end-to-end smoke (real HTTP + real files on disk).

Layer-specific entry points:

```bash
./test/check-extension.sh    # extension consistency, no Node required
./test/smoke.sh              # end-to-end over real HTTP
./test/dev.sh                # local server for extension debugging (foreground)
cd bmsync && dotnet test
cd extension && node --test lib/*.test.js
```

## After changing HLC

C# and JS HLC implementations must produce byte-identical output.
`test/hlc_vectors.json` is the shared contract.

```bash
cd bmsync && dotnet test --filter "FullyQualifiedName~HlcVector"
```

`HlcVectorExportTests.BuildVectors()` is the generator. It does not write
files unless `BMSYNC_WRITE_VECTORS=1` is set, and the target directory must
already exist (a wrong path is a hard error, not a silent success).

## Pull requests

- Keep the diff focused; explain *why*, not *what*.
- `dotnet test` and `./test/all.sh` must pass.
- Update `docs/` when behavior, limits, or the protocol changes.
- Do not commit `deploy/.env`, `deploy/data/`, or `tmp-data/`.
