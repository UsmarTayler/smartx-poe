# syntax=docker/dockerfile:1
#
# The Smart-X device simulator. Build from the repository root:
#   docker build -f src/SmartX.DeviceSimulator/Dockerfile -t smartx-simulator .

# ---- Build ---------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/SmartX.Shared/SmartX.Shared.csproj src/SmartX.Shared/
COPY src/SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj src/SmartX.DeviceSimulator/
RUN dotnet restore src/SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj

# Only the shared contracts and the simulator's own source. The API is deliberately not copied into this
# build: the simulator stands in for hardware, and hardware has no access to the gateway's code.
COPY src/SmartX.Shared/ src/SmartX.Shared/
COPY src/SmartX.DeviceSimulator/ src/SmartX.DeviceSimulator/
RUN dotnet publish src/SmartX.DeviceSimulator/SmartX.DeviceSimulator.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    -p:UseAppHost=false

# ---- Runtime -------------------------------------------------------------------------------------------
# The plain runtime image: this is a console worker, not a web server, so it does not need ASP.NET Core.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

COPY --from=build /app .

# Configuration comes from the environment; see docker-compose.yml. The defaults here match the compose
# network, where the gateway is the service called "api".
ENV Simulator__ApiBaseUrl=http://api:8080

# APP_UID is the unprivileged "app" user the .NET images ship with. Nothing here needs root.
USER $APP_UID

ENTRYPOINT ["dotnet", "SmartX.DeviceSimulator.dll"]
