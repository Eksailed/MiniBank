import { IconBuildingBank } from '@tabler/icons-react'
import styles from './auth.module.css'

export function AuthLogo() {
  return (
    <div className={styles.logo}>
      <span className={styles.logoMark} aria-hidden="true">
        <IconBuildingBank size={24} stroke={1.8} />
      </span>
      <span className={styles.logoText}>MiniBank</span>
    </div>
  )
}
