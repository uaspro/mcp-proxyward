# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY samples/SampleMcpServer/SampleMcpServer.csproj samples/SampleMcpServer/
RUN dotnet restore samples/SampleMcpServer/SampleMcpServer.csproj

COPY samples/SampleMcpServer/ samples/SampleMcpServer/
RUN dotnet publish samples/SampleMcpServer/SampleMcpServer.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "SampleMcpServer.dll"]
