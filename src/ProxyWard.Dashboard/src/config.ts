const defaultApiBaseUrl = 'http://localhost:8081'
const defaultProxyBaseUrl = 'http://127.0.0.1:8080'

function normalizeBaseUrl(value: string | undefined, fallback: string): string {
  const trimmed = value?.trim()
  if (!trimmed) {
    return fallback
  }

  return trimmed.replace(/\/+$/, '')
}

function normalizeOptional(value: string | undefined): string | null {
  const trimmed = value?.trim()
  return trimmed && trimmed.length > 0 ? trimmed : null
}

export const dashboardConfig = {
  apiBaseUrl: normalizeBaseUrl(import.meta.env.VITE_PROXYWARD_API_BASE_URL, defaultApiBaseUrl),
  proxyBaseUrl: normalizeBaseUrl(import.meta.env.VITE_PROXYWARD_PROXY_BASE_URL, defaultProxyBaseUrl),
  adminToken: normalizeOptional(import.meta.env.VITE_PROXYWARD_ADMIN_TOKEN),
} as const
