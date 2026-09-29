#!/bin/sh
# 扩展的静态检查。
#
# 本机可能没有 Node，所以这里只做不需要 JS 引擎的检查：
#   · JSON 文件是否合法（manifest.json）
#   · background 里注册的 message type 与调用方请求的是否一致
#   · import 的文件是否存在
#   · HTML 里引用的 id 与 JS 里 getElementById 的是否一致
#
# 这些都是"拼错名字"类的问题 —— 不会让扩展崩溃，但会让某个按钮
# 永远没反应，而那种 bug 从界面上极难定位。

set -eu
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
EXT="$script_dir/../extension"

pass=0
fail=0
ok()  { pass=$((pass+1)); printf '  \033[32mok\033[0m    %s\n' "$1"; }
bad() { fail=$((fail+1)); printf '  \033[31mFAIL\033[0m  %s\n' "$1"
        [ -n "${2:-}" ] && printf '        %s\n' "$2"; return 0; }
section() { printf '\n\033[1m%s\033[0m\n' "$1"; }

[ -d "$EXT" ] || { echo "找不到 $EXT" >&2; exit 1; }

W=$(mktemp -d)
trap 'rm -rf "$W"' EXIT

# ── 1. JSON 合法性 ────────────────────────────────────────────────────
section "1. JSON 文件"
check_json() {
    if command -v python3 >/dev/null 2>&1 && python3 -c '' 2>/dev/null; then
        if python3 -c "import json,sys;json.load(open(sys.argv[1],encoding='utf-8'))" "$1" 2>/dev/null; then
            ok "$(basename "$1") 是合法 JSON"
        else
            bad "$(basename "$1") JSON 非法" "$(python3 -c "import json,sys;json.load(open(sys.argv[1],encoding='utf-8'))" "$1" 2>&1 | tail -1)"
        fi
    elif command -v jq >/dev/null 2>&1; then
        if jq -e . "$1" >/dev/null 2>&1; then
            ok "$(basename "$1") 是合法 JSON"
        else
            bad "$(basename "$1") JSON 非法"
        fi
    else
        printf '  \033[33mskip\033[0m  %s（无 python3/jq）\n' "$(basename "$1")"
    fi
}
check_json "$EXT/manifest.json"

# ── 2. message type 一致性 ────────────────────────────────────────────
section "2. background 注册的 handler 与调用方请求的一致"

# background 里注册的类型
registered=$(sed -n 's/^listeners\.\([A-Za-z0-9_]*\)\s*=.*/\1/p' "$EXT/background.js" | sort -u)
[ -n "$registered" ] && ok "background 注册了: $(echo $registered | tr '\n' ' ')"

# popup.js / options.js 里 sendMessage 请求的类型
called=$(grep -ho "type: *'[A-Za-z0-9_]*'" "$EXT/popup/popup.js" "$EXT/options/options.js" 2>/dev/null \
    | sed "s/type: *'//;s/'//" | sort -u)
if [ -n "$called" ]; then ok "界面请求了: $(echo $called | tr '\n' ' ')"; fi

missing=""
for m in $called; do
    echo "$registered" | grep -qx "$m" || missing="$missing $m"
done
[ -z "$missing" ] && ok "所有请求的 type 都有对应 handler" \
    || bad "这些 type 没有 handler:$missing（点击后会返回 unknown message type）"

unused=""
for m in $registered; do
    echo "$called" | grep -qx "$m" || unused="$unused $m"
done
[ -z "$unused" ] || printf '  \033[33mnote\033[0m  这些 handler 目前没有界面在用:%s\n' "$unused"

# ── 3. import 的文件都存在 ────────────────────────────────────────────
section "3. ES module import 的目标文件"
bad_imports=0
for f in "$EXT"/*.js "$EXT"/lib/*.js "$EXT"/popup/*.js "$EXT"/options/*.js; do
    [ -f "$f" ] || continue
    dir=$(dirname "$f")
    # 提取相对 import：from './x' 与 import('./x')
    # 用 grep -o 而不是 sed：sed 的 \x 转义在 BRE 里不可移植，Git Bash 会报
    # "Unmatched ) or \" —— 那种报错完全指不到真正的问题。
    for spec in $(grep -o "from *['\"]\(\.[^'\"]*\)['\"]" "$f" | sed "s/.*['\"]\(\.[^'\"]*\)['\"]/\1/" 2>/dev/null); do
        target="$dir/$spec"
        if [ ! -f "$target" ]; then
            bad "$spec 在 $(basename "$f") 中被引用但不存在"
            bad_imports=$((bad_imports+1))
        fi
    done
    # 动态 import()
    for spec in $(grep -o "import *(['\"]\(\.[^'\"]*\)['\"]" "$f" | sed "s/.*['\"]\(\.[^'\"]*\)['\"]/\1/" 2>/dev/null); do
        target="$dir/$spec"
        if [ ! -f "$target" ]; then
            bad "动态 import 的 $spec 在 $(basename "$f") 中不存在"
            bad_imports=$((bad_imports+1))
        fi
    done
done
[ "$bad_imports" -eq 0 ] && ok "所有 import 目标都存在"

# ── 4. HTML 的 id 与 JS 的 getElementById 一致 ────────────────────────
section "4. HTML id 与 JS 引用一致"
check_ids() {
    html="$1"; js="$2"; name="$3"
    # 字符类同样必须含 -：popup.html 里的 id 大多是连字符的
    # （first-run / clock-warning / last-sync）。漏掉 - 的话 ids_html
    # 会是空的，于是"全部存在"这个结论毫无意义。
    grep -oE 'id="[A-Za-z0-9_-]+"' "$html" 2>/dev/null | sed 's/^id="//;s/"$//' | sort -u > "$W/ids_html"

    # 两种取 id 的写法都要抓：popup/options 里写成了
    # `const $ = (id) => document.getElementById(id)` 之后，调用处只剩
    # $('sync')，只 grep getElementById 会一个都匹配不到，
    # 静默变成 "skip" —— 看起来是"没检查"，实际是"检查失效"。
    #
    # 两个细节都是踩过坑的：
    #   · $ 在 BRE 里是行尾锚点，必须用 -E 加 [\$] 字符类匹配字面量；
    #   · 字符类必须包含 -（连字符）。用 [A-Za-z0-9_] 时，像
    #     $('no-such-element') 这样的 id **匹配不上**，于是"检查通过"
    #     只是因为压根没抓到 —— 是个假绿灯。
    {
        grep -oE "getElementById\('[A-Za-z0-9_-]+'\)" "$js" 2>/dev/null | sed "s/getElementById('//;s/')//"
        grep -oE "[\$]\('[A-Za-z0-9_-]+'\)" "$js" 2>/dev/null | sed "s/^[\$]('//;s/')$//"
    } | sort -u > "$W/ids_js"

    if [ ! -s "$W/ids_js" ]; then
        printf '  \033[33mskip\033[0m  %s（未找到取 id 的调用）\n' "$name"
        return 0
    fi
    n=$(wc -l < "$W/ids_js" | tr -d ' ')
    miss=$(comm -13 "$W/ids_html" "$W/ids_js")
    if [ -z "$miss" ]; then
        ok "$name 引用的 $n 个 id 全部存在"
    else
        bad "$name 中这些 id 在 HTML 里不存在:$miss"
    fi
}
check_ids "$EXT/popup/popup.html" "$EXT/popup/popup.js" "popup"
check_ids "$EXT/options/options.html" "$EXT/options/options.js" "options"

# ── 5. manifest 引用了实际存在的文件 ─────────────────────────────────
section "5. manifest 引用的文件都存在"
missing_manifest=""
for f in $(sed -n 's/.*"default_popup": *"\([^"]*\)".*/\1/p' "$EXT/manifest.json") \
         $(sed -n 's/.*"page": *"\([^"]*\)".*/\1/p' "$EXT/manifest.json"); do
    [ -f "$EXT/$f" ] || missing_manifest="$missing_manifest $f"
done
[ -z "$missing_manifest" ] && ok "manifest 指向的页面都存在" \
    || bad "manifest 指向这些不存在的文件:$missing_manifest"

# ── 6. 根目录 id 与 Go 端一致 ────────────────────────────────────────
section "6. 根目录常量与 Go 端一致"
for pair in "ROOT_TOOLBAR:toolbar_____" "ROOT_MENU:menu________" \
            "ROOT_UNFILED:unfiled_____" "ROOT_MOBILE:mobile______"; do
    name="${pair%%:*}"; want="${pair#*:}"
    got=$(sed -n "s/^export const $name *= *'\([^']*\)'.*/\1/p" "$EXT/lib/keys.js")
    if [ "$got" = "$want" ]; then
        ok "$name = $want"
    else
        bad "$name 不一致：JS=$got 期望=$want"
    fi
done

# Go 端常量所在的源文件。代码分成了 cmd/ + internal/ 三层，
# 这两个常量都在 internal/bookmarks/ 里 —— 与 hlc.js / collect.js 对应。
CS_SRC="$script_dir/../bmsync/src/BookmarkSync.Domain"
CS_HLC="$CS_SRC/Hlc.cs"
CS_STATE="$CS_SRC/State.cs"

# 先确认文件在：路径写错时 sed 会静默返回空串，后面的比较会给出
# "两端不一致"这种误导性的错误信息。宁可直接停在这里。
if [ ! -f "$CS_HLC" ] || [ ! -f "$CS_STATE" ]; then
    bad "找不到 C# 源码：$CS_HLC / $CS_STATE —— 目录结构变了？需同步更新本脚本"
    printf '\n\033[1m结果: %d 通过, %d 失败\033[0m\n' "$pass" "$fail"
    exit 1
fi

# ── 7. C# 端 HLC 常量与 JS 端一致 ────────────────────────────────────
section "7. HLC 编码格式两端一致"
cs_digits=$(sed -n 's/.*PhysicalDigits *= *\([0-9]*\).*/\1/p' "$CS_HLC")
cs_cdigits=$(sed -n 's/.*CounterDigits *= *\([0-9]*\).*/\1/p' "$CS_HLC")
js_digits=$(sed -n 's/.*PHYSICAL_DIGITS *= *\([0-9]*\).*/\1/p' "$EXT/lib/hlc.js")
js_cdigits=$(sed -n 's/.*COUNTER_DIGITS *= *\([0-9]*\).*/\1/p' "$EXT/lib/hlc.js")
if [ -n "$cs_digits" ] && [ "$cs_digits" = "$js_digits" ] && [ "$cs_cdigits" = "$js_cdigits" ]; then
    ok "物理位=$cs_digits 计数位=$cs_cdigits（两端一致）"
else
    bad "HLC 位宽不一致 C#=($cs_digits,$cs_cdigits) JS=($js_digits,$js_cdigits)"
fi

# ── 8. schema 版本两端一致 ───────────────────────────────────────────
section "8. schema 版本两端一致"
cs_v=$(sed -n 's/.*public const int Version *= *\([0-9]*\).*/\1/p' "$CS_STATE")
js_v=$(sed -n 's/^export const SCHEMA_VERSION *= *\([0-9]*\).*/\1/p' "$EXT/lib/collect.js")
if [ "$cs_v" = "$js_v" ] && [ -n "$cs_v" ]; then
    ok "schema v$cs_v（两端一致）"
else
    bad "schema 版本不一致 C#='$cs_v' JS='$js_v' —— 同步会被服务端以 400 拒绝"
fi

# ── 9. 四个系统根目录常量两端一致 ────────────────────────────────────
# 根目录 id 是硬编码字符串，grep 比对比运行任何测试都直接：
# 它在检查器启动的第一秒就给出结论，而不必先装好 .NET SDK。
section "9. 根目录常量两端一致"
for rid in toolbar_____ menu________ unfiled_____ mobile______; do
    if grep -q "\"$rid\"" "$CS_STATE" && grep -q "'$rid'" "$EXT/lib/keys.js"; then
        ok "$rid 两端一致"
    else
        bad "根目录 id $rid 在 C# 或 JS 端缺失"
    fi
done

printf '\n\033[1m结果: %d 通过, %d 失败\033[0m\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
