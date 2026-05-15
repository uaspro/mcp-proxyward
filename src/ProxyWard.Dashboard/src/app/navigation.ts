import { Activity, FileCode2, GitBranch, List, Server, type LucideIcon } from 'lucide-react'

export type RouteId = 'overview' | 'audit' | 'drift' | 'policy' | 'settings'

export type NavItem = {
  id: RouteId
  label: string
  icon: LucideIcon
}

export const navItems: NavItem[] = [
  { id: 'overview', label: 'Overview', icon: Activity },
  { id: 'audit', label: 'Audit log', icon: List },
  { id: 'drift', label: 'Schema drift', icon: GitBranch },
  { id: 'policy', label: 'Policy', icon: FileCode2 },
  { id: 'settings', label: 'System', icon: Server },
]

export const routePathById: Record<RouteId, string> = {
  overview: '/',
  audit: '/audit',
  drift: '/schema-drift',
  policy: '/policy',
  settings: '/system',
}

const routeIdByPath: Record<string, RouteId> = {
  '/': 'overview',
  '/overview': 'overview',
  '/audit': 'audit',
  '/audit-log': 'audit',
  '/schema-drift': 'drift',
  '/drift': 'drift',
  '/policy': 'policy',
  '/system': 'settings',
  '/settings': 'settings',
}

export function normalizeRoutePath(pathname: string): string {
  const path = pathname.toLowerCase().replace(/\/+$/, '')
  return path === '' ? '/' : path
}

export function routeFromLocation(): RouteId {
  if (typeof window === 'undefined') {
    return 'overview'
  }

  return routeIdByPath[normalizeRoutePath(window.location.pathname)] ?? 'overview'
}

export function currentRoutePath(): string {
  return `${window.location.pathname}${window.location.search}`
}

export function buildAuditEventRoute(eventId: number): string {
  return `${routePathById.audit}?event=${encodeURIComponent(String(eventId))}`
}

export function readAuditEventRouteId(): number | null {
  if (typeof window === 'undefined') {
    return null
  }

  const raw = new URLSearchParams(window.location.search).get('event')
  if (!raw) {
    return null
  }

  const id = Number(raw)
  return Number.isSafeInteger(id) && id > 0 ? id : null
}

export function writeAuditEventRoute(eventId: number | null, mode: 'push' | 'replace' = 'push'): void {
  const nextPath = eventId === null ? routePathById.audit : buildAuditEventRoute(eventId)
  if (currentRoutePath() === nextPath) {
    return
  }

  const state = eventId === null ? { route: 'audit' } : { route: 'audit', eventId }
  if (mode === 'replace') {
    window.history.replaceState(state, '', nextPath)
    return
  }

  window.history.pushState(state, '', nextPath)
}
