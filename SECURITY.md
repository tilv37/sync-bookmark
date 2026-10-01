# Security Policy

## Supported versions

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |

## Authentication model

bmsync is a single-user, self-hosted service. All `/api/*` endpoints except
`/api/health` require:

```text
Authorization: Bearer <token>
```

- The token must be at least 32 characters (`openssl rand -hex 32`).
- It is injected via the `BMSYNC_TOKEN` environment variable. Never commit it,
  never bake it into images, never log it.
- Comparison uses constant-time equality to avoid timing side channels.

## Reporting a vulnerability

Do not open a public issue for a vulnerability that could cause bookmark loss,
authentication bypass, or remote access to `state.json`.

Instead, open a draft security advisory on GitHub or contact the maintainers
directly with:

1. Affected version / commit
2. Reproduction steps (redacted token)
3. Impact assessment (data loss / auth / availability)

We will acknowledge within 72 hours and publish a fix plus a rollback note
(`history/` snapshots cover data recovery).

## Operational hardening (deployments)

- Bind `127.0.0.1:18081:8080` only; terminate TLS in Nginx Proxy Manager.
- Set `client_max_body_size 32m;` in NPM (default 1 MB rejects large states
  with 413 before bmsync ever sees them).
- Back up `deploy/data/` daily; snapshots in `data/history/` are a second
  layer, not a replacement.
- Rotate the token by updating `deploy/.env` and restarting, then re-enter it
  in the extension settings on every device.
