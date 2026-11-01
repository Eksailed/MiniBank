import type { ReactNode } from 'react'
import { Link } from 'react-router'
import { ApiError } from '../../api/errors'
import type { Account } from '../../api/types'
import { isGuid } from '../../shared/guid'
import { ErrorNotice } from '../../shared/ui'
import { useAccount } from './queries'
import styles from './AccountGate.module.css'

function NotFound() {
  return (
    <div className={styles.notFound}>
      <h1>Счёт не найден</h1>
      <p className={styles.muted}>Проверьте ссылку: открыть можно только свой счёт.</p>
      <Link to="/">К моим счетам</Link>
    </div>
  )
}

/** Загружает счёт и показывает «не найден» / ошибку / загрузку вместо содержимого, пока счёта нет. */
export function AccountGate({ id, children }: { id: string; children: (account: Account) => ReactNode }) {
  const account = useAccount(id)

  if (!isGuid(id) || (account.error instanceof ApiError && account.error.status === 404)) {
    return <NotFound />
  }
  if (account.isError) return <ErrorNotice error={account.error} onRetry={() => account.refetch()} />
  if (!account.data) return <p className={styles.muted}>Загрузка…</p>
  return <>{children(account.data)}</>
}
