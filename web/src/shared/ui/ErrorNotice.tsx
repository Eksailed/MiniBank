import { describeError } from '../../api/errors'
import styles from './ErrorNotice.module.css'

export function ErrorNotice({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const { title, hint, retryable } = describeError(error)
  return (
    <div role="alert" className={styles.notice}>
      <p className={styles.title}>{title}</p>
      {hint && <p className={styles.hint}>{hint}</p>}
      {retryable && onRetry && (
        <button type="button" className={styles.retry} onClick={onRetry}>
          Повторить
        </button>
      )}
    </div>
  )
}
