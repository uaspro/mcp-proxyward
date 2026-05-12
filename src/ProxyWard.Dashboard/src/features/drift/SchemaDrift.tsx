import { useEffect, useMemo, useState } from 'react'
import { Ban, Check, ChevronLeft, ChevronRight, RefreshCw, type LucideIcon } from 'lucide-react'
import { applySchemaDriftAction, getSchemaDriftDetail, getSchemaDriftFilterOptions, getSchemaDrifts, type SchemaDriftAction, type SchemaDriftDetail, type SchemaDriftFilterOption, type SchemaDriftFilterOptions, type SchemaDriftItem, type SchemaDriftPage, type SchemaDriftQuery, type SchemaDriftStatus } from '../../api/drift'
import { Badge, Button, Card, IconButton, StatePanel, Tabs } from '../../components'
import { PageHeader } from '../../components/dashboard'
import { ReasonTags } from '../../shared/ReasonTags'
import { formatApiFailure, formatAuditDateTime } from '../../shared/formatters'

type DriftTab = 'diff' | 'before' | 'after' | 'history'
type DriftStatusFilter = 'all' | 'pending' | 'approved' | 'blocked'
type DriftTimeWindow = '24h' | '7d' | '30d' | 'all'
type DiffLineKind = 'equal' | 'add' | 'del'
type DiffLineRow = {
  kind: DiffLineKind
  beforeLine: number | null
  afterLine: number | null
  text: string
}

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
  { value: 'blocked', label: 'Blocked' },
]

const emptyFilterOptions: SchemaDriftFilterOptions = {
  servers: [],
  tools: [],
}

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

function DriftFilterBar({
  filters,
  filterOptions,
  filterOptionsLoading,
  filterOptionsError,
  onChange,
}: {
  filters: DriftFilters
  filterOptions: SchemaDriftFilterOptions
  filterOptionsLoading: boolean
  filterOptionsError: string | null
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
      <FilterValueInput
        id="schema-drift-server-filter-options"
        label="server"
        value={filters.serverId}
        options={filterOptions.servers}
        loading={filterOptionsLoading}
        onChange={(value) => onChange('serverId', value)}
      />
      <FilterValueInput
        id="schema-drift-tool-filter-options"
        label="tool"
        value={filters.toolName}
        options={filterOptions.tools}
        loading={filterOptionsLoading}
        onChange={(value) => onChange('toolName', value)}
      />
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
      {filterOptionsError ? <span className="filter-error">{filterOptionsError}</span> : null}
    </div>
  )
}

function FilterValueInput({
  id,
  label,
  value,
  options,
  loading,
  onChange,
}: {
  id: string
  label: string
  value: string
  options: SchemaDriftFilterOption[]
  loading: boolean
  onChange: (value: string) => void
}) {
  return (
    <label className="filter-input">
      <span>{label}</span>
      <input
        type="search"
        list={id}
        placeholder={loading ? 'loading' : 'all'}
        value={value}
        autoComplete="off"
        onChange={(event) => onChange(event.target.value)}
      />
      <datalist id={id}>
        {options.map((option) => (
          <option key={option.value} value={option.value} label={`${option.count.toLocaleString()} items`} />
        ))}
      </datalist>
    </label>
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

function ReviewDecisionPanel({
  item,
  actionLoading,
  onAction,
}: {
  item: SchemaDriftItem
  actionLoading: SchemaDriftAction | null
  onAction: (action: SchemaDriftAction) => void
}) {
  const disabled = actionLoading !== null

  return (
    <div className="review-decision-panel">
      <div className="review-decision-copy">
        <div className="review-decision-title">
          {item.status === 'pending' ? 'Choose a decision' : `Decision: ${item.status}`}
        </div>
        <div className="review-decision-detail">
          {formatReviewDecisionDetail(item)}
        </div>
      </div>
      <div className="decision-action-grid">
        <DecisionActionButton
          action="approve"
          icon={Check}
          title="Approve"
          detail="Allow this tool version in enforce mode."
          disabled={disabled || item.status === 'approved'}
          active={item.status === 'approved'}
          onAction={onAction}
        />
        <DecisionActionButton
          action="block"
          icon={Ban}
          title="Block"
          detail="Remove only this tool from discovery and mark unsafe."
          disabled={disabled || item.status === 'blocked'}
          active={item.status === 'blocked'}
          onAction={onAction}
        />
      </div>
    </div>
  )
}

function DecisionActionButton({
  action,
  icon: Icon,
  title,
  detail,
  disabled,
  active,
  onAction,
}: {
  action: SchemaDriftAction
  icon: LucideIcon
  title: string
  detail: string
  disabled: boolean
  active: boolean
  onAction: (action: SchemaDriftAction) => void
}) {
  return (
    <button
      type="button"
      className={`decision-action ${action} ${active ? 'active' : ''}`}
      disabled={disabled}
      onClick={() => onAction(action)}
    >
      <Icon size={16} />
      <span className="decision-action-label">{title}</span>
      <span className="decision-action-detail">{active ? 'Current decision' : detail}</span>
    </button>
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
        <dt>status</dt>
        <dd>
          <DriftStatusBadge status={detail.status} />
        </dd>
        <dt>detected</dt>
        <dd>{formatAuditDateTime(detail.detectedAtUtc)}</dd>
        <dt>reviewed</dt>
        <dd>{detail.reviewedAtUtc ? formatAuditDateTime(detail.reviewedAtUtc) : '-'}</dd>
        <dt>reviewed by</dt>
        <dd>{detail.reviewedBy ?? '-'}</dd>
        <dt>review note</dt>
        <dd>{detail.reviewNote ?? '-'}</dd>
        <dt>policy</dt>
        <dd className="mono">{detail.policyVersion ?? '-'}</dd>
        <dt>diff mode</dt>
        <dd>
          <Badge tone={detail.hasDiffMetadata ? 'info' : 'neutral'}>{detail.diffMode}</Badge>
        </dd>
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
  const rows = createLineDiff(beforeLines, afterLines)

  return (
    <div className="diff-block" aria-label={`${fieldName} diff`}>
      {rows.map((row, index) => (
        <div className={`diff-line ${row.kind}`} key={`${row.kind}-${row.beforeLine ?? 'x'}-${row.afterLine ?? 'x'}-${index}`}>
          <span className="num">{row.beforeLine ?? ''}</span>
          <span className="num">{row.afterLine ?? ''}</span>
          <span className="mark">{formatDiffMarker(row.kind)}</span>
          <span className="text">{row.text}</span>
        </div>
      ))}
    </div>
  )
}

function createLineDiff(beforeLines: string[], afterLines: string[]): DiffLineRow[] {
  const lengths = Array.from({ length: beforeLines.length + 1 }, () => Array(afterLines.length + 1).fill(0))

  for (let beforeIndex = beforeLines.length - 1; beforeIndex >= 0; beforeIndex -= 1) {
    for (let afterIndex = afterLines.length - 1; afterIndex >= 0; afterIndex -= 1) {
      lengths[beforeIndex][afterIndex] = beforeLines[beforeIndex] === afterLines[afterIndex]
        ? lengths[beforeIndex + 1][afterIndex + 1] + 1
        : Math.max(lengths[beforeIndex + 1][afterIndex], lengths[beforeIndex][afterIndex + 1])
    }
  }

  const rows: DiffLineRow[] = []
  let beforeIndex = 0
  let afterIndex = 0
  let beforeLine = 1
  let afterLine = 1

  while (beforeIndex < beforeLines.length && afterIndex < afterLines.length) {
    if (beforeLines[beforeIndex] === afterLines[afterIndex]) {
      rows.push({
        kind: 'equal',
        beforeLine,
        afterLine,
        text: beforeLines[beforeIndex],
      })
      beforeIndex += 1
      afterIndex += 1
      beforeLine += 1
      afterLine += 1
      continue
    }

    if (lengths[beforeIndex + 1][afterIndex] >= lengths[beforeIndex][afterIndex + 1]) {
      rows.push({
        kind: 'del',
        beforeLine,
        afterLine: null,
        text: beforeLines[beforeIndex],
      })
      beforeIndex += 1
      beforeLine += 1
    } else {
      rows.push({
        kind: 'add',
        beforeLine: null,
        afterLine,
        text: afterLines[afterIndex],
      })
      afterIndex += 1
      afterLine += 1
    }
  }

  while (beforeIndex < beforeLines.length) {
    rows.push({
      kind: 'del',
      beforeLine,
      afterLine: null,
      text: beforeLines[beforeIndex],
    })
    beforeIndex += 1
    beforeLine += 1
  }

  while (afterIndex < afterLines.length) {
    rows.push({
      kind: 'add',
      beforeLine: null,
      afterLine,
      text: afterLines[afterIndex],
    })
    afterIndex += 1
    afterLine += 1
  }

  return rows
}

function formatDiffMarker(kind: DiffLineKind): string {
  switch (kind) {
    case 'add':
      return '+'
    case 'del':
      return '-'
    default:
      return ' '
  }
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

function formatFieldLabel(fieldName: string): string {
  return fieldName === 'description'
    ? 'description'
    : fieldName === 'schema'
      ? 'schema'
      : fieldName
}

function formatDecisionStatusMessage(item: SchemaDriftItem): string {
  if (item.status === 'approved') {
    return 'This tool version is approved and can appear in tools/list in enforce mode.'
  }

  if (item.status === 'rejected') {
    return 'This legacy rejected item remains unapproved; enforce mode removes only this tool from matching tools/list responses.'
  }

  if (item.status === 'blocked') {
    return 'This tool version is blocked and remains unapproved; enforce mode removes only this tool from matching tools/list responses.'
  }

  return 'This tool version is waiting for review; approve allows it, block removes only this tool from discovery.'
}

function formatReviewDecisionDetail(item: SchemaDriftItem): string {
  if (item.status === 'approved') {
    return 'Future matching tools/list responses may include this tool version in enforce mode.'
  }

  if (item.status === 'rejected') {
    return 'This item was rejected before the workflow was simplified. Choose Approve to allow it or Block to keep only this tool unavailable.'
  }

  if (item.status === 'blocked') {
    return 'Block keeps only the affected tool out of tools/list and records a stronger unsafe decision.'
  }

  return 'Approve allows this tool version. Block keeps only the affected tool out of tools/list in enforce mode.'
}
