import type { Limits } from '../../api/types'
import { formatMoney, type CurrencyCode } from '../../shared/money'
import styles from './SpentToday.module.css'

/** Доля дневного лимита, уже потраченная; бигинты делим с точностью до процента. */
function spentPercent({ spentToday, daily }: Limits): number {
  if (daily <= 0n) return 0
  return Math.min(100, Number((spentToday * 100n) / daily))
}

export function SpentToday({ limits, currency }: { limits: Limits; currency: CurrencyCode }) {
  const percent = spentPercent(limits)
  return (
    <dl className={styles.stats}>
      <div>
        <dt>Потрачено сегодня</dt>
        <dd>{formatMoney(limits.spentToday, currency)}</dd>
      </div>
      <div className={styles.right}>
        <dt>Осталось сегодня</dt>
        <dd>{formatMoney(limits.remainingToday, currency)}</dd>
      </div>
      <div
        className={styles.bar}
        role="progressbar"
        aria-label="Дневной лимит"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent}
      >
        <span style={{ width: `${percent}%` }} />
      </div>
    </dl>
  )
}
