# bmsync — documentation index

Self-hosted Firefox bookmark sync: a manually triggered two-way merge (LWW + HLC)
where **deletions propagate**. Extension (`extension/`, plain JS, no build) +
server (`bmsync/`, .NET 10, NativeAOT single binary).

## Project status

- Server, extension, Docker packaging, and automated tests are implemented.
- `./test/all.sh` runs 5 layers (see [TESTING](TESTING.md)); currently ~250+ cases green.
- Not yet done on real hardware: two-device sync against real Firefox, VPS deploy
  walkthrough. Biggest unknown: whether the work network can reach the self-hosted
  domain (see [DEPLOYMENT](DEPLOYMENT.md)).

## Doc map

| Doc | What it covers |
|---|---|
| [architecture](architecture.md) | Technical reference: components, identity, merge, HLC, API, deploy |
| [code-review](code-review.md) | Readability pass: what changed, what was kept, next steps |
| [DEVELOPMENT](DEVELOPMENT.md) | Toolchain, `test/dev.sh` local server, test layers, HLC vector regen rule |
| [TESTING](TESTING.md) | 12-item two-device checklist + clock-skew test |
| [DEPLOYMENT](DEPLOYMENT.md) | Single-compose VPS deploy behind Nginx Proxy Manager |
| [OPERATIONS](OPERATIONS.md) | Backup/restore, snapshot rollback, token rotation, logs, monitoring |
| [DATA-FORMAT](DATA-FORMAT.md) | `state.json` schema v1, HLC encoding, tombstone/GC rules |
| [RELEASE](RELEASE.md) | Extension packaging + AMO signing, image publish, versioning |
| [c4/](c4/) | C4 context/container/component + sync sequence diagrams (`.puml`, English) |

Root docs: `README.md` (overview + quickstart), `CONTRIBUTING.md`, `SECURITY.md`.

## Ports

Three different ports exist; they are not interchangeable:

| Where | Address | Meaning |
|---|---|---|
| Container (`deploy/docker-compose.yml`) | `:8080` (`BMSYNC_ADDR`) | What the server listens on inside the container |
| Host (`deploy/docker-compose.yml` `ports:`) | `127.0.0.1:18081:8080` | Loopback-only publish; NPM on the same host proxies this port |
| Local dev (`test/dev.sh` default) | `127.0.0.1:18099` | Bare binary for extension debugging, no Docker |

NPM Proxy Host forwards to the **host** port (`127.0.0.1` / `18081` when bmsync and
NPM share a machine). `/api/health` needs no token; the extension settings page
"Test connection" button relies on that (`bmsync/src/BookmarkSync.Server/ApiEndpoints.cs`).

## Test layers (`test/all.sh`)

| Layer | Content | Needs |
|---|---|---|
| 1 | Shell syntax (`bash -n`) + C# build (warnings as errors) | bash, dotnet |
| 2 | Extension consistency (ids, imports, cross-side constants) | bash only |
| 3 | Extension unit tests (`node --test lib/*.test.js`) | Node 18+ (optional; skipped without it) |
| 4 | .NET unit tests + coverage + Linux publish check | dotnet |
| 5 | End-to-end smoke (real HTTP + real files on disk) | curl + dotnet |

No Node hard dependency: layer 3 is skipped with a clear message when Node is absent.

## Key code entry points

- Merge algorithm: `bmsync/src/BookmarkSync.Domain/Merge.cs`
- HLC (must match `extension/lib/hlc.js` byte-for-byte behavior):
  `bmsync/src/BookmarkSync.Domain/Hlc.cs`; shared vectors `test/hlc_vectors.json`
- State/Item types + validation: `bmsync/src/BookmarkSync.Domain/State.cs`
- HTTP layer: `bmsync/src/BookmarkSync.Server/` (`BmsyncServer.cs`, `ApiTypes.cs`, `ApiEndpoints.cs`)
- Extension collect/apply: `extension/lib/collect.js`, `extension/lib/apply.js`
- Deploy: `deploy/docker-compose.yml`, `deploy/.env.example`, `bmsync/Dockerfile`
