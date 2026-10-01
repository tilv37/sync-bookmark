# DEPLOYMENT

Single container on your VPS, behind your existing Nginx Proxy Manager (NPM).
This project ships **no** proxy layer.

## Prerequisites

1. VPS with Docker + Compose v2.
2. A domain with an A record pointing at the VPS; ports 80/443 open for NPM.
3. NPM already running on the VPS.

## Deploy

```bash
cd deploy
cp .env.example .env
openssl rand -hex 32   # paste into BMSYNC_TOKEN in .env (never commit .env)
docker compose up -d --build
curl https://<your-domain>/api/health
```

`deploy/docker-compose.yml` binds loopback-only (`127.0.0.1:18081:8080`), so the
server is unreachable from the public internet directly. Security defaults:
`read_only: true`, `cap_drop: [ALL]`, `no-new-privileges`, healthcheck via the
binary's own `healthcheck` subcommand (the `scratch` image has no shell).

## NPM Proxy Host

| Field | Value |
|---|---|
| Domain Names | your domain |
| Scheme | `http` |
| Forward Hostname / Port | `127.0.0.1` / `18081` (host port from `deploy/docker-compose.yml`) |
| Block Common Exploits | on |
| Websockets Support | off (not used) |
| SSL | Let's Encrypt, Force SSL on |

(If NPM runs on another machine, bind the LAN IP instead of `127.0.0.1`, or join
both containers to one external network and proxy `bmsync:8080` with no `ports:`.)

### Required: raise the body limit

In the NPM **Advanced** panel add:

```nginx
client_max_body_size 32m;
```

nginx defaults to `1m`; states above ~6000 bookmarks exceed that and get a 413
from nginx before ever reaching bmsync. Confirm `gzip_types` includes
`application/json` if you customized gzip (JSON compresses ~5x).

## Company-network check (do this early)

The biggest single risk: if the work network blocks your domain, the work side
cannot sync at all, and there is no legitimate workaround.

```powershell
curl.exe -I https://<your-domain>/api/health
```

Run this **on the work machine** before investing further. Fallbacks if blocked:
a different domain, the company's existing reverse proxy, or giving up the work-side
sync. Details and proxy notes: [architecture](architecture.md) §9/§11.
