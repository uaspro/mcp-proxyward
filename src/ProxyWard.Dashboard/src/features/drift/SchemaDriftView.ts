import type { SchemaDriftFilterOptions, SchemaDriftItem, SchemaDriftPage, SchemaDriftQuery } from '../../api/drift'

export type DriftTab = 'diff' | 'before' | 'after' | 'history'
export type DriftStatusFilter = 'all' | 'pending' | 'approved' | 'blocked'
export type DriftTimeWindow = '24h' | '7d' | '30d' | 'all'

export type DriftFilters = {
  status: DriftStatusFilter
  serverId: string
  toolName: string
  timeWindow: DriftTimeWindow
}

export const driftPageSize = 20

export const driftStatusOptions: Array<{ value: DriftStatusFilter; label: string }> = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'blocked', label: 'Blocked' },
]

export const emptyFilterOptions: SchemaDriftFilterOptions = {
  servers: [],
  tools: [],
}

export const driftWindowOptions: Array<{ value: DriftTimeWindow; label: string }> = [
  { value: '24h', label: '24h' },
  { value: '7d', label: '7d' },
  { value: '30d', label: '30d' },
  { value: 'all', label: 'All time' },
]

const driftWindowMs: Record<Exclude<DriftTimeWindow, 'all'>, number> = {
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
  '30d': 30 * 24 * 60 * 60 * 1000,
}

export const initialDriftFilters: DriftFilters = {
  status: 'pending',
  serverId: '',
  toolName: '',
  timeWindow: '7d',
}

export function createSchemaDriftQuery(filters: DriftFilters, offset: number, now: Date): SchemaDriftQuery {
  if (filters.timeWindow === 'all') {
    return createBaseQuery(filters, offset)
  }

  return {
    ...createBaseQuery(filters, offset),
    fromUtc: new Date(now.getTime() - driftWindowMs[filters.timeWindow]).toISOString(),
    toUtc: now.toISOString(),
  }
}

export function formatDriftPageRange(page: SchemaDriftPage): string {
  if (page.totalCount === 0) {
    return 'No rows'
  }

  const first = page.offset + 1
  const last = Math.min(page.offset + page.items.length, page.totalCount)

  return `${first.toLocaleString()}-${last.toLocaleString()} of ${page.totalCount.toLocaleString()}`
}

export function formatDiffJson(value: string | null): string {
  if (!value) {
    return '{}'
  }

  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

export function formatFieldLabel(fieldName: string): string {
  return fieldName === 'description'
    ? 'description'
    : fieldName === 'schema'
      ? 'schema'
      : fieldName
}

export function formatDecisionStatusMessage(item: SchemaDriftItem): string {
  if (item.status === 'approved') {
    return 'This tool version is approved and can appear in tools/list in enforce mode.'
  }

  if (item.status === 'rejected') {
    return 'This legacy rejected item remains unapproved; enforce mode removes only this tool from matching tools/list responses.'
  }

  if (item.status === 'blocked') {
    return 'This tool version is blocked and remains unapproved; enforce mode removes only this tool from matching tools/list responses.'
  }

  return 'This tool version is waiting for review; approve allows it, block removes only this tool from discovery.'
}

export function formatReviewDecisionDetail(item: SchemaDriftItem): string {
  if (item.status === 'approved') {
    return 'Future matching tools/list responses may include this tool version in enforce mode.'
  }

  if (item.status === 'rejected') {
    return 'This item was rejected before the workflow was simplified. Choose Approve to allow it or Block to keep only this tool unavailable.'
  }

  if (item.status === 'blocked') {
    return 'Block keeps only the affected tool out of tools/list and records a stronger unsafe decision.'
  }

  return 'Approve allows this tool version. Block keeps only the affected tool out of tools/list in enforce mode.'
}

function createBaseQuery(filters: DriftFilters, offset: number): SchemaDriftQuery {
  return {
    status: filters.status === 'all' ? undefined : filters.status,
    serverId: filters.serverId.trim() || undefined,
    toolName: filters.toolName.trim() || undefined,
    offset,
    pageSize: driftPageSize,
  }
}
