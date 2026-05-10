import { AlertCircle, Loader2, X, type LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'

type Tone = 'neutral' | 'allow' | 'warn' | 'block' | 'info'

export function Button({
  children,
  icon: Icon,
  variant = 'default',
  size = 'md',
  disabled = false,
  onClick,
  type = 'button',
}: {
  children: ReactNode
  icon?: LucideIcon
  variant?: 'default' | 'primary' | 'ghost' | 'danger'
  size?: 'sm' | 'md'
  disabled?: boolean
  onClick?: () => void
  type?: 'button' | 'submit'
}) {
  return (
    <button
      type={type}
      className={`ui-button ${variant} ${size}`}
      disabled={disabled}
      onClick={disabled ? undefined : onClick}
    >
      {Icon ? <Icon size={15} /> : null}
      <span>{children}</span>
    </button>
  )
}

export function IconButton({
  label,
  icon: Icon,
  disabled = false,
  onClick,
}: {
  label: string
  icon: LucideIcon
  disabled?: boolean
  onClick?: () => void
}) {
  return (
    <button
      type="button"
      className="icon-button"
      aria-label={label}
      title={label}
      disabled={disabled}
      onClick={disabled ? undefined : onClick}
    >
      <Icon size={16} />
    </button>
  )
}

export function Badge({ children, tone = 'neutral' }: { children: ReactNode; tone?: Tone }) {
  return <span className={`badge ${tone}`}>{children}</span>
}

export function Card({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section className="panel">
      <div className="panel-header">
        <h2>{title}</h2>
        {action ? <div className="panel-action">{action}</div> : null}
      </div>
      <div className="panel-body">{children}</div>
    </section>
  )
}

export function DataTable({
  columns,
  rows,
}: {
  columns: string[]
  rows: Array<Array<ReactNode>>
}) {
  return (
    <div className="data-table" role="table">
      <div className="data-table-head" role="row">
        {columns.map((column) => (
          <span role="columnheader" key={column}>
            {column}
          </span>
        ))}
      </div>
      {rows.map((row, rowIndex) => (
        <div className="data-table-row" role="row" key={rowIndex}>
          {row.map((cell, cellIndex) => (
            <span role="cell" key={cellIndex}>
              {cell}
            </span>
          ))}
        </div>
      ))}
    </div>
  )
}

export function Dialog({
  open,
  title,
  tone = 'info',
  children,
  footer,
  onClose,
}: {
  open: boolean
  title: string
  tone?: Tone
  children: ReactNode
  footer: ReactNode
  onClose: () => void
}) {
  if (!open) {
    return null
  }

  return (
    <>
      <div className="dialog-backdrop" onClick={onClose} />
      <section className="dialog" role="dialog" aria-modal="true" aria-label={title}>
        <div className="dialog-header">
          <div className={`dialog-icon ${tone}`}>
            <AlertCircle size={18} />
          </div>
          <h2>{title}</h2>
          <IconButton label="Close dialog" icon={X} onClick={onClose} />
        </div>
        <div className="dialog-body">{children}</div>
        <div className="dialog-footer">{footer}</div>
      </section>
    </>
  )
}

export function Drawer({
  open,
  title,
  subtitle,
  children,
  onClose,
}: {
  open: boolean
  title: string
  subtitle?: string
  children: ReactNode
  onClose: () => void
}) {
  if (!open) {
    return null
  }

  return (
    <>
      <div className="drawer-backdrop" onClick={onClose} />
      <aside className="drawer" aria-label={title}>
        <div className="drawer-header">
          <div className="min-w-0">
            <h2>{title}</h2>
            {subtitle ? <p>{subtitle}</p> : null}
          </div>
          <IconButton label="Close drawer" icon={X} onClick={onClose} />
        </div>
        <div className="drawer-body">{children}</div>
      </aside>
    </>
  )
}

export function Tabs<TValue extends string>({
  value,
  options,
  onChange,
}: {
  value: TValue
  options: Array<{ value: TValue; label: string }>
  onChange: (value: TValue) => void
}) {
  return (
    <div className="tabs" role="tablist">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          role="tab"
          aria-selected={value === option.value}
          className={value === option.value ? 'active' : ''}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}

export function Toggle({
  checked,
  label,
  disabled = false,
  onChange,
}: {
  checked: boolean
  label: string
  disabled?: boolean
  onChange: (checked: boolean) => void
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      className={`toggle ${checked ? 'on' : ''}`}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span className="toggle-track" aria-hidden="true">
        <span className="toggle-thumb" />
      </span>
      <span>{label}</span>
    </button>
  )
}

export function SegmentedControl<TValue extends string>({
  value,
  options,
  onChange,
  disabled = false,
}: {
  value: TValue
  options: Array<{ value: TValue; label: string }>
  onChange: (value: TValue) => void
  disabled?: boolean
}) {
  return (
    <div className="segmented" role="group">
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          className={value === option.value ? 'active' : ''}
          disabled={disabled}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  )
}

export function StatePanel({
  state,
  title,
  detail,
  onRetry,
}: {
  state: 'loading' | 'error' | 'empty' | 'disabled'
  title: string
  detail?: string
  onRetry?: () => void
}) {
  return (
    <div className={`state-panel ${state}`}>
      {state === 'loading' ? <Loader2 size={18} className="spin" /> : <AlertCircle size={18} />}
      <div className="min-w-0">
        <div className="state-title">{title}</div>
        {detail ? <div className="state-detail">{detail}</div> : null}
      </div>
      {state === 'error' && onRetry ? (
        <Button variant="ghost" size="sm" onClick={onRetry}>
          Retry
        </Button>
      ) : null}
    </div>
  )
}
