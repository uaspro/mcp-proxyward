import { useEffect, useState } from 'react'
import { ChevronRight } from 'lucide-react'
import { getPolicyModeImpact, switchPolicyMode, type PolicyModeImpactResponse, type PolicyModeSwitchResponse } from '../../api/policy'
import { Button, Dialog, StatePanel } from '../../components'
import { ReasonTags } from '../../shared/ReasonTags'
import { formatApiFailure, formatAuditDateTime } from '../../shared/formatters'
import type { Mode } from '../../shared/runtime'
export function ModeSwitchDialog({
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

export function PolicyEnforceApplyDialog({
  open,
  currentMode,
  applying,
  onClose,
  onConfirm,
}: {
  open: boolean
  currentMode: Mode
  applying: boolean
  onClose: () => void
  onConfirm: () => Promise<void>
}) {
  const targetMode: Mode = 'enforce'
  const [impactState, setImpactState] = useState<{
    impact: PolicyModeImpactResponse | null
    error: string | null
  }>({ impact: null, error: null })
  const [acknowledged, setAcknowledged] = useState(false)
  const [typed, setTyped] = useState('')
  const [confirmError, setConfirmError] = useState<string | null>(null)
  const [reloadKey, setReloadKey] = useState(0)
  const requiresTypedConfirmation = currentMode === 'audit'
  const impactLoading = open && !impactState.impact && !impactState.error
  const canConfirm = Boolean(
    impactState.impact
      && !applying
      && (!requiresTypedConfirmation || (acknowledged && typed === 'ENFORCE')),
  )

  useEffect(() => {
    if (!open) {
      return
    }

    const controller = new AbortController()
    getPolicyModeImpact(targetMode, controller.signal)
      .then((response) => {
        setImpactState({ impact: response, error: null })
      })
      .catch((ex: unknown) => {
        if (!controller.signal.aborted) {
          setImpactState({ impact: null, error: formatApiFailure(ex, 'Mode impact request failed') })
        }
      })

    return () => controller.abort()
  }, [open, reloadKey])

  if (!open) {
    return null
  }

  function closeDialog() {
    if (applying) {
      return
    }

    setAcknowledged(false)
    setTyped('')
    setConfirmError(null)
    setImpactState({ impact: null, error: null })
    onClose()
  }

  async function confirmApply() {
    setConfirmError(null)
    try {
      await onConfirm()
      setAcknowledged(false)
      setTyped('')
    } catch (ex) {
      setConfirmError(formatApiFailure(ex, 'Policy apply failed'))
    }
  }

  return (
    <Dialog
      open
      title="Switch to enforce mode"
      tone="warn"
      onClose={closeDialog}
      footer={
        <>
          <Button variant="ghost" onClick={closeDialog} disabled={applying}>
            Cancel
          </Button>
          <Button variant="primary" onClick={confirmApply} disabled={!canConfirm}>
            {applying ? 'Applying' : 'Apply'}
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
      {impactState.error ? (
        <StatePanel
          state="error"
          title="Impact preview unavailable"
          detail={impactState.error}
          onRetry={() => setReloadKey((current) => current + 1)}
        />
      ) : null}
      {confirmError ? <StatePanel state="error" title="Policy apply failed" detail={confirmError} /> : null}
      {impactState.impact ? <ModeImpactPreview impact={impactState.impact} /> : null}
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
