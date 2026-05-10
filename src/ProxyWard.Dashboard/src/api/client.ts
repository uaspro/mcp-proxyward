import { dashboardConfig } from '../config'

export class ApiError extends Error {
  readonly status?: number

  constructor(message: string, status?: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

export function buildApiUrl(path: string): string {
  const baseUrl = dashboardConfig.apiBaseUrl.replace(/\/+$/, '')
  const normalizedPath = path.startsWith('/') ? path : `/${path}`

  return `${baseUrl}${normalizedPath}`
}

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(buildApiUrl(path), {
    method: 'GET',
    headers: {
      Accept: 'application/json',
    },
    signal,
  })

  if (!response.ok) {
    throw new ApiError(await readErrorMessage(response, `GET ${path} failed`), response.status)
  }

  return (await response.json()) as T
}

export async function postJson<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  return sendJson<T>('POST', path, body, signal)
}

export async function putJson<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  return sendJson<T>('PUT', path, body, signal)
}

export async function patchJson<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  return sendJson<T>('PATCH', path, body, signal)
}

async function sendJson<T>(
  method: 'POST' | 'PUT' | 'PATCH',
  path: string,
  body: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const headers: Record<string, string> = {
    Accept: 'application/json',
    'Content-Type': 'application/json',
  }

  if (dashboardConfig.adminToken) {
    headers.Authorization = `Bearer ${dashboardConfig.adminToken}`
  }

  const response = await fetch(buildApiUrl(path), {
    method,
    headers,
    body: JSON.stringify(body ?? {}),
    signal,
  })

  if (!response.ok) {
    throw new ApiError(await readErrorMessage(response, `${method} ${path} failed`), response.status)
  }

  return (await response.json()) as T
}

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  try {
    const payload = await response.clone().json()
    if (payload && typeof payload.message === 'string' && payload.message.trim()) {
      return payload.message
    }

    if (payload && typeof payload.detail === 'string' && payload.detail.trim()) {
      return payload.detail
    }

    if (payload && typeof payload.error === 'string' && payload.error.trim()) {
      return payload.error
    }
  } catch {
    // Keep the original request-oriented fallback for non-JSON error bodies.
  }

  return fallback
}
