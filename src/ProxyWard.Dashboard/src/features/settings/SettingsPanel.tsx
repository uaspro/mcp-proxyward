import { useCallback, useEffect, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { getSettings, type SettingsResponse } from '../../api/settings'
import { getStatus, type StatusResponse } from '../../api/status'
import { Badge, Button, Card, Sparkline, StatePanel, Toggle } from '../../components'
import { HealthRows, PageHeader } from '../../components/dashboard'
import { dashboardConfig } from '../../config'
import { formatApiFailure, formatAsOf, formatBytes } from '../../shared/formatters'
import { healthTone } from '../../shared/tones'
export function SettingsPanel() {
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
            <span>unsupportedInspection</span>
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
