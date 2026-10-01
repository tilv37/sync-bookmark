#!/bin/sh
# Back up the bmsync data directory.
#
# Install in crontab:  crontab -e   then add
#   17 3 * * *  /opt/bmsync/deploy/backup.sh >> /var/log/bmsync-backup.log 2>&1
#
# Why it matters: data/ holds the only authoritative state. history/ keeps 30
# snapshots, but those are "in-flight intermediate states", not "a state I
# backed up today". When things go wrong you want a restorable whole backup.

set -eu

cd "$(dirname "$0")"

STAMP=$(date -u +%Y%m%dT%H%M%SZ)
KEEP=14
OUT="backup/data-$STAMP.tar.gz"

mkdir -p backup

# --exclude drops a possible leftover temp file: it only takes effect via
# rename after validation, so backing it up is pointless.
tar -czf "$OUT" \
    --exclude='data/state.json.tmp' \
    data/

# Keep only the latest KEEP archives
ls -1t backup/data-*.tar.gz 2>/dev/null | tail -n "+$((KEEP + 1))" | xargs -r rm --

# Remind about off-site copies. This script never uploads — where backups go
# depends on whom you trust.
echo "$(date -u +%FT%TZ) backup done: $OUT ($(du -h "$OUT" | cut -f1))"
echo "  Keeping latest $KEEP. Current count: $(ls -1 backup/data-*.tar.gz 2>/dev/null | wc -l)"
