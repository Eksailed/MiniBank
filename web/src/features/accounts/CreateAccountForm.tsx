import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { CURRENCIES, type CurrencyCode } from '../../shared/money'
import { useAccountDirectory } from './DirectoryProvider'
import { useCreateAccount } from './queries'
import styles from './forms.module.css'

export function CreateAccountForm() {
  const [owner, setOwner] = useState('')
  const [currency, setCurrency] = useState<CurrencyCode>('RUB')
  const [ownerError, setOwnerError] = useState<string>()
  const create = useCreateAccount()
  const directory = useAccountDirectory()
  const navigate = useNavigate()

  function submit(event: FormEvent) {
    event.preventDefault()
    const name = owner.trim()
    if (!name) {
      setOwnerError('Введите имя владельца')
      return
    }
    setOwnerError(undefined)
    create.mutate(
      { owner: name, currency },
      {
        onSuccess: (account) => {
          directory.add(account.id)
          navigate(`/accounts/${account.id}`)
        },
      },
    )
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate>
      <h2 className={styles.heading}>Новый счёт</h2>
      <TextField
        label="Владелец"
        value={owner}
        error={ownerError}
        maxLength={200}
        onChange={(e) => setOwner(e.target.value)}
      />
      <div className={styles.select}>
        <label htmlFor="currency">Валюта</label>
        <select id="currency" value={currency} onChange={(e) => setCurrency(e.target.value as CurrencyCode)}>
          {CURRENCIES.map((code) => (
            <option key={code} value={code}>
              {code}
            </option>
          ))}
        </select>
      </div>
      {create.isError && <ErrorNotice error={create.error} />}
      <Button type="submit" pending={create.isPending}>
        Открыть счёт
      </Button>
    </form>
  )
}
