import { useQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import { useAuth } from './AuthProvider'
import styles from './auth.module.css'

/** Имя пользователя и выход; кладётся в шапку приложения. */
export function UserMenu() {
  const { signOut } = useAuth()
  const me = useQuery({ queryKey: ['me'], queryFn: api.getMe, staleTime: 5 * 60_000 })

  return (
    <div className={styles.menu}>
      {me.data && <span className={styles.name}>{me.data.displayName}</span>}
      <button type="button" className={styles.logout} onClick={signOut}>
        Выйти
      </button>
    </div>
  )
}
