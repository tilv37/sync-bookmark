# DEVELOPMENT

## Toolchain

| Tool | Version | Required for |
|---|---|---|
| .NET SDK | 10.0.x (`bmsync/global.json` pins `10.0.401`, `rollForward: latestFeature`) | Server build + all C# tests |
| bash | any | `test/*.sh` |
| curl | any | `test/smoke.sh`, `test/dev.sh` health wait |
| Node | 18+ (optional) | Extension unit tests only; everything else runs without it |
| Docker | any | VPS deploy only |
| Python / jq | optional | Smoke script falls back to the bundled `test/jsonq` reader |

The extension is plain ESM with no build step and zero npm dependencies.
The server has zero third-party NuGet packages in production code (only
`Microsoft.Extensions.Logging.Abstractions` for the `ILogger` interface).

## Local server (`test/dev.sh`)

Faster than Docker for extension debugging — publishes once and runs in the
foreground (Ctrl+C stops):

```bash
./test/dev.sh                 # default 127.0.0.1:18099, fixed dev token
./test/dev.sh --reset         # wipe the data dir and start fresh
./test/dev.sh --addr 127.0.0.1:18100
./test/dev.sh --no-build      # reuse the existing artifact
./test/dev.sh -h              # full usage
```

It preflights config (`--config-check`: token length, writable data dir) before
listening, and prints the Server URL + Token to paste into the extension settings
page. Data lands in `tmp-data/` (local-debug only; `deploy/data/` is the container
mount). `/api/health` needs no token.

Manual equivalent: `dotnet publish src/BookmarkSync.Cli -c Release -o out` in
`bmsync/`, then run with `BMSYNC_TOKEN` (≥32 chars), `BMSYNC_DATA`, `BMSYNC_ADDR`
set. Entry point is `src/BookmarkSync.Cli` (assembly name `bmsync`).

## Test layers

One command runs everything: `./test/all.sh` (5 layers, fastest first — see
[index.md](index.md) for the table). Run single layers with:

```bash
./test/check-extension.sh    # extension consistency, no Node needed
./test/smoke.sh              # end-to-end over real HTTP (needs BIN= or a published binary)
cd extension && node --test lib/*.test.js
cd bmsync && dotnet test
```

NativeAOT is Docker-only: it needs a Linux C linker and cannot cross-compile, so
local checks verify a plain Linux IL publish while `bmsync/Dockerfile` does the AOT
build (`FROM scratch`).

## HLC vector regen rule

`bmsync/src/BookmarkSync.Domain/Hlc.cs` and `extension/lib/hlc.js` must stay
behavior-equivalent. The shared contract is `test/hlc_vectors.json`, replayed by
both sides. **After any HLC change, regenerate and re-verify:**

```bash
cd bmsync && dotnet test --filter "FullyQualifiedName~HlcVector"
```

The `HlcVectorExportTests.BuildVectors()` generator does **not** write files unless
`BMSYNC_WRITE_VECTORS=1` is set, and it fails hard if the target directory does
not exist. Never hand-edit the vectors file — it is a derived artifact.
