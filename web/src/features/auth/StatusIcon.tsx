import { IconCheck, IconMail, IconX } from '@tabler/icons-react'
import styles from './auth.module.css'

const icons = {
  mail: { Icon: IconMail, className: styles.iconMail },
  success: { Icon: IconCheck, className: styles.iconSuccess },
  error: { Icon: IconX, className: styles.iconError },
}

/** Крупная круглая иконка на экранах «Проверьте почту», «Email подтверждён» и при ошибке ссылки. */
export function StatusIcon({ kind }: { kind: keyof typeof icons }) {
  const { Icon, className } = icons[kind]
  return (
    <span className={`${styles.statusIcon} ${className}`} aria-hidden="true">
      <Icon size={32} stroke={2} />
    </span>
  )
}
