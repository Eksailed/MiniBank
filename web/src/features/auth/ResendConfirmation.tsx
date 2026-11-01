import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { api } from '../../api/client'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { isEmail } from './validation'
import styles from './auth.module.css'

/** Без `email` показывает поле ввода, с `email` — одну кнопку (адрес уже известен, например со страницы входа). */
export function ResendConfirmation({ email }: { email?: string }) {
  const [typed, setTyped] = useState('')
  const [fieldError, setFieldError] = useState<string>()
  const resend = useMutation({ mutationFn: (address: string) => api.resendConfirmation(address) })

  function submit(event: FormEvent) {
    event.preventDefault()
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
      <Button type="submit" variant="secondary" pending={resend.isPending}>
        Отправить письмо ещё раз
      </Button>
    </form>
  )
}
