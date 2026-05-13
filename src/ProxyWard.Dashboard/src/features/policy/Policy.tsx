import { useCallback, useEffect, useMemo, useState } from 'react'
import { Check, Pencil, Plus, RefreshCw, Settings, Trash2, X } from 'lucide-react'
import { applyPolicy, getPolicy, validatePolicy, type PolicyApplyResponse, type PolicyModel, type PolicyResponse, type PolicyValidationResponse, type ServerPolicyModel } from '../../api/policy'
import { discoverTools, getToolInventory, type ToolInventoryResponse } from '../../api/tools'
import { Badge, Button, Card, Dialog, IconButton, StatePanel } from '../../components'
import { PageHeader } from '../../components/dashboard'
import { dashboardConfig } from '../../config'
import { formatApiFailure, formatAuditTime } from '../../shared/formatters'
import { normalizeRuntimeMode, type Mode } from '../../shared/runtime'
import { PolicyEnforceApplyDialog } from './ModeDialogs'
import {
  buildProxyMcpUrl,
  clonePolicyModel,
  createClientValidationResponse,
  createGeneratedNewServerForm,
  createNewServerForm,
  createServerPolicyModel,
  createToolInventoryServer,
  findClientPolicyIssues,
  formatServerCount,
  normalizeNewServerForm,
  normalizeSearchQuery,
  normalizeServerId,
  omitRecordKey,
  policyServerMatchesSearch,
  renameServerPolicy,
  upsertToolInventoryServer,
  validateNewServerForm,
  validateServerPolicyName,
  type NewServerPolicyForm,
} from './policyModel'
import { GlobalPolicyEditor, PolicyField, PolicyIssueList, ServerPolicyEditor } from './PolicyEditors'
export function Policy({
  mode,
  onModeChanged,
  searchQuery = '',
}: {
  mode: Mode
  onModeChanged: (mode: Mode) => void
  searchQuery?: string
}) {
  const [policy, setPolicy] = useState<PolicyResponse | null>(null)
  const [draft, setDraft] = useState<PolicyModel | null>(null)
  const [toolInventory, setToolInventory] = useState<ToolInventoryResponse | null>(null)
  const [selectedKey, setSelectedKey] = useState<string>('global')
  const [loading, setLoading] = useState(true)
  const [toolInventoryLoading, setToolInventoryLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [toolInventoryError, setToolInventoryError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [validation, setValidation] = useState<PolicyValidationResponse | null>(null)
  const [validationLoading, setValidationLoading] = useState(false)
  const [applyLoading, setApplyLoading] = useState(false)
  const [applyError, setApplyError] = useState<string | null>(null)
  const [applyResult, setApplyResult] = useState<PolicyApplyResponse | null>(null)
  const [newServerForm, setNewServerForm] = useState<NewServerPolicyForm | null>(null)
  const [newServerError, setNewServerError] = useState<string | null>(null)
  const [toolDiscoveryErrors, setToolDiscoveryErrors] = useState<Record<string, string>>({})
  const [discoveringServerId, setDiscoveringServerId] = useState<string | null>(null)
  const [pendingDeleteServer, setPendingDeleteServer] = useState<ServerPolicyModel | null>(null)
  const [pendingEnforceApply, setPendingEnforceApply] = useState<PolicyModel | null>(null)
  const [renamingServerId, setRenamingServerId] = useState<string | null>(null)
  const [renameValue, setRenameValue] = useState('')
  const [renameError, setRenameError] = useState<string | null>(null)

  const serverEntries = useMemo(() => Object.entries(draft?.servers ?? {}), [draft])
  const normalizedSearchQuery = normalizeSearchQuery(searchQuery)
  const visibleServerEntries = useMemo(
    () => serverEntries.filter(([serverId, server]) => policyServerMatchesSearch(serverId, server, normalizedSearchQuery)),
    [normalizedSearchQuery, serverEntries],
  )
  const toolInventoryByServer = useMemo(
    () => new Map((toolInventory?.servers ?? []).map((server) => [server.serverId, server])),
    [toolInventory],
  )
  const selectedServer = draft && selectedKey !== 'global' ? draft.servers[selectedKey] ?? null : null
  const selectedBaselineServer = policy && selectedKey !== 'global' ? policy.model.servers[selectedKey] ?? null : null
  const selectedServerInventory = selectedServer ? toolInventoryByServer.get(selectedServer.id) ?? null : null

  const loadToolInventory = useCallback(() => {
    const controller = new AbortController()
    setToolInventoryLoading(true)
    getToolInventory(controller.signal)
      .then((response) => {
        setToolInventory(response)
        setToolInventoryError(null)
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setToolInventoryError(formatApiFailure(ex, 'Tool inventory request failed'))
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setToolInventoryLoading(false)
        }
      })

    return () => controller.abort()
  }, [])

  const applyLoadedPolicy = useCallback((response: PolicyResponse) => {
    const nextDraft = clonePolicyModel(response.model)
    setPolicy(response)
    setDraft(nextDraft)
    setDirty(false)
    setValidation(null)
    setApplyError(null)
    setApplyResult(null)
    setError(null)
    setRenamingServerId(null)
    setRenameValue('')
    setRenameError(null)
    setSelectedKey((current) => {
      if (current === 'global' || nextDraft.servers[current]) {
        return current
      }

      return Object.keys(nextDraft.servers)[0] ?? 'global'
    })
    const loadedMode = normalizeRuntimeMode(response.model.mode)
    if (loadedMode) {
      onModeChanged(loadedMode)
    }
  }, [onModeChanged])

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

  useEffect(() => loadToolInventory(), [loadToolInventory])

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

  function openAddServerDialog() {
    if (!draft) {
      return
    }

    setNewServerForm(createNewServerForm())
    setNewServerError(null)
  }

  function updateNewServerForm(update: Partial<NewServerPolicyForm>) {
    setNewServerError(null)
    setNewServerForm((current) => (current ? { ...current, ...update } : current))
  }

  function confirmAddServer() {
    if (!draft || !newServerForm) {
      return
    }

    const normalized = normalizeNewServerForm(newServerForm, draft.servers)
    const formError = validateNewServerForm(normalized, draft.servers)
    if (formError) {
      setNewServerError(formError)
      return
    }

    setValidation(null)
    updateDraft((current) => ({
      ...current,
      servers: {
        ...current.servers,
        [normalized.id]: createServerPolicyModel(normalized),
      },
    }))
    setSelectedKey(normalized.id)
    setNewServerForm(null)
    void discoverServerTools(normalized.id, normalized.upstream)
  }

  function confirmDeleteServer() {
    if (!pendingDeleteServer) {
      return
    }

    const serverId = pendingDeleteServer.id
    markDirty()
    setValidation(null)
    setDraft((current) => {
      if (!current || !current.servers[serverId]) {
        return current
      }

      const servers = { ...current.servers }
      delete servers[serverId]
      return { ...current, servers }
    })
    setSelectedKey((current) => (current === serverId ? 'global' : current))
    setPendingDeleteServer(null)
  }

  function beginRenameServer(serverId: string) {
    setSelectedKey(serverId)
    setRenamingServerId(serverId)
    setRenameValue(serverId)
    setRenameError(null)
  }

  function cancelRenameServer() {
    setRenamingServerId(null)
    setRenameValue('')
    setRenameError(null)
  }

  function confirmRenameServer() {
    if (!draft || !renamingServerId) {
      return
    }

    const currentServer = draft.servers[renamingServerId]
    if (!currentServer) {
      cancelRenameServer()
      return
    }

    const nextServerId = normalizeServerId(renameValue)
    const formError = validateServerPolicyName(nextServerId, draft.servers, renamingServerId)
    if (formError) {
      setRenameError(formError)
      return
    }

    if (nextServerId === renamingServerId) {
      cancelRenameServer()
      return
    }

    setValidation(null)
    updateDraft((current) => ({
      ...current,
      servers: renameServerPolicy(current.servers, renamingServerId, nextServerId),
    }))
    setSelectedKey(nextServerId)
    setToolInventory((current) => current
      ? {
          servers: current.servers
            .map((server) => (server.serverId === renamingServerId ? { ...server, serverId: nextServerId } : server))
            .sort((left, right) => left.serverId.localeCompare(right.serverId)),
        }
      : current)
    setToolDiscoveryErrors((current) => {
      if (!Object.prototype.hasOwnProperty.call(current, renamingServerId)) {
        return current
      }

      return {
        ...omitRecordKey(current, renamingServerId),
        [nextServerId]: current[renamingServerId],
      }
    })
    setDiscoveringServerId((current) => (current === renamingServerId ? nextServerId : current))
    cancelRenameServer()
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
      if (mode === 'audit' && normalizeRuntimeMode(modelToApply.mode) === 'enforce') {
        setPendingEnforceApply(clonePolicyModel(modelToApply))
        return
      }

      await applyValidatedPolicy(modelToApply)
    } catch (ex) {
      setApplyError(formatApiFailure(ex, 'Policy apply failed'))
    } finally {
      setApplyLoading(false)
    }
  }

  async function applyValidatedPolicy(modelToApply: PolicyModel) {
    const response = await applyPolicy({
      model: modelToApply,
      requestedBy: 'dashboard',
      note: 'dashboard apply',
    })
    const appliedMode = normalizeRuntimeMode(response.mode)
    if (appliedMode) {
      onModeChanged(appliedMode)
    }
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
    void getToolInventory()
      .then((response) => {
        setToolInventory(response)
        setToolInventoryError(null)
      })
      .catch((ex: unknown) => {
        setToolInventoryError(formatApiFailure(ex, 'Tool inventory request failed'))
      })
  }

  async function discoverServerTools(serverId: string, upstream?: string | null) {
    setDiscoveringServerId(serverId)
    setToolDiscoveryErrors((current) => omitRecordKey(current, serverId))
    try {
      const response = await discoverTools({
        serverId,
        upstream: upstream || undefined,
      })
      setToolInventory((current) => upsertToolInventoryServer(current, createToolInventoryServer(response)))
    } catch (ex) {
      setToolDiscoveryErrors((current) => ({
        ...current,
        [serverId]: formatApiFailure(ex, 'Tool discovery failed'),
      }))
    } finally {
      setDiscoveringServerId((current) => (current === serverId ? null : current))
    }
  }

  async function confirmEnforceApply() {
    if (!pendingEnforceApply) {
      return
    }

    setApplyLoading(true)
    setApplyError(null)
    try {
      await applyValidatedPolicy(pendingEnforceApply)
      setPendingEnforceApply(null)
    } catch (ex) {
      setApplyError(formatApiFailure(ex, 'Policy apply failed'))
      throw ex
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

  const generatedNewServer = newServerForm
    ? createGeneratedNewServerForm(newServerForm.upstream, draft.servers, newServerForm.name)
    : null

  return (
    <section className="page">
      <PageHeader
        title="Policy"
        subtitle={`${formatServerCount(serverEntries.length)} - ${policy?.policyHash ?? 'draft'} - ${dirty ? 'unsaved changes' : `loaded ${formatAuditTime(policy?.readOnly.loadedAtUtc ?? '')}`}`}
        action={
          <div className="row-actions">
            <Badge tone={dirty ? 'warn' : 'allow'}>{dirty ? 'dirty' : 'clean'}</Badge>
            <Button icon={Plus} onClick={openAddServerDialog} disabled={loading || validationLoading || applyLoading}>
              Add server
            </Button>
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
        <Card
          title="Policy scope"
          action={<Badge tone="neutral">{formatServerCount(serverEntries.length)}</Badge>}
        >
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
            {visibleServerEntries.map(([serverId, server]) => (
              <div className="policy-server-row" key={serverId}>
                {renamingServerId === serverId ? (
                  <div className="policy-server-rename">
                    <span className="server-initials">{serverId.slice(0, 2).toUpperCase()}</span>
                    <input
                      aria-label="Server policy name"
                      className="form-input"
                      value={renameValue}
                      onChange={(event) => {
                        setRenameValue(event.target.value)
                        setRenameError(null)
                      }}
                      onKeyDown={(event) => {
                        if (event.key === 'Enter') {
                          confirmRenameServer()
                        }

                        if (event.key === 'Escape') {
                          cancelRenameServer()
                        }
                      }}
                      autoFocus
                    />
                    <IconButton label="Save server policy name" icon={Check} onClick={confirmRenameServer} />
                    <IconButton label="Cancel server policy name edit" icon={X} onClick={cancelRenameServer} />
                    {renameError ? <span className="policy-server-rename-error">{renameError}</span> : null}
                  </div>
                ) : (
                  <>
                    <button
                      type="button"
                      className={`policy-server-button ${selectedKey === serverId ? 'selected' : ''}`}
                      onClick={() => setSelectedKey(serverId)}
                    >
                      <span className="server-initials">{serverId.slice(0, 2).toUpperCase()}</span>
                      <span>{serverId}</span>
                      <Badge tone={server.allowed ? 'allow' : 'block'}>{server.allowed ? 'on' : 'off'}</Badge>
                    </button>
                    <IconButton label={`Edit ${serverId} policy name`} icon={Pencil} onClick={() => beginRenameServer(serverId)} />
                  </>
                )}
              </div>
            ))}
            {serverEntries.length === 0 ? (
              <StatePanel state="empty" title="No server policies" detail="Add a server policy before validating or applying." />
            ) : null}
            {serverEntries.length > 0 && visibleServerEntries.length === 0 ? (
              <StatePanel state="empty" title="No matching policy scopes" detail="No server id, route, or upstream matches the top search." />
            ) : null}
          </div>
        </Card>
        <div className="policy-editor-stack">
          {selectedKey === 'global' ? (
            <GlobalPolicyEditor draft={draft} onChange={updateDraft} />
          ) : selectedServer ? (
            <ServerPolicyEditor
              key={selectedServer.id}
              server={selectedServer}
              baselineServer={selectedBaselineServer}
              toolInventory={selectedServerInventory}
              toolsLoading={toolInventoryLoading}
              toolsError={toolDiscoveryErrors[selectedServer.id] ?? (selectedServerInventory ? null : toolInventoryError)}
              discovering={discoveringServerId === selectedServer.id}
              searchQuery={normalizedSearchQuery}
              onDiscover={() => discoverServerTools(selectedServer.id, selectedServer.upstream)}
              onChange={(updater) => updateServer(selectedServer.id, updater)}
              onDelete={() => setPendingDeleteServer(selectedServer)}
            />
          ) : (
            <StatePanel state="empty" title="Select a policy scope" />
          )}
        </div>
      </div>
      <Dialog
        open={pendingDeleteServer !== null}
        title={`Delete ${pendingDeleteServer?.id ?? 'server'}`}
        tone="warn"
        onClose={() => setPendingDeleteServer(null)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setPendingDeleteServer(null)}>
              Cancel
            </Button>
            <Button icon={Trash2} variant="danger" onClick={confirmDeleteServer}>
              Delete server
            </Button>
          </>
        }
      >
        <div className="confirmation-stack">
          <p>
            Remove <strong className="mono">{pendingDeleteServer?.id}</strong> from the draft policy.
          </p>
          <div className="kv-grid">
            <span>route</span>
            <strong className="mono truncate">{pendingDeleteServer?.route ?? '-'}</strong>
            <span>upstream</span>
            <strong className="mono truncate">{pendingDeleteServer?.upstream ?? '-'}</strong>
          </div>
        </div>
      </Dialog>
      <Dialog
        open={newServerForm !== null}
        title="Add server policy"
        onClose={() => setNewServerForm(null)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setNewServerForm(null)}>
              Cancel
            </Button>
            <Button icon={Plus} variant="primary" onClick={confirmAddServer}>
              Add server
            </Button>
          </>
        }
      >
        <div className="confirmation-stack">
          {newServerError ? <StatePanel state="error" title="Server policy invalid" detail={newServerError} /> : null}
          <div className="form-grid">
            <PolicyField label="name">
              <input
                className="form-input"
                value={newServerForm?.name ?? ''}
                placeholder="Generated from upstream"
                onChange={(event) => updateNewServerForm({ name: event.target.value })}
              />
            </PolicyField>
            <PolicyField label="upstream">
              <input
                className="form-input"
                value={newServerForm?.upstream ?? ''}
                placeholder="http://github-mcp:8080/mcp"
                onChange={(event) => updateNewServerForm({ upstream: event.target.value })}
              />
            </PolicyField>
          </div>
          <div className="kv-grid generated-policy-preview">
            <span>id</span>
            <strong className="mono truncate">{generatedNewServer?.id || '-'}</strong>
            <span>route</span>
            <strong className="mono truncate">{generatedNewServer?.route || '-'}</strong>
            <span>mcp.json url</span>
            <strong className="mono truncate">
              {generatedNewServer?.route ? buildProxyMcpUrl(generatedNewServer.route) : '-'}
            </strong>
          </div>
        </div>
      </Dialog>
      <PolicyEnforceApplyDialog
        open={pendingEnforceApply !== null}
        currentMode={mode}
        applying={applyLoading}
        onClose={() => setPendingEnforceApply(null)}
        onConfirm={confirmEnforceApply}
      />
    </section>
  )
}
