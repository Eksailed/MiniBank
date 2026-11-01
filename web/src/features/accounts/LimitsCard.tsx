import { useState, type FormEvent } from 'react'
import type { Limits } from '../../api/types'
import {
  formatAmountInput,
  formatMoney,
  moneyErrorText,
  parseMoney,
  type CurrencyCode,
} from '../../shared/money'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { useLimits, usePutLimits } from './queries'
import styles from './LimitsCard.module.css'

interface FieldErrors {
  perOperation?: string
  daily?: string
}

/** Проверка до запроса: сервер проверяет то же самое, но ответ 400 — не повод заставлять ждать. */
function validate(
  perOperationText: string,
  dailyText: string,
  limits: Limits,
  currency: CurrencyCode,
): { errors: FieldErrors } | { values: { perOperation: bigint; daily: bigint } } {
  const perOperation = parseMoney(perOperationText)
  const daily = parseMoney(dailyText)
  const errors: FieldErrors = {}

  if (!perOperation.ok) errors.perOperation = moneyErrorText(perOperation.error)
  else if (perOperation.minor > limits.maxPerOperation) {
    errors.perOperation = `Не больше ${formatMoney(limits.maxPerOperation, currency)}`
  }

  if (!daily.ok) errors.daily = moneyErrorText(daily.error)
  else if (daily.minor > limits.maxDaily) errors.daily = `Не больше ${formatMoney(limits.maxDaily, currency)}`

  if (perOperation.ok && daily.ok && !errors.perOperation && !errors.daily && daily.minor < perOperation.minor) {
    errors.daily = 'Дневной лимит не может быть меньше лимита на операцию'
  }

  if (errors.perOperation || errors.daily || !perOperation.ok || !daily.ok) return { errors }
  return { values: { perOperation: perOperation.minor, daily: daily.minor } }
}

function LimitsForm({ id, currency, limits }: { id: string; currency: CurrencyCode; limits: Limits }) {
  const [perOperation, setPerOperation] = useState(() => formatAmountInput(limits.perOperation))
  const [daily, setDaily] = useState(() => formatAmountInput(limits.daily))
  const [errors, setErrors] = useState<FieldErrors>({})
  const save = usePutLimits(id)

  function edited() {
    setErrors({})
    if (save.isError || save.isSuccess) save.reset()
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (save.isPending) return
    const result = validate(perOperation, daily, limits, currency)
    if ('errors' in result) {
      setErrors(result.errors)
      return
    }
    setErrors({})
    save.mutate(result.values)
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate>
      <TextField
        label="Лимит на операцию"
        inputMode="decimal"
        autoComplete="off"
        value={perOperation}
        error={errors.perOperation}
        onChange={(e) => {
          setPerOperation(e.target.value)
          edited()
        }}
      />
      <TextField
        label="Лимит в сутки"
        inputMode="decimal"
        autoComplete="off"
        value={daily}
        error={errors.daily}
        onChange={(e) => {
          setDaily(e.target.value)
          edited()
        }}
      />
      {save.isError && <ErrorNotice error={save.error} />}
      {save.isSuccess && (
        <p role="status" className={styles.saved}>
          Лимиты сохранены
        </p>
      )}
      <Button type="submit" variant="secondary" pending={save.isPending}>
        Сохранить лимиты
      </Button>
    </form>
  )
}

export function LimitsCard({ id, currency }: { id: string; currency: CurrencyCode }) {
  const limits = useLimits(id)

  return (
    <section className={styles.card}>
      <h2 className={styles.heading}>Лимиты</h2>
      {limits.isPending && <p className={styles.muted}>Загрузка…</p>}
      {limits.isError && <ErrorNotice error={limits.error} onRetry={() => limits.refetch()} />}
      {limits.data && (
        <>
          <dl className={styles.stats}>
            <div>
              <dt>Потрачено сегодня</dt>
              <dd>{formatMoney(limits.data.spentToday, currency)}</dd>
            </div>
            <div>
              <dt>Осталось сегодня</dt>
              <dd>{formatMoney(limits.data.remainingToday, currency)}</dd>
            </div>
          </dl>
          <LimitsForm id={id} currency={currency} limits={limits.data} />
        </>
      )}
    </section>
  )
}
