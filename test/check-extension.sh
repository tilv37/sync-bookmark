#!/bin/sh
# Static checks for the extension.
#
# No Node on this machine is fine: everything here needs no JS engine:
#   · JSON files valid (manifest.json)
#   · message types registered in background match what callers request
#   · imported files exist
#   · ids referenced in HTML match getElementById usage in JS
#
# All of these are "misspelled name" bugs — they never crash the extension,
# but they leave some button permanently dead, which is extremely hard to
# locate from the UI.

set -eu
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
EXT="$script_dir/../extension"

pass=0
fail=0
ok()  { pass=$((pass+1)); printf '  \033[32mok\033[0m    %s\n' "$1"; }
bad() { fail=$((fail+1)); printf '  \033[31mFAIL\033[0m  %s\n' "$1"
        [ -n "${2:-}" ] && printf '        %s\n' "$2"; return 0; }
section() { printf '\n\033[1m%s\033[0m\n' "$1"; }

[ -d "$EXT" ] || { echo "Missing $EXT" >&2; exit 1; }

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT

# ── 1. JSON validity ────────────────────────────────────────────────
section "1. JSON files"
check_json() {
    if command -v python3 >/dev/null 2>&1 && python3 -c '' 2>/dev/null; then
        if python3 -c "import json,sys;json.load(open(sys.argv[1],encoding='utf-8'))" "$1" 2>/dev/null; then
            ok "$(basename "$1") is valid JSON"
        else
            bad "$(basename "$1") is invalid JSON" "$(python3 -c "import json,sys;json.load(open(sys.argv[1],encoding='utf-8'))" "$1" 2>&1 | tail -1)"
        fi
    elif command -v jq >/dev/null 2>&1; then
        if jq -e . "$1" >/dev/null 2>&1; then
            ok "$(basename "$1") is valid JSON"
        else
            bad "$(basename "$1") is invalid JSON"
        fi
    else
        printf '  \033[33mskip\033[0m  %s (no python3/jq)\n' "$(basename "$1")"
    fi
}
check_json "$EXT/manifest.json"

# ── 2. message type consistency ─────────────────────────────────────
section "2. background handlers match caller requests"

# Types registered in background
registered=$(sed -n 's/^listeners\.\([A-Za-z0-9_]*\)\s*=.*/\1/p' "$EXT/background.js" | sort -u)
[ -n "$registered" ] && ok "background registers: $(echo $registered | tr '\n' ' ')"

# Types requested via sendMessage in popup.js / options.js.
# Only lines containing sendMessage count: options.js also holds a
# field-label map with `type: 'Type'` (conflict field display name), which is
# not a message type. The Chinese text there never matched [A-Za-z0-9_], so
# the English translation exposed this over-broad grep.
called=$(grep -h "sendMessage" "$EXT/popup/popup.js" "$EXT/options/options.js" 2>/dev/null \
    | grep -o "type: *'[A-Za-z0-9_]*'" \
    | sed "s/type: *'//;s/'//" | sort -u)
if [ -n "$called" ]; then ok "UI requests: $(echo $called | tr '\n' ' ')"; fi

missing=""
for m in $called; do
    echo "$registered" | grep -qx "$m" || missing="$missing $m"
done
[ -z "$missing" ] && ok "every requested type has a handler" \
    || bad "these types have no handler:$missing (clicks return unknown message type)"

unused=""
for m in $registered; do
    echo "$called" | grep -qx "$m" || unused="$unused $m"
done
[ -z "$unused" ] || printf '  \033[33mnote\033[0m  handlers with no UI caller yet:%s\n' "$unused"

# ── 3. imported files exist ─────────────────────────────────────────
section "3. ES module import targets"
bad_imports=0
for f in "$EXT"/*.js "$EXT"/lib/*.js "$EXT"/popup/*.js "$EXT"/options/*.js; do
    [ -f "$f" ] || continue
    dir=$(dirname "$f")
    # Extract relative imports: from './x' and import('./x')
    # Use grep -o, not sed: sed \x escapes in BRE are not portable, and Git
    # Bash fails with "Unmatched ) or " — pointing nowhere near the problem.
    for spec in $(grep -o "from *['\"]\(\.[^'\"]*\)['\"]" "$f" | sed "s/.*['\"]\(\.[^'\"]*\)['\"]/\1/" 2>/dev/null); do
        target="$dir/$spec"
        if [ ! -f "$target" ]; then
            bad "$spec referenced in $(basename "$f") but missing"
            bad_imports=$((bad_imports+1))
        fi
    done
    # Dynamic import()
    for spec in $(grep -o "import *(['\"]\(\.[^'\"]*\)['\"]" "$f" | sed "s/.*['\"]\(\.[^'\"]*\)['\"]/\1/" 2>/dev/null); do
        target="$dir/$spec"
        if [ ! -f "$target" ]; then
            bad "dynamic import $spec in $(basename "$f") is missing"
            bad_imports=$((bad_imports+1))
        fi
    done
done
[ "$bad_imports" -eq 0 ] && ok "all import targets exist"

# ── 4. HTML ids match JS getElementById usage ───────────────────────
section "4. HTML ids match JS references"
check_ids() {
    html="$1"; js="$2"; name="$3"
    # The char class must include -: popup.html ids are mostly hyphenated
    # (first-run / clock-warning / last-sync). Without -, ids_html comes out
    # empty and "everything exists" means nothing.
    grep -oE 'id="[A-Za-z0-9_-]+"' "$html" 2>/dev/null | sed 's/^id="//;s/"$//' | sort -u > "$W/ids_html"

    # Both id-fetch spellings must be caught: popup/options define
    # `const $ = (id) => document.getElementById(id)`, so call sites read
    # $('sync'); grepping only getElementById matches nothing and silently
    # becomes "skip" — looking like "not checked" when it is "check broken".
    #
    # Both details bit us before:
    #   · $ is a BRE line anchor, match the literal with -E plus a [\$] class;
    #   · the class must include - (hyphen). With [A-Za-z0-9_], ids like
    #     $('no-such-element') **never match**, so "check passes" is a false green.
    {
        grep -oE "getElementById\('[A-Za-z0-9_-]+'\)" "$js" 2>/dev/null | sed "s/getElementById('//;s/')//"
        grep -oE "[\$]\('[A-Za-z0-9_-]+'\)" "$js" 2>/dev/null | sed "s/^[\$]('//;s/')$//"
    } | sort -u > "$W/ids_js"

    if [ ! -s "$W/ids_js" ]; then
        printf '  \033[33mskip\033[0m  %s (no id lookups found)\n' "$name"
        return 0
    fi
    n=$(wc -l < "$W/ids_js" | tr -d ' ')
    miss=$(comm -13 "$W/ids_html" "$W/ids_js")
    if [ -z "$miss" ]; then
        ok "$name: all $n referenced ids exist"
    else
        bad "$name references ids missing from HTML:$miss"
    fi
}
check_ids "$EXT/popup/popup.html" "$EXT/popup/popup.js" "popup"
check_ids "$EXT/options/options.html" "$EXT/options/options.js" "options"

# ── 5. manifest points at real files ────────────────────────────────
section "5. manifest references exist"
missing_manifest=""
for f in $(sed -n 's/.*"default_popup": *"\([^"]*\)".*/\1/p' "$EXT/manifest.json") \
         $(sed -n 's/.*"page": *"\([^"]*\)".*/\1/p' "$EXT/manifest.json"); do
    [ -f "$EXT/$f" ] || missing_manifest="$missing_manifest $f"
done
# Icons declared in "icons" / "default_icon" must ship in the package:
# AMO rejects the upload when a declared icon file is missing or has
# different dimensions than declared (48/96 here).
for f in $(sed -n 's/.*"[0-9][0-9]*": *"\([^"]*\.png\)".*/\1/p; s/.*"default_icon": *"\([^"]*\)".*/\1/p' "$EXT/manifest.json"); do
    [ -f "$EXT/$f" ] || missing_manifest="$missing_manifest $f"
done
[ -z "$missing_manifest" ] && ok "manifest pages all exist" \
    || bad "manifest points at missing files:$missing_manifest"

# ── 6. root ids match the C# side ───────────────────────────────────
section "6. Root constants match the C# side"
for pair in "ROOT_TOOLBAR:toolbar_____" "ROOT_MENU:menu________" \
            "ROOT_UNFILED:unfiled_____" "ROOT_MOBILE:mobile______"; do
    name="${pair%%:*}"; want="${pair#*:}"
    got=$(sed -n "s/^export const $name *= *'\([^']*\)'.*/\1/p" "$EXT/lib/keys.js")
    if [ "$got" = "$want" ]; then
        ok "$name = $want"
    else
        bad "$name differs: JS=$got expected=$want"
    fi
done

# C# source tree. Code is split into src/ projects; both constants live in
# BookmarkSync.Domain — the counterpart of hlc.js / collect.js.
CS_SRC="$script_dir/../bmsync/src/BookmarkSync.Domain"
CS_HLC="$CS_SRC/Hlc.cs"
CS_STATE="$CS_SRC/State.cs"

# Confirm the files are there first: a wrong path makes sed silently return
# empty, and the comparison below would misreport "sides disagree". Stop here.
if [ ! -f "$CS_HLC" ] || [ ! -f "$CS_STATE" ]; then
    bad "C# sources not found: $CS_HLC / $CS_STATE — layout changed? update this script"
    printf '\n\033[1mResult: %d passed, %d failed\033[0m\n' "$pass" "$fail"
    exit 1
fi

# ── 7. HLC constants match across sides ─────────────────────────────
section "7. HLC encoding matches across sides"
cs_digits=$(sed -n 's/.*PhysicalDigits *= *\([0-9]*\).*/\1/p' "$CS_HLC")
cs_cdigits=$(sed -n 's/.*CounterDigits *= *\([0-9]*\).*/\1/p' "$CS_HLC")
js_digits=$(sed -n 's/.*PHYSICAL_DIGITS *= *\([0-9]*\).*/\1/p' "$EXT/lib/hlc.js")
js_cdigits=$(sed -n 's/.*COUNTER_DIGITS *= *\([0-9]*\).*/\1/p' "$EXT/lib/hlc.js")
if [ -n "$cs_digits" ] && [ "$cs_digits" = "$js_digits" ] && [ "$cs_cdigits" = "$js_cdigits" ]; then
    ok "physical digits=$cs_digits counter digits=$cs_cdigits (both sides match)"
else
    bad "HLC widths differ C#=($cs_digits,$cs_cdigits) JS=($js_digits,$js_cdigits)"
fi

# ── 8. schema versions match across sides ───────────────────────────
section "8. Schema versions match across sides"
cs_v=$(sed -n 's/.*public const int Version *= *\([0-9]*\).*/\1/p' "$CS_STATE")
js_v=$(sed -n 's/^export const SCHEMA_VERSION *= *\([0-9]*\).*/\1/p' "$EXT/lib/collect.js")
if [ "$cs_v" = "$js_v" ] && [ -n "$cs_v" ]; then
    ok "schema v$cs_v (both sides match)"
else
    bad "schema versions differ C#='$cs_v' JS='$js_v' — sync will be rejected with 400"
fi

# ── 9. system root constants match across sides ─────────────────────
# Root ids are hardcoded strings; grepping beats running any test:
# the checker answers in its first second without a .NET SDK.
section "9. Root constants match across sides"
for rid in toolbar_____ menu________ unfiled_____ mobile______; do
    if grep -q "\"$rid\"" "$CS_STATE" && grep -q "'$rid'" "$EXT/lib/keys.js"; then
        ok "$rid matches on both sides"
    else
        bad "root id $rid missing on the C# or JS side"
    fi
done

# ── 10. AMO data-collection disclosure present ───────────────────────
# New AMO submissions are rejected without
# browser_specific_settings.gecko.data_collection_permissions.
# This extension uploads the full bookmark tree to the user's own server,
# so "required" must include bookmarksInfo (see extensionworkshop docs
# "Firefox built-in consent for data collection and transmission").
section "10. AMO data-collection disclosure present"
if grep -q '"data_collection_permissions"' "$EXT/manifest.json" \
   && grep -q '"bookmarksInfo"' "$EXT/manifest.json"; then
    ok "data_collection_permissions requires bookmarksInfo"
else
    bad "manifest lacks data_collection_permissions/bookmarksInfo — AMO rejects the upload"
fi

printf '\n\033[1mResult: %d passed, %d failed\033[0m\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
