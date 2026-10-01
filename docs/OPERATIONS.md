# OPERATIONS

Data layout on the server (`BMSYNC_DATA`, `/data` in the container):
`state.json` (authoritative state), `conflicts.json` (ring buffer, 500 entries),
`history/` (snapshots, keep 30). Code: `bmsync/src/BookmarkSync.Store/BookmarkStore.cs`,
options in `bmsync/src/BookmarkSync.Store/StoreOptions.cs`.

## Backup

`deploy/backup.sh` (daily via crontab) tars `deploy/data/` to
`deploy/backup/data-<UTC-stamp>.tar.gz`, keeping the latest 14. Install with:

```cron
17 3 * * *  /opt/bmsync/deploy/backup.sh >> /var/log/bmsync-backup.log 2>&1
```

`history/` snapshots are in-flight intermediates, not backups — keep off-site
copies of the tarballs. The script never uploads; that part is up to you.

## Restore

Full restore from a tarball:

```bash
cd deploy
docker compose down
tar -xzf backup/data-<stamp>.tar.gz
docker compose up -d
```

## Snapshot rollback

Every successful write snapshots the previous `state.json` into
`data/history/<timestamp>.json` first (snapshot failure never blocks a sync).
To roll back to a snapshot:

```bash
cd deploy
ls -lt data/history/
docker compose down
cp data/history/<snapshot>.json data/state.json
docker compose up -d
```

After a rollback, **re-sync both devices**; a client cache may hold newer data
that gets pushed back up. There is no one-click rollback UI — `GET /api/history`
is read-only by design (`bmsync/src/BookmarkSync.Server/ApiEndpoints.cs`).

## Token rotation

1. Generate a new token: `openssl rand -hex 32`.
2. Update `BMSYNC_TOKEN` in `deploy/.env`, then `docker compose up -d`.
3. Update **every** device's extension settings; devices with the old token get
   401 and change nothing locally.

## Logs and monitoring

- `docker compose logs -f bmsync` — logs method/path/status/duration only,
  **never bodies or tokens** (`bmsync/src/BookmarkSync.Server/BmsyncServer.cs`).
- Watch for: 401 (token mismatch), 413 (NPM body limit — see
  [DEPLOYMENT](DEPLOYMENT.md)), 429 (60/min per IP), 5xx (do not retry blindly;
  clients must not touch local bookmarks on failure).
- Minimal monitoring: container `healthcheck` status + daily backup log +
  `data/state.json` size growth (full-state transfer; multi-MB states are normal).
