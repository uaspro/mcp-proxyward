import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, ChevronLeft, ChevronRight, Download, Eye, RefreshCw, Search } from 'lucide-react'
import { buildAuditExportUrl, getAuditEvent, getAuditEvents, type AuditEventItem, type AuditEventPage, type AuditEventQuery } from '../../api/audit'
import { readAuditEventRouteId, writeAuditEventRoute } from '../../app/navigation'
import { Badge, Button, Card, Drawer, IconButton, StatePanel } from '../../components'
import { DecisionBadge, PageHeader } from '../../components/dashboard'
import { ReasonTags } from '../../shared/ReasonTags'
import { describeReason, formatAsOf, formatAuditDateTime, formatAuditTime, formatBytes, formatDuration, formatJson } from '../../shared/formatters'
type AuditDecisionFilter = 'all' | 'allow' | 'would_block' | 'warn' | 'block'
type AuditTimeWindow = '15m' | '1h' | '24h' | '7d' | 'all'

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
  { value: 'all', label: 'All' },
]

const auditWindowMs: Record<Exclude<AuditTimeWindow, 'all'>, number> = {
  '15m': 15 * 60 * 1000,
  '1h': 60 * 60 * 1000,
  '24h': 24 * 60 * 60 * 1000,
  '7d': 7 * 24 * 60 * 60 * 1000,
}

const initialAuditFilters: AuditFilters = {
  search: '',
  decision: 'all',
  serverId: '',
  timeWindow: 'all',
}

export function AuditLog({ searchQuery = '' }: { searchQuery?: string }) {
  const [filters, setFilters] = useState<AuditFilters>(() => ({
    ...initialAuditFilters,
    search: searchQuery.trim(),
  }))
  const [offset, setOffset] = useState(0)
  const [listQueryTime, setListQueryTime] = useState(() => Date.now())
  const [page, setPage] = useState<AuditEventPage | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [listError, setListError] = useState<string | null>(null)
  const [loadedAt, setLoadedAt] = useState<Date | null>(null)
  const [selectedId, setSelectedId] = useState<number | null>(() => readAuditEventRouteId())
  const [selectedEvent, setSelectedEvent] = useState<AuditEventItem | null>(null)
  const [detailLoading, setDetailLoading] = useState(() => readAuditEventRouteId() !== null)
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
    const handlePopState = () => {
      const routeId = readAuditEventRouteId()
      setSelectedId(routeId)
      setSelectedEvent(null)
      setDetailError(null)
      setDetailLoading(routeId !== null)
    }

    window.addEventListener('popstate', handlePopState)
    return () => window.removeEventListener('popstate', handlePopState)
  }, [])

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
    writeAuditEventRoute(event.id)
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
    writeAuditEventRoute(null, 'replace')
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
  const toUtc = filters.timeWindow === 'all' ? undefined : now.toISOString()
  const fromUtc = filters.timeWindow === 'all'
    ? undefined
    : new Date(now.getTime() - auditWindowMs[filters.timeWindow]).toISOString()

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
