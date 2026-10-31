import { IconMoon, IconSun } from '@tabler/icons-react'
import styles from './ThemeToggle.module.css'
import { useTheme } from './theme'

export function ThemeToggle() {
  const { theme, toggle } = useTheme()
  return (
    <button type="button" className={styles.toggle} aria-label="Переключить тему" onClick={toggle}>
      {theme === 'light' ? <IconMoon size={20} stroke={1.8} /> : <IconSun size={20} stroke={1.8} />}
    </button>
  )
}
