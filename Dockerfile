# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/ProxyWard.Api/ProxyWard.Api.csproj src/ProxyWard.Api/
COPY src/ProxyWard.SharedKernel/ProxyWard.SharedKernel.csproj src/ProxyWard.SharedKernel/
COPY src/ProxyWard.Proxy.Domain/ProxyWard.Proxy.Domain.csproj src/ProxyWard.Proxy.Domain/
COPY src/ProxyWard.Proxy.Application/ProxyWard.Proxy.Application.csproj src/ProxyWard.Proxy.Application/
COPY src/ProxyWard.Proxy.Infrastructure/ProxyWard.Proxy.Infrastructure.csproj src/ProxyWard.Proxy.Infrastructure/
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
