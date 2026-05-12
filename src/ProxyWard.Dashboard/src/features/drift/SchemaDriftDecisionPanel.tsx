import { Ban, Check, type LucideIcon } from 'lucide-react'
import type { SchemaDriftAction, SchemaDriftItem } from '../../api/drift'
import { formatReviewDecisionDetail } from './SchemaDriftView'

export function ReviewDecisionPanel({
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
