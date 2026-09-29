# syntax=docker/dockerfile:1
#
# 多阶段构建：编译（含测试）→ 最小运行时
#
# 最终镜像基于 scratch，约 20–30 MB（Go 版是 8–12 MB）。
# 注意 scratch 没有 shell，所以 healthcheck 只能用 exec 形式调用
# 二进制自身的子命令（见 docker-compose.yml）。
#
# 构建上下文是仓库根（见 deploy/docker-compose.yml 的 build.context），
# 所以路径都相对仓库根。向量文件 test/hlc_vectors.json 必须单独拷进来：
# Domain 测试从二进制目录一路向上找 test/hlc_vectors.json（HlcTests.VectorsPath），
# 保持 /src 下的相对布局与仓库一致，它才能找到。

# ── 构建阶段 ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

# NativeAOT 要调本地 C 链接器 Publish，而 SDK 镜像默认不带。
# 放最前面单独成层：它和源码无关，不会因改代码而重装。
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists

WORKDIR /src

# 依赖描述先拷，让 NuGet 层的缓存能跨源码改动复用。
# .csproj 变了也要重下，所以把它们放在同一组。
COPY bmsync/global.json bmsync/Directory.Build.props bmsync/Directory.Packages.props ./
COPY bmsync/BookmarkSync.slnx ./
COPY bmsync/src/BookmarkSync.Domain/BookmarkSync.Domain.csproj     src/BookmarkSync.Domain/
COPY bmsync/src/BookmarkSync.Store/BookmarkSync.Store.csproj       src/BookmarkSync.Store/
COPY bmsync/src/BookmarkSync.Server/BookmarkSync.Server.csproj     src/BookmarkSync.Server/
COPY bmsync/src/BookmarkSync.Cli/BookmarkSync.Cli.csproj           src/BookmarkSync.Cli/
COPY bmsync/tests/BookmarkSync.Domain.Tests/BookmarkSync.Domain.Tests.csproj   tests/BookmarkSync.Domain.Tests/
COPY bmsync/tests/BookmarkSync.Store.Tests/BookmarkSync.Store.Tests.csproj     tests/BookmarkSync.Store.Tests/
COPY bmsync/tests/BookmarkSync.Server.Tests/BookmarkSync.Server.Tests.csproj   tests/BookmarkSync.Server.Tests/

RUN dotnet restore

COPY bmsync/ .
COPY test/hlc_vectors.json ./test/hlc_vectors.json

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
# 保持 FROM scratch（约 20MB 级），但 NativeAOT 产物并非全静态：
# 它仍动态链接 glibc（ld-linux + libc.so.6 + libm.so.6），scratch 里
# 一个都没有就会报 "exec /bmsync: no such file or directory"。
# 所以把这三个文件与二进制一起拷进来。路径是 Debian(noble) 的固定布局，
# 与构建阶段同一镜像，升级基础镜像后若路径变化，构建会直接失败，
# 不会静默产出一个起不来的镜像。
FROM scratch

COPY --from=build /lib64/ld-linux-x86-64.so.2 /lib64/ld-linux-x86-64.so.2
COPY --from=build /lib/x86_64-linux-gnu/libc.so.6 /lib/x86_64-linux-gnu/libm.so.6 /lib/x86_64-linux-gnu/
COPY --from=build /out/bmsync /bmsync

# state.json / conflicts.json / history/ 都落在这里
VOLUME ["/data"]
EXPOSE 8080

ENV BMSYNC_ADDR=":8080" \
    BMSYNC_DATA="/data"

ENTRYPOINT ["/bmsync"]
