import { useCallback, useEffect, useState } from 'react'
import { List, Pause, Play, RefreshCw } from 'lucide-react'
import { getAuditEvents, type AuditEventItem } from '../../api/audit'
import { getOverview, type OverviewResponse, type OverviewTopRow } from '../../api/overview'
import { getStatus, type StatusResponse } from '../../api/status'
import { Badge, BarChart, Button, Card, StatePanel } from '../../components'
import { CompactList, DecisionBadge, HealthRows, PageHeader, StatCard } from '../../components/dashboard'
import { dashboardConfig } from '../../config'
import { ReasonTags } from '../../shared/ReasonTags'
import { formatAuditOperation, formatAuditSubject, formatAuditTime, formatDuration } from '../../shared/formatters'
import type { Mode } from '../../shared/runtime'

const overviewPollingIntervalMs = 3000
type OverviewProps = {
  mode: Mode
  onOpenAuditEvent: (eventId: number) => void
  onOpenAuditLog: () => void
}

export function Overview({ mode, onOpenAuditEvent, onOpenAuditLog }: OverviewProps) {
  const {
    overview,
    status,
    loading,
    error,
    refreshedAt,
    refresh,
  } = useOverviewData()
  const [streamPaused, setStreamPaused] = useState(false)
  const latestAudit = useLatestAuditEvents(streamPaused)
  const toggleStream = () => setStreamPaused((current) => !current)

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
        title="Latest audit events"
        action={
          <div className="row-actions">
            <Badge tone={streamPaused ? 'warn' : 'info'}>
              {streamPaused ? 'paused' : `live ${formatAsOf(latestAudit.refreshedAt)}`}
            </Badge>
            <Button icon={streamPaused ? Play : Pause} onClick={toggleStream}>
              {streamPaused ? 'Resume' : 'Pause'}
            </Button>
            <Button icon={List} variant="ghost" onClick={onOpenAuditLog}>
              View all
            </Button>
          </div>
        }
      >
        {latestAudit.error ? (
          <StatePanel
            state="error"
            title={latestAudit.events.length > 0 ? 'Live audit is stale' : 'Live audit unavailable'}
            detail={latestAudit.error}
            onRetry={latestAudit.refresh}
          />
        ) : null}
        {!streamPaused && latestAudit.loading && latestAudit.events.length === 0 ? (
          <StatePanel state="loading" title="Loading latest audit events" detail="management API" />
        ) : null}
        {latestAudit.events.length > 0 ? (
          <LatestAuditEventsTable
            events={latestAudit.events}
            loading={latestAudit.loading && !streamPaused}
            onOpen={(event) => onOpenAuditEvent(event.id)}
          />
        ) : null}
        {!latestAudit.loading && latestAudit.events.length === 0 && !latestAudit.error ? (
          <StatePanel state="empty" title="No audit events" detail="Audit events will appear here as the proxy writes them." />
        ) : null}
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
    let disposed = false
    let activeController: AbortController | null = null

    const loadSnapshot = () => {
      activeController?.abort()
      const controller = new AbortController()
      activeController = controller

      void Promise.all([getOverview(controller.signal), getStatus(controller.signal)])
        .then(([overviewResponse, statusResponse]) => {
          if (disposed || controller.signal.aborted) {
            return
          }

          setOverview(overviewResponse)
          setStatus(statusResponse)
          setError(null)
          setRefreshedAt(new Date())
        })
        .catch((ex: unknown) => {
          if (!disposed && !controller.signal.aborted) {
            setError(ex instanceof Error ? ex.message : 'Overview request failed')
          }
        })
        .finally(() => {
          if (!disposed && !controller.signal.aborted) {
            setLoading(false)
          }
        })
    }

    loadSnapshot()
    const timer = window.setInterval(loadSnapshot, overviewPollingIntervalMs)

    return () => {
      disposed = true
      activeController?.abort()
      window.clearInterval(timer)
    }
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

function useLatestAuditEvents(paused: boolean) {
  const [events, setEvents] = useState<AuditEventItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [refreshedAt, setRefreshedAt] = useState<Date | null>(null)

  const refresh = useCallback(() => {
    const controller = new AbortController()
    setLoading(true)
    void getAuditEvents({ offset: 0, pageSize: 8 }, controller.signal)
      .then((response) => {
        setEvents(response.items)
        setError(null)
        setRefreshedAt(new Date())
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setError(ex instanceof Error ? ex.message : 'Latest audit events request failed')
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      })
  }, [])

  useEffect(() => {
    if (paused) {
      return
    }

    let disposed = false
    let activeController: AbortController | null = null

    const load = (showLoading: boolean) => {
      activeController?.abort()
      const controller = new AbortController()
      activeController = controller

      if (showLoading) {
        setLoading(true)
      }

      void getAuditEvents({ offset: 0, pageSize: 8 }, controller.signal)
        .then((response) => {
          if (disposed || controller.signal.aborted) {
            return
          }

          setEvents(response.items)
          setError(null)
          setRefreshedAt(new Date())
        })
        .catch((ex: unknown) => {
          if (!disposed && !controller.signal.aborted) {
            setError(ex instanceof Error ? ex.message : 'Latest audit events request failed')
          }
        })
        .finally(() => {
          if (!disposed && !controller.signal.aborted) {
            setLoading(false)
          }
        })
    }

    load(events.length === 0)
    const timer = window.setInterval(() => load(false), overviewPollingIntervalMs)

    return () => {
      disposed = true
      activeController?.abort()
      window.clearInterval(timer)
    }
  }, [events.length, paused])

  return {
    events,
    loading,
    error,
    refreshedAt,
    refresh,
  }
}

function LatestAuditEventsTable({
  events,
  loading,
  onOpen,
}: {
  events: AuditEventItem[]
  loading: boolean
  onOpen: (event: AuditEventItem) => void
}) {
  return (
    <div className={`audit-table-wrap ${loading ? 'loading' : ''}`}>
      <table className="audit-table latest-audit-table">
        <thead>
          <tr>
            <th>Time</th>
            <th>Operation</th>
            <th>Subject</th>
            <th>Server</th>
            <th>Decision</th>
            <th>Reasons</th>
            <th>Latency</th>
          </tr>
        </thead>
        <tbody>
          {events.map((event) => (
            <tr
              key={event.id}
              onClick={() => onOpen(event)}
              onKeyDown={(keyboardEvent) => {
                if (keyboardEvent.key === 'Enter') {
                  onOpen(event)
                }
              }}
              tabIndex={0}
            >
              <td className="mono">{formatAuditTime(event.timestampUtc)}</td>
              <td className="mono">{formatAuditOperation(event)}</td>
              <td className="mono strong">{formatAuditSubject(event)}</td>
              <td className="mono">{event.serverId}</td>
              <td>
                <DecisionBadge decision={event.decision} />
              </td>
              <td>
                <ReasonTags reasons={event.reasons} />
              </td>
              <td className="mono">{formatDuration(event.durationMs)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
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
