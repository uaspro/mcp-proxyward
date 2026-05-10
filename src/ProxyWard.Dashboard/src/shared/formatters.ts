import { ApiError } from '../api/client'

export function formatAsOf(value: Date | null) {
  if (!value) {
    return 'loading'
  }

  return value.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

export function formatAuditTime(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

export function formatAuditDateTime(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleString([], {
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

export function formatDuration(value: number): string {
  return `${value.toLocaleString()} ms`
}

export function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`
  }

  return `${(value / 1024).toFixed(1)} KB`
}

export function formatJson(value: unknown): string {
  if (value === null || value === undefined) {
    return '{}'
  }

  try {
    return JSON.stringify(value, null, 2) ?? '{}'
  } catch {
    return String(value)
  }
}

export function describeReason(reason: string): string {
  const explanations: Record<string, string> = {
    tool_blocked: 'This tool is denied by policy or absent from an allow list while default deny is active.',
    tool_not_allowed: 'The server tool policy does not allow this tool.',
    dangerous_command: 'A command-like argument matched a configured dangerous command rule.',
    private_network_target: 'A URL or host argument resolved to a blocked private or local network range.',
    path_traversal: 'A path argument contains traversal or escapes configured safe roots.',
    path_outside_allowed_roots: 'A path argument resolves outside the configured allowed roots.',
    host_not_allowed: 'A URL or host argument is not present in the configured allowlist.',
    tool_description_changed: 'The observed tool description differs from the approved schema baseline.',
    tool_schema_changed: 'The observed tool schema differs from the approved schema baseline.',
    mcp_protocol_changed: 'The observed MCP protocol value differs from the approved baseline.',
    inspection_unsupported: 'The request or response could not be inspected within configured limits.',
    secret_return_blocked: 'A tool response contained a configured secret pattern and was blocked.',
  }

  return explanations[reason] ?? 'Policy emitted this deterministic reason code for the event.'
}

export function formatApiFailure(ex: unknown, fallback: string): string {
  if (ex instanceof ApiError && ex.status === 401) {
    return 'Unauthorized management write. Admin token is required.'
  }

  if (ex instanceof ApiError && ex.status) {
    const detail = ex.message && !ex.message.endsWith('failed') ? `: ${ex.message}` : ''
    return `${fallback}${detail} (${ex.status})`
  }

  return ex instanceof Error ? ex.message : fallback
}
