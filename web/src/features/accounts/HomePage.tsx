import { Link } from 'react-router'
import { formatMoney } from '../../shared/money'
import { AddAccountForm } from './AddAccountForm'
import { CreateAccountForm } from './CreateAccountForm'
import { useAccountDirectory } from './DirectoryProvider'
import { useAccount } from './queries'
import styles from './HomePage.module.css'

function AccountCard({ id }: { id: string }) {
  const { data, isError, isPending } = useAccount(id)

  return (
    <Link to={`/accounts/${id}`} className={styles.card}>
      {data ? (
        <>
          <span className={styles.owner}>{data.owner}</span>
          <span className={styles.balance}>{formatMoney(data.balance, data.currency)}</span>
        </>
      ) : (
        <>
          <span className={styles.owner}>{id.slice(0, 8)}…</span>
          <span className={styles.muted}>{isPending ? 'Загрузка…' : isError ? 'Не удалось загрузить' : ''}</span>
        </>
      )}
    </Link>
  )
}

export function HomePage() {
  const { ids } = useAccountDirectory()

  return (
    <>
      <h1 className={styles.title}>Мои счета</h1>
      {ids.length === 0 ? (
        <p className={styles.empty}>Откройте первый счёт</p>
      ) : (
        <ul className={styles.list}>
          {ids.map((id) => (
            <li key={id}>
              <AccountCard id={id} />
            </li>
          ))}
        </ul>
      )}
      <CreateAccountForm />
      <AddAccountForm />
    </>
  )
}
