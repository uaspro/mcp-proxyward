import {
  Activity,
  AlertTriangle,
  Ban,
  BookOpen,
  ChevronLeft,
  ChevronRight,
  Check,
  Code2,
  Download,
  Eye,
  FileCode2,
  GitBranch,
  List,
  Moon,
  Pause,
  Play,
  RefreshCw,
  Search,
  Settings,
  Shield,
  Sun,
  XCircle,
  type LucideIcon,
} from 'lucide-react'
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  buildAuditExportUrl,
  getAuditEvent,
  getAuditEvents,
  type AuditEventItem,
  type AuditEventPage,
  type AuditEventQuery,
} from './api/audit'
import { ApiError } from './api/client'
import {
  applySchemaDriftAction,
  getSchemaDriftDetail,
  getSchemaDrifts,
  type SchemaDriftAction,
  type SchemaDriftDetail,
  type SchemaDriftItem,
  type SchemaDriftPage,
  type SchemaDriftQuery,
  type SchemaDriftStatus,
} from './api/drift'
import { getOverview, type OverviewResponse, type OverviewTopRow } from './api/overview'
import {
  applyPolicy,
  getPolicy,
  getPolicyModeImpact,
  switchPolicyMode,
  validatePolicy,
  type PolicyApplyResponse,
  type PolicyModeImpactResponse,
  type PolicyModeSwitchResponse,
  type PolicyModel,
  type PolicyResponse,
  type PolicyValidationIssue,
  type PolicyValidationResponse,
  type ServerPolicyModel,
} from './api/policy'
import { getSettings, type SettingsResponse } from './api/settings'
import { getStatus, type ComponentReport, type StatusResponse } from './api/status'
import {
  Badge,
  BarChart,
  Button,
  Card,
  DataTable,
  Dialog,
  Drawer,
  IconButton,
  SegmentedControl,
  Sparkline,
  StatePanel,
  Tabs,
  Toggle,
} from './components'
import { dashboardConfig } from './config'

type RouteId = 'overview' | 'audit' | 'drift' | 'policy' | 'settings'
type Theme = 'light' | 'dark'
type Mode = 'audit' | 'enforce'
type DriftTab = 'diff' | 'before' | 'after' | 'history'

type NavItem = {
  id: RouteId
  label: string
  icon: LucideIcon
  badge?: string
  dot?: boolean
}

const navItems: NavItem[] = [
  { id: 'overview', label: 'Overview', icon: Activity },
  { id: 'audit', label: 'Audit log', icon: List, badge: '412k' },
  { id: 'drift', label: 'Schema drift', icon: GitBranch, dot: true },
  { id: 'policy', label: 'Policy', icon: FileCode2 },
  { id: 'settings', label: 'Settings', icon: Settings },
]

const storageKey = 'proxyward.dashboard.theme'

function readStoredTheme(): Theme {
  try {
    const stored = window.localStorage.getItem(storageKey)
    return stored === 'dark' || stored === 'light' ? stored : 'light'
  } catch {
    return 'light'
  }
}

function usePersistedTheme() {
  const [theme, setTheme] = useState<Theme>(() => readStoredTheme())

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme)
    try {
      window.localStorage.setItem(storageKey, theme)
    } catch {
      // Storage can be disabled in hardened browsers; theme still applies for this session.
    }
  }, [theme])

  return [theme, setTheme] as const
}

function App() {
  const [route, setRoute] = useState<RouteId>('overview')
  const [theme, setTheme] = usePersistedTheme()
  const [mode, setMode] = useState<Mode>('audit')
  const [pendingMode, setPendingMode] = useState<Mode | null>(null)
  const activeRoute = useMemo(
    () => navItems.find((item) => item.id === route) ?? navItems[0],
    [route],
  )

  return (
    <div className="app-shell">
      <aside className="sidebar" aria-label="Primary navigation">
        <div className="brand">
          <div className="brand-mark">PW</div>
          <div className="brand-copy">
            <div className="brand-name">ProxyWard</div>
            <div className="brand-meta">operator console</div>
          </div>
          <div className="brand-version">v0.4.2</div>
        </div>

        <nav className="nav">
          <div className="nav-section">Workspace</div>
          {navItems.map((item) => {
            const Icon = item.icon
            return (
              <button
                key={item.id}
                type="button"
                className={`nav-item ${route === item.id ? 'active' : ''}`}
                onClick={() => setRoute(item.id)}
              >
                <Icon size={16} />
                <span>{item.label}</span>
                {item.badge ? <Badge tone="neutral">{item.badge}</Badge> : null}
                {item.dot ? <span className="nav-dot" aria-hidden="true" /> : null}
              </button>
            )
          })}

          <div className="nav-section resources">Resources</div>
          <button type="button" className="nav-item">
            <BookOpen size={16} />
            <span>Docs</span>
          </button>
          <button type="button" className="nav-item">
            <Code2 size={16} />
            <span>API</span>
          </button>
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

          <button type="button" className="search-box">
            <Search size={14} />
            <span>Search audit, tools, policy</span>
            <kbd>Ctrl K</kbd>
          </button>

          <div className="topbar-actions">
            <button
              type="button"
              className={`mode-pill ${mode}`}
              onClick={() => setPendingMode(mode === 'audit' ? 'enforce' : 'audit')}
            >
              <span className="dot" aria-hidden="true" />
              {mode}
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
          {route === 'overview' ? <Overview mode={mode} /> : null}
          {route === 'audit' ? <AuditLog /> : null}
          {route === 'drift' ? <SchemaDrift /> : null}
          {route === 'policy' ? <Policy mode={mode} /> : null}
          {route === 'settings' ? <SettingsPanel /> : null}
        </main>
      </div>

      <ModeSwitchDialog
        currentMode={mode}
        targetMode={pendingMode}
        onClose={() => setPendingMode(null)}
        onSwitched={(response) => {
          setMode(response.mode === 'enforce' ? 'enforce' : 'audit')
          setPendingMode(null)
        }}
      />
    </div>
  )
}

function ModeSwitchDialog({
  currentMode,
  targetMode,
  onClose,
  onSwitched,
}: {
  currentMode: Mode
  targetMode: Mode | null
  onClose: () => void
  onSwitched: (response: PolicyModeSwitchResponse) => void
}) {
  const [impactState, setImpactState] = useState<{
    target: Mode | null
    impact: PolicyModeImpactResponse | null
    error: string | null
  }>({ target: null, impact: null, error: null })
  const [confirmation, setConfirmation] = useState<{
    target: Mode | null
    acknowledged: boolean
    typed: string
  }>({ target: null, acknowledged: false, typed: '' })
  const [switchLoading, setSwitchLoading] = useState(false)
  const [switchError, setSwitchError] = useState<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  const impact = impactState.target === targetMode ? impactState.impact : null
  const impactError = impactState.target === targetMode ? impactState.error : null
  const impactLoading = targetMode !== null && !impact && !impactError
  const acknowledged = confirmation.target === targetMode ? confirmation.acknowledged : false
  const typed = confirmation.target === targetMode ? confirmation.typed : ''
  const requiresTypedConfirmation = currentMode === 'audit' && targetMode === 'enforce'
  const canConfirm = Boolean(
    targetMode
      && impact
      && !switchLoading
      && (!requiresTypedConfirmation || (acknowledged && typed === 'ENFORCE')),
  )

  useEffect(() => {
    if (!targetMode) {
      return
    }

    const controller = new AbortController()
    getPolicyModeImpact(targetMode, controller.signal)
      .then((response) => {
        setImpactState({ target: targetMode, impact: response, error: null })
        setSwitchError(null)
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setImpactState({ target: targetMode, impact: null, error: formatApiFailure(ex, 'Mode impact request failed') })
        }
      })

    return () => controller.abort()
  }, [targetMode, reloadKey])

  if (!targetMode) {
    return null
  }

  function closeDialog() {
    setConfirmation({ target: null, acknowledged: false, typed: '' })
    setSwitchError(null)
    onClose()
  }

  function setAcknowledged(checked: boolean) {
    setConfirmation((current) => ({
      target: targetMode,
      acknowledged: checked,
      typed: current.target === targetMode ? current.typed : '',
    }))
  }

  function setTyped(value: string) {
    setConfirmation((current) => ({
      target: targetMode,
      acknowledged: current.target === targetMode ? current.acknowledged : false,
      typed: value,
    }))
  }

  async function confirmSwitch() {
    if (!targetMode || !impact) {
      return
    }

    setSwitchLoading(true)
    setSwitchError(null)
    try {
      const response = await switchPolicyMode({
        mode: targetMode,
        confirmationToken: impact.confirmationToken,
        impactFromUtc: impact.window.fromUtc,
        impactToUtc: impact.window.toUtc,
        requestedBy: 'dashboard',
        note: `dashboard mode switch to ${targetMode}`,
      })
      setConfirmation({ target: null, acknowledged: false, typed: '' })
      onSwitched(response)
    } catch (ex) {
      setSwitchError(formatApiFailure(ex, 'Mode switch failed'))
    } finally {
      setSwitchLoading(false)
    }
  }

  return (
    <Dialog
      open
      title={`Switch to ${targetMode} mode`}
      tone={targetMode === 'enforce' ? 'warn' : 'info'}
      onClose={closeDialog}
      footer={
        <>
          <Button variant="ghost" onClick={closeDialog} disabled={switchLoading}>
            Cancel
          </Button>
          <Button
            variant={targetMode === 'enforce' ? 'primary' : 'default'}
            onClick={confirmSwitch}
            disabled={!canConfirm}
          >
            {switchLoading ? 'Switching' : 'Confirm'}
          </Button>
        </>
      }
    >
      <div className="mode-shift">
        <span className={`mode-pill ${currentMode}`}>{currentMode}</span>
        <ChevronRight size={16} className="muted-icon" />
        <span className={`mode-pill ${targetMode}`}>{targetMode}</span>
      </div>
      {impactLoading ? <StatePanel state="loading" title="Loading impact preview" detail="management API" /> : null}
      {impactError ? (
        <StatePanel
          state="error"
          title="Impact preview unavailable"
          detail={impactError}
          onRetry={() => setReloadKey((current) => current + 1)}
        />
      ) : null}
      {switchError ? <StatePanel state="error" title="Mode switch failed" detail={switchError} /> : null}
      {impact ? <ModeImpactPreview impact={impact} /> : null}
      {requiresTypedConfirmation ? (
        <div className="confirmation-stack">
          <label className="dialog-check">
            <input
              type="checkbox"
              checked={acknowledged}
              onChange={(event) => setAcknowledged(event.target.checked)}
            />
            <span>I have reviewed the impact preview.</span>
          </label>
          <label className="dialog-field">
            <span>Type ENFORCE to confirm</span>
            <input
              className="dialog-input"
              value={typed}
              onChange={(event) => setTyped(event.target.value)}
              spellCheck={false}
              autoComplete="off"
            />
          </label>
        </div>
      ) : null}
    </Dialog>
  )
}

function ModeImpactPreview({ impact }: { impact: PolicyModeImpactResponse }) {
  return (
    <div className="impact-preview">
      <div className="impact-grid">
        <div>
          <span>would-block</span>
          <strong>{impact.wouldBlockCount.toLocaleString()}</strong>
        </div>
        <div>
          <span>pending drift</span>
          <strong>{impact.pendingDriftCount.toLocaleString()}</strong>
        </div>
        <div>
          <span>unapproved</span>
          <strong>{impact.unapprovedDriftCount.toLocaleString()}</strong>
        </div>
      </div>
      <div className="detail-kv">
        <dt>policy</dt>
        <dd className="mono">{impact.currentPolicyHash}</dd>
        <dt>window</dt>
        <dd>
          {formatAuditDateTime(impact.window.fromUtc)} to {formatAuditDateTime(impact.window.toUtc)}
        </dd>
        <dt>confirmation</dt>
        <dd>{impact.requiresConfirmation ? 'required' : 'not required'}</dd>
      </div>
      {impact.affected.length > 0 ? (
        <div className="impact-table-wrap">
          <table className="impact-table">
            <thead>
              <tr>
                <th>Server</th>
                <th>Tool</th>
                <th>Would-block</th>
                <th>Drift</th>
                <th>Reasons</th>
              </tr>
            </thead>
            <tbody>
              {impact.affected.slice(0, 5).map((item) => (
                <tr key={`${item.serverId}-${item.toolName ?? 'server'}`}>
                  <td className="mono">{item.serverId}</td>
                  <td className="mono">{item.toolName ?? '-'}</td>
                  <td>{item.wouldBlockCount.toLocaleString()}</td>
                  <td>{(item.pendingDriftCount + item.unapprovedDriftCount).toLocaleString()}</td>
                  <td>
                    <ReasonTags reasons={item.reasons} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <StatePanel state="empty" title="No affected tools in window" />
      )}
    </div>
  )
}

function Overview({ mode }: { mode: Mode }) {
  const {
    overview,
    status,
    loading,
    error,
    refreshedAt,
    refresh,
  } = useOverviewData()
  const [streamPaused, setStreamPaused] = useState(false)
  const [frozenStreamRows, setFrozenStreamRows] = useState<LiveRow[] | null>(null)
  const liveRows = useMemo(() => (overview ? createLiveRows(overview) : []), [overview])
  const streamRows = streamPaused ? (frozenStreamRows ?? liveRows) : liveRows
  const toggleStream = () => {
    if (streamPaused) {
      setFrozenStreamRows(null)
      setStreamPaused(false)
      return
    }

    setFrozenStreamRows(liveRows)
    setStreamPaused(true)
  }

  if (!overview && loading) {
    return (
      <section className="page">
        <PageHeader title="Overview" subtitle="Traffic, decisions, and runtime health" />
        <StatePanel state="loading" title="Loading overview" detail="management API" />
      </section>
    )
  }

  if (!overview) {
    return (
      <section className="page">
        <PageHeader title="Overview" subtitle="Traffic, decisions, and runtime health" action={<Button icon={RefreshCw} onClick={refresh}>Retry</Button>} />
        <StatePanel state="error" title="Overview unavailable" detail={error ?? dashboardConfig.apiBaseUrl} onRetry={refresh} />
      </section>
    )
  }

  const staleDetail = error ? `${error}. Showing last successful data.` : overview.metadata.notes
  const topReasonRows = toCompactRows(overview.topReasons)
  const topToolRows = toCompactRows(overview.topTools)

  return (
    <section className="page">
      <PageHeader
        title="Overview"
        subtitle="Traffic, decisions, and runtime health"
        action={
          <div className="row-actions">
            <Badge tone={mode === 'enforce' ? 'allow' : 'warn'}>{mode}</Badge>
            <Button icon={RefreshCw} onClick={refresh} disabled={loading}>
              Refresh
            </Button>
          </div>
        }
      />
      {staleDetail ? (
        <StatePanel
          state={error ? 'error' : 'empty'}
          title={error ? 'Stale data' : overview.metadata.partial ? 'Partial data' : 'Metadata'}
          detail={staleDetail}
          onRetry={error ? refresh : undefined}
        />
      ) : null}
      <div className="stats-grid">
        <StatCard label="Requests/sec" value={formatRate(overview.requestRate)} delta={formatAsOf(refreshedAt)} tone="good" />
        <StatCard label="Block rate" value={formatPercent(overview.blockRate)} delta={`would ${formatPercent(overview.wouldBlockRate)}`} tone="warn" />
        <StatCard label="p95 latency" value={formatLatency(overview.latencyP95Ms)} delta={overview.metadata.source} tone="info" />
        <StatCard label="Error rate" value={formatPercent(overview.errorRate)} delta={overview.metadata.partial ? 'partial' : 'complete'} tone="neutral" />
      </div>
      <div className="dashboard-grid">
        <Card title="Traffic" action={<Badge tone="neutral">60m</Badge>}>
          <BarChart values={overview.series.map((point) => point.total)} />
        </Card>
        <Card title="Health">
          <HealthRows status={status} />
        </Card>
      </div>
      <div className="dashboard-grid">
        <Card title="Top reasons">
          {topReasonRows.length > 0 ? <CompactList rows={topReasonRows} /> : <StatePanel state="empty" title="No reasons in window" />}
        </Card>
        <Card title="Top tools">
          {topToolRows.length > 0 ? <CompactList rows={topToolRows} /> : <StatePanel state="empty" title="No tools in window" />}
        </Card>
      </div>
      <Card
        title="Live stream"
        action={
          <Button icon={streamPaused ? Play : Pause} onClick={toggleStream}>
            {streamPaused ? 'Resume' : 'Pause'}
          </Button>
        }
      >
        <DataTable
          columns={['Time', 'Method', 'Decision', 'Count']}
          rows={streamRows.map((row) => [
            <span className="mono">{row.time}</span>,
            row.method,
            <DecisionBadge decision={row.decision} />,
            <span className="mono">{row.count}</span>,
          ])}
        />
        {streamRows.length === 0 ? <StatePanel state="empty" title="No live rows in window" /> : null}
      </Card>
    </section>
  )
}

function useOverviewData() {
  const [overview, setOverview] = useState<OverviewResponse | null>(null)
  const [status, setStatus] = useState<StatusResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [refreshedAt, setRefreshedAt] = useState<Date | null>(null)

  const refresh = useCallback(async () => {
    const controller = new AbortController()
    setLoading(true)
    try {
      const [overviewResponse, statusResponse] = await Promise.all([
        getOverview(controller.signal),
        getStatus(controller.signal),
      ])
      setOverview(overviewResponse)
      setStatus(statusResponse)
      setError(null)
      setRefreshedAt(new Date())
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : 'Overview request failed')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([getOverview(controller.signal), getStatus(controller.signal)])
      .then(([overviewResponse, statusResponse]) => {
        setOverview(overviewResponse)
        setStatus(statusResponse)
        setError(null)
        setRefreshedAt(new Date())
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setError(ex instanceof Error ? ex.message : 'Overview request failed')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      })

    return () => controller.abort()
  }, [])

  return {
    overview,
    status,
    loading,
    error,
    refreshedAt,
    refresh,
  }
}

type LiveRow = {
  time: string
  method: string
  decision: string
  count: string
}

function createLiveRows(overview: OverviewResponse): LiveRow[] {
  return overview.series
    .filter((point) => point.total > 0)
    .slice(-8)
    .reverse()
    .flatMap((point) => {
      const time = new Date(point.bucketStartUtc).toLocaleTimeString([], {
        hour: '2-digit',
        minute: '2-digit',
      })
      const rows: LiveRow[] = []
      if (point.block > 0) {
        rows.push({ time, method: 'tools/call', decision: 'block', count: String(point.block) })
      }
      if (point.wouldBlock > 0) {
        rows.push({ time, method: 'tools/call', decision: 'would_block', count: String(point.wouldBlock) })
      }
      if (point.warn > 0) {
        rows.push({ time, method: 'tools/list', decision: 'warn', count: String(point.warn) })
      }
      if (point.allow > 0) {
        rows.push({ time, method: 'tools/call', decision: 'allow', count: String(point.allow) })
      }
      return rows
    })
    .slice(0, 8)
}

function toCompactRows(rows: OverviewTopRow[]): [string, string, string][] {
  return rows.map((row) => [row.key, `${row.count.toLocaleString()} events`, 'top'])
}

function formatRate(value: number) {
  return value.toFixed(value >= 10 ? 1 : 2)
}

function formatPercent(value: number) {
  return `${(value * 100).toFixed(1)}%`
}

function formatLatency(value: number | null) {
  return value === null ? 'n/a' : `${value} ms`
}

function formatAsOf(value: Date | null) {
  if (!value) {
    return 'loading'
  }

  return value.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}

function getStatusRows(status: StatusResponse | null): { name: string; report: ComponentReport; icon: LucideIcon }[] {
  if (!status) {
    return [
      { name: 'Management API', report: { status: 'unknown', notes: null, details: null }, icon: Activity },
      { name: 'Proxy control', report: { status: 'unknown', notes: null, details: null }, icon: Shield },
      { name: 'Audit DB', report: { status: 'unknown', notes: null, details: null }, icon: FileCode2 },
      { name: 'Telemetry', report: { status: 'unknown', notes: null, details: null }, icon: AlertTriangle },
    ]
  }

  return [
    { name: 'Management API', report: status.components.managementApi, icon: Activity },
    { name: 'Proxy control', report: status.components.proxyControl, icon: Shield },
    { name: 'Audit DB', report: status.components.auditDb, icon: FileCode2 },
    { name: 'Telemetry', report: status.components.telemetry, icon: AlertTriangle },
  ]
}

function healthTone(status: string) {
  if (status === 'healthy') {
    return 'allow'
  }

  if (status === 'unhealthy') {
    return 'block'
  }

  return 'warn'
}

type AuditDecisionFilter = 'all' | 'allow' | 'would_block' | 'warn' | 'block'
type AuditTimeWindow = '15m' | '1h' | '24h' | '7d'

type AuditFilters = {
  search: string
  decision: AuditDecisionFilter
  serverId: string
  timeWindow: AuditTimeWindow
}

const auditPageSize = 25

const auditDecisionOptions: Array<{ value: AuditDecisionFilter; label: string }> = [
  { value: 'all', label: 'All' },
  { value: 'allow', label: 'Allow' },
  { value: 'would_block', label: 'Would block' },
  { value: 'warn', label: 'Warn' },
  { value: 'block', label: 'Block' },
]

const auditWindowOptions: Array<{ value: AuditTimeWindow; label: string }> = [
  { value: '15m', label: '15m' },
  { value: '1h', label: '1h' },
  { value: '24h', label: '24h' },
  { value: '7d', label: '7d' },
]

const auditWindowMs: Record<AuditTimeWindow, number> = {
  '15m': 15 * 60 * 1000,
  '1h': 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
}

const initialAuditFilters: AuditFilters = {
  search: '',
  decision: 'all',
  serverId: '',
  timeWindow: '1h',
}

function AuditLog() {
  const [filters, setFilters] = useState<AuditFilters>(initialAuditFilters)
  const [offset, setOffset] = useState(0)
  const [listQueryTime, setListQueryTime] = useState(() => Date.now())
  const [page, setPage] = useState<AuditEventPage | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [listError, setListError] = useState<string | null>(null)
  const [loadedAt, setLoadedAt] = useState<Date | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [selectedEvent, setSelectedEvent] = useState<AuditEventItem | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [detailReloadKey, setDetailReloadKey] = useState(0)
  const listQuery = useMemo(
    () => createAuditQuery(filters, offset, new Date(listQueryTime)),
    [filters, offset, listQueryTime],
  )
  const canPageBackward = page !== null && page.offset > 0
  const canPageForward = page !== null && page.offset + page.items.length < page.totalCount

  useEffect(() => {
    const controller = new AbortController()
    getAuditEvents(listQuery, controller.signal)
      .then((response) => {
        setPage(response)
        setListError(null)
        setLoadedAt(new Date())
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setListError(ex instanceof Error ? ex.message : 'Audit events request failed')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setListLoading(false)
        }
      })

    return () => controller.abort()
  }, [listQuery])

  useEffect(() => {
    if (selectedId === null) {
      return
    }

    const controller = new AbortController()
    getAuditEvent(selectedId, controller.signal)
      .then((response) => {
        setSelectedEvent(response)
        setDetailError(null)
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setDetailError(ex instanceof Error ? ex.message : 'Audit event detail request failed')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setDetailLoading(false)
        }
      })

    return () => controller.abort()
  }, [selectedId, detailReloadKey])

  function updateFilter<K extends keyof AuditFilters>(key: K, value: AuditFilters[K]) {
    setListLoading(true)
    setOffset(0)
    bumpListQueryTime()
    setFilters((current) => ({ ...current, [key]: value }))
  }

  function refresh() {
    setListLoading(true)
    bumpListQueryTime()
  }

  function openEvent(event: AuditEventItem) {
    setSelectedId(event.id)
    setSelectedEvent(event)
    setDetailError(null)
    setDetailLoading(true)
  }

  function retryDetail() {
    setDetailLoading(true)
    setDetailReloadKey((current) => current + 1)
  }

  function closeDetail() {
    setSelectedId(null)
    setSelectedEvent(null)
    setDetailError(null)
    setDetailLoading(false)
  }

  function goToPreviousPage() {
    if (!page) {
      return
    }

    setListLoading(true)
    setOffset(Math.max(0, page.offset - page.pageSize))
  }

  function goToNextPage() {
    if (!page) {
      return
    }

    setListLoading(true)
    setOffset(page.offset + page.pageSize)
  }

  function bumpListQueryTime() {
    setListQueryTime((current) => Math.max(Date.now(), current + 1))
  }

  function exportEvents() {
    const exportQuery = createAuditQuery(filters, 0, new Date())
    const link = document.createElement('a')
    link.href = buildAuditExportUrl(exportQuery)
    link.target = '_blank'
    link.rel = 'noreferrer'
    document.body.append(link)
    link.click()
    link.remove()
  }

  return (
    <section className="page">
      <PageHeader
        title="Audit log"
        subtitle={formatAuditSubtitle(page, loadedAt)}
        action={
          <div className="row-actions">
            <Button icon={Download} variant="ghost" onClick={exportEvents}>
              Export NDJSON
            </Button>
            <Button icon={RefreshCw} onClick={refresh} disabled={listLoading}>
              Refresh
            </Button>
          </div>
        }
      />
      {listError ? (
        <StatePanel
          state="error"
          title={page ? 'Stale audit data' : 'Audit events unavailable'}
          detail={page ? `${listError}. Showing last successful page.` : listError}
          onRetry={refresh}
        />
      ) : null}
      <Card
        title="Events"
        action={
          <Badge tone="neutral">
            {formatPageBadge(page)}
          </Badge>
        }
      >
        <AuditFilterBar filters={filters} onChange={updateFilter} />
        {!page && listLoading ? (
          <StatePanel state="loading" title="Loading audit events" detail="management API" />
        ) : null}
        {page ? (
          <>
            <AuditEventsTable
              events={page.items}
              selectedId={selectedId}
              loading={listLoading}
              onOpen={openEvent}
            />
            {page.items.length === 0 && !listLoading ? (
              <StatePanel state="empty" title="No audit events" detail="No rows match the active filters." />
            ) : null}
            <div className="pagination-row">
              <span>{formatPageRange(page)}</span>
              <div className="row-actions">
                <IconButton
                  label="Previous page"
                  icon={ChevronLeft}
                  disabled={!canPageBackward || listLoading}
                  onClick={goToPreviousPage}
                />
                <IconButton
                  label="Next page"
                  icon={ChevronRight}
                  disabled={!canPageForward || listLoading}
                  onClick={goToNextPage}
                />
              </div>
            </div>
          </>
        ) : null}
      </Card>
      <AuditEventDrawer
        event={selectedEvent}
        loading={detailLoading}
        error={detailError}
        open={selectedId !== null}
        onRetry={retryDetail}
        onClose={closeDetail}
      />
    </section>
  )
}

function AuditFilterBar({
  filters,
  onChange,
}: {
  filters: AuditFilters
  onChange: <K extends keyof AuditFilters>(key: K, value: AuditFilters[K]) => void
}) {
  return (
    <div className="filter-bar">
      <label className="search-input">
        <Search size={14} />
        <input
          type="search"
          placeholder="Search server, tool, reason, correlation id"
          value={filters.search}
          onChange={(event) => onChange('search', event.target.value)}
        />
      </label>
      <div className="filter-group">
        <span className="filter-label">decision</span>
        {auditDecisionOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            className={`filter-chip ${filters.decision === option.value ? 'active' : ''}`}
            onClick={() => onChange('decision', option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
      <label className="filter-input">
        <span>server</span>
        <input
          type="text"
          placeholder="all servers"
          value={filters.serverId}
          onChange={(event) => onChange('serverId', event.target.value)}
        />
      </label>
      <div className="filter-group time-window">
        <span className="filter-label">time</span>
        {auditWindowOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            className={`filter-chip ${filters.timeWindow === option.value ? 'active' : ''}`}
            onClick={() => onChange('timeWindow', option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  )
}

function AuditEventsTable({
  events,
  selectedId,
  loading,
  onOpen,
}: {
  events: AuditEventItem[]
  selectedId: number | null
  loading: boolean
  onOpen: (event: AuditEventItem) => void
}) {
  return (
    <div className={`audit-table-wrap ${loading ? 'loading' : ''}`}>
      <table className="audit-table">
        <thead>
          <tr>
            <th>Time</th>
            <th>Method</th>
            <th>Tool</th>
            <th>Server</th>
            <th>Decision</th>
            <th>Reasons</th>
            <th>Latency</th>
            <th>Correlation</th>
            <th aria-label="Actions" />
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr
              key={event.id}
              className={selectedId === event.id ? 'selected' : ''}
              onClick={() => onOpen(event)}
              onKeyDown={(keyboardEvent) => {
                if (keyboardEvent.key === 'Enter') {
                  onOpen(event)
                }
              }}
              tabIndex={0}
            >
              <td className="mono">{formatAuditTime(event.timestampUtc)}</td>
              <td className="mono">{event.method ?? '-'}</td>
              <td className="mono strong">{event.toolName ?? '-'}</td>
              <td className="mono">{event.serverId}</td>
              <td>
                <DecisionBadge decision={event.decision} />
              </td>
              <td>
                <ReasonTags reasons={event.reasons} />
              </td>
              <td className="mono">{formatDuration(event.durationMs)}</td>
              <td className="mono">{event.correlationId}</td>
              <td>
                <IconButton label={`Open audit event ${event.id}`} icon={Eye} onClick={() => onOpen(event)} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function AuditEventDrawer({
  event,
  loading,
  error,
  open,
  onRetry,
  onClose,
}: {
  event: AuditEventItem | null
  loading: boolean
  error: string | null
  open: boolean
  onRetry: () => void
  onClose: () => void
}) {
  return (
    <Drawer
      open={open}
      title={event?.toolName ?? event?.method ?? 'Audit event'}
      subtitle={event ? `${event.serverId} / ${formatAuditDateTime(event.timestampUtc)}` : undefined}
      onClose={onClose}
    >
      {loading && !event ? (
        <StatePanel state="loading" title="Loading audit event" detail="management API detail endpoint" />
      ) : null}
      {error ? (
        <StatePanel
          state="error"
          title={event ? 'Detail is stale' : 'Audit event unavailable'}
          detail={error}
          onRetry={onRetry}
        />
      ) : null}
      {event ? (
        <>
          <div className="detail-status-row">
            <DecisionBadge decision={event.decision} />
            <Badge tone="neutral">{event.mode}</Badge>
            <Badge tone="neutral">{event.eventType}</Badge>
          </div>
          <h4 className="section-heading">Event</h4>
          <dl className="detail-kv">
            <dt>id</dt>
            <dd>{event.id}</dd>
            <dt>timestamp</dt>
            <dd>{formatAuditDateTime(event.timestampUtc)}</dd>
            <dt>correlation</dt>
            <dd className="mono">{event.correlationId}</dd>
            <dt>server</dt>
            <dd className="mono">{event.serverId}</dd>
            <dt>method</dt>
            <dd className="mono">{event.method ?? '-'}</dd>
            <dt>tool</dt>
            <dd className="mono">{event.toolName ?? '-'}</dd>
            <dt>policy</dt>
            <dd className="mono">{event.policyVersion}</dd>
            <dt>request bytes</dt>
            <dd>{formatBytes(event.requestBytes)}</dd>
            <dt>duration</dt>
            <dd>{formatDuration(event.durationMs)}</dd>
          </dl>
          <h4 className="section-heading">Decision reasons</h4>
          {event.reasons.length > 0 ? (
            <div className="reason-card-list">
              {event.reasons.map((reason) => (
                <div className="reason-card" key={reason}>
                  <div className="reason-card-title">
                    <AlertTriangle size={14} />
                    <span className="mono">{reason}</span>
                  </div>
                  <div className="reason-card-detail">{describeReason(reason)}</div>
                </div>
              ))}
            </div>
          ) : (
            <StatePanel state="empty" title="No policy reasons" detail="The request was allowed without a blocking reason." />
          )}
          <h4 className="section-heading">Redacted argument summary</h4>
          <pre className="code-block">{formatJson(event.argumentSummary)}</pre>
          <h4 className="section-heading">Trace</h4>
          <div className="trace-list">
            <div>request received - 0 ms</div>
            <div>request inspection - bounded body parse</div>
            <div>policy decision - {event.decision}</div>
            <div>audit write - redacted summary persisted</div>
            <div>response completed - {formatDuration(event.durationMs)}</div>
          </div>
        </>
      ) : null}
    </Drawer>
  )
}

function createAuditQuery(filters: AuditFilters, offset: number, now: Date): AuditEventQuery {
  const toUtc = now.toISOString()
  const fromUtc = new Date(now.getTime() - auditWindowMs[filters.timeWindow]).toISOString()

  return {
    fromUtc,
    toUtc,
    decision: filters.decision === 'all' ? undefined : filters.decision,
    serverId: filters.serverId.trim() || undefined,
    search: filters.search.trim() || undefined,
    offset,
    pageSize: auditPageSize,
  }
}

function formatAuditSubtitle(page: AuditEventPage | null, loadedAt: Date | null): string {
  const count = page?.totalCount.toLocaleString() ?? '0'
  const suffix = loadedAt ? `updated ${formatAsOf(loadedAt)}` : 'redacted at write time'

  return `${count} events - ${suffix} - sqlite sink`
}

function formatPageRange(page: AuditEventPage): string {
  if (page.totalCount === 0) {
    return 'No rows'
  }

  const first = page.offset + 1
  const last = Math.min(page.offset + page.items.length, page.totalCount)

  return `${first.toLocaleString()}-${last.toLocaleString()} of ${page.totalCount.toLocaleString()}`
}

function formatPageBadge(page: AuditEventPage | null): string {
  if (!page || page.totalCount === 0) {
    return '0 / 0'
  }

  const first = page.offset + 1
  const last = Math.min(page.offset + page.items.length, page.totalCount)

  return `${first}-${last} / ${page.totalCount.toLocaleString()}`
}

function ReasonTags({ reasons }: { reasons: string[] }) {
  if (reasons.length === 0) {
    return <span className="empty-value">-</span>
  }

  const visibleReasons = reasons.slice(0, 3)
  const overflow = reasons.length - visibleReasons.length

  return (
    <div className="tag-list">
      {visibleReasons.map((reason) => (
        <Badge key={reason} tone="neutral">
          {reason}
        </Badge>
      ))}
      {overflow > 0 ? <Badge tone="neutral">+{overflow}</Badge> : null}
    </div>
  )
}

function formatAuditTime(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleTimeString([], {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

function formatAuditDateTime(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }

  return date.toLocaleString([], {
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

function formatDuration(value: number): string {
  return `${value.toLocaleString()} ms`
}

function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`
  }

  return `${(value / 1024).toFixed(1)} KB`
}

function formatJson(value: unknown): string {
  if (value === null || value === undefined) {
    return '{}'
  }

  try {
    return JSON.stringify(value, null, 2) ?? '{}'
  } catch {
    return String(value)
  }
}

function describeReason(reason: string): string {
  const explanations: Record<string, string> = {
    tool_blocked: 'This tool is denied by policy or absent from an allow list while default deny is active.',
    tool_not_allowed: 'The server tool policy does not allow this tool.',
    dangerous_command: 'A command-like argument matched a configured dangerous command rule.',
    private_network_target: 'A URL or host argument resolved to a blocked private or local network range.',
    path_traversal: 'A path argument contains traversal or escapes configured safe roots.',
    path_outside_allowed_roots: 'A path argument resolves outside the configured allowed roots.',
    host_not_allowed: 'A URL or host argument is not present in the configured allowlist.',
    tool_description_changed: 'The observed tool description differs from the approved schema baseline.',
    tool_schema_changed: 'The observed tool schema differs from the approved schema baseline.',
    mcp_protocol_changed: 'The observed MCP protocol value differs from the approved baseline.',
    inspection_unsupported: 'The request or response could not be inspected within configured limits.',
    secret_return_blocked: 'A tool response contained a configured secret pattern and was blocked.',
  }

  return explanations[reason] ?? 'Policy emitted this deterministic reason code for the event.'
}

type DriftStatusFilter = 'all' | 'pending' | 'approved' | 'rejected' | 'blocked'
type DriftTimeWindow = '24h' | '7d' | '30d' | 'all'

type DriftFilters = {
  status: DriftStatusFilter
  serverId: string
  toolName: string
  timeWindow: DriftTimeWindow
}

const driftPageSize = 20

const driftStatusOptions: Array<{ value: DriftStatusFilter; label: string }> = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'Pending' },
  { value: 'approved', label: 'Approved' },
  { value: 'rejected', label: 'Rejected' },
  { value: 'blocked', label: 'Blocked' },
]

const driftWindowOptions: Array<{ value: DriftTimeWindow; label: string }> = [
  { value: '24h', label: '24h' },
  { value: '7d', label: '7d' },
  { value: '30d', label: '30d' },
  { value: 'all', label: 'All time' },
]

const driftWindowMs: Record<Exclude<DriftTimeWindow, 'all'>, number> = {
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
  '30d': 30 * 24 * 60 * 60 * 1000,
}

const initialDriftFilters: DriftFilters = {
  status: 'pending',
  serverId: '',
  toolName: '',
  timeWindow: '7d',
}

function SchemaDrift() {
  const [filters, setFilters] = useState<DriftFilters>(initialDriftFilters)
  const [offset, setOffset] = useState(0)
  const [queryTime, setQueryTime] = useState(() => Date.now())
  const [page, setPage] = useState<SchemaDriftPage | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [listError, setListError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(null)
  const [selectedSummary, setSelectedSummary] = useState<SchemaDriftItem | null>(null)
  const [selectedDetail, setSelectedDetail] = useState<SchemaDriftDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [detailReloadKey, setDetailReloadKey] = useState(0)
  const [actionLoading, setActionLoading] = useState<SchemaDriftAction | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [tab, setTab] = useState<DriftTab>('diff')
  const query = useMemo(
    () => createSchemaDriftQuery(filters, offset, new Date(queryTime)),
    [filters, offset, queryTime],
  )
  const detailWindow = useMemo(
    () => ({ fromUtc: query.fromUtc, toUtc: query.toUtc }),
    [query.fromUtc, query.toUtc],
  )
  const canPageBackward = page !== null && page.offset > 0
  const canPageForward = page !== null && page.offset + page.items.length < page.totalCount

  useEffect(() => {
    const controller = new AbortController()
    getSchemaDrifts(query, controller.signal)
      .then((response) => {
        setPage(response)
        setListError(null)
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setListError(formatApiFailure(ex, 'Schema drift request failed'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setListLoading(false)
        }
      })

    return () => controller.abort()
  }, [query])

  useEffect(() => {
    if (selectedId === null) {
      return
    }

    const controller = new AbortController()
    getSchemaDriftDetail(selectedId, detailWindow, controller.signal)
      .then((response) => {
        setSelectedDetail(response)
        setSelectedSummary(response)
        setDetailError(null)
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setDetailError(formatApiFailure(ex, 'Schema drift detail request failed'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setDetailLoading(false)
        }
      })

    return () => controller.abort()
  }, [selectedId, detailWindow, detailReloadKey])

  function bumpQueryTime() {
    setQueryTime((current) => Math.max(Date.now(), current + 1))
  }

  function updateFilter<K extends keyof DriftFilters>(key: K, value: DriftFilters[K]) {
    setListLoading(true)
    setOffset(0)
    bumpQueryTime()
    setFilters((current) => ({ ...current, [key]: value }))
  }

  function refresh() {
    setListLoading(true)
    bumpQueryTime()
  }

  function selectDrift(item: SchemaDriftItem) {
    setSelectedId(item.id)
    setSelectedSummary(item)
    setSelectedDetail(null)
    setDetailError(null)
    setActionError(null)
    setDetailLoading(true)
    setTab('diff')
  }

  function retryDetail() {
    setDetailLoading(true)
    setDetailReloadKey((current) => current + 1)
  }

  function goToPreviousPage() {
    if (!page) {
      return
    }

    setListLoading(true)
    setOffset(Math.max(0, page.offset - page.pageSize))
  }

  function goToNextPage() {
    if (!page) {
      return
    }

    setListLoading(true)
    setOffset(page.offset + page.pageSize)
  }

  async function runAction(action: SchemaDriftAction) {
    if (selectedId === null) {
      return
    }

    setActionLoading(action)
    setActionError(null)
    try {
      const detail = await applySchemaDriftAction(selectedId, action, {
        reviewedBy: 'dashboard',
        reviewNote: `Reviewed from dashboard: ${action}`,
      })
      setSelectedDetail(detail)
      setSelectedSummary(detail)
      setPage((current) =>
        current
          ? {
              ...current,
              items: current.items.map((item) => (item.id === detail.id ? detail : item)),
            }
          : current,
      )
      setListLoading(true)
      bumpQueryTime()
    } catch (ex) {
      setActionError(formatApiFailure(ex, 'Schema drift action failed'))
    } finally {
      setActionLoading(null)
    }
  }

  return (
    <section className="page">
      <PageHeader
        title="Schema drift"
        subtitle="Review upstream tool definition changes before they become policy baseline"
        action={
          <div className="row-actions">
            <Button icon={RefreshCw} onClick={refresh} disabled={listLoading}>
              Refresh
            </Button>
          </div>
        }
      />
      {listError ? (
        <StatePanel
          state="error"
          title={page ? 'Stale drift queue' : 'Schema drift unavailable'}
          detail={page ? `${listError}. Showing last successful queue.` : listError}
          onRetry={refresh}
        />
      ) : null}
      <div className="drift-layout">
        <Card
          title="Review queue"
          action={<Badge tone={filters.status === 'pending' ? 'warn' : 'neutral'}>{page?.totalCount.toLocaleString() ?? '0'} items</Badge>}
        >
          <DriftFilterBar filters={filters} onChange={updateFilter} />
          {!page && listLoading ? (
            <StatePanel state="loading" title="Loading drift queue" detail="management API" />
          ) : null}
          {page ? (
            <>
              <DriftQueue
                items={page.items}
                selectedId={selectedId}
                loading={listLoading}
                onSelect={selectDrift}
              />
              {page.items.length === 0 && !listLoading ? (
                <StatePanel state="empty" title="No drift items" detail="No rows match the active filters." />
              ) : null}
              <div className="pagination-row">
                <span>{formatDriftPageRange(page)}</span>
                <div className="row-actions">
                  <IconButton
                    label="Previous drift page"
                    icon={ChevronLeft}
                    disabled={!canPageBackward || listLoading}
                    onClick={goToPreviousPage}
                  />
                  <IconButton
                    label="Next drift page"
                    icon={ChevronRight}
                    disabled={!canPageForward || listLoading}
                    onClick={goToNextPage}
                  />
                </div>
              </div>
            </>
          ) : null}
        </Card>
        <DriftDetailPane
          summary={selectedSummary}
          detail={selectedDetail}
          loading={detailLoading}
          error={detailError}
          actionError={actionError}
          actionLoading={actionLoading}
          tab={tab}
          onTabChange={setTab}
          onRetry={retryDetail}
          onAction={runAction}
        />
      </div>
    </section>
  )
}

function DriftFilterBar({
  filters,
  onChange,
}: {
  filters: DriftFilters
  onChange: <K extends keyof DriftFilters>(key: K, value: DriftFilters[K]) => void
}) {
  return (
    <div className="filter-bar">
      <div className="filter-group">
        <span className="filter-label">status</span>
        {driftStatusOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            className={`filter-chip ${filters.status === option.value ? 'active' : ''}`}
            onClick={() => onChange('status', option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
      <label className="filter-input">
        <span>server</span>
        <input
          type="text"
          placeholder="all"
          value={filters.serverId}
          onChange={(event) => onChange('serverId', event.target.value)}
        />
      </label>
      <label className="filter-input">
        <span>tool</span>
        <input
          type="text"
          placeholder="all"
          value={filters.toolName}
          onChange={(event) => onChange('toolName', event.target.value)}
        />
      </label>
      <div className="filter-group time-window">
        <span className="filter-label">time</span>
        {driftWindowOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            className={`filter-chip ${filters.timeWindow === option.value ? 'active' : ''}`}
            onClick={() => onChange('timeWindow', option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  )
}

function DriftQueue({
  items,
  selectedId,
  loading,
  onSelect,
}: {
  items: SchemaDriftItem[]
  selectedId: number | null
  loading: boolean
  onSelect: (item: SchemaDriftItem) => void
}) {
  return (
    <div className={`drift-list ${loading ? 'loading' : ''}`}>
      {items.map((item) => (
        <button
          key={item.id}
          type="button"
          className={`drift-list-item ${selectedId === item.id ? 'selected' : ''}`}
          onClick={() => onSelect(item)}
        >
          <div className="drift-item-topline">
            <span className="mono strong">{item.toolName}</span>
            <DriftStatusBadge status={item.status} />
          </div>
          <div className="drift-item-meta">
            <span>{item.serverId}</span>
            <span>{item.fieldName}</span>
            <span>
              v{item.fromVersion} to v{item.toVersion}
            </span>
          </div>
          <div className="drift-item-footer">
            <span>{formatAuditDateTime(item.detectedAtUtc)}</span>
            <span>{item.impactCount.toLocaleString()} related</span>
            <span>{item.diffMode}</span>
          </div>
        </button>
      ))}
    </div>
  )
}

function DriftDetailPane({
  summary,
  detail,
  loading,
  error,
  actionError,
  actionLoading,
  tab,
  onTabChange,
  onRetry,
  onAction,
}: {
  summary: SchemaDriftItem | null
  detail: SchemaDriftDetail | null
  loading: boolean
  error: string | null
  actionError: string | null
  actionLoading: SchemaDriftAction | null
  tab: DriftTab
  onTabChange: (tab: DriftTab) => void
  onRetry: () => void
  onAction: (action: SchemaDriftAction) => void
}) {
  const item = detail ?? summary

  return (
    <Card
      title={item?.toolName ?? 'Drift detail'}
      action={item ? <DriftStatusBadge status={item.status} /> : null}
    >
      {!item ? (
        <StatePanel state="empty" title="Select a drift item" detail="The detail pane opens from the review queue." />
      ) : null}
      {item ? (
        <>
          <div className="detail-kv drift-detail-kv">
            <dt>server</dt>
            <dd className="mono">{item.serverId}</dd>
            <dt>field</dt>
            <dd className="mono">{item.fieldName}</dd>
            <dt>schema-lock</dt>
            <dd>
              v{item.fromVersion} to v{item.toVersion}
            </dd>
            <dt>policy</dt>
            <dd className="mono">{item.policyVersion ?? '-'}</dd>
            <dt>impact</dt>
            <dd>{item.impactCount.toLocaleString()} related review events</dd>
            <dt>diff mode</dt>
            <dd>
              <Badge tone={item.hasDiffMetadata ? 'info' : 'neutral'}>{item.diffMode}</Badge>
            </dd>
          </div>
          {loading && !detail ? (
            <StatePanel state="loading" title="Loading drift detail" detail="management API detail endpoint" />
          ) : null}
          {error ? (
            <StatePanel
              state="error"
              title={detail ? 'Detail is stale' : 'Drift detail unavailable'}
              detail={error}
              onRetry={onRetry}
            />
          ) : null}
          {actionError ? (
            <StatePanel state="error" title="Review action failed" detail={actionError} />
          ) : null}
          <div className="tabs-row">
            <Tabs
              value={tab}
              onChange={onTabChange}
              options={[
                { value: 'diff', label: 'Diff' },
                { value: 'before', label: 'Before' },
                { value: 'after', label: 'After' },
                { value: 'history', label: 'History' },
              ]}
            />
          </div>
          {!error || detail ? <DriftTabContent detail={detail} summary={item} tab={tab} /> : null}
          <div className="action-bar">
            <span>
              {item.status === 'pending'
                ? 'Pending review'
                : `Reviewed as ${item.status}${item.reviewedBy ? ` by ${item.reviewedBy}` : ''}`}
            </span>
            <Button
              icon={XCircle}
              variant="ghost"
              disabled={actionLoading !== null}
              onClick={() => onAction('reject')}
            >
              Reject
            </Button>
            <Button
              icon={Ban}
              variant="danger"
              disabled={actionLoading !== null}
              onClick={() => onAction('block')}
            >
              Block
            </Button>
            <Button
              icon={Check}
              variant="primary"
              disabled={actionLoading !== null}
              onClick={() => onAction('approve')}
            >
              Approve
            </Button>
          </div>
        </>
      ) : null}
    </Card>
  )
}

function DriftTabContent({
  detail,
  summary,
  tab,
}: {
  detail: SchemaDriftDetail | null
  summary: SchemaDriftItem
  tab: DriftTab
}) {
  if (!detail) {
    return <StatePanel state="loading" title="Waiting for detail" detail="Diff data loads after item selection." />
  }

  if (tab === 'history') {
    return (
      <div className="detail-kv">
        <dt>detected</dt>
        <dd>{formatAuditDateTime(detail.detectedAtUtc)}</dd>
        <dt>reviewed</dt>
        <dd>{detail.reviewedAtUtc ? formatAuditDateTime(detail.reviewedAtUtc) : '-'}</dd>
        <dt>reviewed by</dt>
        <dd>{detail.reviewedBy ?? '-'}</dd>
        <dt>review note</dt>
        <dd>{detail.reviewNote ?? '-'}</dd>
        <dt>reasons</dt>
        <dd>
          <ReasonTags reasons={detail.reasons} />
        </dd>
      </div>
    )
  }

  if (detail.diff.mode !== 'metadata' || (!detail.diff.beforeJson && !detail.diff.afterJson)) {
    return <HashOnlyDiff detail={detail} />
  }

  if (tab === 'before') {
    return <pre className="code-block diff-code">{formatDiffJson(detail.diff.beforeJson)}</pre>
  }

  if (tab === 'after') {
    return <pre className="code-block diff-code">{formatDiffJson(detail.diff.afterJson)}</pre>
  }

  return <DiffBlock beforeJson={detail.diff.beforeJson} afterJson={detail.diff.afterJson} fieldName={summary.fieldName} />
}

function HashOnlyDiff({ detail }: { detail: SchemaDriftDetail }) {
  return (
    <div className="hash-fallback">
      <StatePanel
        state="empty"
        title="Hash-only diff"
        detail="Readable metadata is unavailable for this drift item."
      />
      <div className="detail-kv">
        <dt>before hash</dt>
        <dd className="mono">{detail.diff.beforeHash}</dd>
        <dt>after hash</dt>
        <dd className="mono">{detail.diff.afterHash}</dd>
        <dt>created</dt>
        <dd>{detail.diff.createdAtUtc ? formatAuditDateTime(detail.diff.createdAtUtc) : '-'}</dd>
        <dt>mode</dt>
        <dd>{detail.diff.mode}</dd>
      </div>
    </div>
  )
}

function DiffBlock({
  beforeJson,
  afterJson,
  fieldName,
}: {
  beforeJson: string | null
  afterJson: string | null
  fieldName: string
}) {
  const beforeLines = formatDiffJson(beforeJson).split('\n')
  const afterLines = formatDiffJson(afterJson).split('\n')

  return (
    <div className="diff-block" aria-label={`${fieldName} diff`}>
      {beforeLines.map((line, index) => (
        <div className="diff-line del" key={`before-${index}`}>
          <span className="num">{index + 1}</span>
          <span>- {line}</span>
        </div>
      ))}
      {afterLines.map((line, index) => (
        <div className="diff-line add" key={`after-${index}`}>
          <span className="num">{beforeLines.length + index + 1}</span>
          <span>+ {line}</span>
        </div>
      ))}
    </div>
  )
}

function DriftStatusBadge({ status }: { status: SchemaDriftStatus }) {
  const tone = status === 'approved' ? 'allow' : status === 'pending' ? 'warn' : status === 'blocked' ? 'block' : 'neutral'
  return <Badge tone={tone}>{status}</Badge>
}

function createSchemaDriftQuery(filters: DriftFilters, offset: number, now: Date): SchemaDriftQuery {
  if (filters.timeWindow === 'all') {
    return {
      status: filters.status === 'all' ? undefined : filters.status,
      serverId: filters.serverId.trim() || undefined,
      toolName: filters.toolName.trim() || undefined,
      offset,
      pageSize: driftPageSize,
    }
  }

  const toUtc = now.toISOString()
  const fromUtc = new Date(now.getTime() - driftWindowMs[filters.timeWindow]).toISOString()

  return {
    fromUtc,
    toUtc,
    status: filters.status === 'all' ? undefined : filters.status,
    serverId: filters.serverId.trim() || undefined,
    toolName: filters.toolName.trim() || undefined,
    offset,
    pageSize: driftPageSize,
  }
}

function formatDriftPageRange(page: SchemaDriftPage): string {
  if (page.totalCount === 0) {
    return 'No rows'
  }

  const first = page.offset + 1
  const last = Math.min(page.offset + page.items.length, page.totalCount)

  return `${first.toLocaleString()}-${last.toLocaleString()} of ${page.totalCount.toLocaleString()}`
}

function formatDiffJson(value: string | null): string {
  if (!value) {
    return '{}'
  }

  try {
    return JSON.stringify(JSON.parse(value), null, 2)
  } catch {
    return value
  }
}

function formatApiFailure(ex: unknown, fallback: string): string {
  if (ex instanceof ApiError && ex.status === 401) {
    return 'Unauthorized management write. Admin token is required.'
  }

  if (ex instanceof ApiError && ex.status) {
    return `${fallback} (${ex.status})`
  }

  return ex instanceof Error ? ex.message : fallback
}

function Policy({ mode }: { mode: Mode }) {
  const [policy, setPolicy] = useState<PolicyResponse | null>(null)
  const [draft, setDraft] = useState<PolicyModel | null>(null)
  const [selectedKey, setSelectedKey] = useState<string>('global')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [validation, setValidation] = useState<PolicyValidationResponse | null>(null)
  const [validationLoading, setValidationLoading] = useState(false)
  const [applyLoading, setApplyLoading] = useState(false)
  const [applyError, setApplyError] = useState<string | null>(null)
  const [applyResult, setApplyResult] = useState<PolicyApplyResponse | null>(null)

  const serverEntries = useMemo(() => Object.entries(draft?.servers ?? {}), [draft])
  const selectedServer = draft && selectedKey !== 'global' ? draft.servers[selectedKey] ?? null : null

  const applyLoadedPolicy = useCallback((response: PolicyResponse) => {
    const nextDraft = clonePolicyModel(response.model)
    setPolicy(response)
    setDraft(nextDraft)
    setDirty(false)
    setValidation(null)
    setApplyError(null)
    setApplyResult(null)
    setError(null)
    setSelectedKey((current) => {
      if (current === 'global' || nextDraft.servers[current]) {
        return current
      }

      return Object.keys(nextDraft.servers)[0] ?? 'global'
    })
  }, [])

  const loadPolicy = useCallback(() => {
    const controller = new AbortController()
    setLoading(true)
    getPolicy(controller.signal)
      .then(applyLoadedPolicy)
      .catch((ex: unknown) => {
        setError(formatApiFailure(ex, 'Policy request failed'))
      })
      .finally(() => {
        setLoading(false)
      })

    return () => controller.abort()
  }, [applyLoadedPolicy])

  useEffect(() => {
    const controller = new AbortController()
    getPolicy(controller.signal)
      .then(applyLoadedPolicy)
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setError(formatApiFailure(ex, 'Policy request failed'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      })

    return () => controller.abort()
  }, [applyLoadedPolicy])

  function markDirty() {
    setDirty(true)
    setApplyResult(null)
    setApplyError(null)
  }

  function updateDraft(updater: (current: PolicyModel) => PolicyModel) {
    markDirty()
    setDraft((current) => (current ? updater(current) : current))
  }

  function updateServer(serverId: string, updater: (server: ServerPolicyModel) => ServerPolicyModel) {
    updateDraft((current) => {
      const server = current.servers[serverId]
      if (!server) {
        return current
      }

      return {
        ...current,
        servers: {
          ...current.servers,
          [serverId]: updater(server),
        },
      }
    })
  }

  async function runValidation() {
    if (!draft) {
      return null
    }

    const clientErrors = findClientPolicyIssues(draft)
    if (clientErrors.length > 0) {
      const response = createClientValidationResponse(clientErrors)
      setValidation(response)
      return response
    }

    setValidationLoading(true)
    setApplyError(null)
    try {
      const response = await validatePolicy({
        model: draft,
        requestedBy: 'dashboard',
        note: 'dashboard validation',
      })
      setValidation(response)
      if (response.valid && response.normalizedModel) {
        setDraft(clonePolicyModel(response.normalizedModel))
      }

      return response
    } catch (ex) {
      setApplyError(formatApiFailure(ex, 'Policy validation failed'))
      return null
    } finally {
      setValidationLoading(false)
    }
  }

  async function runApply() {
    if (!draft) {
      return
    }

    setApplyLoading(true)
    setApplyError(null)
    try {
      const clientErrors = findClientPolicyIssues(draft)
      if (clientErrors.length > 0) {
        setValidation(createClientValidationResponse(clientErrors))
        return
      }

      const validationResponse = await validatePolicy({
        model: draft,
        requestedBy: 'dashboard',
        note: 'dashboard pre-apply validation',
      })
      setValidation(validationResponse)
      if (!validationResponse.valid) {
        return
      }

      const modelToApply = validationResponse.normalizedModel ?? draft
      const response = await applyPolicy({
        model: modelToApply,
        requestedBy: 'dashboard',
        note: 'dashboard apply',
      })
      setApplyResult(response)
      setDirty(false)
      setDraft(clonePolicyModel(modelToApply))
      setPolicy((current) =>
        current
          ? {
              ...current,
              policyHash: response.policyHash,
              model: clonePolicyModel(modelToApply),
              readOnly: {
                ...current.readOnly,
                policyHash: response.policyHash,
                serverCount: response.serverCount,
                loadedAtUtc: new Date().toISOString(),
              },
            }
          : current,
      )
    } catch (ex) {
      setApplyError(formatApiFailure(ex, 'Policy apply failed'))
    } finally {
      setApplyLoading(false)
    }
  }

  if (!draft && loading) {
    return (
      <section className="page">
        <PageHeader title="Policy" subtitle="Global mode and server rules" />
        <StatePanel state="loading" title="Loading policy" detail="management API" />
      </section>
    )
  }

  if (!draft) {
    return (
      <section className="page">
        <PageHeader
          title="Policy"
          subtitle="Global mode and server rules"
          action={<Button icon={RefreshCw} onClick={loadPolicy}>Retry</Button>}
        />
        <StatePanel state="error" title="Policy unavailable" detail={error ?? dashboardConfig.apiBaseUrl} onRetry={loadPolicy} />
      </section>
    )
  }

  return (
    <section className="page">
      <PageHeader
        title="Policy"
        subtitle={`${serverEntries.length.toLocaleString()} servers - ${policy?.policyHash ?? 'draft'} - ${dirty ? 'unsaved changes' : `loaded ${formatAuditTime(policy?.readOnly.loadedAtUtc ?? '')}`}`}
        action={
          <div className="row-actions">
            <Badge tone={dirty ? 'warn' : 'allow'}>{dirty ? 'dirty' : 'clean'}</Badge>
            <Button icon={RefreshCw} variant="ghost" onClick={loadPolicy} disabled={loading || validationLoading || applyLoading}>
              Reload
            </Button>
            <Button icon={Check} onClick={runValidation} disabled={validationLoading || applyLoading}>
              Validate
            </Button>
            <Button icon={Check} variant="primary" onClick={runApply} disabled={!dirty || validationLoading || applyLoading}>
              Apply
            </Button>
          </div>
        }
      />
      {error && policy ? (
        <StatePanel state="error" title="Stale policy" detail={`${error}. Showing last successful draft.`} onRetry={loadPolicy} />
      ) : null}
      {applyError ? <StatePanel state="error" title="Policy action failed" detail={applyError} /> : null}
      {applyResult ? (
        <StatePanel
          state="empty"
          title="Policy applied"
          detail={`hash ${applyResult.previousPolicyHash} to ${applyResult.policyHash}`}
        />
      ) : null}
      <PolicyIssueList validation={validation} loading={validationLoading} />
      <div className="policy-layout">
        <Card title="Policy scope" action={<Badge tone="neutral">{serverEntries.length} servers</Badge>}>
          <div className="policy-server-list">
            <button
              type="button"
              className={`policy-server-button ${selectedKey === 'global' ? 'selected' : ''}`}
              onClick={() => setSelectedKey('global')}
            >
              <Settings size={15} />
              <span>Global</span>
              <Badge tone={draft.mode === 'enforce' ? 'allow' : 'warn'}>{draft.mode || mode}</Badge>
            </button>
            {serverEntries.map(([serverId, server]) => (
              <button
                key={serverId}
                type="button"
                className={`policy-server-button ${selectedKey === serverId ? 'selected' : ''}`}
                onClick={() => setSelectedKey(serverId)}
              >
                <span className="server-initials">{serverId.slice(0, 2).toUpperCase()}</span>
                <span>{serverId}</span>
                <Badge tone={server.allowed ? 'allow' : 'block'}>{server.allowed ? 'on' : 'off'}</Badge>
              </button>
            ))}
          </div>
        </Card>
        <div className="policy-editor-stack">
          {selectedKey === 'global' ? (
            <GlobalPolicyEditor draft={draft} onChange={updateDraft} />
          ) : selectedServer ? (
            <ServerPolicyEditor server={selectedServer} onChange={(updater) => updateServer(selectedServer.id, updater)} />
          ) : (
            <StatePanel state="empty" title="Select a policy scope" />
          )}
        </div>
      </div>
    </section>
  )
}

function GlobalPolicyEditor({
  draft,
  onChange,
}: {
  draft: PolicyModel
  onChange: (updater: (current: PolicyModel) => PolicyModel) => void
}) {
  return (
    <>
      <Card title="Global">
        <div className="form-grid">
          <PolicyField label="mode">
            <SegmentedControl
              value={draft.mode}
              onChange={(value) => onChange((current) => ({ ...current, mode: value }))}
              options={[
                { value: 'audit', label: 'Audit' },
                { value: 'enforce', label: 'Enforce' },
              ]}
            />
          </PolicyField>
          <PolicyField label="maxBodyBytes">
            <input
              className="form-input"
              type="number"
              min={0}
              value={draft.inspection.maxBodyBytes}
              onChange={(event) =>
                onChange((current) => ({
                  ...current,
                  inspection: { ...current.inspection, maxBodyBytes: Number(event.target.value) },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="unsupportedStreaming">
            <SegmentedControl
              value={draft.inspection.unsupportedStreaming}
              onChange={(value) =>
                onChange((current) => ({
                  ...current,
                  inspection: { ...current.inspection, unsupportedStreaming: value },
                }))
              }
              options={[
                { value: 'passThrough', label: 'Pass' },
                { value: 'warn', label: 'Warn' },
                { value: 'block', label: 'Block' },
              ]}
            />
          </PolicyField>
          <PolicyField label="batchToolCalls">
            <strong className="mono">{draft.inspection.batchToolCalls}</strong>
          </PolicyField>
          <PolicyField label="audit.sink">
            <input
              className="form-input"
              value={draft.audit.sink}
              onChange={(event) =>
                onChange((current) => ({
                  ...current,
                  audit: { ...current.audit, sink: event.target.value },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="audit.sqlitePath">
            <input
              className="form-input"
              value={draft.audit.sqlitePath ?? ''}
              onChange={(event) =>
                onChange((current) => ({
                  ...current,
                  audit: { ...current.audit, sqlitePath: event.target.value || null },
                }))
              }
            />
          </PolicyField>
        </div>
      </Card>
      <Card title="Observability">
        <div className="form-grid">
          <PolicyField label="serviceName">
            <input
              className="form-input"
              value={draft.observability.serviceName}
              onChange={(event) =>
                onChange((current) => ({
                  ...current,
                  observability: { ...current.observability, serviceName: event.target.value },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="console">
            <Toggle
              checked={draft.observability.console.enabled}
              label="Enabled"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  observability: {
                    ...current.observability,
                    console: { enabled: checked },
                  },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="otlp">
            <Toggle
              checked={draft.observability.otlp.enabled}
              label="Enabled"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  observability: {
                    ...current.observability,
                    otlp: { ...current.observability.otlp, enabled: checked },
                  },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="otlp.endpoint">
            <input
              className="form-input"
              value={draft.observability.otlp.endpoint ?? ''}
              onChange={(event) =>
                onChange((current) => ({
                  ...current,
                  observability: {
                    ...current.observability,
                    otlp: { ...current.observability.otlp, endpoint: event.target.value || null },
                  },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="tracesRatio">
            <input
              className="form-input"
              type="number"
              min={0}
              max={1}
              step={0.01}
              value={draft.observability.sampling.tracesRatio}
              onChange={(event) =>
                onChange((current) => ({
                  ...current,
                  observability: {
                    ...current.observability,
                    sampling: { tracesRatio: Number(event.target.value) },
                  },
                }))
              }
            />
          </PolicyField>
        </div>
      </Card>
    </>
  )
}

function ServerPolicyEditor({
  server,
  onChange,
}: {
  server: ServerPolicyModel
  onChange: (updater: (server: ServerPolicyModel) => ServerPolicyModel) => void
}) {
  const secrets = server.secrets ?? { redactInLogs: true, blockReturn: false, patterns: [] }

  return (
    <>
      <Card title={server.id} action={<Badge tone={server.allowed ? 'allow' : 'block'}>{server.allowed ? 'allowed' : 'blocked'}</Badge>}>
        <div className="form-grid">
          <PolicyField label="route">
            <input
              className="form-input"
              value={server.route}
              onChange={(event) => onChange((current) => ({ ...current, route: event.target.value }))}
            />
          </PolicyField>
          <PolicyField label="upstream">
            <input
              className="form-input"
              value={server.upstream ?? ''}
              onChange={(event) => onChange((current) => ({ ...current, upstream: event.target.value || null }))}
            />
          </PolicyField>
          <PolicyField label="allowed">
            <Toggle
              checked={server.allowed}
              label={server.allowed ? 'Enabled' : 'Disabled'}
              onChange={(checked) => onChange((current) => ({ ...current, allowed: checked }))}
            />
          </PolicyField>
        </div>
      </Card>
      <Card title="Tools">
        <div className="form-grid">
          <PolicyField label="default">
            <SegmentedControl
              value={server.tools.default}
              onChange={(value) =>
                onChange((current) => ({
                  ...current,
                  tools: { ...current.tools, default: value },
                }))
              }
              options={[
                { value: 'allow', label: 'Allow' },
                { value: 'deny', label: 'Deny' },
              ]}
            />
          </PolicyField>
          <TextListEditor
            label="allow"
            values={server.tools.allow}
            onChange={(values) =>
              onChange((current) => ({
                ...current,
                tools: { ...current.tools, allow: values },
              }))
            }
          />
          <TextListEditor
            label="block"
            values={server.tools.block}
            onChange={(values) =>
              onChange((current) => ({
                ...current,
                tools: { ...current.tools, block: values },
              }))
            }
          />
        </div>
      </Card>
      <Card title="Arguments">
        <div className="form-grid">
          <TextListEditor
            label="paths.allowedRoots"
            values={server.arguments.paths.allowedRoots}
            onChange={(values) =>
              onChange((current) => ({
                ...current,
                arguments: {
                  ...current.arguments,
                  paths: { ...current.arguments.paths, allowedRoots: values },
                },
              }))
            }
          />
          <PolicyField label="paths.blockTraversal">
            <Toggle
              checked={server.arguments.paths.blockTraversal}
              label="Block traversal"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  arguments: {
                    ...current.arguments,
                    paths: { ...current.arguments.paths, blockTraversal: checked },
                  },
                }))
              }
            />
          </PolicyField>
          <TextListEditor
            label="hosts.allow"
            values={server.arguments.hosts.allow}
            onChange={(values) =>
              onChange((current) => ({
                ...current,
                arguments: {
                  ...current.arguments,
                  hosts: { ...current.arguments.hosts, allow: values },
                },
              }))
            }
          />
          <PolicyField label="hosts.blockPrivateNetworks">
            <Toggle
              checked={server.arguments.hosts.blockPrivateNetworks}
              label="Block private"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  arguments: {
                    ...current.arguments,
                    hosts: { ...current.arguments.hosts, blockPrivateNetworks: checked },
                  },
                }))
              }
            />
          </PolicyField>
          <TextListEditor
            label="commands.dangerous"
            values={server.arguments.commands.dangerous}
            onChange={(values) =>
              onChange((current) => ({
                ...current,
                arguments: {
                  ...current.arguments,
                  commands: { ...current.arguments.commands, dangerous: values },
                },
              }))
            }
          />
          <PolicyField label="commands.blockShell">
            <Toggle
              checked={server.arguments.commands.blockShell}
              label="Block shell"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  arguments: {
                    ...current.arguments,
                    commands: { ...current.arguments.commands, blockShell: checked },
                  },
                }))
              }
            />
          </PolicyField>
        </div>
      </Card>
      <Card title="Secrets">
        <div className="form-grid">
          <PolicyField label="redactInLogs">
            <Toggle
              checked={secrets.redactInLogs}
              label="Redact"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  secrets: { ...secrets, redactInLogs: checked },
                }))
              }
            />
          </PolicyField>
          <PolicyField label="blockReturn">
            <Toggle
              checked={secrets.blockReturn}
              label="Block return"
              onChange={(checked) =>
                onChange((current) => ({
                  ...current,
                  secrets: { ...secrets, blockReturn: checked },
                }))
              }
            />
          </PolicyField>
          <TextListEditor
            label="patterns"
            values={secrets.patterns}
            onChange={(values) =>
              onChange((current) => ({
                ...current,
                secrets: { ...secrets, patterns: values },
              }))
            }
          />
        </div>
      </Card>
      <Card title="Schema-lock">
        <StatePanel
          state="disabled"
          title="Drift review controls"
          detail="Schema-lock approvals are handled by the schema drift workflow."
        />
      </Card>
    </>
  )
}

function PolicyField({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="policy-field">
      <span>{label}</span>
      <div>{children}</div>
    </label>
  )
}

function TextListEditor({
  label,
  values,
  onChange,
}: {
  label: string
  values: string[]
  onChange: (values: string[]) => void
}) {
  return (
    <label className="policy-field vertical">
      <span>{label}</span>
      <textarea
        className="form-textarea"
        value={formatLines(values)}
        onChange={(event) => onChange(parseLines(event.target.value))}
      />
    </label>
  )
}

function PolicyIssueList({
  validation,
  loading,
}: {
  validation: PolicyValidationResponse | null
  loading: boolean
}) {
  if (loading) {
    return <StatePanel state="loading" title="Validating policy" detail="management API" />
  }

  if (!validation) {
    return null
  }

  if (validation.valid) {
    return (
      <StatePanel
        state="empty"
        title="Policy valid"
        detail={validation.policyHash ? `normalized hash ${validation.policyHash}` : undefined}
      />
    )
  }

  return (
    <div className="validation-list">
      {validation.errors.map((issue) => (
        <PolicyIssue key={`${issue.field}-${issue.code}-${issue.message}`} issue={issue} />
      ))}
      {validation.warnings.map((issue) => (
        <PolicyIssue key={`${issue.field}-${issue.code}-${issue.message}`} issue={issue} warning />
      ))}
    </div>
  )
}

function PolicyIssue({ issue, warning = false }: { issue: PolicyValidationIssue; warning?: boolean }) {
  return (
    <div className={`validation-issue ${warning ? 'warning' : 'error'}`}>
      <Badge tone={warning ? 'warn' : 'block'}>{issue.field}</Badge>
      <span className="mono">{issue.code}</span>
      <span>{issue.message}</span>
    </div>
  )
}

function clonePolicyModel(model: PolicyModel): PolicyModel {
  return JSON.parse(JSON.stringify(model)) as PolicyModel
}

function parseLines(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

function formatLines(values: string[]): string {
  return values.join('\n')
}

function findClientPolicyIssues(model: PolicyModel): PolicyValidationIssue[] {
  return Object.values(model.servers).flatMap((server) => {
    const issues: PolicyValidationIssue[] = []
    if (server.upstream?.includes('***@') || server.upstream?.includes('[masked]')) {
      issues.push({
        field: `servers.${server.id}.upstream`,
        code: 'masked_value_requires_replacement',
        message: 'Masked upstream credentials cannot be applied. Replace the upstream URL explicitly before applying.',
      })
    }

    return issues
  })
}

function createClientValidationResponse(errors: PolicyValidationIssue[]): PolicyValidationResponse {
  return {
    valid: false,
    errors,
    warnings: [],
    policyHash: null,
    normalizedModel: null,
  }
}

function SettingsPanel() {
  const [settings, setSettings] = useState<SettingsResponse | null>(null)
  const [status, setStatus] = useState<StatusResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [refreshedAt, setRefreshedAt] = useState<Date | null>(null)

  const refresh = useCallback(() => {
    const controller = new AbortController()
    setLoading(true)
    Promise.all([getSettings(controller.signal), getStatus(controller.signal)])
      .then(([settingsResponse, statusResponse]) => {
        setSettings(settingsResponse)
        setStatus(statusResponse)
        setError(null)
        setRefreshedAt(new Date())
      })
      .catch((ex: unknown) => {
        setError(formatApiFailure(ex, 'Settings request failed'))
      })
      .finally(() => {
        setLoading(false)
      })

    return () => controller.abort()
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    Promise.all([getSettings(controller.signal), getStatus(controller.signal)])
      .then(([settingsResponse, statusResponse]) => {
        setSettings(settingsResponse)
        setStatus(statusResponse)
        setError(null)
        setRefreshedAt(new Date())
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setError(formatApiFailure(ex, 'Settings request failed'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      })

    return () => controller.abort()
  }, [])

  if (!settings && loading) {
    return (
      <section className="page">
        <PageHeader title="Settings" subtitle="Service and dashboard runtime" />
        <StatePanel state="loading" title="Loading settings" detail="management API" />
      </section>
    )
  }

  if (!settings) {
    return (
      <section className="page">
        <PageHeader title="Settings" subtitle="Service and dashboard runtime" action={<Button icon={RefreshCw} onClick={refresh}>Retry</Button>} />
        <StatePanel state="error" title="Settings unavailable" detail={error ?? dashboardConfig.apiBaseUrl} onRetry={refresh} />
      </section>
    )
  }

  const degraded = status?.status && status.status !== 'healthy'

  return (
    <section className="page">
      <PageHeader
        title="Settings"
        subtitle={`Service and dashboard runtime - updated ${formatAsOf(refreshedAt)}`}
        action={<Button icon={RefreshCw} onClick={refresh} disabled={loading}>Refresh</Button>}
      />
      {error ? (
        <StatePanel state="error" title="Stale settings" detail={`${error}. Showing last successful data.`} onRetry={refresh} />
      ) : null}
      {degraded ? (
        <StatePanel state="error" title="Runtime degraded" detail={`status: ${status?.status}`} />
      ) : null}
      <div className="dashboard-grid">
        <Card title="Observability">
          <div className="settings-stack">
            <Toggle checked={settings.observability.consoleEnabled} onChange={() => undefined} label="Console exporter" disabled />
            <Toggle checked={settings.observability.otlpEnabled} onChange={() => undefined} label="OTLP exporter" disabled />
            <Toggle checked={settings.observability.applicationInsightsEnabled} onChange={() => undefined} label="Application Insights" disabled />
          </div>
          <div className="kv-grid">
            <span>serviceName</span>
            <strong className="mono truncate">{settings.observability.serviceName}</strong>
            <span>otlpEndpoint</span>
            <strong className="mono truncate">{settings.observability.otlpEndpoint ?? '-'}</strong>
            <span>tracesRatio</span>
            <strong className="mono">{settings.observability.tracesRatio}</strong>
            <span>appInsightsEnv</span>
            <strong className="mono truncate">{settings.observability.applicationInsightsConnectionStringEnv}</strong>
          </div>
        </Card>
        <Card title="Runtime health">
          <HealthRows status={status} />
        </Card>
      </div>
      <div className="dashboard-grid">
        <Card title="Audit sink">
          <div className="kv-grid">
            <span>sink</span>
            <strong className="mono">{settings.audit.sink}</strong>
            <span>sqlitePath</span>
            <strong className="mono truncate">{settings.audit.sqlitePath ?? '-'}</strong>
            <span>state</span>
            <Badge tone={healthTone(status?.components.auditDb.status ?? 'unknown')}>{status?.components.auditDb.status ?? 'unknown'}</Badge>
          </div>
        </Card>
        <Card title="Inspection limits">
          <div className="kv-grid">
            <span>maxBodyBytes</span>
            <strong className="mono">{formatBytes(settings.inspection.maxBodyBytes)}</strong>
            <span>unsupportedStreaming</span>
            <strong className="mono">{settings.inspection.unsupportedStreaming}</strong>
            <span>batchToolCalls</span>
            <strong className="mono">{settings.inspection.batchToolCalls}</strong>
          </div>
        </Card>
      </div>
      <div className="dashboard-grid">
        <Card title="Service">
          <div className="kv-grid">
            <span>policyHash</span>
            <strong className="mono truncate">{settings.service.policyHash}</strong>
            <span>sourcePath</span>
            <strong className="mono truncate">{settings.service.sourcePath}</strong>
            <span>serverCount</span>
            <strong className="mono">{settings.service.serverCount}</strong>
            <span>sourceSize</span>
            <strong className="mono">{settings.service.sourceSizeBytes ? formatBytes(settings.service.sourceSizeBytes) : '-'}</strong>
          </div>
        </Card>
        <Card title="Runtime">
          <div className="kv-grid">
            <span>baseUrl</span>
            <strong className="mono truncate">{dashboardConfig.apiBaseUrl}</strong>
            <span>settingsWritable</span>
            <Badge tone={settings.runtime.settingsWritable ? 'allow' : 'neutral'}>{settings.runtime.settingsWritable ? 'yes' : 'read-only'}</Badge>
            <span>editing</span>
            <StatePanel state="disabled" title={settings.runtime.editingSupported ? 'Runtime editing enabled' : 'Runtime editing disabled'} />
          </div>
          <Sparkline values={[12, 14, 13, 16, 15, 19, 17, 22, 21, 24, 20, 26]} />
        </Card>
      </div>
    </section>
  )
}

function PageHeader({
  title,
  subtitle,
  action,
}: {
  title: string
  subtitle: string
  action?: ReactNode
}) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        <p>{subtitle}</p>
      </div>
      {action}
    </div>
  )
}

function StatCard({
  label,
  value,
  delta,
  tone,
}: {
  label: string
  value: string
  delta: string
  tone: 'good' | 'warn' | 'info' | 'neutral'
}) {
  return (
    <div className="stat-card">
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      <div className={`stat-delta ${tone}`}>{delta}</div>
    </div>
  )
}

function DecisionBadge({ decision }: { decision: string }) {
  const tone = decision === 'block' ? 'block' : decision === 'allow' ? 'allow' : 'warn'
  return <Badge tone={tone}>{decision.replace('_', ' ')}</Badge>
}

function HealthRows({ status = null }: { status?: StatusResponse | null }) {
  const rows = getStatusRows(status)

  return (
    <div className="health-list">
      {rows.map(({ name, report, icon: Icon }) => (
        <div className="health-row" key={name}>
          <Icon size={16} />
          <span>{name}</span>
          <Badge tone={healthTone(report.status)}>{report.status}</Badge>
        </div>
      ))}
    </div>
  )
}

function CompactList({ rows }: { rows: [string, string, string][] }) {
  return (
    <div className="compact-list">
      {rows.map(([primary, secondary, status]) => (
        <div className="compact-row" key={`${primary}-${secondary}`}>
          <div>
            <div className="compact-primary">{primary}</div>
            <div className="compact-secondary">{secondary}</div>
          </div>
          <Badge tone="neutral">{status}</Badge>
        </div>
      ))}
    </div>
  )
}

export default App
