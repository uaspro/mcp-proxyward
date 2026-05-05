# ProxyWard Docker Compose Sample

Start the local stack:

```powershell
docker compose up --build
```

Check ProxyWard health:

```powershell
curl.exe http://localhost:8080/health
```

Send a sample MCP `tools/call` through ProxyWard:

```powershell
curl.exe -X POST http://localhost:8080/sample/mcp `
  -H "Content-Type: application/json" `
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hello from compose"}}}'
```

The sample stack queues audit writes and stores rows in the `proxyward-data` Docker volume at `/app/data/proxyward.db`.
Confirm the mounted data volume is present:

```powershell
docker compose exec proxyward ls -l /app/data
```

Telemetry is exported over OTLP to the included collector; inspect it with:

```powershell
docker compose logs otel-collector
```

Stop the stack:

```powershell
docker compose down
```

Remove the local data volume when you want a clean audit database and schema-lock history:

```powershell
docker compose down -v
```
