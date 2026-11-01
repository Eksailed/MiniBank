import { useState } from 'react'
import { Link, useParams } from 'react-router'
import type { Account } from '../../api/types'
import { Button, ErrorNotice } from '../../shared/ui'
import { AccountGate, EntryRow } from '../accounts'
import { EMPTY_FILTER, filterError, toRequestFilter, type EntryFilter } from './filter'
import { useDownloadStatement, useEntriesInfinite } from './queries'
import { StatementFilter } from './StatementFilter'
import styles from './EntriesPage.module.css'

const PAGE_SIZE = 20

function Statement({ account }: { account: Account }) {
  const [filter, setFilter] = useState<EntryFilter>(EMPTY_FILTER)
  // Список перезапрашивается только по корректному периоду; пока период перевёрнут, остаётся прежняя выписка.
  const [applied, setApplied] = useState<EntryFilter>(EMPTY_FILTER)
  const periodError = filterError(filter)
  const query = useEntriesInfinite(account.id, toRequestFilter(applied), PAGE_SIZE)
  const download = useDownloadStatement(account.id)
  const entries = query.data?.pages.flat() ?? []

  return (
    <>
      <Link to={`/accounts/${account.id}`} className={styles.back}>
        ← К счёту
      </Link>
      <h1 className={styles.title}>Выписка</h1>
      <StatementFilter
        value={filter}
        error={periodError}
        onChange={(next) => {
          setFilter(next)
          if (!filterError(next)) setApplied(next)
          if (download.isError) download.reset()
        }}
      />
      <Button
        variant="secondary"
        pending={download.isPending}
        onClick={() => {
          if (!periodError) download.mutate(toRequestFilter(filter))
        }}
      >
        Скачать CSV
      </Button>
      {download.isError && <ErrorNotice error={download.error} />}
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
