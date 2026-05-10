export function BarChart({ values }: { values: number[] }) {
  const max = Math.max(...values, 1)

  return (
    <div className="chart-bars" aria-label="Bar chart">
      {values.map((value, index) => (
        <span key={index} style={{ height: `${Math.max(8, (value / max) * 100)}%` }} />
      ))}
    </div>
  )
}

export function Sparkline({ values }: { values: number[] }) {
  const max = Math.max(...values, 1)
  const min = Math.min(...values, 0)
  const range = max - min || 1
  const points = values
    .map((value, index) => {
      const x = values.length === 1 ? 0 : (index / (values.length - 1)) * 100
      const y = 36 - ((value - min) / range) * 34
      return `${x},${y}`
    })
    .join(' ')

  return (
    <svg className="sparkline" viewBox="0 0 100 38" preserveAspectRatio="none" aria-label="Sparkline">
      <polyline points={points} />
    </svg>
  )
}
