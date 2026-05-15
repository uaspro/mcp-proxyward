# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ProxyWard.Api/ProxyWard.Api.csproj src/ProxyWard.Api/
COPY src/ProxyWard.Audit/ProxyWard.Audit.csproj src/ProxyWard.Audit/
COPY src/ProxyWard.Core/ProxyWard.Core.csproj src/ProxyWard.Core/
COPY src/ProxyWard.Locking/ProxyWard.Locking.csproj src/ProxyWard.Locking/
COPY src/ProxyWard.Policy/ProxyWard.Policy.csproj src/ProxyWard.Policy/
RUN dotnet restore src/ProxyWard.Api/ProxyWard.Api.csproj

COPY src/ src/
RUN dotnet publish src/ProxyWard.Api/ProxyWard.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    PROXYWARD_PERSISTENCE_PROVIDER=sqlite \
    PROXYWARD_DB_PATH=/app/data/proxyward.db

RUN mkdir -p /app/config /app/data

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "ProxyWard.Api.dll"]
