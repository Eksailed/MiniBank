import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { api } from '../../api/client'
import { ApiError } from '../../api/errors'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { ResendConfirmation } from './ResendConfirmation'
import { isEmail, MIN_PASSWORD_LENGTH } from './validation'
import styles from './auth.module.css'

interface FieldErrors {
  email?: string
  password?: string
  displayName?: string
}

export function RegisterPage() {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const register = useMutation({ mutationFn: api.register })

  function submit(event: FormEvent) {
    event.preventDefault()
    const next: FieldErrors = {
      email: isEmail(email.trim()) ? undefined : 'Введите корректный email',
      password: password.length >= MIN_PASSWORD_LENGTH ? undefined : `Пароль — не короче ${MIN_PASSWORD_LENGTH} символов`,
      displayName: displayName.trim() ? undefined : 'Введите имя',
    }
    setErrors(next)
    if (next.email || next.password || next.displayName) return
    register.mutate({ email: email.trim(), password, displayName: displayName.trim() })
  }

  if (register.isSuccess) {
    return (
      <div className={styles.form}>
        <h1 className={styles.heading}>Проверьте почту</h1>
        <p className={styles.text}>
          Мы отправили письмо на {email.trim()}. Перейдите по ссылке из письма, чтобы подтвердить адрес.
        </p>
        <ResendConfirmation email={email.trim()} />
        <Link to="/login">Перейти ко входу</Link>
      </div>
    )
  }

  const emailTaken = register.error instanceof ApiError && register.error.code === 'email_taken'
  const emailError = errors.email ?? (emailTaken ? 'Этот email уже зарегистрирован' : undefined)

  return (
    <>
      <form className={styles.form} onSubmit={submit} noValidate>
        <h1 className={styles.heading}>Регистрация</h1>
        <TextField
          label="Email"
          type="email"
          autoComplete="username"
          value={email}
          error={emailError}
          onChange={(e) => setEmail(e.target.value)}
        />
        <TextField
          label="Пароль"
          type="password"
          autoComplete="new-password"
          value={password}
          error={errors.password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <TextField
          label="Имя"
          autoComplete="name"
          maxLength={100}
          value={displayName}
          error={errors.displayName}
          onChange={(e) => setDisplayName(e.target.value)}
        />
        {register.isError && !emailTaken && <ErrorNotice error={register.error} />}
        <Button type="submit" pending={register.isPending}>
          Зарегистрироваться
        </Button>
      </form>
      <p className={styles.links}>
        Уже есть аккаунт? <Link to="/login">Войти</Link>
      </p>
    </>
  )
}
