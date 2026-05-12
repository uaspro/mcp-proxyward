import { useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight, RefreshCw } from 'lucide-react'
import { applySchemaDriftAction, getSchemaDriftDetail, getSchemaDriftFilterOptions, getSchemaDrifts, type SchemaDriftAction, type SchemaDriftDetail, type SchemaDriftFilterOptions, type SchemaDriftItem, type SchemaDriftPage } from '../../api/drift'
import { Badge, Button, Card, IconButton, StatePanel, Tabs } from '../../components'
import { PageHeader } from '../../components/dashboard'
import { formatApiFailure, formatAuditDateTime } from '../../shared/formatters'
import { ReviewDecisionPanel } from './SchemaDriftDecisionPanel'
import { DriftTabContent } from './SchemaDriftDiff'
import { DriftFilterBar } from './SchemaDriftFilters'
import { DriftStatusBadge } from './SchemaDriftStatusBadge'
import { createSchemaDriftQuery, emptyFilterOptions, formatDecisionStatusMessage, formatDriftPageRange, formatFieldLabel, initialDriftFilters, type DriftFilters, type DriftTab } from './SchemaDriftView'
import './SchemaDrift.css'

export function SchemaDrift() {
  const [filters, setFilters] = useState<DriftFilters>(initialDriftFilters)
  const [offset, setOffset] = useState(0)
  const [queryTime, setQueryTime] = useState(() => Date.now())
  const [page, setPage] = useState<SchemaDriftPage | null>(null)
  const [listLoading, setListLoading] = useState(true)
  const [listError, setListError] = useState<string | null>(null)
  const [filterOptions, setFilterOptions] = useState<SchemaDriftFilterOptions>(emptyFilterOptions)
  const [filterOptionsLoading, setFilterOptionsLoading] = useState(true)
  const [filterOptionsError, setFilterOptionsError] = useState<string | null>(null)
  const [filterOptionsReloadKey, setFilterOptionsReloadKey] = useState(0)
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
    getSchemaDriftFilterOptions(controller.signal)
      .then((response) => {
        setFilterOptions(response)
        setFilterOptionsError(null)
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setFilterOptionsError(formatApiFailure(ex, 'Schema drift filters unavailable'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setFilterOptionsLoading(false)
        }
      })

    return () => controller.abort()
  }, [filterOptionsReloadKey])

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
    setFilterOptionsLoading(true)
    setFilterOptionsReloadKey((current) => current + 1)
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
        subtitle="Review changed tool definitions and decide whether this version may run"
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
          title="Drift queue"
          action={<Badge tone={filters.status === 'pending' ? 'warn' : 'neutral'}>{page?.totalCount.toLocaleString() ?? '0'} items</Badge>}
        >
          <DriftFilterBar
            filters={filters}
            filterOptions={filterOptions}
            filterOptionsLoading={filterOptionsLoading}
            filterOptionsError={filterOptionsError}
            onChange={updateFilter}
          />
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
          <div className="drift-item-server">
            <span className="drift-server-name mono">{item.serverId}</span>
          </div>
          <div className="drift-item-topline">
            <span className="mono strong">{item.toolName}</span>
            <DriftStatusBadge status={item.status} />
          </div>
          <div className="drift-item-meta">
            <span>{formatFieldLabel(item.fieldName)}</span>
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
          <div className="drift-review-summary">
            <div className="min-w-0">
              <div className="summary-label">Changed {formatFieldLabel(item.fieldName)}</div>
              <div className="summary-title">
                v{item.fromVersion} to v{item.toVersion}
              </div>
              <div className="summary-detail">
                {formatDecisionStatusMessage(item)}
              </div>
            </div>
            <div className="summary-meta">
              <span className="mono">{item.serverId}</span>
              <span>{formatAuditDateTime(item.detectedAtUtc)}</span>
              <span>{item.impactCount.toLocaleString()} related</span>
            </div>
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
                { value: 'diff', label: 'Change' },
                { value: 'before', label: 'Previous' },
                { value: 'after', label: 'Current' },
                { value: 'history', label: 'History' },
              ]}
            />
          </div>
          {!error || detail ? <DriftTabContent detail={detail} summary={item} tab={tab} /> : null}
          <ReviewDecisionPanel
            item={item}
            actionLoading={actionLoading}
            onAction={onAction}
          />
        </>
      ) : null}
    </Card>
  )
}
