import type { ReactNode } from 'react'
import { Link, Outlet } from 'react-router'
import { ThemeToggle } from '../ThemeToggle'
import styles from './AppShell.module.css'

/** `actions` — правая часть шапки (имя пользователя, выход); на публичных страницах пусто. */
export function AppShell({ actions }: { actions?: ReactNode }) {
  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <Link to="/" className={styles.logo}>
          MiniBank
        </Link>
        <div className={styles.actions}>
          {actions}
          <ThemeToggle />
        </div>
      </header>
      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  )
}
