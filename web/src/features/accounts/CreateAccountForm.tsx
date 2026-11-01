import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Button, ErrorNotice } from '../../shared/ui'
import { CURRENCIES, type CurrencyCode } from '../../shared/money'
import { useCreateAccount } from './queries'
import styles from './forms.module.css'

/** Владелец — сам пользователь, поэтому выбирается только валюта. */
export function CreateAccountForm() {
  const [currency, setCurrency] = useState<CurrencyCode>('RUB')
  const create = useCreateAccount()
  const navigate = useNavigate()

  function submit(event: FormEvent) {
    event.preventDefault()
    create.mutate({ currency }, { onSuccess: (account) => navigate(`/accounts/${account.id}`) })
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate>
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
