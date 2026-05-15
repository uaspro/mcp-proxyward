import { getJson } from './client'

export type ComponentStatus = 'healthy' | 'degraded' | 'unhealthy' | 'unknown' | string

export type ComponentReport = {
  status: ComponentStatus
  notes: string | null
  details: Record<string, unknown> | null
}

export type StatusResponse = {
  status: ComponentStatus
  service: string
  components: {
    managementApi: ComponentReport
    proxyControl: ComponentReport
    persistenceDb: ComponentReport
    schemaLock: ComponentReport
    telemetry: ComponentReport
  }
}

export function getStatus(signal?: AbortSignal) {
  return getJson<StatusResponse>('/api/status', signal)
}
