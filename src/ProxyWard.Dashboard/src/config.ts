const defaultApiBaseUrl = 'http://localhost:8081'

function normalizeApiBaseUrl(value: string | undefined): string {
  const trimmed = value?.trim()
  if (!trimmed) {
    return defaultApiBaseUrl
  }

  return trimmed.replace(/\/+$/, '')
}

function normalizeOptional(value: string | undefined): string | null {
  const trimmed = value?.trim()
  return trimmed && trimmed.length > 0 ? trimmed : null
}

export const dashboardConfig = {
  apiBaseUrl: normalizeApiBaseUrl(import.meta.env.VITE_PROXYWARD_API_BASE_URL),
  adminToken: normalizeOptional(import.meta.env.VITE_PROXYWARD_ADMIN_TOKEN),
} as const
