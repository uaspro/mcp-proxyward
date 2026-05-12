import { getJson, postJson } from './client'

export type SchemaDriftStatus = 'pending' | 'approved' | 'rejected' | 'blocked' | string
export type SchemaDriftAction = 'approve' | 'block'

export type SchemaDriftWindow = {
  fromUtc: string | null
  toUtc: string | null
}

export type SchemaDriftItem = {
  id: number
  serverId: string
  toolName: string
  fieldName: string
  fromVersion: number
  toVersion: number
  status: SchemaDriftStatus
  reasons: string[]
  policyVersion: string | null
  detectedAtUtc: string
  reviewedAtUtc: string | null
  reviewedBy: string | null
  reviewNote: string | null
  impactCount: number
  hasDiffMetadata: boolean
  diffMode: 'metadata' | 'hash' | string
}

export type SchemaDriftPage = {
  offset: number
  pageSize: number
  totalCount: number
  window: SchemaDriftWindow
  items: SchemaDriftItem[]
}

export type SchemaDriftFilterOption = {
  value: string
  count: number
}

export type SchemaDriftFilterOptions = {
  servers: SchemaDriftFilterOption[]
  tools: SchemaDriftFilterOption[]
}

export type SchemaDriftDiff = {
  beforeJson: string | null
  afterJson: string | null
  beforeHash: string
  afterHash: string
  createdAtUtc: string | null
  mode: 'metadata' | 'hash' | string
}

export type SchemaDriftDetail = SchemaDriftItem & {
  diff: SchemaDriftDiff
}

export type SchemaDriftQuery = {
  fromUtc?: string
  toUtc?: string
  status?: string
  serverId?: string
  toolName?: string
  offset?: number
  pageSize?: number
}

export type SchemaDriftActionRequest = {
  reviewedBy?: string
  reviewNote?: string
}

export function buildSchemaDriftsPath(query: SchemaDriftQuery = {}): string {
  const params = toSearchParams(query)
  const suffix = params.toString()

  return suffix ? `/api/schema/drifts?${suffix}` : '/api/schema/drifts'
}

export function buildSchemaDriftDetailPath(id: number, query: Pick<SchemaDriftQuery, 'fromUtc' | 'toUtc'> = {}): string {
  const params = toSearchParams(query)
  const suffix = params.toString()

  return suffix
    ? `/api/schema/drifts/${encodeURIComponent(String(id))}?${suffix}`
    : `/api/schema/drifts/${encodeURIComponent(String(id))}`
}

export function getSchemaDrifts(query: SchemaDriftQuery = {}, signal?: AbortSignal) {
  return getJson<SchemaDriftPage>(buildSchemaDriftsPath(query), signal)
}

export function getSchemaDriftFilterOptions(signal?: AbortSignal) {
  return getJson<SchemaDriftFilterOptions>('/api/schema/drifts/filters', signal)
}

export function getSchemaDriftDetail(
  id: number,
  query: Pick<SchemaDriftQuery, 'fromUtc' | 'toUtc'> = {},
  signal?: AbortSignal,
) {
  return getJson<SchemaDriftDetail>(buildSchemaDriftDetailPath(id, query), signal)
}

export function applySchemaDriftAction(
  id: number,
  action: SchemaDriftAction,
  request: SchemaDriftActionRequest = {},
  signal?: AbortSignal,
) {
  return postJson<SchemaDriftDetail>(
    `/api/schema/drifts/${encodeURIComponent(String(id))}/${action}`,
    request,
    signal,
  )
}

function toSearchParams(query: SchemaDriftQuery): URLSearchParams {
  const params = new URLSearchParams()

  append(params, 'fromUtc', query.fromUtc)
  append(params, 'toUtc', query.toUtc)
  append(params, 'status', query.status)
  append(params, 'serverId', query.serverId)
  append(params, 'toolName', query.toolName)
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
