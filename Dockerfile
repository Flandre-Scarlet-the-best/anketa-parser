# syntax=docker/dockerfile:1

# Этап SDK восстанавливает зависимости в locked mode по графу из репозитория.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.402-noble
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.14-noble

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# Проектные файлы и lock-файлы копируются первыми для кеширования восстановления.
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

# Npgsql по умолчанию согласует шифрование GSSAPI. Устанавливаем необходимую библиотеку,
# отсутствующую в сокращённом runtime-образе.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build --chown=$APP_UID:$APP_UID /out/host ./host
COPY --from=build --chown=$APP_UID:$APP_UID /out/cli ./cli

# Проверка готовности выполняется внутри контейнера без публикации портов и API.
# Код 4 отмечает контейнер как unhealthy.
HEALTHCHECK --interval=30s --timeout=15s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/cli/AnketaParser.Cli.dll", "doctor", "--json"]

USER $APP_UID
ENTRYPOINT ["dotnet", "/app/host/AnketaParser.Host.dll"]
