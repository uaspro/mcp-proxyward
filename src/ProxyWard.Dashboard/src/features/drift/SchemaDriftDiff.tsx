import type { SchemaDriftDetail, SchemaDriftItem } from '../../api/drift'
import { Badge, StatePanel } from '../../components'
import { ReasonTags } from '../../shared/ReasonTags'
import { formatAuditDateTime } from '../../shared/formatters'
import { DriftStatusBadge } from './SchemaDriftStatusBadge'
import type { DriftTab } from './SchemaDriftView'
import { formatDiffJson } from './SchemaDriftView'

type DiffLineKind = 'equal' | 'add' | 'del'
type DiffLineRow = {
  kind: DiffLineKind
  beforeLine: number | null
  afterLine: number | null
  text: string
}

export function DriftTabContent({
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

  return buildDiffRows(beforeLines, afterLines, lengths)
}

function buildDiffRows(beforeLines: string[], afterLines: string[], lengths: number[][]): DiffLineRow[] {
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

  appendRemainingBeforeRows(rows, beforeLines, beforeIndex, beforeLine)
  appendRemainingAfterRows(rows, afterLines, afterIndex, afterLine)

  return rows
}

function appendRemainingBeforeRows(
  rows: DiffLineRow[],
  beforeLines: string[],
  beforeIndex: number,
  beforeLine: number,
) {
  for (let index = beforeIndex; index < beforeLines.length; index += 1) {
    rows.push({
      kind: 'del',
      beforeLine: beforeLine + index - beforeIndex,
      afterLine: null,
      text: beforeLines[index],
    })
  }
}

function appendRemainingAfterRows(
  rows: DiffLineRow[],
  afterLines: string[],
  afterIndex: number,
  afterLine: number,
) {
  for (let index = afterIndex; index < afterLines.length; index += 1) {
    rows.push({
      kind: 'add',
      beforeLine: null,
      afterLine: afterLine + index - afterIndex,
      text: afterLines[index],
    })
  }
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
