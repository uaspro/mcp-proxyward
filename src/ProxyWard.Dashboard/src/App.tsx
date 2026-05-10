import { ChevronRight, Moon, Search, Sun } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { getPolicy, type PolicyModeSwitchResponse } from './api/policy'
import { getStatus } from './api/status'
import {
  buildAuditEventRoute,
  currentRoutePath,
  navItems,
  routeFromLocation,
  routePathById,
  type RouteId,
} from './app/navigation'
import { usePersistedTheme } from './app/theme'
import { formatNavCount, useNavMetrics } from './app/useNavMetrics'
import { Badge, IconButton } from './components'
import { AuditLog } from './features/audit/AuditLog'
import { SchemaDrift } from './features/drift/SchemaDrift'
import { Overview } from './features/overview/Overview'
import { ModeSwitchDialog } from './features/policy/ModeDialogs'
import { Policy } from './features/policy/Policy'
import { SettingsPanel } from './features/settings/SettingsPanel'
import { normalizeRuntimeMode, runtimeModeFromStatus, type Mode } from './shared/runtime'

function App() {
  const [route, setRoute] = useState<RouteId>(() => routeFromLocation())
  const [theme, setTheme] = usePersistedTheme()
  const [mode, setMode] = useState<Mode>('audit')
  const [modeRefreshing, setModeRefreshing] = useState(true)
  const [pendingMode, setPendingMode] = useState<Mode | null>(null)
  const [topbarSearch, setTopbarSearch] = useState('')
  const searchInputRef = useRef<HTMLInputElement>(null)
  const navMetrics = useNavMetrics()
  const activeRoute = useMemo(
    () => navItems.find((item) => item.id === route) ?? navItems[0],
    [route],
  )
  const readRuntimeMode = useCallback(async (signal?: AbortSignal) => {
    const status = await getStatus(signal)
    return runtimeModeFromStatus(status)
  }, [])
  const navigateToRoute = useCallback((nextRoute: RouteId) => {
    setRoute(nextRoute)

    const nextPath = routePathById[nextRoute]
    if (currentRoutePath() !== nextPath) {
      window.history.pushState({ route: nextRoute }, '', nextPath)
    }
  }, [])
  const navigateToAuditEvent = useCallback((eventId: number) => {
    setRoute('audit')

    const nextPath = buildAuditEventRoute(eventId)
    if (currentRoutePath() !== nextPath) {
      window.history.pushState({ route: 'audit', eventId }, '', nextPath)
    }
  }, [])

  const submitTopbarSearch = useCallback(() => {
    if (topbarSearch.trim() && route !== 'audit') {
      navigateToRoute('audit')
    }
  }, [navigateToRoute, route, topbarSearch])

  useEffect(() => {
    const handlePopState = () => {
      setRoute(routeFromLocation())
    }

    window.addEventListener('popstate', handlePopState)
    return () => window.removeEventListener('popstate', handlePopState)
  }, [])

  useEffect(() => {
    const handleKeyboardShortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        searchInputRef.current?.focus()
      }
    }

    window.addEventListener('keydown', handleKeyboardShortcut)
    return () => window.removeEventListener('keydown', handleKeyboardShortcut)
  }, [])

  useEffect(() => {
    let mounted = true
    const controller = new AbortController()

    readRuntimeMode(controller.signal)
      .then((nextMode) => {
        if (mounted && nextMode) {
          setMode(nextMode)
        }
      })
      .catch(async () => {
        try {
          const policy = await getPolicy(controller.signal)
          const nextMode = normalizeRuntimeMode(policy.model.mode)
          if (mounted && nextMode) {
            setMode(nextMode)
          }
        } catch {
          return
        }
      })
      .finally(() => {
        if (mounted) {
          setModeRefreshing(false)
        }
      })

    return () => {
      mounted = false
      controller.abort()
    }
  }, [readRuntimeMode])

  const openModeSwitchDialog = useCallback(() => {
    setModeRefreshing(true)
    readRuntimeMode()
      .then((freshMode) => {
        const currentMode = freshMode ?? mode
        if (freshMode) {
          setMode(freshMode)
        }

        setPendingMode(currentMode === 'audit' ? 'enforce' : 'audit')
      })
      .catch(() => {
        setPendingMode(mode === 'audit' ? 'enforce' : 'audit')
      })
      .finally(() => setModeRefreshing(false))
  }, [mode, readRuntimeMode])

  const handleModeSwitched = useCallback((response: PolicyModeSwitchResponse) => {
    setMode(normalizeRuntimeMode(response.mode) ?? mode)
    setPendingMode(null)
  }, [mode])

  return (
    <div className="app-shell">
      <aside className="sidebar" aria-label="Primary navigation">
        <div className="brand">
          <div className="brand-mark">PW</div>
          <div className="brand-copy">
            <div className="brand-name">ProxyWard</div>
            <div className="brand-meta">operator console</div>
          </div>
          <div className="brand-version">v0.0.1</div>
        </div>

        <nav className="nav">
          <div className="nav-section">Workspace</div>
          {navItems.map((item) => {
            const Icon = item.icon
            const badge = item.id === 'audit' ? formatNavCount(navMetrics.auditTotal) : null
            const showDriftDot = item.id === 'drift' && (navMetrics.pendingDriftTotal ?? 0) > 0

            return (
              <button
                key={item.id}
                type="button"
                className={`nav-item ${route === item.id ? 'active' : ''}`}
                onClick={() => navigateToRoute(item.id)}
              >
                <Icon size={16} />
                <span>{item.label}</span>
                {badge ? <Badge tone="neutral">{badge}</Badge> : null}
                {showDriftDot ? <span className="nav-dot" aria-label={`${navMetrics.pendingDriftTotal} pending schema drifts`} /> : null}
              </button>
            )
          })}
        </nav>

        <div className="sidebar-footer">
          <span className="status-pulse" aria-hidden="true" />
          <span>healthy</span>
          <span className="footer-port">:8080</span>
        </div>
      </aside>

      <div className="main">
        <header className="topbar">
          <div className="breadcrumb">
            <span>Workspace</span>
            <ChevronRight size={13} className="muted-icon" />
            <span className="breadcrumb-current">{activeRoute.label}</span>
          </div>

          <div className="topbar-spacer" />

          <form
            className="search-box"
            role="search"
            onSubmit={(event) => {
              event.preventDefault()
              submitTopbarSearch()
            }}
          >
            <Search size={14} />
            <input
              ref={searchInputRef}
              type="search"
              aria-label="Search audit, tools, policy"
              placeholder="Search audit, tools, policy"
              value={topbarSearch}
              onChange={(event) => setTopbarSearch(event.target.value)}
            />
            <kbd>Ctrl K</kbd>
          </form>

          <div className="topbar-actions">
            <button
              type="button"
              className={`mode-pill ${mode}`}
              onClick={openModeSwitchDialog}
              disabled={modeRefreshing}
            >
              <span className="dot" aria-hidden="true" />
              {modeRefreshing ? 'syncing' : mode}
            </button>
            <IconButton
              label="Toggle theme"
              icon={theme === 'dark' ? Sun : Moon}
              onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
            />
            <div className="avatar" aria-label="Signed in administrator">
              SE
            </div>
          </div>
        </header>

        <main className="content">
          {route === 'overview' ? (
            <Overview mode={mode} onOpenAuditEvent={navigateToAuditEvent} onOpenAuditLog={() => navigateToRoute('audit')} />
          ) : null}
          {route === 'audit' ? <AuditLog key={topbarSearch} searchQuery={topbarSearch} /> : null}
          {route === 'drift' ? <SchemaDrift /> : null}
          {route === 'policy' ? <Policy mode={mode} onModeChanged={setMode} searchQuery={topbarSearch} /> : null}
          {route === 'settings' ? <SettingsPanel /> : null}
        </main>
      </div>

      <ModeSwitchDialog
        currentMode={mode}
        targetMode={pendingMode}
        onClose={() => setPendingMode(null)}
        onSwitched={handleModeSwitched}
      />
    </div>
  )
}

export default App
