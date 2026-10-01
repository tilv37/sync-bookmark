#!/bin/sh
# End-to-end smoke test: drive the real server over plain HTTP.
#
# Division of labor with dotnet test: dotnet test uses an in-memory Kestrel
# fixture to verify logic; this script uses a real socket + real disk writes
# to verify the "JSON codec + auth + rate limiting + atomic write +
# snapshot" chain — the in-memory fixture cannot expose serialization bugs.
#
# Usage:
#   ./smoke.sh                                       # start a server automatically, clean up afterwards
#   HOST=example.com TOKEN=xxx ./smoke.sh --remote   # hit a remote (mutates real data)
#
# Requires: curl. JSON reads degrade in three levels: python3 -> jq -> the
# bundled test/jsonq helper.
#
# ── Why payloads always go through files ───────────────────────────────
#
# Early versions inlined JSON on the command line:
#   api POST /sync "{\"device\":...$(bm ...)}".
# Double quotes nesting command substitution nesting function calls meant the
# error message never pointed at the real spot
# ("syntax error near unexpected token `)'" for a missing backslash).
# Now everything is written to temp files via heredoc and sent with
# curl --data-binary @file — zero quoting levels, JSON reads as JSON.

set -eu

# ── Path resolution ───────────────────────────────────────────────────
#
# Resolve from **the script's own directory**, never from the cwd. Otherwise
# `bash test/smoke.sh` (from the repo root) and `cd test && bash smoke.sh`
# point at different binaries — a bug that is hard to notice because changing
# directories "fixes" it.
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
[ -n "$script_dir" ] || script_dir=$(pwd -P)

HOST="${HOST:-127.0.0.1:18099}"
TOKEN="${TOKEN:-}"
API="http://$HOST/api"
BIN="${BIN:-$script_dir/../bmsync/src/BookmarkSync.Cli/bin/Release/net10.0/bmsync}"
KILL_PID=""
DATA_DIR=""
WORK=""

cleanup() {
    # Only kill what this script actually started: after a failed preflight
    # KILL_PID is empty, and taskkill by **image name** would kill a server
    # the user is running themselves.
    if [ -n "$KILL_PID" ]; then
        # `kill` in Git Bash cannot kill native Windows processes: $! is
        # MSYS's internal PID, not the real Windows PID of bmsync.exe.
        # The result is a script that "finished" while the server still holds
        # the port — the next run fails with "address already in use" and a
        # streak of 500s pointing in a completely wrong direction.
        # Hard to track down because the previous round was all green.
        #
        # Killing by image name (//IM instead of //PID) works around the
        # unusable PID. This script starts at most one bmsync and only kills
        # when KILL_PID is non-empty, so nothing else is harmed.
        if command -v taskkill.exe >/dev/null 2>&1; then
            taskkill.exe //F //IM "$(basename "$BIN")" >/dev/null 2>&1 || true
        else
            kill "$KILL_PID" 2>/dev/null || true
        fi
    fi
    [ -n "$DATA_DIR" ] && rm -rf "$DATA_DIR"
    [ -n "$WORK" ] && rm -rf "$WORK"
    return 0
}
trap cleanup EXIT INT TERM

pass=0
fail=0
ok()  { pass=$((pass+1)); printf '  \033[32mok\033[0m    %s\n' "$1"; }
bad() { fail=$((fail+1)); printf '  \033[31mFAIL\033[0m  %s\n' "$1"
        [ -n "${2:-}" ] && printf '        %s\n' "$2"; return 0; }
section() { printf '\n\033[1m%s\033[0m\n' "$1"; }

WORK=$(mktemp -d)

# ── JSON backend ──────────────────────────────────────────────────────
# Each backend has its own expression syntax, so instead of a generic
# expression function every query is a named function dispatching per
# backend inside. Assertions stay readable.
MODE=""
JQ_BIN=""

setup_json() {
    if command -v python3 >/dev/null 2>&1 && python3 -c '' 2>/dev/null; then
        MODE=py
    elif command -v jq >/dev/null 2>&1; then
        MODE=jq
    else
        JQ_BIN="${JSONQ:-$script_dir/jsonq/jsonq}"
        if command -v go >/dev/null 2>&1; then
            # Always rebuild, not only when the file is missing.
            # Previously it compiled only on `[ ! -x ]`, so edits to main.go
            # kept running against the stale binary — new function names then
            # failed with "path must start with d" while nothing pointed at
            # "the binary is old".
            #
            # Building into the source dir (not a temp dir) is deliberate:
            # jsonq/ is already a standalone go module, treating it as "a
            # small in-repo tool" reads more naturally, and .gitignore holds
            # a single line for it.
            (cd "$script_dir/jsonq" && go build -o jsonq .) >/dev/null 2>&1 || true
        fi
        if [ -x "$JQ_BIN" ]; then
            MODE=bin
        else
            echo "Cannot read JSON: need python3, jq, or a working go to build test/jsonq" >&2
            exit 1
        fi
    fi
    printf 'JSON backend: %s\n' "$MODE"
}

py() {
    printf '%s' "$1" | python3 -c "import sys,json
d=json.load(sys.stdin)
print($2)" 2>/dev/null || echo ""
}

# ── Named queries ─────────────────────────────────────────────────────

# Total items in the response state
q_items() {
    case "$MODE" in
        py)  py "$1" "len(d['state']['items'])" ;;
        jq)  printf '%s' "$1" | jq -r '(.state.items | length)' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'len(d["state"]["items"])' 2>/dev/null || echo "" ;;
    esac
}

# summary.<field> of the response
q_summary() {
    case "$MODE" in
        py)  py "$1" "d['summary']['$2']" ;;
        jq)  printf '%s' "$1" | jq -r ".summary[\"$2\"]" 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" "get(d[\"summary\"], \"$2\")" 2>/dev/null || echo "" ;;
    esac
}

# state.<field> of the response
q_state() {
    case "$MODE" in
        py)  py "$1" "d['state'].get('$2','')" ;;
        jq)  printf '%s' "$1" | jq -r ".state[\"$2\"] // empty" 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" "get(d[\"state\"], \"$2\")" 2>/dev/null || echo "" ;;
    esac
}

# Tombstone count in the response state (d == true)
q_tombstones() {
    case "$MODE" in
        py)  py "$1" "sum(1 for v in d['state']['items'].values() if v.get('d'))" ;;
        jq)  printf '%s' "$1" | jq -r '[.state.items[] | select(.d == true)] | length' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'count(d["state"]["items"], "d")' 2>/dev/null || echo "" ;;
    esac
}

# Active item count in the response state
q_active() {
    case "$MODE" in
        py)  py "$1" "sum(1 for v in d['state']['items'].values() if not v.get('d'))" ;;
        jq)  printf '%s' "$1" | jq -r '[.state.items[] | select(.d != true)] | length' 2>/dev/null || echo "" ;;
        bin)
            # jsonq only offers "count-if-true", no "count-if-false", so subtract.
            _t=$(printf '%s' "$1" | "$JQ_BIN" 'len(d["state"]["items"])' 2>/dev/null || echo "")
            _d=$(printf '%s' "$1" | "$JQ_BIN" 'count(d["state"]["items"], "d")' 2>/dev/null || echo "")
            if [ -n "$_t" ] && [ -n "$_d" ]; then echo $((_t - _d)); else echo ""; fi
            ;;
    esac
}

# Conflict count in the response
q_conflicts() {
    case "$MODE" in
        py)  py "$1" "len(d['conflicts'])" ;;
        jq)  printf '%s' "$1" | jq -r '(.conflicts | length)' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'len(d["conflicts"])' 2>/dev/null || echo "" ;;
    esac
}

# One field of a conflict entry (that field of the last array element)
#
# Note the jsonq backend uses last(...) rather than get(...): conflicts is an
# **array**, and get on an array quietly returns an empty string — the
# "feature missing" kind of false image.
q_conflict_field() {
    case "$MODE" in
        py)  py "$1" "d['conflicts'][-1]['$2'] if d['conflicts'] else ''" ;;
        jq)  printf '%s' "$1" | jq -r ".conflicts[-1][\"$2\"] // empty" 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" "last(d[\"conflicts\"], \"$2\")" 2>/dev/null || echo "" ;;
    esac
}

# Snapshot count in the response
q_snapshots() {
    case "$MODE" in
        py)  py "$1" "len(d['snapshots'])" ;;
        jq)  printf '%s' "$1" | jq -r '(.snapshots | length)' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'len(d["snapshots"])' 2>/dev/null || echo "" ;;
    esac
}

# Item count of the on-disk state.json.
# The file has items at the top level; HTTP bodies wrap it in state. Try both.
q_file_items() {
    case "$MODE" in
        py)  py "$(cat "$1")" "len(d.get('items', d.get('state',{}).get('items',{})))" ;;
        jq)  jq -r '(.items // .state.items) | length' <"$1" 2>/dev/null || echo "" ;;
        bin)
            if "$JQ_BIN" 'len(d["items"])' <"$1" >/dev/null 2>&1; then
                "$JQ_BIN" 'len(d["items"])' <"$1" 2>/dev/null || echo ""
            else
                "$JQ_BIN" 'len(d["state"]["items"])' <"$1" 2>/dev/null || echo ""
            fi
            ;;
    esac
}

# ── Time and HLC ────────────────────────────────────────────────────

now_ms() {
    if [ "$MODE" = py ]; then
        python3 -c 'import time;print(int(time.time()*1000))'
    else
        awk 'BEGIN{printf "%d", systime()*1000}'
    fi
}

hlc() { printf '%013d-%05d' "$1" "${2:-0}"; }

# ── Payload construction ────────────────────────────────────────────
# Everything goes into files, sent with curl --data-binary @file. JSON is
# written as JSON looks, with no command-line escaping.

R_TOOLBAR="toolbar_____"
K1=11111111111111111111111111111111
K2=22222222222222222222222222222222
K3=33333333333333333333333333333333
K4=44444444444444444444444444444444
K5=55555555555555555555555555555555

# bm <key> <title> <url> <millis> <counter> [extra JSON fragment] — emits one item
bm() {
    printf '"%s":{"p":"%s","t":"b","n":"%s","u":"%s","a":"%s","m":"%s"%s}' \
        "$1" "$R_TOOLBAR" "$2" "$3" "$(hlc "$4" 0)" "$(hlc "$4" "$5")" "${6:-}"
}

# build_sync <output file> <device name> <items-json> [base-json]
build_sync() {
    {
        printf '{"device":"%s","state":{"v":1,"items":{%s}},"base":{%s}}' \
            "$2" "$3" "${4:-}"
    } > "$1"
}

# ── HTTP ────────────────────────────────────────────────────────────
# Status code goes to $CODE, body to $BODY.
#
# WARNING: deliberately **no echo of the status**. Early versions used
# `c=$(api ...)`; then api ran in a subshell, its BODY died with the
# subshell, every JSON query got an empty string, and all assertions failed
# spuriously. Globals are the return channel.
CODE=""
BODY=""

api_file() {  # api_file <method> <path> <payload file|->
    _m="$1"; _p="$2"; _f="$3"
    _out=$(mktemp)
    if [ "$_f" = "-" ]; then
        CODE=$(curl -sS -o "$_out" -w '%{http_code}' -X "$_m" "$API$_p" \
            -H "Authorization: Bearer $TOKEN" 2>/dev/null) || CODE=000
    else
        CODE=$(curl -sS -o "$_out" -w '%{http_code}' -X "$_m" "$API$_p" \
            -H "Authorization: Bearer $TOKEN" \
            -H 'Content-Type: application/json' \
            --data-binary "@$_f" 2>/dev/null) || CODE=000
    fi
    BODY=$(cat "$_out"); rm -f "$_out"
}

api_noauth_file() {  # without Authorization
    _m="$1"; _p="$2"; _f="$3"
    _out=$(mktemp)
    CODE=$(curl -sS -o "$_out" -w '%{http_code}' -X "$_m" "$API$_p" \
        -H 'Content-Type: application/json' \
        --data-binary "@$_f" 2>/dev/null) || CODE=000
    BODY=$(cat "$_out"); rm -f "$_out"
}

# ── Start a local server ────────────────────────────────────────────
setup_json

if [ "${1:-}" != "--remote" ]; then
    # On Windows the artifact has an .exe suffix, and Git Bash `[ -x ]` does
    # not match it — without this branch the script exits with "bmsync not
    # executable" on Windows while the file sits right there under another name.
    if [ ! -f "$BIN" ] && [ ! -f "$BIN.exe" ]; then
        echo "Cannot find an executable $BIN; publish first:" >&2
        echo "  cd bmsync && dotnet publish src/BookmarkSync.Cli -c Release -o out" >&2
        exit 1
    fi
    case "$BIN" in *.exe) ;; *) [ -f "$BIN.exe" ] && BIN="$BIN.exe" ;; esac

    # HOST may be the ":18099" form — fill in a concrete host, or Kestrel
    # rejects it while curl to ":18099" reports "up but health never answers".
    # This must run **before** the port preflight: the preflight uses this URL.
    case "$HOST" in
        :*) HOST="127.0.0.1$HOST" ;;
    esac

    # Port preflight: confirm nobody listens before starting.
    #
    # Without it, a crashed earlier run's leftover process hits this run with
    # "address already in use", and then **all 28 assertions fail with 500** —
    # a wall of red with not one line naming the real cause (port conflict).
    # Failing early and loudly beats guessing from 28 failures.
    if curl -sf -o /dev/null "http://$HOST/api/health" 2>/dev/null; then
        echo "A server is already running on port $HOST. Stop it first, or pick another port:" >&2
        echo "  HOST=127.0.0.1:18098 $0" >&2
        exit 1
    fi
    DATA_DIR=$(mktemp -d)
    TOKEN="0123456789abcdef0123456789abcdef01234567"

    BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$HOST" \
        "$BIN" >"$DATA_DIR/server.log" 2>&1 &
    KILL_PID=$!

    # Wait 10 seconds (not 5): Release JIT + source-generated JSON first
    # deserialization is slower than Debug, and 5s flakes on slow machines —
    # with a timeout indistinguishable from a real startup failure.
    for _ in $(seq 1 20); do
        curl -sf -o /dev/null "http://$HOST/api/health" 2>/dev/null && break
        sleep 0.5
    done
    if ! curl -sf -o /dev/null "http://$HOST/api/health" 2>/dev/null; then
        echo "Server did not start, log:" >&2; cat "$DATA_DIR/server.log" >&2; exit 1
    fi
fi

[ -n "$TOKEN" ] || { echo "TOKEN environment variable is required" >&2; exit 1; }

EMPTY="$WORK/empty.json"
cat > "$EMPTY" <<'JSON'
{"device":"pc-work","state":{"v":1,"items":{}},"base":{}}
JSON

# ── Scenarios ───────────────────────────────────────────────────────

section "0. Connectivity and auth"
api_file GET /health -
[ "$CODE" = 200 ] && ok "health returns 200" || bad "health returned $CODE" "$BODY"

api_noauth_file GET /health "$EMPTY"
[ "$CODE" = 200 ] && ok "health needs no token (the options-page Test connection relies on this)" \
    || bad "health unexpectedly requires auth, $CODE"

_bad=$(mktemp)
printf '{"device":"x","state":{"v":1,"items":{}},"base":{}}' > "$_bad"
_out=$(mktemp)
CODE=$(curl -sS -o "$_out" -w '%{http_code}' -X POST "$API/sync" \
    -H "Authorization: Bearer wrongwrongwrongwrongwrongwrongwrongwrong" \
    -H 'Content-Type: application/json' --data-binary "@$_bad" 2>/dev/null) || CODE=000
rm -f "$_bad" "$_out"
[ "$CODE" = 401 ] && ok "wrong token rejected (401)" || bad "wrong token should be 401, got $CODE"

section "1. Device A uploads 5 bookmarks"
NOW=$(now_ms)
ITEMS=""
for n in One Two Three Four Five; do
    case "$n" in
        One) kk=$K1; u=https://one.example ;;
        Two) kk=$K2; u=https://two.example ;;
        Three) kk=$K3; u=https://three.example ;;
        Four) kk=$K4; u=https://four.example ;;
        Five) kk=$K5; u=https://five.example ;;
    esac
    ITEMS="$ITEMS$(bm "$kk" "$n" "$u" "$NOW" 1),"
done
ITEMS=${ITEMS%,}
P="$WORK/up5.json"
build_sync "$P" pc-personal "$ITEMS"
api_file POST /sync "$P"
[ "$CODE" = 200 ] && ok "upload succeeded" || bad "upload failed $CODE" "$BODY"
n=$(q_items "$BODY")
[ "$n" = 5 ] && ok "server stored 5 items" || bad "expected 5 items, got '$n'"
cr=$(q_summary "$BODY" created)
[ "$cr" = 5 ] && ok "summary.created = 5" || bad "created expected 5, got '$cr'"
h=$(q_state "$BODY" hlc)
[ -n "$h" ] && ok "response carries hlc (clients calibrate from it)" || bad "response missing hlc"

section "2. Blank device B should receive all 5"
api_file POST /sync "$EMPTY"
[ "$CODE" = 200 ] && ok "sync succeeded" || bad "sync failed $CODE" "$BODY"
n=$(q_items "$BODY")
[ "$n" = 5 ] && ok "work PC received 5 items" || bad "expected 5 items, got '$n'"

section "3. B deletes 2 items and uploads; A pulls and should see the deletes"
NOW=$(now_ms)
DEL=",\"d\":true,\"x\":$NOW"
ITEMS="$(bm "$K1" One https://one.example "$NOW" 1),$(bm "$K2" Two https://two.example "$NOW" 2 "$DEL"),$(bm "$K3" Three https://three.example "$NOW" 3 "$DEL")"
P="$WORK/del2.json"
build_sync "$P" pc-work "$ITEMS"
api_file POST /sync "$P"
[ "$CODE" = 200 ] && ok "delete upload succeeded" || bad "delete upload failed $CODE" "$BODY"

api_file POST /sync "$EMPTY"
[ "$CODE" = 200 ] || bad "A pull failed $CODE" "$BODY"
t=$(q_tombstones "$BODY")
[ "$t" = 2 ] && ok "A sees 2 tombstones (deletes propagated)" || bad "expected 2 tombstones, got '$t'"
a=$(q_active "$BODY")
[ "$a" = 3 ] && ok "3 active bookmarks remain" || bad "expected 3 active, got '$a'"

section "4. Idempotency: same request 3 times in a row"
prev=""
drift=no
i=1
while [ "$i" -le 3 ]; do
    api_file POST /sync "$P"
    n=$(q_items "$BODY")
    if [ -n "$prev" ] && [ "$n" != "$prev" ]; then
        bad "attempt $i drifted: $prev -> $n"; drift=yes; break
    fi
    prev="$n"
    i=$((i + 1))
done
[ "$drift" = no ] && ok "repeated syncs are stable ($prev items)"

section "5. Concurrent edits -> conflict log"
NOW=$(now_ms)
BM_BASE=$(hlc 1000 1)
BASEMAP="\"$K1\":\"$BM_BASE\""

P="$WORK/srv-edit.json"
build_sync "$P" pc-personal "$(bm "$K1" ServerEdit https://one.example "$NOW" 0)" "$BASEMAP"
api_file POST /sync "$P"
[ "$CODE" = 200 ] || bad "server-side edit upload failed $CODE" "$BODY"

P2="$WORK/cli-edit.json"
build_sync "$P2" pc-work "$(bm "$K1" ClientEdit https://one.example "$((NOW + 1))" 0)" "$BASEMAP"
api_file POST /sync "$P2"
[ "$CODE" = 200 ] || bad "client-side edit upload failed $CODE" "$BODY"

api_file GET '/conflicts?limit=10' -
[ "$CODE" = 200 ] || bad "conflict read failed $CODE" "$BODY"
n=$(q_conflicts "$BODY")
case "$n" in
    ''|0) bad "expected at least 1 conflict, got '$n'" ;;
    *)    ok "logged $n conflicts" ;;
esac
r=$(q_conflict_field "$BODY" reason)
[ "$r" = concurrent_edit ] && ok "type is concurrent_edit" || bad "wrong type: '$r'"
f=$(q_conflict_field "$BODY" field)
[ "$f" = title ] && ok "conflict pinned to title" || bad "wrong field: '$f'"

section "6. Limits and malformed input"

post_raw() {  # post_raw <raw JSON> — send a literal, exercising the parse layer
    _f=$(mktemp)
    printf '%s' "$1" > "$_f"
    api_file POST /sync "$_f"
    rm -f "$_f"
}

post_raw '{"device":"x","state":{"v":99,"items":{}},"base":{}}'
[ "$CODE" = 400 ] && ok "schema version mismatch -> 400" || bad "expected 400, got $CODE"

post_raw '{"device":"x","state":{"v":1,"items":{"short":{"p":"toolbar_____","t":"b","n":"x","u":"https://x.example","m":"0000000001000-00000"}}},"base":{}}'
[ "$CODE" = 400 ] && ok "illegal key -> 400" || bad "expected 400, got $CODE"

post_raw '{"not":"valid json'
[ "$CODE" = 400 ] && ok "malformed JSON -> 400" || bad "expected 400, got $CODE"

post_raw '{"device":"x","state":{"v":1,"items":{}},"base":{},"typo":1}'
[ "$CODE" = 400 ] && ok "unknown field -> 400 (client typos surface immediately)" || bad "expected 400, got $CODE"

api_file POST /sync "$EMPTY"
[ "$CODE" = 200 ] && ok "valid empty state -> 200" || bad "expected 200, got $CODE"

section "7. History snapshots (restore points)"
api_file GET /history -
[ "$CODE" = 200 ] && ok "history readable" || bad "history failed $CODE" "$BODY"
n=$(q_snapshots "$BODY")
case "$n" in
    ''|0) bad "expected at least 1 snapshot, got '$n'" ;;
    *)    ok "produced $n snapshots" ;;
esac

section "8. Rate limiting"
# The cap is 60/min — see why a manually triggered tool cannot use 10/min.
# Firing 60 times is too slow; just verify normal usage is not throttled.
flooded=no
i=1
while [ "$i" -le 3 ]; do
    api_file POST /sync "$EMPTY"
    [ "$CODE" = 429 ] && { flooded=yes; break; }
    i=$((i + 1))
done
[ "$flooded" = no ] && ok "normal usage is not throttled (cap deliberately at 60/min)" \
    || bad "throttled after only 3 requests — cap too tight for manual triggers"

section "9. On-disk state and file integrity"
if [ -n "$DATA_DIR" ]; then
    [ -f "$DATA_DIR/state.json" ] && ok "state.json written to disk" || bad "state.json missing"
    [ -d "$DATA_DIR/history" ]   && ok "history dir created"   || bad "history dir missing"
    [ -f "$DATA_DIR/state.json.tmp" ] && bad "temp file left behind (atomic write incomplete)" \
        || ok "no temp file left (rename completed)"
    if [ -f "$DATA_DIR/state.json" ]; then
        n=$(q_file_items "$DATA_DIR/state.json")
        [ -n "$n" ] && ok "on-disk file parses ($n items)" || bad "on-disk file unparsable — atomic write broken"
    fi
else
    ok "(remote mode, skipping on-disk checks)"
fi

printf '\n\033[1mResult: %d passed, %d failed\033[0m\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
