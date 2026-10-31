import { IconArrowsExchange, IconCopy, IconFileText, IconPlus } from '@tabler/icons-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError } from '../../api/errors'
import { isGuid } from '../../shared/guid'
import type { CurrencyCode } from '../../shared/money'
import { ActionTile, Button, ErrorNotice } from '../../shared/ui'
import { BalanceCard } from './BalanceCard'
import { useAccountDirectory } from './DirectoryProvider'
import { EntryRow } from './EntryRow'
import { useAccount, useEntries } from './queries'
import styles from './AccountPage.module.css'

function NotFound({ id }: { id: string }) {
  const directory = useAccountDirectory()
  const navigate = useNavigate()

  return (
    <div className={styles.notFound}>
      <h1>Счёт не найден</h1>
      <p className={styles.muted}>Проверьте ID или уберите счёт из списка.</p>
      <Button
        variant="secondary"
        onClick={() => {
          directory.remove(id)
          navigate('/')
        }}
      >
        Убрать из списка
      </Button>
    </div>
  )
}

function RecentEntries({ id, currency }: { id: string; currency: CurrencyCode }) {
  const entries = useEntries(id, 1, 5)

  return (
    <section className={styles.section}>
      <h2>Последние операции</h2>
      {entries.isPending && <p className={styles.muted}>Загрузка…</p>}
      {entries.isError && <ErrorNotice error={entries.error} onRetry={() => entries.refetch()} />}
      {entries.data?.length === 0 && <p className={styles.muted}>Операций пока нет</p>}
      {entries.data?.map((entry) => <EntryRow key={entry.id} entry={entry} currency={currency} />)}
    </section>
  )
}

export function AccountPage() {
  const { id = '' } = useParams()
  const account = useAccount(id)
  const [copied, setCopied] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)

  useEffect(() => () => clearTimeout(timer.current), [])

  if (!isGuid(id) || (account.error instanceof ApiError && account.error.status === 404)) {
    return <NotFound id={id} />
  }
  if (account.isError) return <ErrorNotice error={account.error} onRetry={() => account.refetch()} />
  if (!account.data) return <p className={styles.muted}>Загрузка…</p>

  const { data } = account
  async function copyId() {
    try {
      await navigator.clipboard.writeText(id)
    } catch {
      return
    }
    setCopied(true)
    clearTimeout(timer.current)
    timer.current = setTimeout(() => setCopied(false), 2000)
  }

  return (
    <>
      <Link to="/" className={styles.back}>
        ← Мои счета
      </Link>
      <BalanceCard account={data} />
      <div className={styles.tiles}>
        <ActionTile primary icon={<IconPlus size={26} stroke={1.8} />} label="Пополнить" to={`/accounts/${id}/deposit`} />
        <ActionTile icon={<IconArrowsExchange size={26} stroke={1.8} />} label="Перевести" to={`/accounts/${id}/transfer`} />
        <ActionTile icon={<IconFileText size={26} stroke={1.8} />} label="Выписка" to={`/accounts/${id}/entries`} />
        <ActionTile icon={<IconCopy size={26} stroke={1.8} />} label={copied ? 'Скопировано' : 'Реквизиты'} onClick={copyId} />
      </div>
      <RecentEntries id={id} currency={data.currency} />
    </>
  )
}
