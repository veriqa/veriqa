# Veriqa HTTP channel conformance suite — multi-stage Dockerfile
# Built from the repository root, like the image of Veriqa:
#        docker build -f src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Dockerfile -t veriqa-http-channel-conformance .
#        docker run --rm -e Conformance__ChannelType=acme-chat ... veriqa-http-channel-conformance
# The exit code of the container is the exit code of the suite: 0 — no scenario failed, 1 — at least
# one failed, 2 — the run could not be carried out. The wire schema is at /schema/http-channel-wire-v1.schema.json.

# ── Stage 1: Restore inputs ────────────────────────────────────────────────────
# The restore layer needs every MSBuild input of the project graph; the set is derived from the build
# context, as in build/docker/Dockerfile: copy it whole, then keep only the restore inputs.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore-inputs

WORKDIR /src

COPY . .

RUN find . -type f \
        ! -name "*.csproj" \
        ! -name "*.props" \
        ! -name "*.targets" \
        ! -name "BannedSymbols.txt" \
        -delete \
    && find . -type d -empty -delete

# ── Stage 2: Build ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY --from=restore-inputs /src .

# Runtime identifier of the publish, derived from the target architecture of the build (see
# build/docker/Dockerfile).
ARG TARGETARCH
RUN case "$TARGETARCH" in \
        amd64|"") echo "linux-x64" ;; \
        arm64) echo "linux-arm64" ;; \
        *) echo "Unsupported TARGETARCH '$TARGETARCH': expected amd64 or arm64." >&2; exit 1 ;; \
    esac > /tmp/rid

RUN dotnet restore "src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Veriqa.Core.ChannelAdapter.Http.Conformance.csproj" \
    -r "$(cat /tmp/rid)"

COPY . .

# PathMap keeps the source layout of the build machine out of the stack traces the image prints (see
# build/docker/Dockerfile). The value must contain no comma.
RUN dotnet publish "src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Veriqa.Core.ChannelAdapter.Http.Conformance.csproj" \
    --configuration Release \
    --no-restore \
    -r "$(cat /tmp/rid)" \
    --self-contained false \
    --output /app/publish \
    /p:UseAppHost=false \
    /p:PathMap=/src/=/_/

# ── Stage 3: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

LABEL org.opencontainers.image.title="Veriqa HTTP channel conformance suite"
LABEL org.opencontainers.image.description="Checks an external channel adapter of Veriqa against the HTTP channel wire contract"
LABEL org.opencontainers.image.vendor="Veriqa"
LABEL org.opencontainers.image.licenses="MPL-2.0"
LABEL org.opencontainers.image.url="https://veriqa.app"
LABEL org.opencontainers.image.source="https://gitlab.com/veriqa/veriqa"

WORKDIR /app

# The receiver of events listens on the port of the base image (ASPNETCORE_HTTP_PORTS=8080) in the
# interactive and automatic modes; ASPNETCORE_URLS or ASPNETCORE_HTTP_PORTS move it.
EXPOSE 8080

# Non-root user, as in build/docker/Dockerfile.
ARG APP_UID=1654
USER $APP_UID

COPY --from=build /app/publish .

# The schema of the wire, for the authors of adapters; the suite itself reads the copy compiled into it.
COPY src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Schema/http-channel-wire-v1.schema.json /schema/

# The license texts and the third-party notices travel with the binaries, as in build/docker/Dockerfile.
COPY LICENSE licenses/LICENSE-MIT licenses/THIRD-PARTY-NOTICES.md ./

ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "Veriqa.Core.ChannelAdapter.Http.Conformance.dll"]
