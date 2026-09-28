#!/bin/sh
# 端到端冒烟测试：用纯 HTTP 驱动真实服务端。
#
# 与 go test 的分工：go test 走 httptest 内存服务器，验证逻辑；
# 这个脚本走真实 socket + 真实落盘，验证「JSON 编解码 + 鉴权 + 限流 +
# 原子写 + 快照」这条链路 —— httptest 不会暴露序列化层的 bug。
#
# 用法：
#   ./smoke.sh                                       # 自动起服务、跑完清理
#   HOST=example.com TOKEN=xxx ./smoke.sh --remote   # 打远程（会改真实数据）
#
# 依赖：curl。读 JSON 三级降级：python3 → jq → 项目自带的 test/jsonq。
#
# ── 为什么 payload 一律用文件 ──────────────────────────────────────────
#
# 早期版本把 JSON 直接写在命令行里：api POST /sync "{\"device\":...$(bm ...)}"。
# 双重引号里嵌命令替换再嵌函数调用，出错时的报错信息完全指不到真正的
# 位置（"syntax error near unexpected token `)'"，而实际是少了一个反斜杠）。
# 现在统一用 heredoc 写临时文件、curl --data-binary @file 发送 ——
# 引号层级从三层降到零，JSON 可以按 JSON 的样子读。

set -eu

# ── 路径解析 ──────────────────────────────────────────────────────────
#
# 必须基于**脚本自身所在目录**解析，而不是当前工作目录。否则
# `bash test/smoke.sh`（从仓库根跑）和 `cd test && bash smoke.sh` 会指向
# 不同的二进制文件 —— 那种 bug 极难察觉，因为换个目录就好了。
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
[ -n "$script_dir" ] || script_dir=$(pwd -P)

HOST="${HOST:-127.0.0.1:18099}"
TOKEN="${TOKEN:-}"
API="http://$HOST/api"
BIN="${BIN:-$script_dir/../bmsync/bmsync}"
KILL_PID=""
DATA_DIR=""
WORK=""

cleanup() {
    [ -n "$KILL_PID" ] && kill "$KILL_PID" 2>/dev/null || true
    [ -n "$DATA_DIR" ] && rm -rf "$DATA_DIR"
    [ -n "$WORK" ] && rm -rf "$WORK"
    return 0
}
trap cleanup EXIT

pass=0
fail=0
ok()  { pass=$((pass+1)); printf '  \033[32mok\033[0m    %s\n' "$1"; }
bad() { fail=$((fail+1)); printf '  \033[31mFAIL\033[0m  %s\n' "$1"
        [ -n "${2:-}" ] && printf '        %s\n' "$2"; return 0; }
section() { printf '\n\033[1m%s\033[0m\n' "$1"; }

WORK=$(mktemp -d)

# ── JSON 后端 ─────────────────────────────────────────────────────────
# 表达式的语法三种后端各不相同，所以不写通用表达式函数，而是把每个查询
# 写成具名函数在内部按后端分派。断言处因此保持可读。
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
            # 总是重建，不只在文件缺失时。
            # 之前只在 `[ ! -x ]` 时编译，于是改了 main.go 之后脚本仍在用
            # 旧二进制 —— 表现为"新加的函数名报 路径必须以 d 开头"，
            # 而错误信息完全指不到"二进制是旧的"这件事。
            #
            # 编到源目录（而不是临时目录）是有意的：jsonq/ 已经是一个
            # 独立的 go module，把它当"仓库内的小工具"更符合直觉，
            # 且 .gitignore 里只有一条。
            (cd "$script_dir/jsonq" && go build -o jsonq .) >/dev/null 2>&1 || true
        fi
        if [ -x "$JQ_BIN" ]; then
            MODE=bin
        else
            echo "无法读取 JSON：需要 python3、jq，或可用的 go 来编译 test/jsonq" >&2
            exit 1
        fi
    fi
    printf 'JSON 后端: %s\n' "$MODE"
}

py() {
    printf '%s' "$1" | python3 -c "import sys,json
d=json.load(sys.stdin)
print($2)" 2>/dev/null || echo ""
}

# ── 具名查询 ─────────────────────────────────────────────────────────

# 响应体里 state 的 item 总数
q_items() {
    case "$MODE" in
        py)  py "$1" "len(d['state']['items'])" ;;
        jq)  printf '%s' "$1" | jq -r '(.state.items | length)' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'len(d["state"]["items"])' 2>/dev/null || echo "" ;;
    esac
}

# 响应体里 summary.<field>
q_summary() {
    case "$MODE" in
        py)  py "$1" "d['summary']['$2']" ;;
        jq)  printf '%s' "$1" | jq -r ".summary[\"$2\"]" 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" "get(d[\"summary\"], \"$2\")" 2>/dev/null || echo "" ;;
    esac
}

# 响应体里 state.<field>
q_state() {
    case "$MODE" in
        py)  py "$1" "d['state'].get('$2','')" ;;
        jq)  printf '%s' "$1" | jq -r ".state[\"$2\"] // empty" 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" "get(d[\"state\"], \"$2\")" 2>/dev/null || echo "" ;;
    esac
}

# 响应体里 state 里的墓碑数（d == true）
q_tombstones() {
    case "$MODE" in
        py)  py "$1" "sum(1 for v in d['state']['items'].values() if v.get('d'))" ;;
        jq)  printf '%s' "$1" | jq -r '[.state.items[] | select(.d == true)] | length' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'count(d["state"]["items"], "d")' 2>/dev/null || echo "" ;;
    esac
}

# 响应体里 state 里的活跃项数
q_active() {
    case "$MODE" in
        py)  py "$1" "sum(1 for v in d['state']['items'].values() if not v.get('d'))" ;;
        jq)  printf '%s' "$1" | jq -r '[.state.items[] | select(.d != true)] | length' 2>/dev/null || echo "" ;;
        bin)
            # jsonq 只提供"为真计数"，没有"为假计数"，所以用总数减墓碑数。
            _t=$(printf '%s' "$1" | "$JQ_BIN" 'len(d["state"]["items"])' 2>/dev/null || echo "")
            _d=$(printf '%s' "$1" | "$JQ_BIN" 'count(d["state"]["items"], "d")' 2>/dev/null || echo "")
            if [ -n "$_t" ] && [ -n "$_d" ]; then echo $((_t - _d)); else echo ""; fi
            ;;
    esac
}

# 响应体里 conflicts 的条数
q_conflicts() {
    case "$MODE" in
        py)  py "$1" "len(d['conflicts'])" ;;
        jq)  printf '%s' "$1" | jq -r '(.conflicts | length)' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'len(d["conflicts"])' 2>/dev/null || echo "" ;;
    esac
}

# 冲突条目的某个字段（取数组里最后一个元素的该字段）
#
# 注意 jsonq 后端用的是 last(...) 而不是 get(...)：conflicts 是**数组**，
# get 对数组会安静地返回空串，那会变成"功能没实现"式的假象。
q_conflict_field() {
    case "$MODE" in
        py)  py "$1" "d['conflicts'][-1]['$2'] if d['conflicts'] else ''" ;;
        jq)  printf '%s' "$1" | jq -r ".conflicts[-1][\"$2\"] // empty" 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" "last(d[\"conflicts\"], \"$2\")" 2>/dev/null || echo "" ;;
    esac
}

# 响应体里 snapshots 的条数
q_snapshots() {
    case "$MODE" in
        py)  py "$1" "len(d['snapshots'])" ;;
        jq)  printf '%s' "$1" | jq -r '(.snapshots | length)' 2>/dev/null || echo "" ;;
        bin) printf '%s' "$1" | "$JQ_BIN" 'len(d["snapshots"])' 2>/dev/null || echo "" ;;
    esac
}

# 落盘 state.json 的 item 数。
# 落盘文件顶层就有 items；HTTP 响应体才包一层 state。两种都试。
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

# ── 时间与 HLC ───────────────────────────────────────────────────────

now_ms() {
    if [ "$MODE" = py ]; then
        python3 -c 'import time;print(int(time.time()*1000))'
    else
        awk 'BEGIN{printf "%d", systime()*1000}'
    fi
}

hlc() { printf '%013d-%05d' "$1" "${2:-0}"; }

# ── payload 构造 ──────────────────────────────────────────────────────
# 全部写进文件，curl 用 --data-binary @file 发送。JSON 长什么样就怎么写，
# 不做命令行转义。

R_TOOLBAR="toolbar_____"
K1=11111111111111111111111111111111
K2=22222222222222222222222222222222
K3=33333333333333333333333333333333
K4=44444444444444444444444444444444
K5=55555555555555555555555555555555

# bm <key> <标题> <url> <毫秒> <计数> [额外JSON片段] —— 输出一条 item
bm() {
    printf '"%s":{"p":"%s","t":"b","n":"%s","u":"%s","a":"%s","m":"%s"%s}' \
        "$1" "$R_TOOLBAR" "$2" "$3" "$(hlc "$4" 0)" "$(hlc "$4" "$5")" "${6:-}"
}

# build_sync <输出文件> <设备名> <items-json> [base-json]
build_sync() {
    {
        printf '{"device":"%s","state":{"v":1,"items":{%s}},"base":{%s}}' \
            "$2" "$3" "${4:-}"
    } > "$1"
}

# ── HTTP ─────────────────────────────────────────────────────────────
# 状态码写入 $CODE，响应正文写入 $BODY。
#
# ⚠️ 刻意**不 echo 状态码**。早期是 `c=$(api ...)`，那样 api 在子 shell
# 里执行，函数内设置的 BODY 随子 shell 一起消失，所有 JSON 查询拿到空串，
# 断言全变成假失败。必须靠全局变量回传。
CODE=""
BODY=""

api_file() {  # api_file <method> <path> <payload文件|-> 
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

api_noauth_file() {  # 不带 Authorization
    _m="$1"; _p="$2"; _f="$3"
    _out=$(mktemp)
    CODE=$(curl -sS -o "$_out" -w '%{http_code}' -X "$_m" "$API$_p" \
        -H 'Content-Type: application/json' \
        --data-binary "@$_f" 2>/dev/null) || CODE=000
    BODY=$(cat "$_out"); rm -f "$_out"
}

# ── 启动本地服务 ─────────────────────────────────────────────────────
setup_json

if [ "${1:-}" != "--remote" ]; then
    if [ ! -x "$BIN" ]; then
        echo "找不到可执行的 $BIN，先在 bmsync/ 目录跑 go build" >&2
        exit 1
    fi
    DATA_DIR=$(mktemp -d)
    TOKEN="0123456789abcdef0123456789abcdef01234567"
    # HOST 已经是 "ip:port" 形式，直接用作监听地址。不要再补冒号 ——
    # ":127.0.0.1:18099" 会被 Go 判为非法地址。
    BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$HOST" \
        "$BIN" >"$DATA_DIR/server.log" 2>&1 &
    KILL_PID=$!
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        curl -sf -o /dev/null "http://$HOST/api/health" 2>/dev/null && break
        sleep 0.5
    done
    if ! curl -sf -o /dev/null "http://$HOST/api/health" 2>/dev/null; then
        echo "服务端没起来，日志：" >&2; cat "$DATA_DIR/server.log" >&2; exit 1
    fi
fi

[ -n "$TOKEN" ] || { echo "需要 TOKEN 环境变量" >&2; exit 1; }

EMPTY="$WORK/empty.json"
cat > "$EMPTY" <<'JSON'
{"device":"pc-work","state":{"v":1,"items":{}},"base":{}}
JSON

# ── 场景 ─────────────────────────────────────────────────────────────

section "0. 连通性与鉴权"
api_file GET /health -
[ "$CODE" = 200 ] && ok "health 返回 200" || bad "health 返回 $CODE" "$BODY"

api_noauth_file GET /health "$EMPTY"
[ "$CODE" = 200 ] && ok "health 无需 token（options 页「测试连接」依赖这一点）" \
    || bad "health 竟需要鉴权，$CODE"

_bad=$(mktemp)
printf '{"device":"x","state":{"v":1,"items":{}},"base":{}}' > "$_bad"
_out=$(mktemp)
CODE=$(curl -sS -o "$_out" -w '%{http_code}' -X POST "$API/sync" \
    -H "Authorization: Bearer wrongwrongwrongwrongwrongwrongwrongwrong" \
    -H 'Content-Type: application/json' --data-binary "@$_bad" 2>/dev/null) || CODE=000
rm -f "$_bad" "$_out"
[ "$CODE" = 401 ] && ok "错误 token 被拒（401）" || bad "错误 token 应 401，实际 $CODE"

section "1. 设备 A 上传 5 个书签"
NOW=$(now_ms)
ITEMS=""
for n in 一 二 三 四 五; do
    case "$n" in
        一) kk=$K1; u=https://one.example ;;
        二) kk=$K2; u=https://two.example ;;
        三) kk=$K3; u=https://three.example ;;
        四) kk=$K4; u=https://four.example ;;
        五) kk=$K5; u=https://five.example ;;
    esac
    ITEMS="$ITEMS$(bm "$kk" "$n" "$u" "$NOW" 1),"
done
ITEMS=${ITEMS%,}
P="$WORK/up5.json"
build_sync "$P" pc-personal "$ITEMS"
api_file POST /sync "$P"
[ "$CODE" = 200 ] && ok "上传成功" || bad "上传失败 $CODE" "$BODY"
n=$(q_items "$BODY")
[ "$n" = 5 ] && ok "服务端存下 5 项" || bad "期望 5 项，实际 '$n'"
cr=$(q_summary "$BODY" created)
[ "$cr" = 5 ] && ok "summary.created = 5" || bad "created 期望 5，实际 '$cr'"
h=$(q_state "$BODY" hlc)
[ -n "$h" ] && ok "响应带 hlc（客户端据此校时）" || bad "响应缺少 hlc"

section "2. 设备 B 本地空白，应收到全部 5 项"
api_file POST /sync "$EMPTY"
[ "$CODE" = 200 ] && ok "同步成功" || bad "同步失败 $CODE" "$BODY"
n=$(q_items "$BODY")
[ "$n" = 5 ] && ok "工作电脑收到 5 项" || bad "期望 5 项，实际 '$n'"

section "3. B 删除 2 项后上报，A 拉取应看到删除传播"
NOW=$(now_ms)
DEL=",\"d\":true,\"x\":$NOW"
ITEMS="$(bm "$K1" 一 https://one.example "$NOW" 1),$(bm "$K2" 二 https://two.example "$NOW" 2 "$DEL"),$(bm "$K3" 三 https://three.example "$NOW" 3 "$DEL")"
P="$WORK/del2.json"
build_sync "$P" pc-work "$ITEMS"
api_file POST /sync "$P"
[ "$CODE" = 200 ] && ok "删除上报成功" || bad "删除上报失败 $CODE" "$BODY"

api_file POST /sync "$EMPTY"
[ "$CODE" = 200 ] || bad "A 拉取失败 $CODE" "$BODY"
t=$(q_tombstones "$BODY")
[ "$t" = 2 ] && ok "A 端看到 2 个墓碑（删除已传播）" || bad "期望 2 墓碑，实际 '$t'"
a=$(q_active "$BODY")
[ "$a" = 3 ] && ok "剩余 3 个活跃书签" || bad "期望 3 活跃，实际 '$a'"

section "4. 幂等性：同一请求连发 3 次"
prev=""
drift=no
i=1
while [ "$i" -le 3 ]; do
    api_file POST /sync "$P"
    n=$(q_items "$BODY")
    if [ -n "$prev" ] && [ "$n" != "$prev" ]; then
        bad "第 $i 次结果漂移：$prev → $n"; drift=yes; break
    fi
    prev="$n"
    i=$((i + 1))
done
[ "$drift" = no ] && ok "重复同步结果稳定（$prev 项）"

section "5. 并发编辑 → 冲突日志"
NOW=$(now_ms)
BM_BASE=$(hlc 1000 1)
BASEMAP="\"$K1\":\"$BM_BASE\""

P="$WORK/srv-edit.json"
build_sync "$P" pc-personal "$(bm "$K1" 云端改的 https://one.example "$NOW" 0)" "$BASEMAP"
api_file POST /sync "$P"
[ "$CODE" = 200 ] || bad "服务端侧改动上报失败 $CODE" "$BODY"

P2="$WORK/cli-edit.json"
build_sync "$P2" pc-work "$(bm "$K1" 本地改的 https://one.example "$((NOW + 1))" 0)" "$BASEMAP"
api_file POST /sync "$P2"
[ "$CODE" = 200 ] || bad "客户端侧改动上报失败 $CODE" "$BODY"

api_file GET '/conflicts?limit=10' -
[ "$CODE" = 200 ] || bad "读取冲突失败 $CODE" "$BODY"
n=$(q_conflicts "$BODY")
case "$n" in
    ''|0) bad "期望至少 1 条冲突，实际 '$n'" ;;
    *)    ok "记录到 $n 条冲突" ;;
esac
r=$(q_conflict_field "$BODY" reason)
[ "$r" = concurrent_edit ] && ok "类型为 concurrent_edit" || bad "类型错误：'$r'"
f=$(q_conflict_field "$BODY" field)
[ "$f" = title ] && ok "冲突字段定位到 title" || bad "字段定位错误：'$f'"

section "6. 限额与畸形输入"

post_raw() {  # post_raw <原始JSON> —— 直接发字面量，测解析层
    _f=$(mktemp)
    printf '%s' "$1" > "$_f"
    api_file POST /sync "$_f"
    rm -f "$_f"
}

post_raw '{"device":"x","state":{"v":99,"items":{}},"base":{}}'
[ "$CODE" = 400 ] && ok "schema 版本不符 → 400" || bad "期望 400，实际 $CODE"

post_raw '{"device":"x","state":{"v":1,"items":{"short":{"p":"toolbar_____","t":"b","n":"x","u":"https://x.example","m":"0000000001000-00000"}}},"base":{}}'
[ "$CODE" = 400 ] && ok "非法 key → 400" || bad "期望 400，实际 $CODE"

post_raw '{"not":"valid json'
[ "$CODE" = 400 ] && ok "畸形 JSON → 400" || bad "期望 400，实际 $CODE"

post_raw '{"device":"x","state":{"v":1,"items":{}},"base":{},"typo":1}'
[ "$CODE" = 400 ] && ok "未知字段 → 400（客户端拼错能立刻发现）" || bad "期望 400，实际 $CODE"

api_file POST /sync "$EMPTY"
[ "$CODE" = 200 ] && ok "合法空 state → 200" || bad "期望 200，实际 $CODE"

section "7. 历史快照（回滚点）"
api_file GET /history -
[ "$CODE" = 200 ] && ok "history 可读" || bad "history 失败 $CODE" "$BODY"
n=$(q_snapshots "$BODY")
case "$n" in
    ''|0) bad "期望至少 1 个快照，实际 '$n'" ;;
    *)    ok "产生 $n 个快照" ;;
esac

section "8. 限流"
# 上限是 60/min —— 见 limits.go 里为什么手动触发的工具不能用 10/min。
# 打满 60 次太慢，这里只验证"正常用量不被限流"。
flooded=no
i=1
while [ "$i" -le 3 ]; do
    api_file POST /sync "$EMPTY"
    [ "$CODE" = 429 ] && { flooded=yes; break; }
    i=$((i + 1))
done
[ "$flooded" = no ] && ok "正常用量不被限流（上限刻意放宽到 60/min）" \
    || bad "仅 3 次请求就被限流 —— 上限对手动触发场景过紧"

section "9. 落盘与文件完整性"
if [ -n "$DATA_DIR" ]; then
    [ -f "$DATA_DIR/state.json" ] && ok "state.json 已落盘" || bad "state.json 不存在"
    [ -d "$DATA_DIR/history" ]   && ok "history 目录已建"   || bad "history 目录不存在"
    [ -f "$DATA_DIR/state.json.tmp" ] && bad "临时文件残留（原子写未完成）" \
        || ok "无临时文件残留（rename 已完成）"
    if [ -f "$DATA_DIR/state.json" ]; then
        n=$(q_file_items "$DATA_DIR/state.json")
        [ -n "$n" ] && ok "落盘文件可解析（$n 项）" || bad "落盘文件无法解析 —— 原子写被破坏"
    fi
else
    ok "（远程模式，跳过落盘检查）"
fi

printf '\n\033[1m结果: %d 通过, %d 失败\033[0m\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
