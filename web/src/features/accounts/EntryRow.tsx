import { IconArrowDown, IconArrowUp } from '@tabler/icons-react'
import type { Entry } from '../../api/types'
import { formatMoney, type CurrencyCode } from '../../shared/money'
import styles from './EntryRow.module.css'

const dateFormat = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short' })

export function EntryRow({ entry, currency }: { entry: Entry; currency: CurrencyCode }) {
  const credit = entry.amount > 0n
  const amount = `${credit ? '+' : ''}${formatMoney(entry.amount, currency)}`
  const Arrow = credit ? IconArrowDown : IconArrowUp

  return (
    <div className={styles.row}>
      <span className={`${styles.icon} ${credit ? styles.iconIn : styles.iconOut}`} aria-hidden="true">
        <Arrow size={20} stroke={2} />
      </span>
      <div className={styles.what}>
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
