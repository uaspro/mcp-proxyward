import http from 'node:http'

const port = Number(process.env.PROXYWARD_E2E_API_PORT ?? 8091)
const now = new Date('2026-05-10T15:00:00.000Z').toISOString()

const auditEvent = {
  id: 101,
  timestampUtc: now,
  eventType: 'tool_call_policy',
  mode: 'audit',
  decision: 'would_block',
  serverId: 'sample',
  method: 'tools/call',
  toolName: 'fs.read',
  reasons: ['path_traversal'],
  policyVersion: 'sha256:e2e',
  correlationId: 'e2e-correlation',
  requestBytes: 512,
  durationMs: 37,
  argumentSummary: { path: '[redacted-path]' },
}

const driftItem = {
  id: 201,
  serverId: 'sample',
  toolName: 'repos.search',
  fieldName: 'description',
  fromVersion: 1,
  toVersion: 2,
  status: 'pending',
  reasons: ['description_changed'],
  policyVersion: 'sha256:e2e',
  detectedAtUtc: now,
  reviewedAtUtc: null,
  reviewedBy: null,
  reviewNote: null,
  impactCount: 3,
  hasDiffMetadata: true,
  diffMode: 'metadata',
}

const policyModel = {
  mode: 'audit',
  inspection: {
    maxBodyBytes: 1048576,
    unsupportedStreaming: 'warn',
    batchToolCalls: 'failClosed',
  },
  audit: {
    sink: 'sqlite',
    sqlitePath: '/app/data/proxyward.db',
  },
  observability: {
    serviceName: 'mcp-proxyward',
    console: { enabled: true },
    otlp: { enabled: true, endpoint: 'http://otel-collector:4317' },
    applicationInsights: {
      enabled: false,
      connectionStringEnv: 'APPLICATIONINSIGHTS_CONNECTION_STRING',
    },
    sampling: { tracesRatio: 1 },
  },
  servers: {
    sample: {
      id: 'sample',
      route: '/sample/mcp',
      upstream: 'http://sample-mcp:8080/mcp',
      allowed: true,
      secrets: {
        redactInLogs: true,
        blockReturn: false,
        patterns: ['ghp_'],
      },
      tools: {
        default: 'allow',
        allow: ['fs.read'],
        block: ['shell.exec'],
      },
      arguments: {
        paths: {
          allowedRoots: ['/workspace'],
          blockTraversal: true,
        },
        hosts: {
          allow: ['api.github.com'],
          blockPrivateNetworks: true,
        },
        commands: {
          blockShell: true,
          dangerous: ['rm'],
        },
        overrides: {},
      },
    },
  },
}

const responses = {
  '/api/status': {
    status: 'healthy',
    service: 'MCP ProxyWard Management API',
    components: {
      managementApi: { status: 'healthy', notes: null, details: null },
      proxyControl: {
        status: 'healthy',
        notes: null,
        details: {
          mode: 'audit',
          policyVersion: 'sha256:e2e',
          serverCount: 1,
          routeVersion: 1,
        },
      },
      auditDb: {
        status: 'healthy',
        notes: null,
        details: { sqlitePath: '/app/data/proxyward.db' },
      },
      schemaLock: {
        status: 'healthy',
        notes: null,
        details: { trackedSnapshotCount: 1 },
      },
      telemetry: {
        status: 'healthy',
        notes: null,
        details: { source: 'audit-db' },
      },
    },
  },
  '/api/overview': {
    requestRate: 12.4,
    blockRate: 1.2,
    wouldBlockRate: 3.4,
    errorRate: 0,
    latencyP95Ms: 42,
    topReasons: [{ key: 'path_traversal', count: 7 }],
    topTools: [{ key: 'fs.read', count: 11 }],
    series: [
      {
        bucketStartUtc: '2026-05-10T14:58:00.000Z',
        allow: 8,
        block: 1,
        wouldBlock: 2,
        warn: 1,
        total: 12,
      },
      {
        bucketStartUtc: '2026-05-10T14:59:00.000Z',
        allow: 10,
        block: 0,
        wouldBlock: 3,
        warn: 0,
        total: 13,
      },
    ],
    metadata: {
      source: 'audit-db',
      asOfUtc: now,
      partial: false,
      notes: null,
    },
  },
  '/api/audit/events': {
    offset: 0,
    pageSize: 50,
    totalCount: 1,
    items: [auditEvent],
  },
  '/api/audit/events/101': auditEvent,
  '/api/schema/drifts': {
    offset: 0,
    pageSize: 50,
    totalCount: 1,
    window: {
      fromUtc: '2026-05-10T14:00:00.000Z',
      toUtc: now,
    },
    items: [driftItem],
  },
  '/api/schema/drifts/201': {
    ...driftItem,
    diff: {
      beforeJson: '{"description":"old search"}',
      afterJson: '{"description":"new search"}',
      beforeHash: 'sha256:before',
      afterHash: 'sha256:after',
      createdAtUtc: now,
      mode: 'metadata',
    },
  },
  '/api/policy': {
    yaml: 'mode: audit\nservers:\n  sample:\n    route: /sample/mcp\n',
    policyHash: 'sha256:e2e',
    source: {
      path: '/app/config/proxyward.yaml',
      format: 'yaml',
      exists: true,
      lastModifiedUtc: now,
      sizeBytes: 1024,
    },
    model: policyModel,
    readOnly: {
      policyHash: 'sha256:e2e',
      sourcePath: '/app/config/proxyward.yaml',
      serverCount: 1,
      loadedAtUtc: now,
    },
  },
  '/api/settings': {
    observability: {
      serviceName: 'mcp-proxyward',
      consoleEnabled: true,
      otlpEnabled: true,
      otlpEndpoint: 'http://otel-collector:4317',
      applicationInsightsEnabled: false,
      applicationInsightsConnectionStringEnv: 'APPLICATIONINSIGHTS_CONNECTION_STRING',
      tracesRatio: 1,
    },
    audit: {
      sink: 'sqlite',
      sqlitePath: '/app/data/proxyward.db',
    },
    inspection: {
      maxBodyBytes: 1048576,
      unsupportedStreaming: 'warn',
      batchToolCalls: 'failClosed',
    },
    service: {
      policyHash: 'sha256:e2e',
      sourcePath: '/app/config/proxyward.yaml',
      serverCount: 1,
      loadedAtUtc: now,
      sourceLastModifiedUtc: now,
      sourceSizeBytes: 1024,
    },
    runtime: {
      editingSupported: false,
      settingsWritable: false,
    },
  },
  '/api/tools': {
    servers: [
      {
        serverId: 'sample',
        route: '/sample/mcp',
        upstream: 'http://sample-mcp:8080/mcp',
        tools: [
          {
            name: 'fs.read',
            title: 'Read file',
            description: 'Read a file from the workspace',
            version: 2,
            lastSeenUtc: now,
            status: 'approved',
          },
        ],
      },
    ],
  },
}

const server = http.createServer((request, response) => {
  const url = new URL(request.url ?? '/', `http://${request.headers.host}`)

  response.setHeader('Access-Control-Allow-Origin', '*')
  response.setHeader('Access-Control-Allow-Headers', 'authorization,content-type,accept')
  response.setHeader('Access-Control-Allow-Methods', 'GET,POST,PUT,PATCH,OPTIONS')

  if (request.method === 'OPTIONS') {
    response.writeHead(204)
    response.end()
    return
  }

  const payload = responses[url.pathname]
  if (payload === undefined) {
    response.writeHead(404, { 'content-type': 'application/json' })
    response.end(JSON.stringify({ error: 'not_found', path: url.pathname }))
    return
  }

  response.writeHead(200, { 'content-type': 'application/json' })
  response.end(JSON.stringify(payload))
})

server.listen(port, '127.0.0.1', () => {
  console.log(`ProxyWard e2e management API fixture listening on http://127.0.0.1:${port}`)
})

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    server.close(() => process.exit(0))
  })
}
