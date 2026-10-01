#!/bin/sh
# Run every check. Execute this before committing.
#
#   ./test/all.sh
#
# Five layers, fastest first:
#   1. Syntax checks + C# build — seconds
#   2. Extension consistency   — no JS engine needed, runs without Node
#   3. Extension unit tests    — needs Node 18+
#   4. .NET unit tests         — needs dotnet
#   5. End-to-end smoke        — needs curl + dotnet publish
#
# Deliberately does **not** require Node: dev machines may lack it, while
# dotnet is always present (the server is written in it).

set -eu
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
root=$(CDPATH='' cd -- "$script_dir/.." && pwd -P)

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT

pass=0
fail=0
skipped=0

# Four layers -> five (added a JS test layer). Fastest first:
#   1. Syntax checks + C# build — seconds
#   2. Extension consistency   — no Node needed
#   3. Extension unit tests    — needs Node 18+
#   4. .NET unit tests         — needs dotnet
#   5. End-to-end smoke        — needs curl + dotnet publish
#
# Deliberately does **not** require Node: dev machines may lack it, while
# dotnet is always present (the server is written in it).
# With Node present, one extra extension-unit-test layer runs; without it,
# that layer is explicitly skipped rather than faked as passed.

ok()  { pass=$((pass+1)); printf '  \033[32m✓\033[0m %s\n' "$1"; }
no()  { fail=$((fail+1)); printf '  \033[31m✗\033[0m %s\n' "$1"; }
skip(){ skipped=$((skipped+1)); printf '  \033[33m−\033[0m %s (skipped: %s)\n' "$1" "$2"; }

have() { command -v "$1" >/dev/null 2>&1; }

hdr() { printf '\n\033[1;36m━━ %s ━━\033[0m\n' "$1"; }

# ── 1. Syntax checks ─────────────────────────────────────────────────
hdr "1. Syntax checks"

if have bash; then
  for s in "$root"/test/*.sh "$root"/deploy/*.sh; do
    [ -f "$s" ] || continue
    if out=$(bash -n "$s" 2>&1); then
      ok "$(basename "$s") syntax OK"
    else
      no "$(basename "$s") syntax error: $out"
    fi
  done
else
  skip "shell syntax" "no bash"
fi

# Use build instead of dotnet format / analyzers: Directory.Build.props sets
# TreatWarningsAsErrors, so a passing build means zero compiler warnings —
# and warnings are the main signal for "missed a spot" during refactors (a
# renamed type warns at only one reference, easy to skim past).
if have dotnet; then
  if (cd "$root/bmsync" && dotnet build -v quiet --nologo 2>"$W/build"); then
    ok "C# build passed (warnings as errors)"
  else
    no "C# build failed: $(head -5 "$W/build")"
  fi
else
  skip "C# build" "no dotnet"
fi

# ── 2. Extension consistency ─────────────────────────────────────────
hdr "2. Extension consistency"
if have bash; then
  if out=$(bash "$script_dir/check-extension.sh" 2>&1); then
    printf '%s\n' "$out" | grep -cE 'ok' >/dev/null 2>&1 || true
    ok "extension consistency passed ($(printf '%s' "$out" | sed -n 's/.*Result: \([0-9]*\) passed.*/\1/p') checks)"
  else
    no "extension consistency failed"
    printf '%s\n' "$out" | sed 's/^/      /'
  fi
else
  skip "extension consistency" "no bash"
fi

# ── 3. Extension unit tests (Node, optional) ─────────────────────────
hdr "3. Extension unit tests (Node)"
if have node; then
  node_major=$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)
  if [ "$node_major" -ge 18 ]; then
    if (cd "$root/extension" && node --test --test-reporter=tap lib/*.test.js >"$W/jstest" 2>&1); then
      n=$(sed -n 's/^# pass \([0-9]*\).*/\1/p' "$W/jstest")
      ok "JS tests passed ($n cases)"
    else
      no "JS tests failed"
      grep -E '^(not ok|# (tests|pass|fail))' "$W/jstest" | head -15 | sed 's/^/      /'
    fi
  else
    skip "JS tests" "Node 18+ required (have $node_major)"
  fi
else
  skip "JS tests" "no node"
fi

# ── 4. C# unit tests ─────────────────────────────────────────────────
hdr "4. C# unit tests"
if have dotnet; then
  if (cd "$root/bmsync" && dotnet test --nologo -v quiet >"$W/gotest" 2>&1); then
    # Count [Fact]/[Theory] recursively with find instead of parsing the
    # `dotnet test` counter. Reason in the coverage section below: parsing the
    # runner output goes silently to 0 when it changes format.
    n=$(find "$root/bmsync/tests" -name '*.cs' -exec grep -hoE '\[(Fact|Theory)\]' {} + 2>/dev/null | wc -l | tr -d ' ')
    if [ "$n" -gt 0 ]; then
      ok "all passed ($n cases)"
    else
      no "case count is 0 — counting pattern broken?"
    fi
  else
    no "C# tests failed"
    grep -E '\[FAIL\]|Failed' "$W/gotest" | head -10 | sed 's/^/      /'
  fi

  # Coverage runs separately with its own verdict, not sharing `dotnet test`'s
  # exit code — the two fail for entirely different reasons (tests red vs.
  # collector rebuild needed), and merging the reports hides the real cause.
  rm -rf "$root/bmsync/tests"/*/TestResults
  if (cd "$root/bmsync" && dotnet test --nologo --collect:"XPlat Code Coverage" >"$W/cov" 2>&1); then
    # Read from the cobertura report, not via regex on dotnet test output.
    # The CLI output format changes every version; cobertura is a stable contract.
    #
    # Report the **lowest** test project, not the average: the question is
    # "where is the weakest link", and averaging flattens the Store gap into
    # a pretty number.
    cov_report=$(
      for f in "$root/bmsync/tests"/*/TestResults/*/coverage.cobertura.xml; do
        [ -f "$f" ] || continue
        proj=$(basename "$(dirname "$(dirname "$(dirname "$f")")")")
        rate=$(sed -n 's/.*line-rate="\([0-9.]*\)".*/\1/p' "$f" | head -1)
        [ -n "$rate" ] && awk -v p="$proj" -v r="$rate" \
          'BEGIN { printf "%s|%.1f\n", p, r*100 }'
      done | sort -t'|' -k2 -n | awk -F'|' '
        NR==1 { min=$2; worst=$1; n=0 }
        { detail = detail sprintf("  %s %s%%", $1, $2); n++ }
        END { if (n==0) exit 1; printf "%s|%s|%s", min, worst, detail }'
    ) || no "no coverage produced ($(tail -3 "$W/cov" | tr '\n' ' '))"

    if [ -n "${cov_report:-}" ]; then
      cov_min=${cov_report%%|*}
      cov_rest=${cov_report#*|}
      cov_worst=${cov_rest%%|*}
      ok "lowest coverage ${cov_min}% (${cov_worst})"
      printf '       %s\n' "${cov_rest#*|}"
    fi
  else
    # Do not grep -i fail for the cause: the **passing** summary line of
    # dotnet test contains "Failed:     0", which matches and then tops the
    # real cause with a misleading "Failed: 0" — exactly why this check once
    # reported "coverage failed" while nobody saw why dotnet test exited nonzero.
    no "coverage collection failed: $(tail -3 "$W/cov" | tr '\n' ' ')"
  fi
  rm -rf "$root/bmsync/tests"/*/TestResults

  # Cross-platform publish check: the production image runs on Linux.
  #
  # Deliberately **without** NativeAOT (-p:PublishAot=true): it needs a C++
  # linker (the C++ workload of VS on Windows) and cannot cross-compile —
  # publish -r linux-x64 + AOT on Windows fails with
  # "Cross-OS native compilation is not supported". AOT happens in the Linux
  # Dockerfile; locally only verify "a Linux IL build works".
  if (cd "$root/bmsync" && dotnet publish src/BookmarkSync.Cli -c Release -r linux-x64 \
        --self-contained true -o "$W/lin" -v quiet --nologo 2>"$W/xbuild"); then
    ok "Linux publish passed ($(wc -c < "$W/lin/bmsync" | tr -d ' ') bytes)"
  else
    no "Linux publish failed: $(head -5 "$W/xbuild")"
  fi
else
  skip "C# tests and cross-platform publish" "no dotnet"
fi

# ── 5. End-to-end smoke ──────────────────────────────────────────────
hdr "5. End-to-end smoke"
if have dotnet && have curl; then
  # Publish into a temp dir, never into the repo. After 5 runs you would find
  # a pile of one-off build artifacts in .gitignore — that signals "tooling
  # litters", not something for .gitignore to absorb.
  rm -rf "$W/app"
  (cd "$root/bmsync" && dotnet publish src/BookmarkSync.Cli -c Release \
      -o "$W/app" -v quiet --nologo 2>"$W/build") || true

  # Windows binaries carry .exe; Linux ones do not.
  if   [ -x "$W/app/bmsync" ];    then BIN="$W/app/bmsync"
  elif [ -x "$W/app/bmsync.exe" ]; then BIN="$W/app/bmsync.exe"
  else
    BIN=""
  fi

  if [ -z "$BIN" ]; then
    no "failed to publish server: $(head -5 "$W/build")"
  elif out=$(BIN="$BIN" bash "$script_dir/smoke.sh" 2>&1); then
    n=$(printf '%s' "$out" | sed -n 's/.*Result: \([0-9]*\) passed.*/\1/p')
    ok "smoke tests passed ($n assertions)"
  else
    no "smoke tests failed"
    printf '%s\n' "$out" | sed 's/^/      /' | tail -40
  fi
else
  skip "end-to-end smoke" "dotnet or curl missing"
fi

# ── Summary ──────────────────────────────────────────────────────────
printf '\n\033[1m%s\033[0m\n' "────────────────────────────"
if [ "$fail" -eq 0 ]; then
  printf '\033[1;32mall passed\033[0m  %d passed' "$pass"
  [ "$skipped" -gt 0 ] && printf ', %d skipped' "$skipped"
  printf '\n'
  exit 0
else
  printf '\033[1;31m%d failed\033[0m (passed %d, skipped %d)\n' "$fail" "$pass" "$skipped"
  exit 1
fi
