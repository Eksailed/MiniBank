import { useQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import { useAuth } from '../auth'
import { ThemeToggle } from '../../shared/ThemeToggle'
import { Button } from '../../shared/ui'
import styles from './ProfilePage.module.css'

export function ProfilePage() {
  const { signOut } = useAuth()
  const me = useQuery({ queryKey: ['me'], queryFn: api.getMe, staleTime: 5 * 60_000 })

  return (
    <>
      <h1 className={styles.title}>Профиль</h1>
      <section className={styles.card} aria-label="Данные пользователя">
        <span className={styles.avatar} aria-hidden="true">
          {me.data?.displayName.charAt(0).toUpperCase()}
        </span>
        <div className={styles.who}>
          <p className={styles.name}>{me.data?.displayName ?? 'Загрузка…'}</p>
          <p className={styles.email}>{me.data?.email}</p>
        </div>
      </section>
      <section className={styles.row}>
        <span>Тёмная тема</span>
        <ThemeToggle />
      </section>
      <Button variant="secondary" onClick={signOut}>
        Выйти
      </Button>
    </>
  )
}
