# syntax=docker/dockerfile:1
#
# 多阶段构建：编译（含测试）→ 最小运行时
#
# 最终镜像基于 scratch，约 20–30 MB（Go 版是 8–12 MB）。
# 注意 scratch 没有 shell，所以 healthcheck 只能用 exec 形式调用
# 二进制自身的子命令（见 docker-compose.yml）。
#
# 构建上下文是 bmsync/ 目录（见 deploy/docker-compose.yml 的 `build: ../bmsync`），
# 所以路径都相对该目录。

# ── 构建阶段 ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# 依赖描述先拷，让 NuGet 层的缓存能跨源码改动复用。
# .csproj 变了也要重下，所以把它们放在同一组。
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BookmarkSync.Domain/BookmarkSync.Domain.csproj     src/BookmarkSync.Domain/
COPY src/BookmarkSync.Store/BookmarkSync.Store.csproj       src/BookmarkSync.Store/
COPY src/BookmarkSync.Server/BookmarkSync.Server.csproj     src/BookmarkSync.Server/
COPY src/BookmarkSync.Cli/BookmarkSync.Cli.csproj           src/BookmarkSync.Cli/
COPY tests/BookmarkSync.Domain.Tests/BookmarkSync.Domain.Tests.csproj   tests/BookmarkSync.Domain.Tests/
COPY tests/BookmarkSync.Store.Tests/BookmarkSync.Store.Tests.csproj     tests/BookmarkSync.Store.Tests/
COPY tests/BookmarkSync.Server.Tests/BookmarkSync.Server.Tests.csproj   tests/BookmarkSync.Server.Tests/

RUN dotnet restore

COPY . .

# 先跑测试，避免把坏镜像推上去。
#
# 刻意在**测试之后**才 publish：反过来的话，一份编译不过的代码会先花几分钟
# 做 NativeAOT 编译，然后才在测试步骤失败 —— 而 NativeAOT 编译是这个项目里
# 最慢的一步，顺序反了会让每次改坏代码都白等它。
RUN dotnet test -c Release --nologo

# NativeAOT：产物不再需要 .so / libicu / libssl，这是 FROM scratch 唯一可行的方式。
#
# -p:PublishAot=true 必须在这里传（而不是写死在 csproj 里）：它要 C++ 链接器，
# 且**不能交叉编译** —— 在 Windows 上 publish -r linux-x64 + AOT 会报
# "Cross-OS native compilation is not supported"。所以 AOT 只能在这个
# Linux 容器里做，开发机上的 `dotnet test` 不受它影响。
#
# AssemblyName 是 bmsync（见 Cli.csproj），healthcheck 依赖这个名字。
RUN dotnet publish src/BookmarkSync.Cli \
        -c Release \
        -r linux-x64 \
        -p:PublishAot=true \
        -p:InvariantGlobalization=true \
        --output /out

# ── 运行阶段 ──────────────────────────────────────────────────────────
FROM scratch

COPY --from=build /out/bmsync /bmsync

# state.json / conflicts.json / history/ 都落在这里
VOLUME ["/data"]
EXPOSE 8080

ENV BMSYNC_ADDR=":8080" \
    BMSYNC_DATA="/data"

ENTRYPOINT ["/bmsync"]
