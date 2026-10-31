import { Link, Outlet } from 'react-router'
import { ThemeToggle } from '../ThemeToggle'
import styles from './AppShell.module.css'

export function AppShell() {
  return (
    <div className={styles.shell}>
      <header className={styles.header}>
        <Link to="/" className={styles.logo}>
          MiniBank
        </Link>
        <ThemeToggle />
      </header>
      <main className={styles.main}>
        <Outlet />
      </main>
    </div>
  )
}
