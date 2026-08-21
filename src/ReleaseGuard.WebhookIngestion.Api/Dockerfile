FROM mcr.microsoft.com/dotnet/sdk:8.0.416-bookworm-slim AS build
WORKDIR /source

COPY Directory.Build.props global.json ReleaseGuard.sln ./
COPY src/ReleaseGuard.WebhookIngestion.Api/ReleaseGuard.WebhookIngestion.Api.csproj src/ReleaseGuard.WebhookIngestion.Api/
COPY tests/ReleaseGuard.WebhookIngestion.Api.Tests/ReleaseGuard.WebhookIngestion.Api.Tests.csproj tests/ReleaseGuard.WebhookIngestion.Api.Tests/
RUN dotnet restore src/ReleaseGuard.WebhookIngestion.Api/ReleaseGuard.WebhookIngestion.Api.csproj --disable-parallel

COPY src/ReleaseGuard.WebhookIngestion.Api/ src/ReleaseGuard.WebhookIngestion.Api/
RUN dotnet publish src/ReleaseGuard.WebhookIngestion.Api/ReleaseGuard.WebhookIngestion.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0.22-bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "ReleaseGuard.WebhookIngestion.Api.dll"]
