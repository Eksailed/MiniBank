import { IconChevronRight } from '@tabler/icons-react'
import { Link } from 'react-router'
import { formatMoney } from '../../shared/money'
import { ErrorNotice } from '../../shared/ui'
import { useAccounts } from '../accounts'
import styles from './PaymentsPage.module.css'

/** Выбор счёта-источника: сам перевод живёт на экране счёта. */
export function PaymentsPage() {
  const accounts = useAccounts()

  return (
    <>
      <h1 className={styles.title}>Платежи</h1>
      <p className={styles.hint}>Выберите счёт, с которого хотите перевести</p>
      {accounts.isPending && <p className={styles.hint}>Загрузка…</p>}
      {accounts.isError && <ErrorNotice error={accounts.error} onRetry={() => accounts.refetch()} />}
      {accounts.data?.length === 0 && <p className={styles.hint}>Сначала откройте счёт на главной</p>}
      {accounts.data && accounts.data.length > 0 && (
        <ul className={styles.list}>
          {accounts.data.map((account) => (
            <li key={account.id}>
              <Link to={`/accounts/${account.id}/transfer`} className={styles.item}>
                <span>
                  <span className={styles.owner}>{account.owner}</span>
                  <span className={styles.balance}>{formatMoney(account.balance, account.currency)}</span>
                </span>
                <IconChevronRight size={20} stroke={1.8} aria-hidden="true" />
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
