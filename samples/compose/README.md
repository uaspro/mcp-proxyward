# ProxyWard Docker Compose Sample

Start the local stack:

```powershell
docker compose up --build
```

Check ProxyWard health:

```powershell
curl.exe http://localhost:8080/health
```

Check the management API and dashboard:

```powershell
curl.exe http://localhost:8081/api/status
curl.exe http://localhost:8082/
```

Send a sample MCP `tools/call` through ProxyWard:

```powershell
curl.exe -X POST http://localhost:8080/sample/mcp `
  -H "Content-Type: application/json" `
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hello from compose"}}}'
```

The sample stack runs `proxyward`, `management-api`, `dashboard`, `sample-mcp`, and `otel-collector`. Published ports bind to `127.0.0.1`, and the local compose admin token is wired between the dashboard, management API, and proxy runtime-control API. Audit writes and schema-lock history are stored in the `proxyward-data` Docker volume at `/app/data/proxyward.db`, which is mounted into both the proxy and management API containers.
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
