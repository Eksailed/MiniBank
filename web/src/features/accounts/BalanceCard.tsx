import type { Account } from '../../api/types'
import { formatMoney } from '../../shared/money'
import styles from './BalanceCard.module.css'

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
    </section>
  )
}
