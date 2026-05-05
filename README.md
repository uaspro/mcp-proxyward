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

The fastest way to see ProxyWard in action is the bundled Docker Compose stack, which boots ProxyWard, a sample MCP server, and an OpenTelemetry collector together.

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

This brings up three services:

| Service          | Purpose                                 | Port  |
| ---------------- | --------------------------------------- | ----- |
| `proxyward`      | The guard proxy itself                  | 8080  |
| `sample-mcp`     | A tiny MCP echo server used as upstream | (internal) |
| `otel-collector` | Receives OTLP logs / traces / metrics   | 4317 / 4318 |

The compose file mounts [`samples/compose/proxyward.yaml`](samples/compose/proxyward.yaml) into the container at `/app/config/proxyward.yaml`. SQLite audit data and schema-lock history live in the named `proxyward-data` volume at `/app/data/`.

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
  "serverCount": 1
}
```

### 4. Point your MCP client at ProxyWard

Replace your client's MCP server URL with the route configured in `proxyward.yaml`. The bundled sample exposes the `sample-mcp` upstream at:

```text
http://localhost:8080/sample/mcp
```

### Running without Docker

If you prefer to run the proxy directly against your own upstream:

```bash
# 1. Restore and build
dotnet build McpProxyWard.slnx

# 2. Point at any policy file
export PROXYWARD_POLICY_PATH=./proxyward.yaml      # bash / zsh
$env:PROXYWARD_POLICY_PATH = ".\proxyward.yaml"    # PowerShell

# 3. Run the API host
dotnet run --project src/ProxyWard.Api
```

ProxyWard listens on `http://localhost:8080` by default. Override with `ASPNETCORE_URLS` if you need a different port.

To stop the compose stack and wipe the audit DB and schema-lock history for a clean run:

```bash
docker compose down -v
```

---

## Use Cases

### Example 1 — Simple: audit a single MCP server

You want to drop ProxyWard in front of one MCP server and just *watch* what your agent is doing. No blocking, just visibility.

**`proxyward.yaml`:**

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
  sample:
    route: /sample/mcp
    upstream: http://localhost:9000/mcp     # your existing MCP server
    allowed: true
    tools:
      default: allow
      allow: []
      block: []
```

Send a `tools/call` through the proxy:

```bash
curl -X POST http://localhost:8080/sample/mcp \
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

**`proxyward.yaml`:**

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

A single YAML file drives the proxy. Top-level keys:

| Key             | Purpose                                                           |
| --------------- | ----------------------------------------------------------------- |
| `mode`          | `audit` or `enforce`                                              |
| `inspection`    | Body size limits, streaming behavior, batch handling              |
| `audit`         | Audit sink type and storage path                                  |
| `observability` | Service name, console / OTLP / Application Insights export        |
| `servers.<id>`  | Per-server route, upstream URL, allow flag, tool rules, arg rules |

See [`proxyward.yaml`](proxyward.yaml) for the canonical example and [`samples/compose/proxyward.yaml`](samples/compose/proxyward.yaml) for the compose variant.

---

## Project Status

MVP-stage. Implemented today: reverse proxy via YARP, server allowlist, JSON-RPC parsing, tool allow/block, DB-backed `tools/list` schema-lock persistence and drift detection, path / host / command argument rules, redacted SQLite audit, OpenTelemetry logs / traces / metrics with optional OTLP and Application Insights export, Docker Compose sample.

Deferred (designed for, not built yet): approval workflow queue, PostgreSQL audit sink, stdio sidecar transport, response mutation to hide disallowed tools from `tools/list`, remote/managed policy.

---

## Stars

If ProxyWard is useful to you, a star helps other people find it.

[![GitHub stars](https://img.shields.io/github/stars/OWNER/mcp-proxyward?style=social)](https://github.com/OWNER/mcp-proxyward/stargazers)

[![Star History Chart](https://api.star-history.com/svg?repos=OWNER/mcp-proxyward&type=Date)](https://star-history.com/#OWNER/mcp-proxyward&Date)
