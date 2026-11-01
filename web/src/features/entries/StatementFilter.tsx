import { TextField } from '../../shared/ui'
import type { EntryFilter } from './filter'
import styles from './StatementFilter.module.css'

/** Период (обе даты включительно) и вид операций; `error` относится к полю «С». */
export function StatementFilter({
  value,
  error,
  onChange,
}: {
  value: EntryFilter
  error?: string
  onChange: (next: EntryFilter) => void
}) {
  return (
    <div className={styles.filter}>
      <TextField
        label="С"
        type="date"
        value={value.from}
        error={error}
        onChange={(e) => onChange({ ...value, from: e.target.value })}
      />
      <TextField label="По" type="date" value={value.to} onChange={(e) => onChange({ ...value, to: e.target.value })} />
      <div className={styles.select}>
        <label htmlFor="direction">Операции</label>
        <select
          id="direction"
          value={value.direction}
          onChange={(e) => onChange({ ...value, direction: e.target.value as EntryFilter['direction'] })}
        >
          <option value="">Все</option>
          <option value="in">Зачисления</option>
          <option value="out">Списания</option>
        </select>
      </div>
    </div>
  )
}
