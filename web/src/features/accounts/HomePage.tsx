import { IconArrowsExchange, IconFileText, IconPlus } from '@tabler/icons-react'
import { useState, type UIEvent } from 'react'
import { Link } from 'react-router'
import type { Account } from '../../api/types'
import { formatMoney } from '../../shared/money'
import { ActionTile, Button, ErrorNotice, Sheet } from '../../shared/ui'
import { accountMask } from './BalanceCard'
import { CreateAccountForm } from './CreateAccountForm'
import { useAccounts, useLimits } from './queries'
import { SpentToday } from './SpentToday'
import cardStyles from './BalanceCard.module.css'
import styles from './HomePage.module.css'

function AccountCard({ account }: { account: Account }) {
  return (
    <Link to={`/accounts/${account.id}`} className={cardStyles.card}>
      <span className={cardStyles.top}>
        <span>{account.owner}</span>
        <span className={cardStyles.chip}>{account.currency}</span>
      </span>
      <span className={cardStyles.balance}>{formatMoney(account.balance, account.currency)}</span>
      <span className={cardStyles.mask}>{accountMask(account.id)}</span>
    </Link>
  )
}

/** Карточки листаются свайпом (scroll-snap); активная определяется по положению прокрутки. */
function Carousel({ accounts, active, onActive }: { accounts: Account[]; active: number; onActive: (index: number) => void }) {
  function onScroll(event: UIEvent<HTMLUListElement>) {
    const list = event.currentTarget
    const first = list.firstElementChild as HTMLElement | null
    if (!first || first.offsetWidth === 0) return
    const step = first.offsetWidth + 12
    onActive(Math.min(accounts.length - 1, Math.max(0, Math.round(list.scrollLeft / step))))
  }

  return (
    <>
      <ul className={styles.carousel} onScroll={onScroll}>
        {accounts.map((account) => (
          <li key={account.id} className={styles.slide}>
            <AccountCard account={account} />
          </li>
        ))}
      </ul>
      {accounts.length > 1 && (
        <div className={styles.dots} aria-hidden="true">
          {accounts.map((account, index) => (
            <span key={account.id} className={index === active ? styles.dotActive : styles.dot} />
          ))}
        </div>
      )}
    </>
  )
}

function ActiveAccount({ account }: { account: Account }) {
  const limits = useLimits(account.id)
  const { id } = account

  return (
    <>
      <div className={styles.tiles}>
        <ActionTile primary icon={<IconPlus size={26} stroke={1.8} />} label="Пополнить" to={`/accounts/${id}/deposit`} />
        <ActionTile icon={<IconArrowsExchange size={26} stroke={1.8} />} label="Перевести" to={`/accounts/${id}/transfer`} />
        <ActionTile icon={<IconFileText size={26} stroke={1.8} />} label="Выписка" to={`/accounts/${id}/entries`} />
      </div>
      {limits.data && (
        <section className={styles.panel} aria-label="Потрачено сегодня">
          <SpentToday limits={limits.data} currency={account.currency} />
        </section>
      )}
    </>
  )
}

export function HomePage() {
  const accounts = useAccounts()
  const [active, setActive] = useState(0)
  const [creating, setCreating] = useState(false)
  const current = accounts.data?.[active]

  return (
    <>
      <h1 className={styles.title}>Мои счета</h1>
      {accounts.isPending && <p className={styles.muted}>Загрузка…</p>}
      {accounts.isError && <ErrorNotice error={accounts.error} onRetry={() => accounts.refetch()} />}
      {accounts.data?.length === 0 && <p className={styles.empty}>Откройте первый счёт</p>}
      {accounts.data && accounts.data.length > 0 && (
        <>
          <Carousel accounts={accounts.data} active={active} onActive={setActive} />
          {current && <ActiveAccount key={current.id} account={current} />}
        </>
      )}
      <Button variant="secondary" onClick={() => setCreating(true)}>
        Новый счёт
      </Button>
      {creating && (
        <Sheet title="Новый счёт" onClose={() => setCreating(false)}>
          <CreateAccountForm />
        </Sheet>
      )}
    </>
  )
}
