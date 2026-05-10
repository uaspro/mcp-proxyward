import { useMemo, useState, type ReactNode } from 'react'
import { RefreshCw, Trash2 } from 'lucide-react'
import type { PolicyModel, PolicyValidationIssue, PolicyValidationResponse, ServerPolicyModel } from '../../api/policy'
import type { ToolInventoryServer } from '../../api/tools'
import { Badge, Button, Card, SegmentedControl, StatePanel, Toggle } from '../../components'
import {
  argumentPolicyPlaceholders,
  createMcpJsonSnippet,
  createToolPolicyRows,
  driftTone,
  formatLines,
  parseLines,
  secretPatternPlaceholder,
  toolPolicyRowMatchesSearch,
  updateToolDisposition,
  type ToolDisposition,
} from './policyModel'
export function GlobalPolicyEditor({
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
                { value: 'audit', label: 'Audit', tone: 'warn' },
                { value: 'enforce', label: 'Enforce', tone: 'allow' },
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
          <PolicyField label="unsupportedInspection">
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
            <strong className="mono">{draft.audit.sink}</strong>
          </PolicyField>
          <PolicyField label="audit.sqlitePath">
            <strong className="mono truncate">{draft.audit.sqlitePath ?? '-'}</strong>
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

export function ServerPolicyEditor({
  server,
  baselineServer,
  toolInventory,
  toolsLoading,
  toolsError,
  discovering,
  searchQuery,
  onDiscover,
  onChange,
  onDelete,
}: {
  server: ServerPolicyModel
  baselineServer: ServerPolicyModel | null
  toolInventory: ToolInventoryServer | null
  toolsLoading: boolean
  toolsError: string | null
  discovering: boolean
  searchQuery: string
  onDiscover: () => void
  onChange: (updater: (server: ServerPolicyModel) => ServerPolicyModel) => void
  onDelete: () => void
}) {
  const secrets = server.secrets ?? { redactInLogs: true, blockReturn: false, patterns: [] }

  return (
    <>
      <Card
        title={server.id}
        action={
          <div className="row-actions">
            <Badge tone={server.allowed ? 'allow' : 'block'}>{server.allowed ? 'allowed' : 'blocked'}</Badge>
            <Button icon={Trash2} size="sm" variant="danger" onClick={onDelete}>
              Delete
            </Button>
          </div>
        }
      >
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
      <Card title="mcp.json">
        <pre className="code-block compact">{createMcpJsonSnippet(server)}</pre>
      </Card>
      <Card
        title="Tools"
        action={
          <Button
            icon={RefreshCw}
            size="sm"
            variant="ghost"
            disabled={discovering || !server.upstream}
            onClick={onDiscover}
          >
            {discovering ? 'Discovering' : 'Discover'}
          </Button>
        }
      >
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
          <ToolPolicySelector
            server={server}
            inventory={toolInventory}
            baselineServer={baselineServer}
            loading={toolsLoading || discovering}
            error={toolsError}
            searchQuery={searchQuery}
            onChange={onChange}
          />
        </div>
      </Card>
      <Card title="Arguments">
        <div className="form-grid">
          <TextListEditor
            label="paths.allowedRoots"
            values={server.arguments.paths.allowedRoots}
            placeholder={argumentPolicyPlaceholders.allowedRoots}
            description="One filesystem root per line. Path arguments outside these roots are treated as policy violations."
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
          <PolicyField
            label="paths.blockTraversal"
            description="Rejects path arguments that contain traversal segments such as ../."
          >
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
            placeholder={argumentPolicyPlaceholders.hostsAllow}
            description="One hostname per line. Leave empty when the server should not enforce a host allow list."
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
          <PolicyField
            label="hosts.blockPrivateNetworks"
            description="Blocks private, loopback, and link-local network targets in URL and host arguments."
          >
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
            placeholder={argumentPolicyPlaceholders.dangerousCommands}
            description="One command name or risky command fragment per line."
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
          <PolicyField
            label="commands.blockShell"
            description="Blocks shell interpreter usage instead of allowing raw shell execution."
          >
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
          <PolicyField
            label="redactInLogs"
            description="Redacts matching values before audit events are stored or displayed."
          >
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
          <PolicyField
            label="blockReturn"
            description="Blocks tool responses that contain a configured secret pattern while in enforce mode."
          >
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
            placeholder={secretPatternPlaceholder}
            description="One literal or /regex/ pattern per line. Matches are redacted and can be blocked on return."
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

function ToolPolicySelector({
  server,
  inventory,
  baselineServer,
  loading,
  error,
  searchQuery,
  onChange,
}: {
  server: ServerPolicyModel
  inventory: ToolInventoryServer | null
  baselineServer: ServerPolicyModel | null
  loading: boolean
  error: string | null
  searchQuery: string
  onChange: (updater: (server: ServerPolicyModel) => ServerPolicyModel) => void
}) {
  const rows = useMemo(
    () => createToolPolicyRows(server, inventory, baselineServer),
    [server, inventory, baselineServer],
  )
  const visibleRows = useMemo(
    () => rows.filter((row) => toolPolicyRowMatchesSearch(row, searchQuery)),
    [rows, searchQuery],
  )
  const defaultDispositionTone = server.tools.default === 'allow' ? 'allow' : 'deny'

  return (
    <div className="tool-policy-panel">
      {loading && rows.length === 0 ? <StatePanel state="loading" title="Discovering tools" detail="tools/list" /> : null}
      {error ? <StatePanel state="error" title="Tool discovery unavailable" detail={error} /> : null}
      {rows.length === 0 && !loading ? (
        <StatePanel
          state="empty"
          title="No discovered tools"
          detail="Run discovery or call tools/list through the proxy to populate this list."
        />
      ) : null}
      {rows.length > 0 && visibleRows.length === 0 ? (
        <StatePanel state="empty" title="No matching tools" detail="No discovered or configured tools match the top search." />
      ) : null}
      {visibleRows.length > 0 ? (
        <div className={`tool-policy-list ${loading ? 'loading' : ''}`}>
          {visibleRows.map((row) => (
            <div className="tool-policy-row" key={row.name}>
              <div className="tool-policy-main">
                <div className="tool-policy-title">
                  <strong className="mono truncate">{row.name}</strong>
                  <Badge tone={row.policyDirty ? 'warn' : 'allow'}>{row.policyDirty ? 'dirty' : 'clean'}</Badge>
                  {!row.discovered || row.driftStatus !== 'clean' ? (
                    <Badge tone={row.discovered ? driftTone(row.driftStatus) : 'neutral'}>
                      {row.discovered ? row.driftStatus : 'configured'}
                    </Badge>
                  ) : null}
                </div>
                <div className="tool-policy-description">
                  {row.title ? <span>{row.title}</span> : null}
                  {row.description ? <span>{row.description}</span> : null}
                </div>
              </div>
              <SegmentedControl<ToolDisposition>
                value={row.disposition}
                options={[
                  { value: 'default', label: 'Default', tone: defaultDispositionTone },
                  { value: 'allow', label: 'Allow' },
                  { value: 'block', label: 'Block' },
                ]}
                onChange={(value) =>
                  onChange((current) => updateToolDisposition(current, row.name, value))
                }
              />
            </div>
          ))}
        </div>
      ) : null}
    </div>
  )
}

export function PolicyField({
  label,
  children,
  description,
}: {
  label: string
  children: ReactNode
  description?: string
}) {
  return (
    <label className="policy-field">
      <span>{label}</span>
      <div className="policy-field-control">
        {children}
        {description ? <small className="policy-field-description">{description}</small> : null}
      </div>
    </label>
  )
}

function TextListEditor({
  label,
  values,
  onChange,
  placeholder,
  description,
}: {
  label: string
  values: string[]
  onChange: (values: string[]) => void
  placeholder?: string
  description?: string
}) {
  const [text, setText] = useState(() => formatLines(values))
  const configuredCount = values.length
  const overrideStatus = configuredCount === 0
    ? 'No configured overrides. Placeholder examples are not active.'
    : `${configuredCount} configured override${configuredCount === 1 ? '' : 's'}.`

  return (
    <label className="policy-field vertical">
      <span>{label}</span>
      <div className="policy-field-control">
        <div className={`policy-override-status ${configuredCount > 0 ? 'active' : ''}`}>
          {overrideStatus}
        </div>
        <textarea
          className="form-textarea"
          value={text}
          placeholder={placeholder}
          onChange={(event) => {
            const nextText = event.target.value
            setText(nextText)
            onChange(parseLines(nextText))
          }}
        />
        {description ? <small className="policy-field-description">{description}</small> : null}
      </div>
    </label>
  )
}

export function PolicyIssueList({
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

export function PolicyIssue({ issue, warning = false }: { issue: PolicyValidationIssue; warning?: boolean }) {
  return (
    <div className={`validation-issue ${warning ? 'warning' : 'error'}`}>
      <Badge tone={warning ? 'warn' : 'block'}>{issue.field}</Badge>
      <span className="mono">{issue.code}</span>
      <span>{issue.message}</span>
    </div>
  )
}
