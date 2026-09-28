#!/bin/sh
# 跑全部检查。提交前执行这个。
#
#   ./test/all.sh
#
# 分四层，从快到慢：
#   1. 语法检查        —— 秒级
#   2. 扩展一致性      —— 不需要 JS 引擎，Node 不在也能跑
#   3. Go 单元测试     —— 需要 go
#   4. 端到端冒烟      —— 需要 curl + 可编译的二进制
#
# 有意**不**依赖 Node：开发环境不一定有，但 Go 一定有（服务端就是它写的）。

set -eu
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
root=$(CDPATH='' cd -- "$script_dir/.." && pwd -P)

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT

pass=0
fail=0
skipped=0

# 四层 → 五层（加了一层 JS 测试）。分从快到慢：
#   1. 语法检查        —— 秒级
#   2. 扩展一致性      —— 不需要 Node
#   3. 扩展单元测试    —— 需要 Node 18+
#   4. Go 单元测试     —— 需要 go
#   5. 端到端冒烟      —— 需要 curl + 可编译的二进制
#
# 有意**不**依赖 Node：开发环境不一定有，但 Go 一定有（服务端就是它写的）。
# Node 在时多跑一层扩展单元测试；不在时明确跳过而不是假装通过。

ok()  { pass=$((pass+1)); printf '  \033[32m✓\033[0m %s\n' "$1"; }
no()  { fail=$((fail+1)); printf '  \033[31m✗\033[0m %s\n' "$1"; }
skip(){ skipped=$((skipped+1)); printf '  \033[33m−\033[0m %s（跳过：%s）\n' "$1" "$2"; }

have() { command -v "$1" >/dev/null 2>&1; }

hdr() { printf '\n\033[1;36m━━ %s ━━\033[0m\n' "$1"; }

# ── 1. 语法检查 ──────────────────────────────────────────────────────
hdr "1. 语法检查"

if have bash; then
  for s in "$root"/test/*.sh "$root"/deploy/*.sh; do
    [ -f "$s" ] || continue
    if out=$(bash -n "$s" 2>&1); then
      ok "$(basename "$s") 语法正确"
    else
      no "$(basename "$s") 语法错误: $out"
    fi
  done
else
  skip "shell 语法" "无 bash"
fi

if have go; then
  if (cd "$root/bmsync" && go vet ./... 2>"$W/vet"); then
    ok "go vet 通过"
  else
    no "go vet 失败: $(head -3 "$W/vet")"
  fi
else
  skip "go vet" "无 go"
fi

# ── 2. 扩展一致性 ────────────────────────────────────────────────────
hdr "2. 扩展一致性"
if have bash; then
  if out=$(bash "$script_dir/check-extension.sh" 2>&1); then
    printf '%s\n' "$out" | grep -cE 'ok' >/dev/null 2>&1 || true
    ok "扩展一致性检查通过（$(printf '%s' "$out" | sed -n 's/.*结果: \([0-9]*\) 通过.*/\1/p') 项）"
  else
    no "扩展一致性检查失败"
    printf '%s\n' "$out" | sed 's/^/      /'
  fi
else
  skip "扩展一致性" "无 bash"
fi

# ── 3. 扩展单元测试（Node，可选）────────────────────────────────────
hdr "3. 扩展单元测试（Node）"
if have node; then
  node_major=$(node -p 'process.versions.node.split(".")[0]' 2>/dev/null || echo 0)
  if [ "$node_major" -ge 18 ]; then
    if (cd "$root/extension" && node --test --test-reporter=tap lib/*.test.js >"$W/jstest" 2>&1); then
      n=$(sed -n 's/^# pass \([0-9]*\).*/\1/p' "$W/jstest")
      ok "JS 测试通过（$n 个用例）"
    else
      no "JS 测试失败"
      grep -E '^(not ok|# (tests|pass|fail))' "$W/jstest" | head -15 | sed 's/^/      /'
    fi
  else
    skip "JS 测试" "Node 需 18+（当前 $node_major）"
  fi
else
  skip "JS 测试" "无 node"
fi

# ── 4. Go 测试 ───────────────────────────────────────────────────────
hdr "4. Go 单元测试"
if have go; then
  if (cd "$root/bmsync" && go test -count=1 ./... >"$W/gotest" 2>&1); then
    # grep -c 会把每个文件的匹配都打出来，数字要取最后一个
    n=$(grep -h '^func Test' "$root"/bmsync/*_test.go 2>/dev/null | wc -l | tr -d ' ')
    ok "全部通过（$n 个用例）"
  else
    no "Go 测试失败"
    grep -E '^(---|\s+---)? *(FAIL|--- FAIL)' "$W/gotest" | head -10 | sed 's/^/      /'
  fi

  # 覆盖率单独跑一次并独立判定，不与上面的 `go test` 共享退出码 ——
  # 两者失败的原因可能完全不同（单测挂 vs. 只是 -cover 需要重建包），
  # 混在一起报告会让真正的原因被掩盖。
  if (cd "$root/bmsync" && go test -cover -count=1 ./... >"$W/cov" 2>&1); then
    pct=$(sed -n 's/.*coverage: \([0-9.]*\)%.*/\1/p' "$W/cov")
    if [ -n "$pct" ]; then
      ok "覆盖率 ${pct}%"
    else
      no "覆盖率未输出（$(tail -2 "$W/cov" | tr '\n' ' ')）"
    fi
  else
    no "覆盖率统计失败: $(grep -m1 FAIL "$W/cov" 2>/dev/null || tail -2 "$W/cov" | tr '\n' ' ')"
  fi

  # 跨平台编译验证：生产镜像跑在 Linux 上
  if (cd "$root/bmsync" && GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o "$W/bmsync-linux" . 2>"$W/xbuild"); then
    ok "Linux 静态编译通过（$(wc -c < "$W/bmsync-linux" | tr -d ' ') 字节）"
  else
    no "Linux 编译失败: $(head -3 "$W/xbuild")"
  fi
else
  skip "Go 测试与交叉编译" "无 go"
fi

# ── 5. 端到端冒烟 ────────────────────────────────────────────────────
hdr "5. 端到端冒烟"
if have go && have curl; then
  # 编到临时目录而不是仓库里。跑 5 遍你就会发现 .gitignore 里多出一堆
  # 一次性的构建产物条目 —— 那是"工具产生垃圾"的信号，不是要靠
  # .gitignore 兜住。
  (cd "$root/bmsync" && CGO_ENABLED=0 go build -o "$W/bmsync" . 2>"$W/build") || true
  if [ ! -x "$W/bmsync" ]; then
    no "编译服务端失败: $(head -3 "$W/build")"
  elif out=$(BIN="$W/bmsync" bash "$script_dir/smoke.sh" 2>&1); then
    n=$(printf '%s' "$out" | sed -n 's/.*结果: \([0-9]*\) 通过.*/\1/p')
    ok "冒烟测试通过（$n 项断言）"
  else
    no "冒烟测试失败"
    printf '%s\n' "$out" | sed 's/^/      /' | tail -40
  fi
else
  skip "端到端冒烟" "缺 go 或 curl"
fi

# ── 汇总 ─────────────────────────────────────────────────────────────
printf '\n\033[1m%s\033[0m\n' "────────────────────────────"
if [ "$fail" -eq 0 ]; then
  printf '\033[1;32m全部通过\033[0m  %d 项通过' "$pass"
  [ "$skipped" -gt 0 ] && printf '，%d 项跳过' "$skipped"
  printf '\n'
  exit 0
else
  printf '\033[1;31m有 %d 项失败\033[0m（通过 %d，跳过 %d）\n' "$fail" "$pass" "$skipped"
  exit 1
fi
