# bmsync

<img src="extension/icon.png" alt="bmsync icon" width="96" />

Manually triggered, two-way Firefox bookmark sync with **delete propagation**.
A Firefox extension plus a self-hosted server; merges happen in exactly one
place (the server), so repeated syncs are safe.

Built for one concrete scenario: personal PC uses Firefox bookmark sync while
a work PC cannot sign into a personal account. Both end up with the same tree,
including folder structure, deletions, and moves.

- Server: .NET 10, zero third-party NuGet packages, NativeAOT single binary
  (~20–30 MB, `FROM scratch`)
- Extension: pure JS ES modules, no build step, no framework, no npm deps
- Sync: full-state POST, server-side LWW + HLC merge, tombstones for deletes
- Tests: `./test/all.sh` — extension + .NET + end-to-end smoke

> Phonetics: "bmsync" = bookmarks sync. The binary, service, and extension
> share the name.

---

## What it solves

- **Personal PC**: bookmarks go through your own channel to your VPS
- **Work PC**: one click restores the full tree (folders included)
- Adds, edits, and **deletes** on either side converge
- Conflicts resolve silently by LWW with a queryable field-level log
- No reverse-proxy layer in this repo (use your existing Nginx Proxy Manager)

Not a Floccus clone. Floccus is a full CRDT engine (auto sync, encrypted
sync, multiple backends). This is the minimal manually triggered core:
.NET 10 server + pure JS extension.

---

## Quickstart

### 1. Server

```bash
git clone <your-repo> sync-bookmark
cd sync-bookmark/deploy
cp .env.example .env
# Generate a token and put it in .env
openssl rand -hex 32
docker compose up -d --build
```

Create a Proxy Host in Nginx Proxy Manager:

| Field | Value |
|---|---|
| Domain Names | your domain |
| Scheme | `http` |
| Forward Hostname / Port | `127.0.0.1` / `8080` |
| SSL | Let's Encrypt, Force SSL on |

**This line is required** in the Advanced panel, otherwise large libraries
get 413 from nginx before reaching bmsync:

```nginx
client_max_body_size 32m;
```

Verify:

```bash
curl https://your-domain/api/health
# {"ok":true,"service":"bmsync",...}
```

Details: [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md),
[docs/OPERATIONS.md](docs/OPERATIONS.md).

### 2. Extension

1. Open `about:debugging#/runtime/this-firefox`
2. "Load Temporary Add-on" → select `extension/manifest.json`
3. Open the extension settings page, enter server URL + token
4. First sync prompts for site permission — allow it
5. Click the toolbar icon → "Sync now"

For real use, package and sign (see [docs/RELEASE.md](docs/RELEASE.md)):

```bash
cd extension && zip -r ../bmsync.xpi *
```

---

## How sync works

```text
Extension collects bookmark tree → POST /api/sync (state + base)
                                        ↓
                          Server merges (LWW + HLC)
                                        ↓
                    Returns authoritative state → extension applies
```

**Merge exists in exactly one implementation (the server).** The extension
only collects and applies. Operations are therefore idempotent.

### Identity

Two-way merge needs "these two bookmarks are the same thing". Firefox
`bookmarks` API only gives local integer ids, so keys are content-derived:

```text
Bookmark: SHA-256("u:" + url)                    first 16 bytes → 32 hex chars
Folder:   SHA-256("f:" + parentKey + \x00 + title)
```

No metadata is stored anywhere. Cost: changing a URL or a folder name shows
up as delete + add. Full trade-offs: [docs/architecture.md §2](docs/architecture.md).

### Why deletes sync

Deletion writes a **tombstone** (`d: true` item) whose HLC timestamp takes
part in normal LWW resolution. Tombstones live 90 days (long enough to reach
every device), then the server GCs them. This is an LWW-Element-Set, not a
naive diff — naive diffs cannot tell "deleted" from "never seen".

### Why HLC

Merge resolves by timestamp. Raw `Date.now()` with a 5-minute skew silently
lets "my 10:00 bookmark" lose to "your stale 10:03 copy". HLC (Hybrid Logical
Clock) preserves causality: once B has seen A's operation, every later B
timestamp is strictly greater, regardless of wall-clock skew.

C# (`BookmarkSync.Domain/Hlc.cs`) and JS (`extension/lib/hlc.js`) must stay
behavior-equivalent, cross-validated by `test/hlc_vectors.json`.

---

## Development

### One command for all checks

```bash
./test/all.sh
```

Five layers, fastest first:

| Layer | Content | Needs |
|---|---|---|
| 1 | Syntax + C# build (warnings as errors) | bash / dotnet |
| 2 | Extension consistency (ids, imports, cross-port constants) | bash |
| 3 | Extension unit tests | **Node 18+** |
| 4 | .NET unit tests + coverage + Linux publish | dotnet |
| 5 | End-to-end smoke (real HTTP + real files) | curl + dotnet |

No hard Node dependency — without it, layer 3 is explicitly skipped.

```bash
./test/check-extension.sh    # extension consistency without Node
./test/smoke.sh              # end-to-end over real HTTP
./test/dev.sh                # local server for extension debugging
cd bmsync && dotnet test
cd extension && node --test lib/*.test.js
```

.NET SDK 10 is required (`global.json` pins 10.0.401,
`rollForward: latestFeature`). Extension has no build step; reload it from
`about:debugging` after edits.

After any HLC change, replay the cross-port vectors:

```bash
cd bmsync && dotnet test --filter "FullyQualifiedName~HlcVector"
```

To regenerate vectors for JS: `BMSYNC_WRITE_VECTORS=1` (target dir must
already exist — a wrong path is a hard error).

Cross-platform check (production runs on Linux):

```bash
cd bmsync
dotnet publish src/BookmarkSync.Cli -c Release -r linux-x64 --self-contained true -o out
```

Entry point is `src/BookmarkSync.Cli` (assembly name `bmsync`; the
healthcheck depends on it). **NativeAOT stays in Docker**: `FROM scratch`
needs a dependency-free binary, and AOT cannot cross-compile — local builds
verify IL publish only.

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and
[CONTRIBUTING.md](CONTRIBUTING.md).

---

## Repository layout

```text
bmsync/            .NET 10 server
  global.json      SDK pin 10.0.401
  Directory.Build.props / Directory.Packages.props
  src/
    BookmarkSync.Domain/  Domain: model + merge + HLC + GC
      Hlc.cs            Hybrid Logical Clock (mirrors lib/hlc.js)
      State.cs          State / Item types, validation, schema version
      Merge.cs          Merge algorithm — the most-read file in this repo
      TombstoneGc.cs    Tombstone collection
      Limits.cs         Domain caps
      StateJsonConverters.cs  Byte-compatible JSON (omitempty + key order)
    BookmarkSync.Store/   Persistence: atomic writes, mutex, snapshots
    BookmarkSync.Server/  HTTP: routes, auth/ratelimit, handlers, config
    BookmarkSync.Cli/     Entry (assembly bmsync): startup, shutdown, healthcheck
  tests/                  Three test projects mirroring src
  Dockerfile              Test → NativeAOT → FROM scratch
extension/         Firefox extension (pure ESM, no build, no framework)
  background.js    Message routing + sync orchestration
  lib/hlc.js       Must stay byte-equivalent with the server
  lib/keys.js      Identity derivation
  lib/collect.js   Collect: bookmark tree → state
  lib/apply.js     Apply: state → bookmark tree
  lib/sync.js      One full sync
  lib/store.js     IndexedDB
  lib/client.js    HTTP + error mapping
  popup/           Toolbar popup
  options/         Settings, conflicts, history
deploy/            docker-compose, .env template, backup script
docs/              Guides + technical reference (all English)
test/
  all.sh                 All five layers
  smoke.sh               End-to-end smoke (real HTTP + real files)
  dev.sh                 Local server for extension debugging
  check-extension.sh     Extension consistency without Node
  hlc_vectors.json       C#↔JS HLC cross-validation vectors
  jsonq/                 Minimal JSON reader for smoke (legacy Go helper)
```

Dependency direction is one-way:

```text
Cli → Server → Store → Domain
```

So `Merge.cs` is never buried under HTTP code and can be tested without
starting any server. Conventions:

- `BookmarkSync.Domain` references no other project, not even logging. Pure
  domain logic with value-type inputs/outputs.
- `BookmarkSync.Store` has its own minimal `StoreOptions`
  (`DataDir` / `HistoryKeep` / `TombstoneTtl`), never reuses `ServerOptions`.

---

## Known limitations

- **No ordering**: content converges; position inside a folder does not. New
  items append at the end.
- **Same URL in different folders merges into one**. Bookmark keys depend on
  URL only — the price of correct "move" sync.
- **Renaming a folder rebuilds its subtree** (no content loss, but many
  tombstones). Folder keys depend on the parent key.
- **No cross-device resurrection guard past 90 days**: a device offline longer
  than tombstone TTL may re-upload a deleted bookmark as "new".
  See [docs/architecture.md §11](docs/architecture.md).
- **No tags / annotations / reading list**. The `bookmarks` API does not
  expose annotations.

---

## Docs

| Doc | Content |
|---|---|
| [docs/index.md](docs/index.md) | Doc map, status, ports, test layers |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Toolchain, dev server, test layers |
| [docs/TESTING.md](docs/TESTING.md) | 12-item device checklist, clock-skew test |
| [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) | NPM, compose, network check |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Backup/restore, rollback, token rotation |
| [docs/DATA-FORMAT.md](docs/DATA-FORMAT.md) | state.json schema v1, HLC, GC |
| [docs/RELEASE.md](docs/RELEASE.md) | xpi packaging/signing, image publish |
| [docs/architecture.md](docs/architecture.md) | Technical reference: components, merge, HLC, API |
| [docs/code-review.md](docs/code-review.md) | Readability pass: what changed and why |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution rules, HLC parity rule |
| [SECURITY.md](SECURITY.md) | Token handling, reporting, hardening |

---

## License

MIT — see [LICENSE](LICENSE).
