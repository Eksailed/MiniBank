import type { Account } from '../../api/types'
import { formatMoney } from '../../shared/money'
import styles from './BalanceCard.module.css'

/** Последние четыре символа номера счёта: по ним счета различают на карточках. */
export const accountMask = (id: string) => `•• ${id.slice(-4).toUpperCase()}`

export function BalanceCard({ account }: { account: Account }) {
  return (
    <section className={styles.card} aria-label="Баланс счёта">
      <div className={styles.top}>
        <span className={styles.owner}>{account.owner}</span>
        <span className={styles.chip}>{account.currency}</span>
      </div>
      <p className={styles.balance} data-testid="balance">
        {formatMoney(account.balance, account.currency)}
      </p>
      <p className={styles.mask}>{accountMask(account.id)}</p>
    </section>
  )
}
