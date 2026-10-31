import type { Entry } from '../../api/types'
import { formatMoney, type CurrencyCode } from '../../shared/money'
import styles from './EntryRow.module.css'

const dateFormat = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short' })

export function EntryRow({ entry, currency }: { entry: Entry; currency: CurrencyCode }) {
  const credit = entry.amount > 0n
  const amount = `${credit ? '+' : ''}${formatMoney(entry.amount, currency)}`

  return (
    <div className={styles.row}>
      <div>
        <p className={styles.kind}>{credit ? 'Зачисление' : 'Списание'}</p>
        <p className={styles.date}>{dateFormat.format(new Date(entry.createdAt))}</p>
      </div>
      <div className={styles.right}>
        <p className={credit ? styles.credit : styles.debit}>{amount}</p>
        <p className={styles.date}>Остаток {formatMoney(entry.balanceAfter, currency)}</p>
      </div>
    </div>
  )
}
