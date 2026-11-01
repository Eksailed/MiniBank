import { useQuery } from '@tanstack/react-query'
import { Link, useLocation } from 'react-router'
import { api } from '../../api/client'
import { ApiError } from '../../api/errors'
import { ErrorNotice } from '../../shared/ui'
import { ResendConfirmation } from './ResendConfirmation'
import { StatusIcon } from './StatusIcon'
import styles from './auth.module.css'

export function ConfirmEmailPage() {
  // Токен лежит во фрагменте (#token=…): браузер не отправляет его ни серверу, ни в Referer, и он не попадает в логи nginx.
  const { hash } = useLocation()
  const token = new URLSearchParams(hash.replace(/^#/, '')).get('token')?.trim() ?? ''

  // useQuery, а не эффект: токен одноразовый, а запрос должен уйти ровно один раз даже при двойном монтировании StrictMode.
  const confirm = useQuery({
    queryKey: ['confirm-email', token],
    queryFn: () => api.confirmEmail(token).then(() => true),
    enabled: token !== '',
    retry: false,
    staleTime: Infinity,
    gcTime: Infinity,
  })

  if (confirm.isSuccess) {
    return (
      <div className={`${styles.screen} ${styles.center}`}>
        <StatusIcon kind="success" />
        <h1 className={styles.heading}>Email подтверждён</h1>
        <p className={styles.text}>Теперь можно войти в MiniBank.</p>
        <Link to="/login" className={`${styles.primaryLink} ${styles.fullWidth}`}>
          Войти
        </Link>
      </div>
    )
  }

  if (token === '' || confirm.isError) {
    const error = token === '' ? new ApiError(400, { code: 'invalid_confirmation_token' }) : confirm.error
    const isInvalidToken = error instanceof ApiError && error.code === 'invalid_confirmation_token'
    return (
      <div className={`${styles.screen} ${styles.center}`}>
        <StatusIcon kind="error" />
        <div className={styles.fullWidth}>
          <ErrorNotice error={error} onRetry={isInvalidToken ? undefined : () => void confirm.refetch()} />
        </div>
        {isInvalidToken && (
          <div className={styles.fullWidth}>
            <ResendConfirmation label="Отправить новое письмо" />
          </div>
        )}
      </div>
    )
  }

  return (
    <div className={`${styles.screen} ${styles.center}`}>
      <p className={styles.status}>Подтверждаем email…</p>
    </div>
  )
}
