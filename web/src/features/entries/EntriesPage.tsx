import { Link, useParams } from 'react-router'
import type { Account } from '../../api/types'
import { Button, ErrorNotice } from '../../shared/ui'
import { AccountGate, EntryRow } from '../accounts'
import { useEntriesInfinite } from './queries'
import styles from './EntriesPage.module.css'

const PAGE_SIZE = 20

function Statement({ account }: { account: Account }) {
  const query = useEntriesInfinite(account.id, PAGE_SIZE)
  const entries = query.data?.pages.flat() ?? []

  return (
    <>
      <Link to={`/accounts/${account.id}`} className={styles.back}>
        ← К счёту
      </Link>
      <h1 className={styles.title}>Выписка</h1>
      {query.isPending && <p className={styles.muted}>Загрузка…</p>}
      {query.isError && (
        <ErrorNotice
          error={query.error}
          onRetry={() => (query.data ? query.fetchNextPage() : query.refetch())}
        />
      )}
      {query.data && entries.length === 0 && (
        <div className={styles.empty}>
          <p className={styles.muted}>Операций пока нет</p>
          <Link to={`/accounts/${account.id}/deposit`}>Пополнить</Link>
        </div>
      )}
      <div className={styles.list}>
        {entries.map((entry) => (
          <EntryRow key={entry.id} entry={entry} currency={account.currency} />
        ))}
      </div>
      {query.hasNextPage && (
        <Button variant="secondary" pending={query.isFetchingNextPage} onClick={() => query.fetchNextPage()}>
          Показать ещё
        </Button>
      )}
    </>
  )
}

export function EntriesPage() {
  const { id = '' } = useParams()
  return <AccountGate id={id}>{(account) => <Statement account={account} />}</AccountGate>
}
