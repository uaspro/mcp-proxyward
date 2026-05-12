import type { SchemaDriftFilterOption, SchemaDriftFilterOptions } from '../../api/drift'
import type { DriftFilters } from './SchemaDriftView'
import { driftStatusOptions, driftWindowOptions } from './SchemaDriftView'

export function DriftFilterBar({
  filters,
  filterOptions,
  filterOptionsLoading,
  filterOptionsError,
  onChange,
}: {
  filters: DriftFilters
  filterOptions: SchemaDriftFilterOptions
  filterOptionsLoading: boolean
  filterOptionsError: string | null
  onChange: <K extends keyof DriftFilters>(key: K, value: DriftFilters[K]) => void
}) {
  return (
    <div className="filter-bar">
      <div className="filter-group">
        <span className="filter-label">status</span>
        {driftStatusOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            className={`filter-chip ${filters.status === option.value ? 'active' : ''}`}
            onClick={() => onChange('status', option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
      <FilterValueInput
        id="schema-drift-server-filter-options"
        label="server"
        value={filters.serverId}
        options={filterOptions.servers}
        loading={filterOptionsLoading}
        onChange={(value) => onChange('serverId', value)}
      />
      <FilterValueInput
        id="schema-drift-tool-filter-options"
        label="tool"
        value={filters.toolName}
        options={filterOptions.tools}
        loading={filterOptionsLoading}
        onChange={(value) => onChange('toolName', value)}
      />
      <div className="filter-group time-window">
        <span className="filter-label">time</span>
        {driftWindowOptions.map((option) => (
          <button
            key={option.value}
            type="button"
            className={`filter-chip ${filters.timeWindow === option.value ? 'active' : ''}`}
            onClick={() => onChange('timeWindow', option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
      {filterOptionsError ? <span className="filter-error">{filterOptionsError}</span> : null}
    </div>
  )
}

function FilterValueInput({
  id,
  label,
  value,
  options,
  loading,
  onChange,
}: {
  id: string
  label: string
  value: string
  options: SchemaDriftFilterOption[]
  loading: boolean
  onChange: (value: string) => void
}) {
  return (
    <label className="filter-input">
      <span>{label}</span>
      <input
        type="search"
        list={id}
        placeholder={loading ? 'loading' : 'all'}
        value={value}
        autoComplete="off"
        onChange={(event) => onChange(event.target.value)}
      />
      <datalist id={id}>
        {options.map((option) => (
          <option key={option.value} value={option.value} label={`${option.count.toLocaleString()} items`} />
        ))}
      </datalist>
    </label>
  )
}
