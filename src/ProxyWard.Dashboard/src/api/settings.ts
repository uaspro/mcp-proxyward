import { getJson } from './client'

export type SettingsResponse = {
  observability: SettingsObservability
  audit: SettingsAudit
  persistence: SettingsPersistence
  inspection: SettingsInspection
  service: SettingsServiceInfo
  runtime: SettingsRuntime
}

export type SettingsObservability = {
  serviceName: string
  consoleEnabled: boolean
  otlpEnabled: boolean
  otlpEndpoint: string | null
  applicationInsightsEnabled: boolean
  applicationInsightsConnectionStringEnv: string
  tracesRatio: number
}

export type SettingsAudit = {
  enabled: boolean
}

export type SettingsPersistence = {
  provider: string
  source: string
  connectionConfigured: boolean
}

export type SettingsInspection = {
  maxBodyBytes: number
  unsupportedStreaming: string
  batchToolCalls: string
}

export type SettingsServiceInfo = {
  policyHash: string
  sourcePath: string
  serverCount: number
  loadedAtUtc: string
  sourceLastModifiedUtc: string | null
  sourceSizeBytes: number | null
}

export type SettingsRuntime = {
  editingSupported: boolean
  settingsWritable: boolean
}

export function getSettings(signal?: AbortSignal) {
  return getJson<SettingsResponse>('/api/settings', signal)
}
