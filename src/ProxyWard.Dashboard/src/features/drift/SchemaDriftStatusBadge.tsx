import type { SchemaDriftStatus } from '../../api/drift'
import { Badge } from '../../components'

export function DriftStatusBadge({ status }: { status: SchemaDriftStatus }) {
  const tone = status === 'approved' ? 'allow' : status === 'pending' ? 'warn' : status === 'blocked' ? 'block' : 'neutral'
  return <Badge tone={tone}>{status}</Badge>
}
