import { getJson, patchJson, postJson, putJson } from './client'

export type PolicyResponse = {
  yaml: string
  policyHash: string
  source: PolicySource
  model: PolicyModel
  readOnly: PolicyReadOnly
}

export type PolicySource = {
  path: string
  format: string
  exists: boolean
  lastModifiedUtc: string | null
  sizeBytes: number | null
}

export type PolicyReadOnly = {
  policyHash: string
  sourcePath: string
  serverCount: number
  loadedAtUtc: string
}

export type PolicyModel = {
  mode: 'audit' | 'enforce' | string
  inspection: InspectionPolicy
  audit: AuditPolicy
  observability: ObservabilityPolicy
  servers: Record<string, ServerPolicyModel>
}

export type InspectionPolicy = {
  maxBodyBytes: number
  unsupportedStreaming: string
  batchToolCalls: string
}

export type AuditPolicy = {
  enabled: boolean
}

export type ObservabilityPolicy = {
  serviceName: string
  console: { enabled: boolean }
  otlp: { enabled: boolean; endpoint: string | null }
  applicationInsights: { enabled: boolean; connectionStringEnv: string }
  sampling: { tracesRatio: number }
}

export type ServerPolicyModel = {
  id: string
  route: string
  upstream: string | null
  allowed: boolean
  secrets: SecretsPolicy | null
  tools: ToolPolicy
  arguments: ArgumentPolicy
}

export type SecretsPolicy = {
  redactInLogs: boolean
  blockReturn: boolean
  patterns: string[]
}

export type ToolPolicy = {
  default: 'allow' | 'deny' | 'hide' | string
  allow: string[]
  block: string[]
  hide: string[]
}

export type ArgumentPolicy = {
  paths: PathArgumentPolicy
  hosts: HostArgumentPolicy
  commands: CommandArgumentPolicy
  overrides: Record<string, ToolArgumentOverride>
}

export type PathArgumentPolicy = {
  allowedRoots: string[]
  blockTraversal: boolean
}

export type HostArgumentPolicy = {
  allow: string[]
  blockPrivateNetworks: boolean
}

export type CommandArgumentPolicy = {
  blockShell: boolean
  dangerous: string[]
}

export type ToolArgumentOverride = {
  toolName: string
  paths: Partial<PathArgumentPolicy> | null
  hosts: Partial<HostArgumentPolicy> | null
  commands: Partial<CommandArgumentPolicy> | null
}

export type PolicyProposal = {
  model: PolicyModel
  requestedBy?: string
  note?: string
}

export type PolicyValidationResponse = {
  valid: boolean
  errors: PolicyValidationIssue[]
  warnings: PolicyValidationIssue[]
  policyHash: string | null
  normalizedModel: PolicyModel | null
}

export type PolicyValidationIssue = {
  field: string
  code: string
  message: string
}

export type PolicyApplyResponse = {
  previousMode: string
  mode: string
  previousPolicyHash: string
  policyHash: string
  serverCount: number
  routeVersion: number | null
  yarp: unknown
}

export type PolicyModeImpactResponse = {
  currentMode: string
  targetMode: string
  currentPolicyHash: string
  requiresConfirmation: boolean
  confirmationToken: string | null
  window: {
    fromUtc: string
    toUtc: string
  }
  wouldBlockCount: number
  pendingDriftCount: number
  unapprovedDriftCount: number
  affected: PolicyModeImpactItem[]
}

export type PolicyModeImpactItem = {
  serverId: string
  toolName: string | null
  wouldBlockCount: number
  pendingDriftCount: number
  unapprovedDriftCount: number
  reasons: string[]
}

export type PolicyModeSwitchRequest = {
  mode: string
  confirmationToken?: string | null
  impactFromUtc?: string
  impactToUtc?: string
  requestedBy?: string
  note?: string
}

export type PolicyModeSwitchResponse = {
  mode: string
  previousMode: string
  policyHash: string
  previousPolicyHash: string
  serverCount: number
  routeVersion: number | null
  impact: PolicyModeImpactResponse
}

export function getPolicy(signal?: AbortSignal) {
  return getJson<PolicyResponse>('/api/policy', signal)
}

export function validatePolicy(proposal: PolicyProposal, signal?: AbortSignal) {
  return postJson<PolicyValidationResponse>('/api/policy/validate', proposal, signal)
}

export function applyPolicy(proposal: PolicyProposal, signal?: AbortSignal) {
  return putJson<PolicyApplyResponse>('/api/policy', proposal, signal)
}

export function buildPolicyImpactPath(mode: string, now = new Date()): string {
  const toUtc = now.toISOString()
  const fromUtc = new Date(now.getTime() - 24 * 60 * 60 * 1000).toISOString()
  const params = new URLSearchParams({ mode, fromUtc, toUtc })

  return `/api/policy/impact?${params.toString()}`
}

export function getPolicyModeImpact(mode: string, signal?: AbortSignal) {
  return getJson<PolicyModeImpactResponse>(buildPolicyImpactPath(mode), signal)
}

export function switchPolicyMode(request: PolicyModeSwitchRequest, signal?: AbortSignal) {
  return patchJson<PolicyModeSwitchResponse>('/api/policy/mode', request, signal)
}
