import { Badge } from '../components'

export function ReasonTags({ reasons }: { reasons: string[] }) {
  if (reasons.length === 0) {
    return <span className="empty-value">-</span>
  }

  const visibleReasons = reasons.slice(0, 3)
  const overflow = reasons.length - visibleReasons.length

  return (
    <div className="tag-list">
      {visibleReasons.map((reason) => (
        <Badge key={reason} tone="neutral">
          {reason}
        </Badge>
      ))}
      {overflow > 0 ? <Badge tone="neutral">+{overflow}</Badge> : null}
    </div>
  )
}
