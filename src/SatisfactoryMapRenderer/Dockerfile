# syntax=docker/dockerfile:1

# The publish output is architecture-independent (it carries SkiaSharp's native libraries for every
# Linux architecture under runtimes/), so the build stage runs natively on the build machine. Only
# the final stage is per-architecture, which keeps multi-arch builds fast.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/SatisfactoryMapRenderer/SatisfactoryMapRenderer.csproj src/SatisfactoryMapRenderer/
RUN dotnet restore src/SatisfactoryMapRenderer/SatisfactoryMapRenderer.csproj

COPY src/SatisfactoryMapRenderer/ src/SatisfactoryMapRenderer/
# The changelog is built into the app: it shows what changed after an update.
COPY CHANGELOG.md ./
# The native libraries for Windows, macOS, Android and musl are never used in this image and
# make up most of the publish output.
RUN dotnet publish src/SatisfactoryMapRenderer/SatisfactoryMapRenderer.csproj -c Release -o /app/publish --no-restore \
 && find /app/publish/runtimes -mindepth 1 -maxdepth 1 ! -name linux-x64 ! -name linux-arm64 ! -name linux ! -name unix -exec rm -rf {} +

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# The net.unraid.docker.* labels give the container its icon and "WebUI" link on Unraid's Docker
# page even when it isn't installed from the Community Applications template (e.g. via Compose).
LABEL org.opencontainers.image.title="Satisfactory Map Renderer" \
      org.opencontainers.image.description="Interactive map of your Satisfactory save game, straight from a file or a running dedicated server" \
      org.opencontainers.image.source="https://github.com/Tunefish92/SatisfactoryMapRenderer" \
      org.opencontainers.image.licenses="MIT" \
      net.unraid.docker.icon="https://raw.githubusercontent.com/Tunefish92/SatisfactoryMapRenderer/main/unraid/satisfactory-map-renderer.png" \
      net.unraid.docker.webui="http://[IP]:[PORT:8080]/"

# - curl: container health check
# - openssh-client: the "SSH" save game source
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl openssh-client \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /data

COPY --from=build /app/publish .
# The game's icons, names, level data and in-game map, so the map works without a game
# installation. They are Coffee Stain Studios' assets, see gamedata/README.md.
COPY gamedata/ /app/gamedata/
COPY src/SatisfactoryMapRenderer/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
# Strip CR in case the script was checked out with Windows line endings. The `map-renderer` command
# runs the renderer's own commands, e.g. `docker exec satisfactory-map map-renderer --help`.
RUN sed -i 's/\r$//' /usr/local/bin/docker-entrypoint.sh \
 && chmod 755 /usr/local/bin/docker-entrypoint.sh \
 && printf '#!/bin/sh\nexec dotnet /app/SatisfactoryMapRenderer.dll "$@"\n' > /usr/local/bin/map-renderer \
 && chmod 755 /usr/local/bin/map-renderer

ENV SMR_DATA_PATH=/data \
    SMR_MAX_ZOOM=9 \
    PUID=1000 \
    PGID=1000

# Starts as root only long enough for docker-entrypoint.sh to give /data to PUID:PGID, then
# runs the app as that user.
VOLUME /data
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:8080/api/health || exit 1

ENTRYPOINT ["docker-entrypoint.sh"]
