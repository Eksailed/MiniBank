import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import type { Account } from '../../api/types'
import { isGuid } from '../../shared/guid'
import { moneyErrorText, parseMoney } from '../../shared/money'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { AccountGate, useAccountDirectory } from '../accounts'
import { useTransfer } from './mutations'
import { useIdempotentKey } from './useIdempotentKey'
import styles from './forms.module.css'

function TransferForm({ account }: { account: Account }) {
  const { id, currency } = account
  const [recipient, setRecipient] = useState('')
  const [amount, setAmount] = useState('')
  const [recipientError, setRecipientError] = useState<string>()
  const [amountError, setAmountError] = useState<string>()
  const toId = recipient.trim().toLowerCase()
  const { key, rotate } = useIdempotentKey([id, toId, amount])
  const transfer = useTransfer()
  const { ids } = useAccountDirectory()
  const navigate = useNavigate()

  function edited() {
    setRecipientError(undefined)
    setAmountError(undefined)
    if (transfer.isError) transfer.reset()
  }

  function submit() {
    if (transfer.isPending) return
    const parsed = parseMoney(amount)
    const nextRecipientError = !isGuid(toId)
      ? 'Это не похоже на ID счёта — он выглядит как 3f2b1c0e-0000-4000-8000-000000000001'
      : toId === id
        ? 'Нельзя перевести на тот же счёт'
        : undefined
    const nextAmountError = parsed.ok ? undefined : moneyErrorText(parsed.error)
    setRecipientError(nextRecipientError)
    setAmountError(nextAmountError)
    if (nextRecipientError || !parsed.ok) return

    transfer.mutate(
      { input: { fromAccountId: id, toAccountId: toId, amount: parsed.minor, currency }, key },
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
      <h1 className={styles.title}>Перевод</h1>
      <p className={styles.currency}>Валюта: {currency}</p>
      <TextField
        label="ID получателя"
        list="my-accounts"
        autoComplete="off"
        spellCheck={false}
        value={recipient}
        error={recipientError}
        onChange={(e) => {
          setRecipient(e.target.value)
          edited()
        }}
      />
      <datalist id="my-accounts">
        {ids
          .filter((other) => other !== id)
          .map((other) => (
            <option key={other} value={other} />
          ))}
      </datalist>
      <TextField
        label="Сумма"
        inputMode="decimal"
        autoComplete="off"
        value={amount}
        error={amountError}
        onChange={(e) => {
          setAmount(e.target.value)
          edited()
        }}
      />
      {transfer.isError && <ErrorNotice error={transfer.error} onRetry={submit} />}
      <Button type="submit" pending={transfer.isPending}>
        Перевести
      </Button>
    </form>
  )
}

export function TransferPage() {
  const { id = '' } = useParams()
  return <AccountGate id={id}>{(account) => <TransferForm account={account} />}</AccountGate>
}
