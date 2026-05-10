import type { StatusResponse } from '../api/status'

export type Mode = 'audit' | 'enforce'

export function normalizeRuntimeMode(value: unknown): Mode | null {
  if (typeof value !== 'string') {
    return null
  }

  const normalized = value.trim().toLowerCase()
  return normalized === 'audit' || normalized === 'enforce' ? normalized : null
}

export function runtimeModeFromStatus(status: StatusResponse): Mode | null {
  return normalizeRuntimeMode(status.components.proxyControl.details?.mode)
}
