# syntax=docker/dockerfile:1
#
# Multi-stage build: compile (with tests) -> minimal runtime
#
# The final image is based on scratch, ~20-30 MB (the Go build was 8-12 MB).
# Note scratch has no shell, so healthcheck can only exec-call the binary's
# own subcommand (see docker-compose.yml).
#
# The build context is the repo root (see build.context in
# deploy/docker-compose.yml), so all paths are repo-root relative. The vector
# file test/hlc_vectors.json must be copied in separately: the Domain tests
# locate test/hlc_vectors.json by walking up from the binary dir
# (HlcTests.VectorsPath), so keeping the /src layout identical to the repo is
# what lets them find it.

# ── Build stage ───────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

# NativeAOT needs a local C linker for publish, and the SDK image lacks one.
# First as its own layer: independent of sources, never reinstalled on code edits.
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists

WORKDIR /src

# Dependency manifests first, so the NuGet layer cache survives source edits.
# .csproj changes also re-fetch, hence the same group.
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

# Test first so a broken image is never pushed.
#
# Deliberately **after** tests comes publish: reversed, broken code would
# first spend minutes on NativeAOT compilation and only then fail the tests —
# and NativeAOT is the slowest step here, so reversed order wastes the wait
# on every broken edit.
RUN dotnet test -c Release --nologo

# NativeAOT: the artifact no longer needs .so / libicu / libssl — the only way
# FROM scratch works.
#
# -p:PublishAot=true is passed here (never hardcoded in csproj): it needs a
# C++ linker and **cannot cross-compile** — publish -r linux-x64 + AOT on
# Windows fails with "Cross-OS native compilation is not supported". So AOT
# only happens in this Linux container; `dotnet test` on dev machines is unaffected.
#
# AssemblyName is bmsync (see Cli.csproj); healthcheck relies on that name.
RUN dotnet publish src/BookmarkSync.Cli \
        -c Release \
        -r linux-x64 \
        -p:PublishAot=true \
        -p:InvariantGlobalization=true \
        --output /out

# ── Runtime stage ─────────────────────────────────────────────────────
# Keep FROM scratch (~20MB class), but the NativeAOT artifact is not fully
# static: it still links glibc dynamically (ld-linux + libc.so.6 +
# libm.so.6), and scratch ships none of them — without copies the container
# fails with "exec /bmsync: no such file or directory".
# So copy those three files with the binary. Paths are Debian (noble)'s fixed
# layout from the same image as the build stage; a base-image upgrade that
# moves them fails the build instead of silently shipping a dead image.
FROM scratch

COPY --from=build /lib64/ld-linux-x86-64.so.2 /lib64/ld-linux-x86-64.so.2
COPY --from=build /lib/x86_64-linux-gnu/libc.so.6 /lib/x86_64-linux-gnu/libm.so.6 /lib/x86_64-linux-gnu/
COPY --from=build /out/bmsync /bmsync

# state.json / conflicts.json / history/ all land here
VOLUME ["/data"]
EXPOSE 8080

ENV BMSYNC_ADDR=":8080" \
    BMSYNC_DATA="/data"

ENTRYPOINT ["/bmsync"]
