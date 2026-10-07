# Veriqa Core Auth Server — multi-stage Dockerfile
# Target platforms: linux/amd64 (default) and linux/arm64
# Usage (from the repository root, which is the build context):
#        docker build -f build/docker/Dockerfile -t veriqa-authserver .
#        docker run -p 8080:8080 veriqa-authserver
# Release builds also pass the product version for the version label (see the final stage):
#        docker build --build-arg VERIQA_VERSION="$(sed -n 's:.*<VeriqaCoreVersion>\(.*\)</VeriqaCoreVersion>.*:\1:p' build/Versions.props)" -f build/docker/Dockerfile -t veriqa-authserver .

# ── Stage 1: Restore inputs ────────────────────────────────────────────────────
# The restore layer needs every MSBuild input of the host graph: the project file of each
# transitively referenced project plus the build-wide props/targets. Enumerating them by hand
# rotted repeatedly — a project was added and this list was not updated — so the set is derived
# from the build context instead: copy it whole, then keep only the restore inputs. The build
# stage below copies just that pruned tree, so its restore layer is reused as long as no project
# or props/targets file changes: editing sources does not invalidate it.
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

# Runtime identifier of the publish, derived from the target architecture of the build (set by
# BuildKit; empty under the legacy builder). A RID-specific publish copies only the native assets of
# that platform: a portable one would also carry runtimes/win-* libraries (for example the SQL Server
# SNI) into a Linux image. The RID is written to a file so restore and publish use the same value.
ARG TARGETARCH
RUN case "$TARGETARCH" in \
        amd64|"") echo "linux-x64" ;; \
        arm64) echo "linux-arm64" ;; \
        *) echo "Unsupported TARGETARCH '$TARGETARCH': expected amd64 or arm64." >&2; exit 1 ;; \
    esac > /tmp/rid

RUN dotnet restore "src/core/Veriqa.Core.AuthServer.Host/Veriqa.Core.AuthServer.Host.csproj" \
    -r "$(cat /tmp/rid)"

# Copy the sources and publish
COPY . .

# PathMap rewrites the build context root (/src/) to /_/ in every compiled path the image carries:
# the file:line frames of stack traces, embedded and portable PDBs, CallerFilePath. Without it an
# operator reading the container log sees the absolute source layout of the build machine. The
# mapping is spelled out rather than obtained through ContinuousIntegrationBuild: that switch derives
# PathMap from the git SourceRoot, and Dockerfile.dockerignore keeps .git out of this context, so there is no
# repository to locate and the deterministic-path targets fail with "SourceRoot items must include at
# least one top-level item". /_/ is the same prefix a git-based deterministic build produces. The
# value must contain no comma: on the command line a comma separates properties.
RUN dotnet publish "src/core/Veriqa.Core.AuthServer.Host/Veriqa.Core.AuthServer.Host.csproj" \
    --configuration Release \
    --no-restore \
    -r "$(cat /tmp/rid)" \
    --self-contained false \
    --output /app/publish \
    /p:UseAppHost=false \
    /p:PathMap=/src/=/_/

# ── Stage 3: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Image metadata
LABEL org.opencontainers.image.title="Veriqa Core Auth Server"
LABEL org.opencontainers.image.description="Veriqa OpenIddict OIDC Auth Server — cross-device authentication via messenger channels"
LABEL org.opencontainers.image.vendor="Veriqa"
LABEL org.opencontainers.image.licenses="MPL-2.0"
# "URL to find more information on the image" — the product site, which is also where the
# source-code page and the mirrors are linked from.
LABEL org.opencontainers.image.url="https://veriqa.app"
# "URL to get source code for building the image" — the public repository. MPL-2.0 §3.2 requires
# a distribution in Executable Form to tell the recipient where the Source Code Form is; the label
# is that notice in the form an image scanner reads. The same tree is mirrored to other forges,
# the product site lists them all.
LABEL org.opencontainers.image.source="https://gitlab.com/veriqa/veriqa"
# The version of the product, not of the base image: the aspnet base image is Ubuntu and declares
# org.opencontainers.image.version=24.04, and every label not set here is inherited from it. The value
# is build/Versions.props VeriqaCoreVersion, passed as a build argument (usage at the top); a build
# without the argument gets an empty label rather than the version of the base image.
ARG VERIQA_VERSION=""
LABEL org.opencontainers.image.version="${VERIQA_VERSION}"

WORKDIR /app

# Expose the HTTP port (HTTPS is terminated at the reverse proxy)
EXPOSE 8080

# curl is needed for HEALTHCHECK and is not part of the base aspnet image
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Non-root user for security (running as root is not allowed).
# APP_UID=1654 is the standard UID of the "app" user in mcr.microsoft.com/dotnet/aspnet 8+.
# Declared explicitly so the build does not depend on the base image ENV.
ARG APP_UID=1654
USER $APP_UID

# Copy the build artifacts
COPY --from=build /app/publish .

# The license texts and the third-party notices travel with the binaries: MPL-2.0 §3.2 for the
# product itself, and the notice-retention clauses of the incorporated packages (Apache-2.0 §4,
# MIT) for its dependencies. Same set as the OS service archive.
COPY LICENSE licenses/LICENSE-MIT licenses/THIRD-PARTY-NOTICES.md ./

# Health check — probe the liveness endpoint
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

# Integrator settings: the host reads an optional /app/config/veriqa.json (override the location with
# VERIQA_SETTINGS_FILE; a file named by the variable is required, and without it the host does not start)
# over the appsettings files of the image and under the environment. Mount the integrator's file there;
# appsettings.Production.json carries the production defaults and stays as is.

# Default environment
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "Veriqa.Core.AuthServer.Host.dll"]
