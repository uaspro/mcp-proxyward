import { getJson } from './client'

export type OverviewTopRow = {
  key: string
  count: number
}

export type OverviewSeriesPoint = {
  bucketStartUtc: string
  allow: number
  block: number
  wouldBlock: number
  warn: number
  total: number
}

export type OverviewMetadata = {
  source: string
  asOfUtc: string | null
  partial: boolean
  notes: string | null
}

export type OverviewResponse = {
  requestRate: number
  blockRate: number
  wouldBlockRate: number
  errorRate: number
  latencyP95Ms: number | null
  topReasons: OverviewTopRow[]
  topTools: OverviewTopRow[]
  series: OverviewSeriesPoint[]
  metadata: OverviewMetadata
}

export function buildOverviewPath(now = new Date()) {
  const toUtc = now.toISOString()
  const fromUtc = new Date(now.getTime() - 60 * 60 * 1000).toISOString()
  const params = new URLSearchParams({
    fromUtc,
    toUtc,
    bucketSeconds: '60',
    topReasons: '6',
    topTools: '6',
  })

  return `/api/overview?${params.toString()}`
}

export function getOverview(signal?: AbortSignal) {
  return getJson<OverviewResponse>(buildOverviewPath(), signal)
}
