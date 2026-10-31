import { IconArrowsExchange, IconCopy, IconFileText, IconPlus } from '@tabler/icons-react'
import { useEffect, useRef, useState } from 'react'
import { Link, useParams } from 'react-router'
import type { Account } from '../../api/types'
import type { CurrencyCode } from '../../shared/money'
import { ActionTile, ErrorNotice } from '../../shared/ui'
import { AccountGate } from './AccountGate'
import { BalanceCard } from './BalanceCard'
import { EntryRow } from './EntryRow'
import { useEntries } from './queries'
import styles from './AccountPage.module.css'

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

function AccountView({ account }: { account: Account }) {
  const { id } = account
  const [copied, setCopied] = useState(false)
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined)

  useEffect(() => () => clearTimeout(timer.current), [])

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
      <BalanceCard account={account} />
      <div className={styles.tiles}>
        <ActionTile primary icon={<IconPlus size={26} stroke={1.8} />} label="Пополнить" to={`/accounts/${id}/deposit`} />
        <ActionTile icon={<IconArrowsExchange size={26} stroke={1.8} />} label="Перевести" to={`/accounts/${id}/transfer`} />
        <ActionTile icon={<IconFileText size={26} stroke={1.8} />} label="Выписка" to={`/accounts/${id}/entries`} />
        <ActionTile icon={<IconCopy size={26} stroke={1.8} />} label={copied ? 'Скопировано' : 'Реквизиты'} onClick={copyId} />
      </div>
      <RecentEntries id={id} currency={account.currency} />
    </>
  )
}

export function AccountPage() {
  const { id = '' } = useParams()
  return <AccountGate id={id}>{(account) => <AccountView account={account} />}</AccountGate>
}
