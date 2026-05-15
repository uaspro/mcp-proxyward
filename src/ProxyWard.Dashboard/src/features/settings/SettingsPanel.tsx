import { useCallback, useEffect, useState } from 'react'
import { RefreshCw } from 'lucide-react'
import { getSettings, type SettingsResponse } from '../../api/settings'
import { getStatus, type StatusResponse } from '../../api/status'
import { Badge, Button, Card, StatePanel } from '../../components'
import { HealthRows, PageHeader } from '../../components/dashboard'
import { dashboardConfig } from '../../config'
import { formatApiFailure, formatAsOf, formatAuditDateTime, formatBytes } from '../../shared/formatters'
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
        setError(formatApiFailure(ex, 'System request failed'))
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
          setError(formatApiFailure(ex, 'System request failed'))
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
        <PageHeader title="System" subtitle="Runtime health and deployment details" />
        <StatePanel state="loading" title="Loading system details" detail="management API" />
      </section>
    )
  }

  if (!settings) {
    return (
      <section className="page">
        <PageHeader title="System" subtitle="Runtime health and deployment details" action={<Button icon={RefreshCw} onClick={refresh}>Retry</Button>} />
        <StatePanel state="error" title="System details unavailable" detail={error ?? dashboardConfig.apiBaseUrl} onRetry={refresh} />
      </section>
    )
  }

  const degraded = status?.status && status.status !== 'healthy'
  const exporters = formatExporters(settings)

  return (
    <section className="page">
      <PageHeader
        title="System"
        subtitle={`Runtime health and deployment details - updated ${formatAsOf(refreshedAt)}`}
        action={<Button icon={RefreshCw} onClick={refresh} disabled={loading}>Refresh</Button>}
      />
      {error ? (
        <StatePanel state="error" title="Stale system details" detail={`${error}. Showing last successful data.`} onRetry={refresh} />
      ) : null}
      {degraded ? (
        <StatePanel state="error" title="Runtime degraded" detail={`status: ${status?.status}`} />
      ) : null}
      <div className="dashboard-grid">
        <Card title="Runtime health">
          <HealthRows status={status} />
        </Card>
        <Card title="Dashboard connection">
          <div className="kv-grid">
            <span>management API</span>
            <strong className="mono truncate">{dashboardConfig.apiBaseUrl}</strong>
            <span>proxy endpoint</span>
            <strong className="mono truncate">{dashboardConfig.proxyBaseUrl}</strong>
            <span>admin token</span>
            <Badge tone={dashboardConfig.adminToken ? 'allow' : 'neutral'}>{dashboardConfig.adminToken ? 'configured' : 'not set'}</Badge>
            <span>policy editing</span>
            <Badge tone={settings.runtime.editingSupported ? 'allow' : 'neutral'}>{settings.runtime.editingSupported ? 'available' : 'disabled'}</Badge>
          </div>
        </Card>
      </div>
      <div className="dashboard-grid">
        <Card title="Policy snapshot">
          <div className="kv-grid">
            <span>policy hash</span>
            <strong className="mono truncate">{settings.service.policyHash}</strong>
            <span>policy source</span>
            <strong className="mono truncate">{settings.service.sourcePath}</strong>
            <span>loaded</span>
            <strong className="mono">{formatDateTime(settings.service.loadedAtUtc)}</strong>
            <span>last modified</span>
            <strong className="mono">{formatDateTime(settings.service.sourceLastModifiedUtc)}</strong>
            <span>servers</span>
            <strong className="mono">{settings.service.serverCount}</strong>
            <span>source size</span>
            <strong className="mono">{formatOptionalBytes(settings.service.sourceSizeBytes)}</strong>
          </div>
        </Card>
        <Card title="Persistence database">
          <div className="kv-grid">
            <span>provider</span>
            <strong className="mono">{settings.persistence.provider}</strong>
            <span>source</span>
            <strong className="mono truncate">{settings.persistence.source}</strong>
            <span>connection</span>
            <Badge tone={settings.persistence.connectionConfigured ? 'allow' : 'neutral'}>
              {settings.persistence.connectionConfigured ? 'configured' : 'missing'}
            </Badge>
            <span>state</span>
            <Badge tone={healthTone(status?.components.persistenceDb.status ?? 'unknown')}>
              {status?.components.persistenceDb.status ?? 'unknown'}
            </Badge>
            <span>audit capture</span>
            <Badge tone={settings.audit.enabled ? 'allow' : 'neutral'}>{settings.audit.enabled ? 'enabled' : 'disabled'}</Badge>
          </div>
        </Card>
      </div>
      <div className="dashboard-grid">
        <Card title="Inspection behavior">
          <div className="kv-grid">
            <span>max response body</span>
            <strong className="mono">{formatBytes(settings.inspection.maxBodyBytes)}</strong>
            <span>unsupported response</span>
            <strong className="mono">{settings.inspection.unsupportedStreaming}</strong>
            <span>batch tool calls</span>
            <strong className="mono">{settings.inspection.batchToolCalls}</strong>
          </div>
        </Card>
        <Card title="Observability wiring">
          <div className="kv-grid">
            <span>service name</span>
            <strong className="mono truncate">{settings.observability.serviceName}</strong>
            <span>exporters</span>
            <strong className="mono truncate">{exporters}</strong>
            <span>OTLP endpoint</span>
            <strong className="mono truncate">{settings.observability.otlpEndpoint ?? '-'}</strong>
            <span>trace sample ratio</span>
            <strong className="mono">{settings.observability.tracesRatio}</strong>
            <span>App Insights env</span>
            <strong className="mono truncate">{settings.observability.applicationInsightsConnectionStringEnv}</strong>
          </div>
        </Card>
      </div>
    </section>
  )
}

function formatDateTime(value: string | null): string {
  return value ? formatAuditDateTime(value) : '-'
}

function formatOptionalBytes(value: number | null): string {
  return value === null ? '-' : formatBytes(value)
}

function formatExporters(settings: SettingsResponse): string {
  const exporters = [
    settings.observability.consoleEnabled ? 'console' : null,
    settings.observability.otlpEnabled ? 'OTLP' : null,
    settings.observability.applicationInsightsEnabled ? 'Application Insights' : null,
  ].filter(Boolean)

  return exporters.length > 0 ? exporters.join(', ') : 'none'
}
