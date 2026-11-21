# syntax=docker/dockerfile:1

# Этап SDK восстанавливает зависимости в locked mode по графу из репозитория.
# Образ обязан содержать SDK, который допускает global.json: восстановление внутри
# контейнера завершается ошибкой, если запрошенная версия SDK в образе отсутствует.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.404-noble
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.15-noble

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
# Версия берётся из репозитория выбранного базового образа; пересмотреть при
# появлении поддерживаемого снимка APT с воспроизводимыми обновлениями безопасности.
# Зеркало Canonical обслуживает те же подписанные наборы пакетов по HTTPS.
# Меняем адреса основного архива и архива безопасности Ubuntu, сохраняя suites и ключи.
# hadolint ignore=DL3008
RUN sed -i -E 's,^URIs: https?://(archive|security)[.]ubuntu[.]com/ubuntu/?$,URIs: https://azure.archive.ubuntu.com/ubuntu/,' /etc/apt/sources.list.d/ubuntu.sources \
 && apt-get -o APT::Update::Error-Mode=any update \
 && apt-get install -y --no-install-recommends apt libapt-pkg6.0t64 libgssapi-krb5-2 \
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

# UID задаётся официальным runtime-образом; пересмотреть при смене базового образа.
# hadolint ignore=DL3066
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/host/AnketaParser.Host.dll"]

FROM runtime AS llm-worker
USER 0
# CPU-бэкенд llama.cpp в NuGet использует OpenMP. Пакет берётся из репозитория базового образа.
# hadolint ignore=DL3008
RUN apt-get -o APT::Update::Error-Mode=any update \
 && apt-get install -y --no-install-recommends libgomp1 \
 && rm -rf /var/lib/apt/lists/*
HEALTHCHECK NONE
# hadolint ignore=DL3066
USER $APP_UID
ENTRYPOINT ["dotnet", "/app/cli/AnketaParser.Cli.dll", "llm"]
CMD ["worker"]

# Официальный PostgreSQL содержит gosu со старым Go. Собираем закреплённый исходник
# исправленным компилятором; пересмотреть после обновления gosu в базовом образе.
FROM golang:1.27.4-alpine3.24@sha256:70bd7ddea8b6cbe5d6943cde279d6b67feb10cc924fb9100d1d8757c1575b280 AS gosu-build
RUN CGO_ENABLED=0 GOTOOLCHAIN=local GOBIN=/out GOFLAGS=-trimpath \
    go install github.com/tianon/gosu@29b939aa5bd4dc588af765b8248f6e39150d2565

# Общая база сохраняет штатный entrypoint PostgreSQL и обновляет пакеты Alpine.
FROM postgres:18.7-alpine AS postgres
COPY --from=gosu-build /out/gosu /usr/local/bin/gosu
ENV GOSU_VERSION=1.20
# Обновление установленных пакетов необходимо для исправлений безопасности Alpine;
# пересмотреть исключение, когда базовый образ будет содержать актуальные исправления.
# hadolint ignore=DL3017
RUN apk upgrade --no-cache \
 && gosu --version \
 && gosu nobody true

# Обслуживание использует штатные утилиты PostgreSQL 18 и тот же собранный CLI.
FROM mcr.microsoft.com/dotnet/runtime:10.0.15-alpine3.24 AS maintenance-runtime
FROM postgres AS maintenance
# Версии APK определяет репозиторий Alpine базового образа; пересмотреть при
# появлении поддерживаемого снимка пакетов с обновлениями безопасности.
# hadolint ignore=DL3018
RUN apk add --no-cache libstdc++ icu-libs krb5-libs \
 && mkdir /backups && chown 1654:1654 /backups
COPY --from=maintenance-runtime /usr/share/dotnet /usr/share/dotnet
COPY --from=build /out/cli /app/cli
ENV PATH="/usr/share/dotnet:${PATH}" DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
USER 1654:1654
ENTRYPOINT ["dotnet", "/app/cli/AnketaParser.Cli.dll"]
