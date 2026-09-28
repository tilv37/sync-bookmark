#!/bin/sh
# 本地开发服务：一键编译并以前台方式起 bmsync，方便调扩展。
#
# 用法：
#   ./test/dev.sh [选项]
#
# 选项：
#   --addr ADDR    监听地址，默认 127.0.0.1:18099（可用 BMSYNC_ADDR 覆盖）
#   --data DIR     数据目录，默认 <仓库根>/tmp-data（可用 BMSYNC_DATA 覆盖）
#   --token TOKEN  Bearer 令牌，默认开发用固定串（可用 BMSYNC_TOKEN 覆盖）
#   --no-build     跳过 go build，直接运行已有二进制
#   --reset        启动前清空数据目录（删 state.json / conflicts.json / history/）
#   -h, --help     显示本帮助
#
# 环境变量优先于默认值，命令行参数优先于环境变量。
# 数据目录与 deploy/ 无关：deploy/data/ 是容器挂载，tmp-data/ 仅本地调试用。
#
# 前台运行，Ctrl+C 即停。扩展设置页填下面打印的地址与 token 即可联调。

set -eu

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd -P)
root=$(CDPATH='' cd -- "$script_dir/.." && pwd -P)

DEFAULT_ADDR="127.0.0.1:18099"
DEFAULT_DATA="$root/tmp-data"
DEFAULT_TOKEN="0123456789abcdef0123456789abcdef01234567"
BIN="$root/bmsync/bmsync"

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
        --addr)  [ $# -ge 2 ] || { echo "--addr 缺少参数" >&2; exit 1; }; ADDR="$2"; shift 2 ;;
        --data)  [ $# -ge 2 ] || { echo "--data 缺少参数" >&2; exit 1; }; DATA_DIR="$2"; shift 2 ;;
        --token) [ $# -ge 2 ] || { echo "--token 缺少参数" >&2; exit 1; }; TOKEN="$2"; shift 2 ;;
        --no-build) NO_BUILD=1; shift ;;
        --reset) RESET=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "未知参数: $1（用 --help 查看）" >&2; exit 1 ;;
    esac
done

have() { command -v "$1" >/dev/null 2>&1; }

# health 检查用的 host:port。ADDR 可能是 ":8080" 形式，curl 需要补上 127.0.0.1。
health_host() {
    case "$ADDR" in
        :*) printf '127.0.0.1%s' "$ADDR" ;;
        *)  printf '%s' "$ADDR" ;;
    esac
}

if ! have go && [ "$NO_BUILD" -eq 0 ]; then
    echo "找不到 go，且未传 --no-build。请先安装 Go ≥ 1.23，或用 --no-build 复用已有二进制。" >&2
    exit 1
fi
if ! have curl; then
    echo "找不到 curl，无法做启动后的 health 等待。请先安装 curl。" >&2
    exit 1
fi

if [ "$TOKEN" = "$DEFAULT_TOKEN" ]; then
    printf '注意：使用默认开发 token，仅限本机调试，不要用于任何真实部署。\n' >&2
fi

if [ "$RESET" -eq 1 ] && [ -d "$DATA_DIR" ]; then
    rm -rf "$DATA_DIR"
    printf '已清空数据目录: %s\n' "$DATA_DIR"
fi
mkdir -p "$DATA_DIR"

if [ "$NO_BUILD" -eq 0 ]; then
    printf '编译中...\n'
    (cd "$root/bmsync" && CGO_ENABLED=0 go build -o "$BIN" .) || {
        echo "编译失败，见上方 go build 输出。" >&2
        exit 1
    }
elif [ ! -x "$BIN" ]; then
    echo "找不到可执行的 $BIN，先不带 --no-build 跑一次以编译。" >&2
    exit 1
fi

# 预检配置：token 长度、数据目录可写等问题在这里提前暴露，
# 而不是等服务起一半才从日志里翻。
if ! BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$ADDR" "$BIN" -config-check >/dev/null 2>&1; then
    echo "配置预检失败，原样再跑一次看报错：" >&2
    BMSYNC_TOKEN="$TOKEN" BMSYNC_DATA="$DATA_DIR" BMSYNC_ADDR="$ADDR" "$BIN" -config-check
    exit 1
fi

HOSTPORT=$(health_host)

# 服务已在跑时直接提示，而不是再起一个抢端口。
if curl -sf -o /dev/null "http://$HOSTPORT/api/health" 2>/dev/null; then
    echo "该地址已有服务在跑: http://$HOSTPORT/api/health" >&2
    echo "先停掉它，或换端口：./test/dev.sh --addr 127.0.0.1:18100" >&2
    exit 1
fi

printf '数据目录: %s\n' "$DATA_DIR"
printf '启动服务: %s (Ctrl+C 停止)\n' "$ADDR"

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
    echo "服务 10 秒内没起来，已终止（PID $PID）。" >&2
    kill "$PID" 2>/dev/null || true
    exit 1
fi

cat <<EOF

服务已就绪 ──────────────────────────────
  health:  curl http://$HOSTPORT/api/health
  sync:    curl -H "Authorization: Bearer \$BMSYNC_TOKEN" -X POST http://$HOSTPORT/api/sync

  扩展设置页填：
    服务器地址  http://$HOSTPORT
    Token       $TOKEN

  数据文件： $DATA_DIR/state.json
  快照目录： $DATA_DIR/history/
  清空重来： ./test/dev.sh --reset
──────────────────────────────────────────
日志直接打到本终端，Ctrl+C 停止服务。
EOF

wait "$PID"
