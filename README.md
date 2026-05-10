# MCP ProxyWard

**A lightweight, self-hosted guard proxy for MCP Streamable HTTP servers.**

ProxyWard sits between MCP clients (Claude Desktop, Cursor, agent frameworks, etc.) and one or more upstream MCP servers, transparently proxies traffic through YARP, inspects the MCP JSON-RPC messages on the wire, and enforces YAML-defined security policy around server access, tool discovery, tool invocation, schema drift, dangerous arguments, and audit logging.

It is built for individual developers, small teams, and self-hosted environments that want **visibility and control** over MCP tool usage without modifying upstream MCP servers.

---

## The Problem

MCP gives clients the power to discover and call arbitrary tools exposed by upstream servers. That power has a few uncomfortable properties:

- **Tool surfaces drift silently.** A `tools/list` response can change a description or input schema between sessions, and the client (and the model behind it) has no built-in way to notice.
- **Tool calls can carry dangerous arguments.** A `tools/call` may try to read paths outside the workspace, hit private-network hosts, or run shell-like commands.
- **Clients see whatever the upstream advertises.** There is no native allow/deny layer for "this server is fine, but only these tools, and never with those arguments."
- **There is no audit trail by default.** When something does go wrong, you cannot easily answer "what tool did the agent actually call, with what arguments, and why was it allowed?"

Teams need a small deployable guard layer that can **observe, warn, and block** high-risk MCP behavior *before* tool calls reach the upstream — without rewriting any of the MCP servers they already use.

## The Solution

ProxyWard is a single ASP.NET Core service:

```text
                 ┌────────────────────────────────────────┐
   MCP client ──►│  MCP ProxyWard                         │──► Upstream MCP server
                 │  ─────────────────────────────────     │
                 │  YARP routing + clusters               │
                 │  Server allowlist                      │
                 │  JSON-RPC parser + method classifier   │
                 │  Tool allow/block rules                │
                 │  tools/list schema-lock drift checks   │
                 │  Path / host / command argument rules  │
                 │  Redacted SQLite audit log             │
                 │  OpenTelemetry logs / traces / metrics │
                 └────────────────────────────────────────┘
```

Highlights:

- **Audit mode and enforce mode** share the same decision engine, so you can roll out as `audit` first, watch the would-block events, then flip to `enforce` with confidence.
- **YAML policy** describes servers, routes, tool allow/block lists, and argument rules in a single reviewable file.
- **DB-backed tool schema lock** captures stable hashes of each tool's name, title, description, input schema, and output schema. Drift produces a deterministic warn-or-block decision and keeps a versioned history in SQLite.
- **Argument rules** cover path traversal / allowed roots, host allowlists, private-network targets, and dangerous shell-like commands.
- **Redacted audit events** are persisted to SQLite — sensitive argument values never leak into the audit DB, logs, traces, or metrics.
- **OpenTelemetry-compatible** logs, traces, and metrics, with optional OTLP and Azure Monitor Application Insights export.

Stack: .NET 10 · ASP.NET Core · YARP · YAML · SQLite · OpenTelemetry · Docker.

---

## Quick Start

The fastest way to see ProxyWard in action is the bundled Docker Compose stack, which boots ProxyWard, the management API, the dashboard, and an OpenTelemetry collector together.

### Prerequisites

- Docker Desktop (or any Docker engine) with Compose v2.
- Optional, for local builds: [.NET 10 SDK](https://dotnet.microsoft.com/download).

### 1. Clone the repository

```bash
git clone https://github.com/OWNER/mcp-proxyward.git
cd mcp-proxyward
```

### 2. Start the stack

```bash
docker compose up --build
```

This brings up four services:

| Service          | Purpose                                 | Port  |
| ---------------- | --------------------------------------- | ----- |
| `proxyward`      | The guard proxy itself                  | 8080  |
| `management-api` | Dashboard/stats/control-plane API       | 8081  |
| `dashboard`      | React operator dashboard                | 8082  |
| `otel-collector` | Receives OTLP logs / traces / metrics   | 4317 / 4318 |

The compose stack stores policy snapshots, audit data, and schema-lock history in the named `proxyward-data` volume at `/app/data/proxyward.db`. The proxy boots from that SQLite policy snapshot table and keeps the active policy cached in memory; the management API persists policy edits to the same DB and immediately pushes the accepted snapshot to the proxy runtime-control API. The stack binds published ports to `127.0.0.1`, uses a local compose admin token for management writes and proxy runtime control, and serves dashboard same-origin `/api/*` requests through its web server.

### 3. Verify the proxy is healthy

```bash
curl http://localhost:8080/health
```

You should see something like:

```json
{
  "status": "healthy",
  "service": "MCP ProxyWard",
  "mode": "audit",
  "policyVersion": "sha256:…",
  "serverCount": 0
}
```

Verify the management API and dashboard:

```bash
curl http://localhost:8081/api/status
curl http://localhost:8082/
```

### 4. Point your MCP client at ProxyWard

Add a server policy from the dashboard using the upstream MCP server URL. The dashboard generates the server id, proxy route, and an `mcp.json` snippet that points your MCP client at ProxyWard.

### Running without Docker

If you prefer to run the services directly, use three terminals. PowerShell uses `$env:NAME = "value"` instead of `export NAME=value`.

```bash
# 1. Restore and build once
dotnet build McpProxyWard.slnx

# 2. Terminal 1: proxy
export PROXYWARD_DB_PATH=./data/proxyward.db
export PROXYWARD_CONTROL_ENABLED=true
export PROXYWARD_ADMIN_TOKEN=local-dev-token
dotnet run --project src/ProxyWard.Api --urls http://localhost:8080

# 3. Terminal 2: management API
export PROXYWARD_MANAGEMENT_AUDIT_DB_PATH=./data/proxyward.db
export PROXYWARD_PROXY_CONTROL_URL=http://localhost:8080
export PROXYWARD_ADMIN_TOKEN=local-dev-token
export PROXYWARD_MANAGEMENT_CORS_ALLOWED_ORIGINS=http://localhost:5173
dotnet run --project src/ProxyWard.Management.Api --urls http://localhost:8081

# 4. Terminal 3: dashboard
cd src/ProxyWard.Dashboard
export VITE_PROXYWARD_API_BASE_URL=http://localhost:8081
export VITE_PROXYWARD_ADMIN_TOKEN=local-dev-token
npm run dev
```

ProxyWard listens on `http://localhost:8080`, the management API listens on `http://localhost:8081`, and Vite serves the dashboard at `http://localhost:5173`.

To stop the compose stack and wipe the audit DB and schema-lock history for a clean run:

```bash
docker compose down -v
```

---

## Services, Security, and APIs

ProxyWard is split into three deployable pieces:

| Service | Project | Local port | Responsibility |
| ------- | ------- | ---------- | -------------- |
| MCP proxy | `src/ProxyWard.Api` | 8080 | Data plane for MCP traffic, policy enforcement, audit writes, OpenTelemetry export, and minimal authenticated `/control/*` runtime changes. |
| Management API | `src/ProxyWard.Management.Api` | 8081 | Dashboard-facing stats, status, audit log, drift review, policy editing, settings, and management-to-proxy control calls. |
| Dashboard | `src/ProxyWard.Dashboard` | 8082 in Compose, 5173 with Vite dev | React SPA for operators. Browser code talks to the management API only, never directly to proxy `/control/*`. |

Compose binds published ports to `127.0.0.1` and wires the dashboard through Nginx so `/api/*` is same-origin from `http://localhost:8082`. If you run the Vite dev server separately on `http://localhost:5173`, configure management CORS explicitly.

### Key Environment Variables

| Variable | Service | Purpose |
| -------- | ------- | ------- |
| `PROXYWARD_DB_PATH` | Proxy | SQLite DB path containing `policy_snapshots`, audit rows, and schema-lock tables. |
| `PROXYWARD_CONTROL_ENABLED` | Proxy | Enables the minimal `/control/*` runtime-control endpoints. |
| `PROXYWARD_CONTROL_TOKEN` | Proxy | Bearer token for proxy control endpoints. Falls back to `PROXYWARD_ADMIN_TOKEN`. |
| `PROXYWARD_ADMIN_TOKEN` | Proxy and management | Shared local admin-token fallback used by Compose. Prefer secret injection outside local development. |
| `PROXYWARD_MANAGEMENT_AUDIT_DB_PATH` | Management API | SQLite DB path used for policy snapshots, audit/schema-lock data, and dashboard reads. |
| `PROXYWARD_PROXY_CONTROL_URL` | Management API | Internal URL of the proxy control API, for example `http://proxyward:8080` in Compose. |
| `PROXYWARD_PROXY_CONTROL_TOKEN` | Management API | Bearer token used by management API when calling proxy `/control/*`; falls back to `PROXYWARD_ADMIN_TOKEN`. |
| `PROXYWARD_MANAGEMENT_ADMIN_TOKEN` | Management API | Bearer token required by privileged management write endpoints outside explicit local-dev mode. |
| `PROXYWARD_MANAGEMENT_LOCAL_DEV` | Management API | Set to `true` only for explicit local development if you need management writes without an admin token. |
| `PROXYWARD_MANAGEMENT_CORS_ALLOWED_ORIGINS` | Management API | Comma- or semicolon-separated browser origins allowed for cross-origin dashboard/API calls, such as `http://localhost:5173`. Empty by default. |
| `VITE_PROXYWARD_API_BASE_URL` | Dashboard | Build/dev API base URL. Compose uses `/` so Nginx proxies same-origin `/api/*`; Vite dev commonly uses `http://localhost:8081`. |
| `VITE_PROXYWARD_ADMIN_TOKEN` | Dashboard | Optional local dashboard token sent on privileged management writes. Do not bake production secrets into a static dashboard bundle. |

### Endpoint Summary

Proxy endpoints:

| Method | Path | Purpose |
| ------ | ---- | ------- |
| `GET` | `/health` | Proxy health, active mode, policy hash, and server count. |
| `GET` | `/control/status` | Authenticated runtime-control status. |
| `PATCH` | `/control/mode` | Authenticated runtime mode change. |
| `PUT` | `/control/policy-snapshot` | Authenticated in-memory policy snapshot replacement. |
| `PUT` | `/control/yarp-config` | Authenticated dynamic YARP route/cluster replacement. |
| `*` | configured MCP routes, for example `/github/mcp` | Proxied MCP Streamable HTTP traffic. |

Management API endpoints:

| Method | Path | Purpose |
| ------ | ---- | ------- |
| `GET` | `/api/status` | Management, proxy control, audit DB, schema lock, and telemetry health. |
| `GET` | `/api/settings` | Read-only effective settings summary for the dashboard. |
| `GET` | `/api/overview` | Dashboard aggregate stats and time-series data from the audit DB. |
| `GET` | `/api/audit/events` | Paged/filterable audit events. |
| `GET` | `/api/audit/events/{id}` | Audit event detail. |
| `GET` | `/api/audit/export.ndjson` | Bounded NDJSON audit export. |
| `GET` | `/api/schema/drifts` | Paged/filterable schema drift review queue. |
| `GET` | `/api/schema/drifts/{id}` | Schema drift review detail and safe diff metadata. |
| `POST` | `/api/schema/drifts/{id}/approve|reject|block` | Privileged drift review action. |
| `GET` | `/api/policy` | Structured policy read model and redacted YAML. |
| `POST` | `/api/policy/validate` | Validate YAML or structured policy input. |
| `PUT` | `/api/policy` | Privileged policy apply workflow through management-to-proxy control calls. |
| `GET` | `/api/policy/impact` | Mode-switch impact preview. |
| `PATCH` | `/api/policy/mode` | Privileged runtime mode switch. |
| `GET` | `/api/tools` | Tool inventory from schema-lock history. |

Privileged management writes require `Authorization: Bearer <admin-token>` unless `PROXYWARD_MANAGEMENT_LOCAL_DEV=true` is explicitly set. Auth failures are logged and written to the audit DB without token values.

---

## Use Cases

### Example 1 — Simple: audit a single MCP server

You want to drop ProxyWard in front of one MCP server and just *watch* what your agent is doing. No blocking, just visibility.

**Policy YAML submitted through the dashboard or management API:**

```yaml
mode: audit
audit:
  sink: sqlite
  sqlitePath: ./data/proxyward.db
observability:
  serviceName: mcp-proxyward
  console:
    enabled: true
servers:
  github:
    route: /github/mcp
    upstream: http://localhost:9000/mcp     # your existing MCP server
    allowed: true
    tools:
      default: allow
      allow: []
      block: []
```

Send a `tools/call` through the proxy:

```bash
curl -X POST http://localhost:8080/github/mcp \
  -H "Content-Type: application/json" \
  -d '{
        "jsonrpc": "2.0",
        "id": 1,
        "method": "tools/call",
        "params": { "name": "echo", "arguments": { "message": "hello" } }
      }'
```

What you get:

- The call is **proxied normally** to the upstream and the client sees the real response.
- The proxy emits a structured log line and an OpenTelemetry trace for the request.
- A redacted row is written to `./data/proxyward.db` (table `audit_events`) capturing timestamp, server id, method, tool name, decision, mode, and policy version.
- The first time `tools/list` is called, ProxyWard records each tool's hashes into the `tool_schema_versions` table in `./data/proxyward.db`. From then on, any change to a tool's description or schema becomes a versioned audit event tied to the policy hash.

This mode is the recommended starting point for any new deployment.

### Example 2 — Complex: enforce mode with allowlists, argument rules, and drift detection

You're putting ProxyWard in front of a GitHub-style MCP server. Agents should be able to search repos and list issues, but **must not**:

- call any other tool on this server,
- write outside `/workspace`,
- hit anything other than `api.github.com`,
- ever reach a private-network address,
- run shell-like commands such as `rm`, `curl`, `wget`, or `bash`,
- silently keep working after the upstream changes a tool's description or schema.

**Policy YAML submitted through the dashboard or management API:**

```yaml
mode: enforce
inspection:
  maxBodyBytes: 1048576
  unsupportedStreaming: block
  batchToolCalls: failClosed
audit:
  sink: sqlite
  sqlitePath: ./data/proxyward.db
observability:
  serviceName: mcp-proxyward
  console:
    enabled: true
  otlp:
    enabled: true
    endpoint: http://otel-collector:4317
  applicationInsights:
    enabled: false
    connectionStringEnv: APPLICATIONINSIGHTS_CONNECTION_STRING
  sampling:
    tracesRatio: 1.0

servers:
  github:
    route: /github/mcp
    upstream: https://github-mcp.internal/mcp
    allowed: true
    tools:
      default: deny
      allow:
        - repos.search
        - issues.list
      block:
        - shell.exec
    arguments:
      paths:
        allowedRoots:
          - /workspace
        blockTraversal: true
      hosts:
        allow:
          - api.github.com
        blockPrivateNetworks: true
      commands:
        blockShell: true
        dangerous:
          - rm
          - curl
          - wget
          - nc
          - powershell
          - bash
```

Now consider three calls a client might make through `http://localhost:8080/github/mcp`:

**a) Allowed call — `repos.search`**

```jsonc
{
  "jsonrpc": "2.0", "id": 1, "method": "tools/call",
  "params": {
    "name": "repos.search",
    "arguments": { "query": "language:csharp yarp" }
  }
}
```

→ Tool is on the allow list, no argument rule trips. The proxy forwards to upstream and writes an `allow` audit row.

**b) Blocked tool — `shell.exec`**

```jsonc
{
  "jsonrpc": "2.0", "id": 2, "method": "tools/call",
  "params": { "name": "shell.exec", "arguments": { "cmd": "rm -rf /" } }
}
```

→ Two reasons fire: `tool_blocked` *and* `dangerous_command`. The upstream is **never called**. The client receives a valid JSON-RPC error response:

```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "error": {
    "code": -32001,
    "message": "MCP ProxyWard blocked this tool call",
    "data": { "reasons": ["tool_blocked", "dangerous_command"] }
  }
}
```

**c) Allowed tool, dangerous argument — host rule trips**

```jsonc
{
  "jsonrpc": "2.0", "id": 3, "method": "tools/call",
  "params": {
    "name": "repos.search",
    "arguments": { "callback": "http://10.0.0.5/exfil" }
  }
}
```

→ `repos.search` is allowed, but the argument inspector resolves `10.0.0.5` and matches `blockPrivateNetworks: true`. Reason `private_network_target` is recorded; the upstream is not called and the client gets the JSON-RPC error.

**Drift on top of all of the above:** if the upstream later changes the description of `repos.search`, the next `tools/list` produces a `tool_description_changed` decision. In `enforce` mode that blocks according to response-inspection policy until the change is reviewed; in `audit` mode it produces a warn event you can spot in logs and the audit DB.

You can flip between `mode: audit` and `mode: enforce` without changing any other rule — the same engine produces `would_block` decisions in audit and real `block` decisions in enforce.

---

## Configuration Reference (short)

Policy snapshots are persisted in SQLite and cached by the proxy at runtime. The dashboard and management API still accept YAML or structured JSON policy proposals with these top-level keys:

| Key             | Purpose                                                           |
| --------------- | ----------------------------------------------------------------- |
| `mode`          | `audit` or `enforce`                                              |
| `inspection`    | Body size limits, streaming behavior, batch handling              |
| `audit`         | Audit sink type and storage path                                  |
| `observability` | Service name, console / OTLP / Application Insights export        |
| `servers.<id>`  | Per-server route, upstream URL, allow flag, tool rules, arg rules |

The Compose stack bootstraps an empty DB-backed policy into `policy_snapshots` when the DB is empty; subsequent edits are persisted in SQLite and pushed to the proxy runtime.

---

## Project Status

MVP-stage. Implemented today: reverse proxy via YARP, server allowlist, JSON-RPC parsing, tool allow/block, DB-backed `tools/list` schema-lock persistence and drift detection, path / host / command argument rules, redacted SQLite audit, OpenTelemetry logs / traces / metrics with optional OTLP and Application Insights export, Docker Compose stack.

Deferred (designed for, not built yet): approval workflow queue, PostgreSQL audit sink, stdio sidecar transport, response mutation to hide disallowed tools from `tools/list`, remote/managed policy.

---

## Stars

If ProxyWard is useful to you, a star helps other people find it.

[![GitHub stars](https://img.shields.io/github/stars/uaspro/mcp-proxyward?style=social)](https://github.com/uaspro/mcp-proxyward/stargazers)

[![Star History Chart](https://api.star-history.com/svg?repos=uaspro/mcp-proxyward&type=Date)](https://star-history.com/#uaspro/mcp-proxyward&Date)
