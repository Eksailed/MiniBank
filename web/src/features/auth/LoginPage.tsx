import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, Navigate, useSearchParams } from 'react-router'
import { api } from '../../api/client'
import { ApiError } from '../../api/errors'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { useAuth } from './AuthProvider'
import { AuthLogo } from './AuthLogo'
import { ResendConfirmation } from './ResendConfirmation'
import { safeNext } from './safeNext'
import styles from './auth.module.css'

export function LoginPage() {
  const { isAuthenticated, signIn } = useAuth()
  const [params] = useSearchParams()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [errors, setErrors] = useState<{ email?: string; password?: string }>({})
  const login = useMutation({
    mutationFn: (input: { email: string; password: string }) => api.login(input),
    onSuccess: ({ accessToken }) => signIn(accessToken),
  })

  if (isAuthenticated) return <Navigate to={safeNext(params.get('next'))} replace />

  function submit(event: FormEvent) {
    event.preventDefault()
    const next = {
      email: email.trim() ? undefined : 'Введите email',
      password: password ? undefined : 'Введите пароль',
    }
    setErrors(next)
    if (next.email || next.password) return
    login.mutate({ email: email.trim(), password })
  }

  const notConfirmed = login.error instanceof ApiError && login.error.code === 'email_not_confirmed'

  return (
    <div className={styles.screen}>
      <AuthLogo />
      <form className={styles.form} onSubmit={submit} noValidate>
        <h1 className={styles.heading}>Вход</h1>
        <p className={styles.text}>Войдите, чтобы увидеть счета</p>
        <TextField
          label="Email"
          type="email"
          autoComplete="username"
          value={email}
          error={errors.email}
          onChange={(e) => setEmail(e.target.value)}
        />
        <TextField
          label="Пароль"
          type="password"
          autoComplete="current-password"
          value={password}
          error={errors.password}
          onChange={(e) => setPassword(e.target.value)}
        />
        {login.isError && <ErrorNotice error={login.error} />}
        <Button type="submit" pending={login.isPending}>
          Войти
        </Button>
      </form>
      {notConfirmed && <ResendConfirmation email={email.trim()} />}
      <p className={styles.links}>
        Нет аккаунта? <Link to="/register">Зарегистрироваться</Link>
      </p>
    </div>
  )
}
