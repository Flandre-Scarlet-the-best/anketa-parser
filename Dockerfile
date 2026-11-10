# syntax=docker/dockerfile:1

# Multi-stage build. The SDK stage restores in locked mode so the container build uses exactly
# the dependency graph that is committed to the repository.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-noble
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.14-noble

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Project files and lock files are copied first so that the restore layer is cached.
COPY global.json Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/AnketaParser.Core/AnketaParser.Core.csproj src/AnketaParser.Core/packages.lock.json src/AnketaParser.Core/
COPY src/AnketaParser.Infrastructure/AnketaParser.Infrastructure.csproj src/AnketaParser.Infrastructure/packages.lock.json src/AnketaParser.Infrastructure/
COPY src/AnketaParser.Host/AnketaParser.Host.csproj src/AnketaParser.Host/packages.lock.json src/AnketaParser.Host/
COPY src/AnketaParser.Cli/AnketaParser.Cli.csproj src/AnketaParser.Cli/packages.lock.json src/AnketaParser.Cli/

RUN dotnet restore src/AnketaParser.Host/AnketaParser.Host.csproj --locked-mode \
 && dotnet restore src/AnketaParser.Cli/AnketaParser.Cli.csproj --locked-mode

COPY src/ ./src/

RUN dotnet publish src/AnketaParser.Host/AnketaParser.Host.csproj -c Release --no-restore -o /out/host /p:UseAppHost=false \
 && dotnet publish src/AnketaParser.Cli/AnketaParser.Cli.csproj -c Release --no-restore -o /out/cli /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
WORKDIR /app

# Npgsql negotiates GSSAPI encryption by default; the library it probes is not part of the slim
# runtime image. Installing it keeps PostgreSQL connectivity quiet and complete.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build --chown=$APP_UID:$APP_UID /out/host ./host
COPY --from=build --chown=$APP_UID:$APP_UID /out/cli ./cli

# Readiness check runs inside the container only: no port is published and no administrative
# endpoint is exposed. Exit code 4 makes Docker report the container as unhealthy.
HEALTHCHECK --interval=30s --timeout=15s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/cli/AnketaParser.Cli.dll", "doctor", "--json"]

USER $APP_UID
ENTRYPOINT ["dotnet", "/app/host/AnketaParser.Host.dll"]
