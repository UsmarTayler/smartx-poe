# syntax=docker/dockerfile:1
#
# The Smart-X client: the published Blazor WebAssembly files served by nginx. Build from the repository root:
#   docker build -f src/SmartX.Client/Dockerfile -t smartx-client .

# ---- Build ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/SmartX.Shared/SmartX.Shared.csproj src/SmartX.Shared/
COPY src/SmartX.Client/SmartX.Client.csproj src/SmartX.Client/
RUN dotnet restore src/SmartX.Client/SmartX.Client.csproj

COPY src/SmartX.Shared/ src/SmartX.Shared/
COPY src/SmartX.Client/ src/SmartX.Client/
RUN dotnet publish src/SmartX.Client/SmartX.Client.csproj \
    --configuration Release \
    --no-restore \
    --output /app

# Publishing pre-compresses static files, appsettings.json included. The container rewrites that file at
# start-up, so its compressed copies would be stale; they are removed here, where the build still has
# permission to delete them.
RUN rm -f /app/wwwroot/appsettings.json.br /app/wwwroot/appsettings.json.gz

# ---- Runtime -------------------------------------------------------------------------------------------
# A WebAssembly app is static files once published, so the image that serves it needs no .NET at all.
# The unprivileged nginx image runs as a non-root user and listens on 8080 rather than 80.
FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime

COPY src/SmartX.Client/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY --chmod=755 src/SmartX.Client/nginx/40-smartx-client-config.sh /docker-entrypoint.d/

# Owned by the nginx user, so the start-up script can rewrite appsettings.json from the environment.
COPY --from=build --chown=nginx:nginx /app/wwwroot /usr/share/nginx/html

ENV API_BASE_ADDRESS=/ \
    TRUST_POLL_INTERVAL_SECONDS=3

EXPOSE 8080
