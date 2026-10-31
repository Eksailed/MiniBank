import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import type { Account } from '../../api/types'
import { moneyErrorText, parseMoney } from '../../shared/money'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { AccountGate } from '../accounts'
import { useDeposit } from './mutations'
import { useIdempotentKey } from './useIdempotentKey'
import styles from './forms.module.css'

function DepositForm({ account }: { account: Account }) {
  const { id, currency } = account
  const [amount, setAmount] = useState('')
  const [amountError, setAmountError] = useState<string>()
  const { key, rotate } = useIdempotentKey([id, amount])
  const deposit = useDeposit(id)
  const navigate = useNavigate()

  function submit() {
    if (deposit.isPending) return
    const parsed = parseMoney(amount)
    if (!parsed.ok) {
      setAmountError(moneyErrorText(parsed.error))
      return
    }
    setAmountError(undefined)
    deposit.mutate(
      { input: { amount: parsed.minor, currency }, key },
      {
        onSuccess: () => {
          rotate()
          navigate(`/accounts/${id}`)
        },
      },
    )
  }

  return (
    <form
      className={styles.form}
      noValidate
      onSubmit={(event) => {
        event.preventDefault()
        submit()
      }}
    >
      <Link to={`/accounts/${id}`} className={styles.back}>
        ← К счёту
      </Link>
      <h1 className={styles.title}>Пополнение</h1>
      <p className={styles.currency}>Валюта: {currency}</p>
      <TextField
        label="Сумма"
        inputMode="decimal"
        autoComplete="off"
        value={amount}
        error={amountError}
        onChange={(e) => {
          setAmount(e.target.value)
          setAmountError(undefined)
          if (deposit.isError) deposit.reset()
        }}
      />
      {deposit.isError && <ErrorNotice error={deposit.error} onRetry={submit} />}
      <Button type="submit" pending={deposit.isPending}>
        Пополнить
      </Button>
    </form>
  )
}

export function DepositPage() {
  const { id = '' } = useParams()
  return <AccountGate id={id}>{(account) => <DepositForm account={account} />}</AccountGate>
}
