import { IconArrowsExchange, IconHome, IconUser } from '@tabler/icons-react'
import { NavLink, Outlet } from 'react-router'
import styles from './AppShell.module.css'

const items = [
  { to: '/', label: 'Главная', icon: IconHome, end: true },
  { to: '/payments', label: 'Платежи', icon: IconArrowsExchange, end: false },
  { to: '/profile', label: 'Профиль', icon: IconUser, end: false },
]

/** `nav` — нижняя навигация для экранов после входа; публичные страницы без неё. */
export function AppShell({ nav = false }: { nav?: boolean }) {
  return (
    <div className={`${styles.shell} ${nav ? styles.withNav : ''}`}>
      <main className={styles.main}>
        <Outlet />
      </main>
      {nav && (
        <nav className={styles.nav} aria-label="Основная навигация">
          {items.map(({ to, label, icon: Icon, end }) => (
            <NavLink key={to} to={to} end={end} className={({ isActive }) => `${styles.item} ${isActive ? styles.active : ''}`}>
              <Icon size={24} stroke={1.8} aria-hidden="true" />
              <span>{label}</span>
            </NavLink>
          ))}
        </nav>
      )}
    </div>
  )
}
