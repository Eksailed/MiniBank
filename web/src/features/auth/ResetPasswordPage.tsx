import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useLocation } from 'react-router'
import { api } from '../../api/client'
import { ApiError } from '../../api/errors'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { AuthLogo } from './AuthLogo'
import { StatusIcon } from './StatusIcon'
import { MIN_PASSWORD_LENGTH, passwordStrength } from './validation'
import styles from './auth.module.css'

interface FieldErrors {
  password?: string
  repeat?: string
}

export function ResetPasswordPage() {
  // Токен лежит во фрагменте (#token=…), как на странице подтверждения: на сервер он уходит только в теле POST.
  const { hash } = useLocation()
  const token = new URLSearchParams(hash.replace(/^#/, '')).get('token')?.trim() ?? ''
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const reset = useMutation({ mutationFn: (newPassword: string) => api.resetPassword(token, newPassword) })

  function submit(event: FormEvent) {
    event.preventDefault()
    const next: FieldErrors = {
      password: password.length >= MIN_PASSWORD_LENGTH ? undefined : `Пароль — не короче ${MIN_PASSWORD_LENGTH} символов`,
      repeat: password === repeat ? undefined : 'Пароли не совпадают',
    }
    setErrors(next)
    if (next.password || next.repeat) return
    reset.mutate(password)
  }

  if (reset.isSuccess) {
    return (
      <div className={`${styles.screen} ${styles.center}`}>
        <StatusIcon kind="success" />
        <h1 className={styles.heading}>Пароль изменён</h1>
        <p className={styles.text}>Войдите с новым паролем. Прежние сессии завершены.</p>
        <Link to="/login" className={`${styles.primaryLink} ${styles.fullWidth}`}>
          Войти
        </Link>
      </div>
    )
  }

  const invalidLink = token === '' || (reset.error instanceof ApiError && reset.error.code === 'invalid_reset_token')
  if (invalidLink) {
    const error = reset.error ?? new ApiError(400, { code: 'invalid_reset_token' })
    return (
      <div className={`${styles.screen} ${styles.center}`}>
        <StatusIcon kind="error" />
        <div className={styles.fullWidth}>
          <ErrorNotice error={error} />
        </div>
        <Link to="/forgot-password" className={`${styles.primaryLink} ${styles.fullWidth}`}>
          Запросить новую
        </Link>
      </div>
    )
  }

  const strength = passwordStrength(password)

  return (
    <div className={styles.screen}>
      <AuthLogo />
      <form className={styles.form} onSubmit={submit} noValidate>
        <h1 className={styles.heading}>Новый пароль</h1>
        <TextField
          label="Новый пароль"
          type="password"
          autoComplete="new-password"
          value={password}
          error={errors.password}
          onChange={(e) => setPassword(e.target.value)}
        />
        <div className={styles.strength} aria-hidden="true">
          {[1, 2, 3, 4].map((level) => (
            <span key={level} className={strength >= level ? styles[`on${strength}`] : undefined} />
          ))}
        </div>
        <TextField
          label="Повторите пароль"
          type="password"
          autoComplete="new-password"
          value={repeat}
          error={errors.repeat}
          onChange={(e) => setRepeat(e.target.value)}
        />
        {reset.isError && <ErrorNotice error={reset.error} />}
        <Button type="submit" pending={reset.isPending}>
          Сменить пароль
        </Button>
      </form>
    </div>
  )
}
