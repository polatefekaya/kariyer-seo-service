# One image for every role. Unlike the freshness service there is no second, heavier variant:
# no role here carries a browser or any other outsized dependency, so a single image keeps the
# builder and the reactor byte-identical and makes SERVICE_ROLE the only difference between
# two deployments.

ARG DOTNET_VERSION=10.0

# ── Build ─────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
ARG GITHUB_USER
ARG GITHUB_TOKEN

WORKDIR /src

# GitHub Packages hosts Kariyer.Messaging.Contracts. Passed as build args (not BuildKit
# secrets) because podman-compose cannot translate an environment-sourced compose secret
# into a podman build --secret flag. The ARGs above are visible to the RUN below as
# environment variables, which is what resolves nuget.config's %GITHUB_USER%/%GITHUB_TOKEN%.
COPY nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/Kariyer.Seo.Domain/*.csproj ./src/Kariyer.Seo.Domain/
COPY src/Kariyer.Seo.Worker/*.csproj ./src/Kariyer.Seo.Worker/

# Restore before copying sources so a code-only change reuses the package layer.
RUN dotnet restore src/Kariyer.Seo.Worker/Kariyer.Seo.Worker.csproj \
        -a "${TARGETARCH:-amd64}"

COPY src/ ./src/

# Framework-dependent, explicitly. Passing -a makes `dotnet publish` default to
# self-contained, which would ship an entire copy of the runtime INSIDE an image that
# already is the runtime — roughly doubling it for nothing.
RUN dotnet publish src/Kariyer.Seo.Worker/Kariyer.Seo.Worker.csproj \
    -c "${BUILD_CONFIGURATION}" \
    -a "${TARGETARCH:-amd64}" \
    --no-restore \
    --self-contained false \
    -o /app

# ── Runtime ───────────────────────────────────────────────────────────────────
# aspnet rather than runtime: the service serves /health, /health/ready, /metrics and the
# diagnostics routes, and those endpoints are how Kubernetes and Prometheus see it at all.
# It does NOT serve the sitemaps — Cloudflare does, straight from R2 (PLAN §1).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS runtime

# Non-root. This process holds write credentials for the bucket Google reads as our
# statement about our own site, so the blast radius of any bug should stop well short of the
# container filesystem.
USER $APP_UID
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=1 \
    # Invariant globalization is set in the project; stated here too so it is visible to
    # anyone reading the image rather than buried in a csproj. Turkish case folding does not
    # depend on it — see Domain/Indexation/TurkishFold, which maps the letters explicitly.
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

EXPOSE 8080

COPY --from=build /app .

# No default SERVICE_ROLE, deliberately. PLAN §5 names 'all' as the launch configuration,
# but defaulting to it here would mean a deployment that forgot the variable silently gets a
# SECOND full rebuilder — and two replicas staging sitemap.xml concurrently can swap in each
# other's half-finished index, which no log or metric would ever show. The role has to be
# stated where the replica count is stated.
#   docker run -e SERVICE_ROLE=all ...

# Liveness only. Readiness needs Postgres and RabbitMQ and belongs to the orchestrator,
# which can take a pod out of rotation without killing it — a container runtime that fails a
# health check RESTARTS the container, so wiring readiness here would turn a brief database
# blip into a restart storm.
#
# Re-enters the same binary rather than shelling out to curl, which these images do not carry.
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
    CMD ["dotnet", "/app/Kariyer.Seo.Worker.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "/app/Kariyer.Seo.Worker.dll"]
