import { getJson } from './client'

export type OverviewRange = '1h' | '4h' | '1d' | '7d' | '30d'

type OverviewRangeOption = {
  value: OverviewRange
  label: string
  durationMs: number
  bucketSeconds: number
}

export const defaultOverviewRange: OverviewRange = '1h'

export const overviewRangeOptions: OverviewRangeOption[] = [
  { value: '1h', label: '1h', durationMs: 60 * 60 * 1000, bucketSeconds: 60 },
  { value: '4h', label: '4h', durationMs: 4 * 60 * 60 * 1000, bucketSeconds: 4 * 60 },
  { value: '1d', label: '1d', durationMs: 24 * 60 * 60 * 1000, bucketSeconds: 30 * 60 },
  { value: '7d', label: '7d', durationMs: 7 * 24 * 60 * 60 * 1000, bucketSeconds: 3 * 60 * 60 },
  { value: '30d', label: '30d', durationMs: 30 * 24 * 60 * 60 * 1000, bucketSeconds: 12 * 60 * 60 },
]

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

export function buildOverviewPath(range: OverviewRange = defaultOverviewRange, now = new Date()) {
  const rangeOption = getOverviewRangeOption(range)
  const toUtc = now.toISOString()
  const fromUtc = new Date(now.getTime() - rangeOption.durationMs).toISOString()
  const params = new URLSearchParams({
    fromUtc,
    toUtc,
    bucketSeconds: rangeOption.bucketSeconds.toString(),
    topReasons: '6',
    topTools: '6',
  })

  return `/api/overview?${params.toString()}`
}

export function getOverview(range: OverviewRange = defaultOverviewRange, signal?: AbortSignal) {
  return getJson<OverviewResponse>(buildOverviewPath(range), signal)
}

export function getOverviewRangeLabel(range: OverviewRange) {
  return getOverviewRangeOption(range).label
}

function getOverviewRangeOption(range: OverviewRange) {
  return overviewRangeOptions.find((option) => option.value === range) ?? overviewRangeOptions[0]
}
