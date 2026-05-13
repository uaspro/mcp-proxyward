import { dashboardConfig } from '../../config'
import type { PolicyModel, PolicyValidationIssue, PolicyValidationResponse, ServerPolicyModel } from '../../api/policy'
import type { ToolDiscoveryResponse, ToolInventoryResponse, ToolInventoryServer, ToolInventoryTool } from '../../api/tools'

export type NewServerPolicyForm = {
  id: string
  route: string
  upstream: string
}

export type ToolDisposition = 'default' | 'allow' | 'block' | 'hide'
export type ToolDispositionFilter = 'all' | ToolDisposition

export type ToolPolicyRow = {
  name: string
  title: string | null
  description: string | null
  driftStatus: string
  discovered: boolean
  disposition: ToolDisposition
  policyDirty: boolean
}
export function formatServerCount(count: number): string {
  return `${count.toLocaleString()} ${count === 1 ? 'server' : 'servers'}`
}

export function createToolInventoryServer(response: ToolDiscoveryResponse): ToolInventoryServer {
  return {
    serverId: response.serverId,
    latestVersion: response.latestVersion,
    driftStatus: 'clean',
    tools: response.tools,
  }
}

export function upsertToolInventoryServer(
  inventory: ToolInventoryResponse | null,
  server: ToolInventoryServer,
): ToolInventoryResponse {
  return {
    servers: [
      ...(inventory?.servers ?? []).filter((item) => item.serverId !== server.serverId),
      server,
    ].sort((left, right) => left.serverId.localeCompare(right.serverId)),
  }
}

export function omitRecordKey<TValue>(record: Record<string, TValue>, key: string): Record<string, TValue> {
  const next = { ...record }
  delete next[key]
  return next
}

export function createToolPolicyRows(
  server: ServerPolicyModel,
  inventory: ToolInventoryServer | null,
  baselineServer: ServerPolicyModel | null,
): ToolPolicyRow[] {
  const discovered = new Map<string, ToolInventoryTool>()
  for (const tool of inventory?.tools ?? []) {
    discovered.set(tool.name, tool)
  }

  const toolNames = new Set<string>([
    ...discovered.keys(),
    ...(server.tools.allow ?? []),
    ...(server.tools.block ?? []),
    ...(server.tools.hide ?? []),
  ])

  return [...toolNames]
    .sort((left, right) => left.localeCompare(right))
    .map((name) => {
      const tool = discovered.get(name) ?? null
      const disposition = getToolDisposition(server, name)
      return {
        name,
        title: tool?.title ?? null,
        description: tool?.description ?? null,
        driftStatus: tool?.driftStatus ?? 'unknown',
        discovered: tool !== null,
        disposition,
        policyDirty: disposition !== getToolDispositionOrDefault(baselineServer, name),
      }
    })
}

export function normalizeSearchQuery(value: string): string {
  return value.trim().toLowerCase()
}

export function includesSearch(value: string | null | undefined, searchQuery: string): boolean {
  return value?.toLowerCase().includes(searchQuery) ?? false
}

export function policyServerMatchesSearch(
  serverId: string,
  server: ServerPolicyModel,
  searchQuery: string,
): boolean {
  if (!searchQuery) {
    return true
  }

  return includesSearch(serverId, searchQuery)
    || includesSearch(server.route, searchQuery)
    || includesSearch(server.upstream, searchQuery)
}

export function toolPolicyRowMatchesSearch(row: ToolPolicyRow, searchQuery: string): boolean {
  if (!searchQuery) {
    return true
  }

  return includesSearch(row.name, searchQuery)
    || includesSearch(row.title, searchQuery)
    || includesSearch(row.description, searchQuery)
    || includesSearch(row.driftStatus, searchQuery)
    || includesSearch(row.disposition, searchQuery)
}

export function toolPolicyRowMatchesState(row: ToolPolicyRow, stateFilter: ToolDispositionFilter): boolean {
  return stateFilter === 'all' || row.disposition === stateFilter
}

export function getToolDispositionOrDefault(server: ServerPolicyModel | null, toolName: string): ToolDisposition {
  return server ? getToolDisposition(server, toolName) : 'default'
}

export function getToolDisposition(server: ServerPolicyModel, toolName: string): ToolDisposition {
  if ((server.tools.block ?? []).includes(toolName)) {
    return 'block'
  }

  if ((server.tools.hide ?? []).includes(toolName)) {
    return 'hide'
  }

  if ((server.tools.allow ?? []).includes(toolName)) {
    return 'allow'
  }

  return 'default'
}

export function updateToolDisposition(
  server: ServerPolicyModel,
  toolName: string,
  disposition: ToolDisposition,
): ServerPolicyModel {
  const allow = new Set((server.tools.allow ?? []).filter((name) => name !== toolName))
  const block = new Set((server.tools.block ?? []).filter((name) => name !== toolName))
  const hide = new Set((server.tools.hide ?? []).filter((name) => name !== toolName))

  if (disposition === 'allow') {
    allow.add(toolName)
  }

  if (disposition === 'block') {
    block.add(toolName)
  }

  if (disposition === 'hide') {
    hide.add(toolName)
  }

  return {
    ...server,
    tools: {
      ...server.tools,
      allow: [...allow].sort((left, right) => left.localeCompare(right)),
      block: [...block].sort((left, right) => left.localeCompare(right)),
      hide: [...hide].sort((left, right) => left.localeCompare(right)),
    },
  }
}

export function driftTone(status: string): 'neutral' | 'allow' | 'warn' | 'block' | 'info' {
  if (status === 'pending' || status === 'rejected') {
    return 'warn'
  }

  if (status === 'blocked') {
    return 'block'
  }

  if (status === 'approved' || status === 'clean') {
    return 'allow'
  }

  return 'neutral'
}

export function createNextServerId(
  servers: Record<string, ServerPolicyModel>,
  preferredId = 'server',
): string {
  const base = normalizeServerId(preferredId) || 'server'
  if (!servers[base]) {
    return base
  }

  for (let index = 2; ; index += 1) {
    const candidate = `${base}-${index}`
    if (!servers[candidate]) {
      return candidate
    }
  }
}

export function createNewServerForm(): NewServerPolicyForm {
  return {
    id: '',
    route: '',
    upstream: '',
  }
}

export function createGeneratedNewServerForm(
  upstream: string,
  servers: Record<string, ServerPolicyModel>,
): NewServerPolicyForm {
  const normalizedUpstream = upstream.trim()
  const id = createServerIdFromUpstream(normalizedUpstream, servers)

  return {
    id,
    route: id ? `/${id}/mcp` : '',
    upstream: normalizedUpstream,
  }
}

export function createServerIdFromUpstream(
  upstream: string,
  servers: Record<string, ServerPolicyModel>,
): string {
  const preferredId = createPreferredServerId(upstream)
  if (!preferredId) {
    return ''
  }

  return createNextServerId(servers, preferredId)
}

export function createPreferredServerId(upstream: string): string | null {
  try {
    const url = new URL(upstream)
    const hostname = url.hostname.replace(/^www\./i, '')
    const preferred = isLoopbackHost(hostname) && url.port
      ? `${hostname}-${url.port}`
      : hostname

    return normalizeServerId(preferred)
  } catch {
    return null
  }
}

export function isLoopbackHost(hostname: string): boolean {
  const normalized = hostname.toLowerCase()
  return normalized === 'localhost' || normalized === '127.0.0.1' || normalized === '::1'
}

export function normalizeServerId(value: string): string {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

export function normalizeNewServerForm(
  form: NewServerPolicyForm,
  existingServers: Record<string, ServerPolicyModel>,
): NewServerPolicyForm {
  const generated = createGeneratedNewServerForm(form.upstream, existingServers)

  return {
    id: generated.id.trim(),
    route: generated.route.trim(),
    upstream: generated.upstream,
  }
}

export function validateNewServerForm(
  form: NewServerPolicyForm,
  existingServers: Record<string, ServerPolicyModel>,
): string | null {
  if (!form.upstream) {
    return 'Upstream MCP server URL is required.'
  }

  try {
    const url = new URL(form.upstream)
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      return 'Upstream must be an absolute HTTP or HTTPS URL.'
    }
  } catch {
    return 'Upstream must be an absolute HTTP or HTTPS URL.'
  }

  if (!form.id) {
    return 'Server id could not be generated from the upstream URL.'
  }

  if (existingServers[form.id]) {
    return `Server ${form.id} already exists.`
  }

  if (!form.route.startsWith('/')) {
    return 'Route must start with /.'
  }

  return null
}

export function buildProxyMcpUrl(route: string): string {
  const normalizedRoute = route.startsWith('/') ? route : `/${route}`
  return `${dashboardConfig.proxyBaseUrl}${normalizedRoute}`
}

export function createMcpJsonSnippet(server: ServerPolicyModel): string {
  return JSON.stringify(
    {
      servers: {
        [server.id]: {
          type: 'http',
          url: buildProxyMcpUrl(server.route),
        },
      },
    },
    null,
    2,
  )
}

export const argumentPolicyPlaceholders = {
  allowedRoots: ['Example only - not configured', '/workspace', '/repos/proxyward'].join('\n'),
  hostsAllow: ['Example only - not configured', 'api.github.com', 'github.com'].join('\n'),
  dangerousCommands: ['Example only - not configured', 'rm', 'curl | sh', 'powershell -EncodedCommand'].join('\n'),
}

export const secretPatternPlaceholder = [
  'Example only - not configured',
  'ghp_',
  'github_pat_',
  'sk-',
  '/github_pat_[A-Za-z0-9_]+/',
  '/sk-[A-Za-z0-9]{20,}/',
].join('\n')

export function createServerPolicyModel(form: NewServerPolicyForm): ServerPolicyModel {
  return {
    id: form.id,
    route: form.route,
    upstream: form.upstream,
    allowed: true,
    secrets: {
      redactInLogs: true,
      blockReturn: false,
      patterns: [],
    },
    tools: {
      default: 'deny',
      allow: [],
      block: [],
      hide: [],
    },
    arguments: {
      paths: {
        allowedRoots: [],
        blockTraversal: true,
      },
      hosts: {
        allow: [],
        blockPrivateNetworks: true,
      },
      commands: {
        blockShell: true,
        dangerous: [],
      },
      overrides: {},
    },
  }
}

export function clonePolicyModel(model: PolicyModel): PolicyModel {
  return JSON.parse(JSON.stringify(model)) as PolicyModel
}

export function parseLines(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

export function formatLines(values: string[]): string {
  return values.join('\n')
}

export function findClientPolicyIssues(model: PolicyModel): PolicyValidationIssue[] {
  const issues: PolicyValidationIssue[] = []
  for (const server of Object.values(model.servers)) {
    if (server.upstream?.includes('***@') || server.upstream?.includes('[masked]')) {
      issues.push({
        field: `servers.${server.id}.upstream`,
        code: 'masked_value_requires_replacement',
        message: 'Masked upstream credentials cannot be applied. Replace the upstream URL explicitly before applying.',
      })
    }
  }

  return issues
}

export function createClientValidationResponse(errors: PolicyValidationIssue[]): PolicyValidationResponse {
  return {
    valid: false,
    errors,
    warnings: [],
    policyHash: null,
    normalizedModel: null,
  }
}
