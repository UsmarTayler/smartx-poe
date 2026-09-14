# syntax=docker/dockerfile:1
#
# The Smart-X API. Build from the repository root:
#   docker build -f src/SmartX.Api/Dockerfile -t smartx-api .

# ---- Build ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Project files first, so the restore layer is reused until a package reference changes rather than on
# every source edit.
COPY src/SmartX.Shared/SmartX.Shared.csproj src/SmartX.Shared/
COPY src/SmartX.Api/SmartX.Api.csproj src/SmartX.Api/
RUN dotnet restore src/SmartX.Api/SmartX.Api.csproj

COPY src/SmartX.Shared/ src/SmartX.Shared/
COPY src/SmartX.Api/ src/SmartX.Api/
RUN dotnet publish src/SmartX.Api/SmartX.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    -p:UseAppHost=false

# ---- Runtime -------------------------------------------------------------------------------------------
# The ASP.NET Core runtime only: no SDK, no compiler, no source in the image that runs.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Attachments are written to /data/attachments, where docker-compose mounts a volume. The directory is
# created here and given to the non-root user, because a new named volume copies the ownership of the
# directory it is mounted over; without this the API could not write to its own volume.
RUN mkdir -p /data/attachments && chown -R "$APP_UID" /data

COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Attachments__StoragePath=/data/attachments

# APP_UID is the unprivileged "app" user the .NET images ship with. Nothing in the API needs root.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
