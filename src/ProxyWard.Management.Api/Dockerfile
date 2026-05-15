# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ProxyWard.Management.Api/ProxyWard.Management.Api.csproj src/ProxyWard.Management.Api/
COPY src/ProxyWard.Audit/ProxyWard.Audit.csproj src/ProxyWard.Audit/
COPY src/ProxyWard.Core/ProxyWard.Core.csproj src/ProxyWard.Core/
COPY src/ProxyWard.Locking/ProxyWard.Locking.csproj src/ProxyWard.Locking/
COPY src/ProxyWard.Management.Application/ProxyWard.Management.Application.csproj src/ProxyWard.Management.Application/
COPY src/ProxyWard.Management.Infrastructure/ProxyWard.Management.Infrastructure.csproj src/ProxyWard.Management.Infrastructure/
COPY src/ProxyWard.Policy/ProxyWard.Policy.csproj src/ProxyWard.Policy/
RUN dotnet restore src/ProxyWard.Management.Api/ProxyWard.Management.Api.csproj

COPY src/ src/
RUN dotnet publish src/ProxyWard.Management.Api/ProxyWard.Management.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8081 \
    PROXYWARD_MANAGEMENT_AUDIT_DB_PATH=/app/data/proxyward.db

RUN mkdir -p /app/config /app/data

COPY --from=build /app/publish .

EXPOSE 8081
ENTRYPOINT ["dotnet", "ProxyWard.Management.Api.dll"]
