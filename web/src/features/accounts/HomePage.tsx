import { Link } from 'react-router'
import type { Account } from '../../api/types'
import { formatMoney } from '../../shared/money'
import { ErrorNotice } from '../../shared/ui'
import { CreateAccountForm } from './CreateAccountForm'
import { useAccounts } from './queries'
import styles from './HomePage.module.css'

function AccountCard({ account }: { account: Account }) {
  return (
    <Link to={`/accounts/${account.id}`} className={styles.card}>
      <span className={styles.owner}>{account.owner}</span>
      <span className={styles.balance}>{formatMoney(account.balance, account.currency)}</span>
    </Link>
  )
}

export function HomePage() {
  const accounts = useAccounts()

  return (
    <>
      <h1 className={styles.title}>Мои счета</h1>
      {accounts.isPending && <p className={styles.muted}>Загрузка…</p>}
      {accounts.isError && <ErrorNotice error={accounts.error} onRetry={() => accounts.refetch()} />}
      {accounts.data?.length === 0 && <p className={styles.empty}>Откройте первый счёт</p>}
      {accounts.data && accounts.data.length > 0 && (
        <ul className={styles.list}>
          {accounts.data.map((account) => (
            <li key={account.id}>
              <AccountCard account={account} />
            </li>
          ))}
        </ul>
      )}
      <CreateAccountForm />
    </>
  )
}
