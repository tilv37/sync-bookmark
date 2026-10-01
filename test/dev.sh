#!/bin/sh
# Local dev server: publish once and run bmsync in the foreground for extension work.
#
# Usage:
#   ./test/dev.sh [options]
#
# Options:
#   --addr ADDR    Listen address, default 127.0.0.1:18099 (BMSYNC_ADDR overrides)
#   --data DIR     Data dir, default <repo root>/tmp-data (BMSYNC_DATA overrides)
#   --token TOKEN  Bearer token, default fixed dev string (BMSYNC_TOKEN overrides)
#   --no-build     Skip dotnet publish, reuse the existing artifact
#   --reset        Empty the data dir before starting (removes state.json / conflicts.json / history/)
#   -h, --help     Show this help
#
# Environment beats defaults, CLI flags beat environment.
# The data dir is unrelated to deploy/: deploy/data/ is the container mount,
# tmp-data/ is local-debug only.
#
# Runs in the foreground, Ctrl+C stops. Fill the printed address and token
# into the extension settings page to debug together.

set -eu

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
root=$(CDPATH='' cd -- "$script_dir/.." && pwd -P)

DEFAULT_ADDR="127.0.0.1:18099"
DEFAULT_DATA="$root/tmp-data"
DEFAULT_TOKEN="0123456789abcdef0123456789abcdef01234567"
# Publish output dir (.gitignore already ignores bmsync/out/). --no-build reuses it.
OUT="$root/bmsync/out"
BIN=""

ADDR="${BMSYNC_ADDR:-$DEFAULT_ADDR}"
DATA_DIR="${BMSYNC_DATA:-$DEFAULT_DATA}"
TOKEN="${BMSYNC_TOKEN:-$DEFAULT_TOKEN}"
NO_BUILD=0
RESET=0

usage() {
    sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
}

while [ $# -gt 0 ]; do
    case "$1" in
        --addr)  [ $# -ge 2 ] || { echo "--addr needs a value" >&2; exit 1; }; ADDR="$2"; shift 2 ;;
        --data)  [ $# -ge 2 ] || { echo "--data needs a value" >&2; exit 1; }; DATA_DIR="$2"; shift 2 ;;
        --token) [ $# -ge 2 ] || { echo "--token needs a value" >&2; exit 1; }; TOKEN="$2"; shift 2 ;;
        --no-build) NO_BUILD=1; shift ;;
        --reset) RESET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1 (see --help)" >&2; exit 1 ;;
    esac
done

have() { command -v "$1" >/dev/null 2>&1; }

# Host:port for the health check. ADDR may be the ":8080" form; curl needs 127.0.0.1 filled in.
health_host() {
    case "$ADDR" in
        :*) printf '127.0.0.1%s' "$ADDR" ;;
        *)  printf '%s' "$ADDR" ;;
    esac
}

if ! have dotnet && [ "$NO_BUILD" -eq 0 ]; then
    echo "dotnet not found and --no-build not passed. Install .NET SDK 10 or reuse an artifact with --no-build." >&2
    exit 1
fi
if ! have curl; then
    echo "curl not found, cannot wait on health after start. Install curl first." >&2
    exit 1
fi

if [ "$TOKEN" = "$DEFAULT_TOKEN" ]; then
    printf 'Note: using the default dev token, local debugging only — never deploy with it.\n' >&2
fi

if [ "$RESET" -eq 1 ] && [ -d "$DATA_DIR" ]; then
    rm -rf "$DATA_DIR"
    printf 'Emptied data dir: %s\n' "$DATA_DIR"
fi
mkdir -p "$DATA_DIR"

if [ "$NO_BUILD" -eq 0 ]; then
    printf 'Publishing (dotnet publish)...\n'
    (cd "$root/bmsync" && dotnet publish src/BookmarkSync.Cli -c Release -o "$OUT" -v quiet --nologo) || {
        echo "Publish failed, see dotnet output above." >&2
        exit 1
    }
fi
# Windows builds bmsync.exe, Linux builds bmsync.
if   [ -x "$OUT/bmsync" ];    then BIN="$OUT/bmsync"
elif [ -x "$OUT/bmsync.exe" ]; then BIN="$OUT/bmsync.exe"
else
    echo "No executable at $OUT/bmsync (or bmsync.exe); run once without --no-build to publish." >&2
    exit 1
fi

# Preflight the config: token length, writable data dir, etc. surface here
# instead of halfway through startup logs.
if ! BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$ADDR" "$BIN" --config-check >/dev/null 2>&1; then
    echo "Config preflight failed, re-running verbosely:" >&2
    BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$ADDR" "$BIN" --config-check
    exit 1
fi

HOSTPORT=$(health_host)

# When a server already runs, say so instead of starting a second one fighting for the port.
if curl -sf -o /dev/null "http://$HOSTPORT/api/health" 2>/dev/null; then
    echo "A server already runs at: http://$HOSTPORT/api/health" >&2
    echo "Stop it first, or switch ports: ./test/dev.sh --addr 127.0.0.1:18100" >&2
    exit 1
fi

printf 'Data dir: %s\n' "$DATA_DIR"
printf 'Starting server: %s (Ctrl+C stops)\n' "$ADDR"

BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$ADDR" "$BIN" &
PID=$!
trap 'kill $PID 2>/dev/null || true' INT TERM

ok=0
i=1
while [ "$i" -le 20 ]; do
    if curl -sf -o /dev/null "http://$HOSTPORT/api/health" 2>/dev/null; then
        ok=1
        break
    fi
    sleep 0.5
    i=$((i + 1))
done

if [ "$ok" -ne 1 ]; then
    echo "Server not up within 10s, killed (PID $PID)." >&2
    kill "$PID" 2>/dev/null || true
    exit 1
fi

cat <<EOF

Server ready ──────────────────────────────
  health:  curl http://$HOSTPORT/api/health
  sync:    curl -H "Authorization: Bearer \$BMSYNC_TOKEN" -X POST http://$HOSTPORT/api/sync

  Fill into the extension settings page:
    Server URL  http://$HOSTPORT
    Token       $TOKEN

  Data file: $DATA_DIR/state.json
  Snapshots: $DATA_DIR/history/
  Fresh start: ./test/dev.sh --reset
──────────────────────────────────────────
Logs stream to this terminal, Ctrl+C stops the server.
EOF

wait "$PID"
