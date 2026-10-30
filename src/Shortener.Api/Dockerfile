# Build context: repo root (docker compose -f deploy/docker-compose.yml build).
ARG DOTNET_SDK_VERSION=10.0.403
ARG DOTNET_RUNTIME_VERSION=10.0.14

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /src
# Restore first, from project files only, so the restore layer is cached until a csproj or props file changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Shortener.Shared/Shortener.Shared.csproj src/Shortener.Shared/
COPY src/Shortener.Api/Shortener.Api.csproj src/Shortener.Api/
RUN dotnet restore src/Shortener.Api/Shortener.Api.csproj
COPY src/Shortener.Shared/ src/Shortener.Shared/
COPY src/Shortener.Api/ src/Shortener.Api/
RUN dotnet publish src/Shortener.Api/Shortener.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION}
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app .
# Non-root user shipped with the .NET images (uid 1654).
USER app
ENTRYPOINT ["dotnet", "Shortener.Api.dll"]
