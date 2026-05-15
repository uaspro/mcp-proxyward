import { Activity, AlertTriangle, FileCode2, Shield, type LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'
import type { ComponentReport, StatusResponse } from '../api/status'
import { healthTone } from '../shared/tones'
import { Badge } from './ui'

export function PageHeader({
  title,
  subtitle,
  action,
}: {
  title: string
  subtitle: string
  action?: ReactNode
}) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        <p>{subtitle}</p>
      </div>
      {action}
    </div>
  )
}

export function StatCard({
  label,
  value,
  delta,
  tone,
}: {
  label: string
  value: string
  delta: string
  tone: 'good' | 'warn' | 'info' | 'neutral'
}) {
  return (
    <div className="stat-card">
      <div className="stat-label">{label}</div>
      <div className="stat-value">{value}</div>
      <div className={`stat-delta ${tone}`}>{delta}</div>
    </div>
  )
}

export function DecisionBadge({ decision }: { decision: string }) {
  const tone = decision === 'block' ? 'block' : decision === 'allow' ? 'allow' : 'warn'
  return <Badge tone={tone}>{decision.replace('_', ' ')}</Badge>
}

export function HealthRows({ status = null }: { status?: StatusResponse | null }) {
  const rows = getStatusRows(status)

  return (
    <div className="health-list">
      {rows.map(({ name, report, icon: Icon }) => (
        <div className="health-row" key={name}>
          <Icon size={16} />
          <span>{name}</span>
          <Badge tone={healthTone(report.status)}>{report.status}</Badge>
        </div>
      ))}
    </div>
  )
}

export function CompactList({ rows }: { rows: [string, string, string][] }) {
  return (
    <div className="compact-list">
      {rows.map(([primary, secondary, status]) => (
        <div className="compact-row" key={`${primary}-${secondary}`}>
          <div>
            <div className="compact-primary">{primary}</div>
            <div className="compact-secondary">{secondary}</div>
          </div>
          <Badge tone="neutral">{status}</Badge>
        </div>
      ))}
    </div>
  )
}

function getStatusRows(status: StatusResponse | null): { name: string; report: ComponentReport; icon: LucideIcon }[] {
  if (!status) {
    return [
      { name: 'Management API', report: { status: 'unknown', notes: null, details: null }, icon: Activity },
      { name: 'Proxy control', report: { status: 'unknown', notes: null, details: null }, icon: Shield },
      { name: 'Persistence DB', report: { status: 'unknown', notes: null, details: null }, icon: FileCode2 },
      { name: 'Telemetry', report: { status: 'unknown', notes: null, details: null }, icon: AlertTriangle },
    ]
  }

  return [
    { name: 'Management API', report: status.components.managementApi, icon: Activity },
    { name: 'Proxy control', report: status.components.proxyControl, icon: Shield },
    { name: 'Persistence DB', report: status.components.persistenceDb, icon: FileCode2 },
    { name: 'Telemetry', report: status.components.telemetry, icon: AlertTriangle },
  ]
}
