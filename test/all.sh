#!/bin/sh
# 跑全部检查。提交前执行这个。
#
#   ./test/all.sh
#
# 分五层，从快到慢：
#   1. 语法检查 + C# 编译 —— 秒级
#   2. 扩展一致性      —— 不需要 JS 引擎，Node 不在也能跑
#   3. 扩展单元测试    —— 需要 Node 18+
#   4. .NET 单元测试   —— 需要 dotnet
#   5. 端到端冒烟      —— 需要 curl + dotnet 发布
#
# 有意**不**依赖 Node：开发环境不一定有，但 dotnet 一定有（服务端就是它写的）。

set -eu
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
root=$(CDPATH='' cd -- "$script_dir/.." && pwd -P)

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT

pass=0
fail=0
skipped=0

# 四层 → 五层（加了一层 JS 测试）。分从快到慢：
#   1. 语法检查 + C# 编译 —— 秒级
#   2. 扩展一致性      —— 不需要 Node
#   3. 扩展单元测试    —— 需要 Node 18+
#   4. .NET 单元测试   —— 需要 dotnet
#   5. 端到端冒烟      —— 需要 curl + dotnet 发布
#
# 有意**不**依赖 Node：开发环境不一定有，但 dotnet 一定有（服务端就是它写的）。
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

# 用 build 而不是 dotnet format / analyzers：Directory.Build.props 里开了
# TreatWarningsAsErrors，所以 build 通过就意味着没有编译警告 ——
# 而警告恰恰是重构期"漏改"的主要提示（换了类型名只在某个引用点提示一次，
# 很容易划过去）。
if have dotnet; then
  if (cd "$root/bmsync" && dotnet build -v quiet --nologo 2>"$W/build"); then
    ok "C# 编译通过（含警告即错误）"
  else
    no "C# 编译失败: $(head -5 "$W/build")"
  fi
else
  skip "C# 编译" "无 dotnet"
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

# ── 4. C# 单元测试 ───────────────────────────────────────────────────
hdr "4. C# 单元测试"
if have dotnet; then
  if (cd "$root/bmsync" && dotnet test --nologo -v quiet >"$W/gotest" 2>&1); then
    # 用 find 递归数 [Fact]/[Theory]，而不是 `dotnet test` 输出的计数。
    # 原因见下面 coverage 段的注释：解析测试运行器的输出格式，
    # 会在它改版时静默给出 0。
    n=$(find "$root/bmsync/tests" -name '*.cs' -exec grep -hoE '\[(Fact|Theory)\]' {} + 2>/dev/null | wc -l | tr -d ' ')
    if [ "$n" -gt 0 ]; then
      ok "全部通过（$n 个用例）"
    else
      no "用例数为 0 —— 统计用的匹配式失效了？"
    fi
  else
    no "C# 测试失败"
    grep -E '\[FAIL\]|失败' "$W/gotest" | head -10 | sed 's/^/      /'
  fi

  # 覆盖率单独跑一次并独立判定，不与上面的 `dotnet test` 共享退出码 ——
  # 两者失败的原因可能完全不同（单测挂 vs. 只是加采集器需要重建），
  # 混在一起报告会让真正的原因被掩盖。
  rm -rf "$root/bmsync/tests"/*/TestResults
  if (cd "$root/bmsync" && dotnet test --nologo --collect:"XPlat Code Coverage" >"$W/cov" 2>&1); then
    # 从 cobertura 报告里读，而不是从 dotnet test 的输出里正则抠。
    # dotnet test 的输出格式每个版本都在变，而 cobertura 是稳定契约。
    #
    # 报**最低**那个测试项目而不是平均：这里要回答的是"最薄弱的环节在哪"，
    # 拿平均值会把 Store 层的短板摊平成一个好看的数字。
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
    ) || no "覆盖率未产出（$(tail -3 "$W/cov" | tr '\n' ' ')）"

    if [ -n "${cov_report:-}" ]; then
      cov_min=${cov_report%%|*}
      cov_rest=${cov_report#*|}
      cov_worst=${cov_rest%%|*}
      ok "覆盖率最低 ${cov_min}%（${cov_worst}）"
      printf '       %s\n' "${cov_rest#*|}"
    fi
  else
    # 不要用 `grep -i fail` 找原因：dotnet test 的**通过**汇总行里就含
    # "Failed:     0"，会被它匹配上，于是真正的原因被一行误导性的
    # "Failed: 0" 顶掉 —— 而这正是当初这条检查写成"覆盖率统计失败"却
    # 没人看出 dotnet test 为什么返回非 0 的原因。
    no "覆盖率统计失败: $(tail -3 "$W/cov" | tr '\n' ' ')"
  fi
  rm -rf "$root/bmsync/tests"/*/TestResults

  # 跨平台发布验证：生产镜像跑在 Linux 上。
  #
  # 这里刻意**不**开 NativeAOT（-p:PublishAot=true）：它要 C++ 链接器
  # （Windows 上要 VS 的 C++ 工作负载），而且不能交叉编译 ——
  # 在 Windows 上 publish -r linux-x64 + AOT 会直接报
  # "Cross-OS native compilation is not supported"。AOT 交给 Dockerfile
  # 在 Linux 容器里做，本地只验证"能编出 Linux 版的 IL 程序集"。
  if (cd "$root/bmsync" && dotnet publish src/BookmarkSync.Cli -c Release -r linux-x64 \
        --self-contained true -o "$W/lin" -v quiet --nologo 2>"$W/xbuild"); then
    ok "Linux 发布通过（$(wc -c < "$W/lin/bmsync" | tr -d ' ') 字节）"
  else
    no "Linux 发布失败: $(head -5 "$W/xbuild")"
  fi
else
  skip "C# 测试与跨平台发布" "无 dotnet"
fi

# ── 5. 端到端冒烟 ────────────────────────────────────────────────────
hdr "5. 端到端冒烟"
if have dotnet && have curl; then
  # 发布到临时目录而不是仓库里。跑 5 遍你就会发现 .gitignore 里多出一堆
  # 一次性的构建产物条目 —— 那是"工具产生垃圾"的信号，不是要靠
  # .gitignore 兜住。
  rm -rf "$W/app"
  (cd "$root/bmsync" && dotnet publish src/BookmarkSync.Cli -c Release \
      -o "$W/app" -v quiet --nologo 2>"$W/build") || true

  # Windows 上没有 bmsync.exe 之外的可执行名；Linux 上则相反。
  if   [ -x "$W/app/bmsync" ];    then BIN="$W/app/bmsync"
  elif [ -x "$W/app/bmsync.exe" ]; then BIN="$W/app/bmsync.exe"
  else
    BIN=""
  fi

  if [ -z "$BIN" ]; then
    no "发布服务端失败: $(head -5 "$W/build")"
  elif out=$(BIN="$BIN" bash "$script_dir/smoke.sh" 2>&1); then
    n=$(printf '%s' "$out" | sed -n 's/.*结果: \([0-9]*\) 通过.*/\1/p')
    ok "冒烟测试通过（$n 项断言）"
  else
    no "冒烟测试失败"
    printf '%s\n' "$out" | sed 's/^/      /' | tail -40
  fi
else
  skip "端到端冒烟" "缺 dotnet 或 curl"
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
