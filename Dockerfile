# syntax=docker/dockerfile:1
#
# nocat.farm in Docker - headless: the log goes to `docker logs`, the dashboard is on port 7242.
#
#   docker compose up -d --build  (after copying docker-compose.example.yml to docker-compose.yml)
#
# A bare `docker build` needs Docker's buildx plugin (this file uses BuildKit's --platform): Ubuntu's own docker.io
# package doesn't bring it, and the old builder stops at "invalid OS component". Compose builds with BuildKit
# either way - or `sudo apt install docker-buildx` first.
#
# Framework-dependent, on Microsoft's ASP.NET Core runtime image, rather than self-contained on runtime-deps:
#   - nocat.farm IS an ASP.NET Core app, so that image carries exactly the runtime it needs, and Microsoft keeps
#     it patched: rebuilding picks up .NET security fixes without waiting for a nocat.farm release.
#   - The build output is plain IL, so one build serves every CPU. The same Dockerfile builds amd64 and arm64
#     (docker buildx build --platform linux/amd64,linux/arm64 .) without cross-compiling anything.
#   - The app's own layer is a few MB rather than ~100MB of bundled runtime, so an update pulls only that.
# The Linux release zips are the self-contained ones - they're for machines with no .NET at all.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore on its own layer, so it only runs again when the project file changes.
COPY src/NocatFarm/NocatFarm.csproj src/NocatFarm/
RUN dotnet restore src/NocatFarm/NocatFarm.csproj

COPY src/NocatFarm/ src/NocatFarm/
RUN dotnet publish src/NocatFarm/NocatFarm.csproj -c Release -o /out --no-restore \
        -p:UseAppHost=false -p:DebugType=none

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}

# The program in /app, owned by root and read-only to the app; everything it writes goes to /data.
# The data folders are open to any user id, so `user:` in docker-compose.yml can run it as the owner of your
# bind-mounted folders instead of the image's default user (uid 1654).
COPY --from=build /out/ /app/
RUN mkdir -p /data/config /data/logs /data/backups /data/plugins \
 && chmod 0777 /data /data/config /data/logs /data/backups /data/plugins

# The dashboard has to listen beyond the container to be reachable through a published port. It still lets
# nobody in without a password - set NOCATFARM_WEB_PASSWORD (docker-compose.example.yml refuses to start without).
# The base image's own ASPNETCORE_HTTP_PORTS=8080 is cleared: the dashboard picks its port itself (WebPort, 7242).
ENV NOCATFARM_WEB_HOST=0.0.0.0 \
    ASPNETCORE_HTTP_PORTS=

EXPOSE 7242
# backups/ too: the 'backup' command and every restore's "before" copy land there, and kept only inside the container
# they were gone with the next rebuild or re-create.
VOLUME ["/data/config", "/data/logs", "/data/backups"]

USER $APP_UID
WORKDIR /data

# Healthy while the dashboard answers. The image has no curl or wget, so the app asks itself (--ping: reads the port
# from the settings, asks /api/ping, starts nothing). A dashboard that's switched off counts as healthy.
HEALTHCHECK --interval=1m --timeout=15s --start-period=1m --retries=3 \
    CMD ["dotnet", "/app/nocatFarm.dll", "--path", "/data", "--ping"]

# SIGTERM (docker stop) signs every account out cleanly before exiting - give it time: stop_grace_period in compose.
ENTRYPOINT ["dotnet", "/app/nocatFarm.dll", "--path", "/data"]
