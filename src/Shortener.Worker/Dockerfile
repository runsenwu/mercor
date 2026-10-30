# Build context: repo root (docker compose -f deploy/docker-compose.yml build).
ARG DOTNET_SDK_VERSION=10.0.403
ARG DOTNET_RUNTIME_VERSION=10.0.14

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /src
# Restore first, from project files only, so the restore layer is cached until a csproj or props file changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Shortener.Shared/Shortener.Shared.csproj src/Shortener.Shared/
COPY src/Shortener.Worker/Shortener.Worker.csproj src/Shortener.Worker/
RUN dotnet restore src/Shortener.Worker/Shortener.Worker.csproj
COPY src/Shortener.Shared/ src/Shortener.Shared/
COPY src/Shortener.Worker/ src/Shortener.Worker/
RUN dotnet publish src/Shortener.Worker/Shortener.Worker.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_RUNTIME_VERSION}
WORKDIR /app

COPY --from=build /app .
# Non-root user shipped with the .NET images (uid 1654).
USER app
ENTRYPOINT ["dotnet", "Shortener.Worker.dll"]
