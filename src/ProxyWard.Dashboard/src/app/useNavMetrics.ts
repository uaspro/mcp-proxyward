import { useEffect, useState } from 'react'
import { getAuditEvents } from '../api/audit'
import { getSchemaDrifts } from '../api/drift'

export function useNavMetrics() {
  const [metrics, setMetrics] = useState<{
    auditTotal: number | null
    pendingDriftTotal: number | null
  }>({ auditTotal: null, pendingDriftTotal: null })

  useEffect(() => {
    const controller = new AbortController()

    Promise.allSettled([
      getAuditEvents({ offset: 0, pageSize: 1 }, controller.signal),
      getSchemaDrifts({ status: 'pending', offset: 0, pageSize: 1 }, controller.signal),
    ]).then(([auditResult, driftResult]) => {
      if (controller.signal.aborted) {
        return
      }

      setMetrics({
        auditTotal: auditResult.status === 'fulfilled' ? auditResult.value.totalCount : null,
        pendingDriftTotal: driftResult.status === 'fulfilled' ? driftResult.value.totalCount : null,
      })
    })

    return () => controller.abort()
  }, [])

  return metrics
}

export function formatNavCount(count: number | null): string | null {
  if (count === null) {
    return null
  }

  return new Intl.NumberFormat(undefined, {
    notation: 'compact',
    maximumFractionDigits: 1,
  }).format(count)
}
