import { buildApiUrl, getJson } from './client'

export type AuditEventItem = {
  id: number
  timestampUtc: string
  eventType: string
  mode: string
  decision: string
  serverId: string
  method: string | null
  toolName: string | null
  reasons: string[]
  policyVersion: string
  correlationId: string
  requestBytes: number
  durationMs: number
  argumentSummary: unknown | null
}

export type AuditEventPage = {
  offset: number
  pageSize: number
  totalCount: number
  items: AuditEventItem[]
}

export type AuditEventQuery = {
  fromUtc?: string
  toUtc?: string
  decision?: string
  serverId?: string
  method?: string
  toolName?: string
  correlationId?: string
  search?: string
  offset?: number
  pageSize?: number
}

export function buildAuditEventsPath(query: AuditEventQuery = {}): string {
  const params = toSearchParams(query)
  const suffix = params.toString()

  return suffix ? `/api/audit/events?${suffix}` : '/api/audit/events'
}

export function buildAuditEventPath(id: number): string {
  return `/api/audit/events/${encodeURIComponent(String(id))}`
}

export function buildAuditExportPath(query: AuditEventQuery = {}): string {
  const exportQuery = { ...query }
  delete exportQuery.offset
  delete exportQuery.pageSize

  const params = toSearchParams(exportQuery)
  const suffix = params.toString()

  return suffix ? `/api/audit/export.ndjson?${suffix}` : '/api/audit/export.ndjson'
}

export function buildAuditExportUrl(query: AuditEventQuery = {}): string {
  return buildApiUrl(buildAuditExportPath(query))
}

export function getAuditEvents(query: AuditEventQuery = {}, signal?: AbortSignal) {
  return getJson<AuditEventPage>(buildAuditEventsPath(query), signal)
}

export function getAuditEvent(id: number, signal?: AbortSignal) {
  return getJson<AuditEventItem>(buildAuditEventPath(id), signal)
}

function toSearchParams(query: AuditEventQuery): URLSearchParams {
  const params = new URLSearchParams()

  append(params, 'fromUtc', query.fromUtc)
  append(params, 'toUtc', query.toUtc)
  append(params, 'decision', query.decision)
  append(params, 'serverId', query.serverId)
  append(params, 'method', query.method)
  append(params, 'toolName', query.toolName)
  append(params, 'correlationId', query.correlationId)
  append(params, 'search', query.search)
  append(params, 'offset', query.offset)
  append(params, 'pageSize', query.pageSize)

  return params
}

function append(params: URLSearchParams, key: string, value: string | number | undefined): void {
  if (value === undefined) {
    return
  }

  const normalized = String(value).trim()
  if (normalized.length === 0) {
    return
  }

  params.set(key, normalized)
}
