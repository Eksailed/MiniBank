import { useMutation } from '@tanstack/react-query'
import { useEffect, useState, type FormEvent } from 'react'
import { api } from '../../api/client'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { isEmail } from './validation'
import styles from './auth.module.css'

const COOLDOWN_SECONDS = 60

/**
 * Без `email` показывает поле ввода, с `email` — одну кнопку (адрес уже известен, например со страницы входа).
 * Таймер после отправки — подсказка в интерфейсе, а не защита: ограничение повторов живёт на сервере.
 */
export function ResendConfirmation({ email, label = 'Отправить ещё раз' }: { email?: string; label?: string }) {
  const [typed, setTyped] = useState('')
  const [fieldError, setFieldError] = useState<string>()
  const [secondsLeft, setSecondsLeft] = useState(0)
  const resend = useMutation({
    mutationFn: (address: string) => api.resendConfirmation(address),
    onSuccess: () => setSecondsLeft(COOLDOWN_SECONDS),
  })

  useEffect(() => {
    if (secondsLeft <= 0) return
    const timer = setTimeout(() => setSecondsLeft((s) => s - 1), 1000)
    return () => clearTimeout(timer)
  }, [secondsLeft])

  function submit(event: FormEvent) {
    event.preventDefault()
    if (secondsLeft > 0) return
    const address = (email ?? typed).trim()
    if (!isEmail(address)) {
      setFieldError('Введите email')
      return
    }
    setFieldError(undefined)
    resend.mutate(address)
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate>
      {email === undefined && (
        <TextField
          label="Email"
          type="email"
          autoComplete="email"
          value={typed}
          error={fieldError}
          onChange={(e) => setTyped(e.target.value)}
        />
      )}
      {resend.isError && <ErrorNotice error={resend.error} />}
      {resend.isSuccess && <p className={styles.status}>Письмо отправлено</p>}
      <Button type="submit" variant="secondary" pending={resend.isPending} disabled={secondsLeft > 0}>
        {label}
      </Button>
      {secondsLeft > 0 && <p className={styles.hint}>Отправить повторно можно через {secondsLeft} с</p>}
    </form>
  )
}
