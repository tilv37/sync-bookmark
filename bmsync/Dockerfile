# syntax=docker/dockerfile:1
#
# 多阶段构建：编译 → 最小运行时
#
# 最终镜像基于 scratch，约 8–12 MB。
# 注意 scratch 没有 shell，所以 healthcheck 只能用 exec 形式调用
# 二进制自身的子命令（见 docker-compose.yml）。

# ── 构建阶段 ──────────────────────────────────────────────────────────
FROM golang:1.23-alpine AS build

WORKDIR /src

# 先只拷依赖描述，让依赖层能被缓存
COPY go.mod ./
RUN go mod download

COPY . .

# CGO_ENABLED=0 产出静态二进制，这是能跑在 scratch 上的前提。
# -trimpath 去掉构建机路径；-s -w 去符号表。
RUN CGO_ENABLED=0 GOOS=linux go build \
      -trimpath \
      -ldflags="-s -w" \
      -o /out/bmsync .

# 用静态分析器和测试先过一遍，避免把坏镜像推上去
RUN go vet ./... && go test ./...

# ── 运行阶段 ──────────────────────────────────────────────────────────
FROM scratch

COPY --from=build /out/bmsync /bmsync

# state.json / conflicts.json / history/ 都落在这里
VOLUME ["/data"]
EXPOSE 8080

ENV BMSYNC_ADDR=":8080" \
    BMSYNC_DATA="/data"

ENTRYPOINT ["/bmsync"]
