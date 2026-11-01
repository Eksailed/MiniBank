import { IconX } from '@tabler/icons-react'
import { useEffect, type ReactNode } from 'react'
import styles from './Sheet.module.css'

/** Нижний лист поверх экрана: закрывается крестиком, Escape и нажатием на затемнение. */
export function Sheet({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => event.key === 'Escape' && onClose()
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className={styles.overlay} onClick={onClose}>
      <div className={styles.sheet} role="dialog" aria-modal="true" aria-label={title} onClick={(e) => e.stopPropagation()}>
        <div className={styles.header}>
          <h2 className={styles.title}>{title}</h2>
          <button type="button" className={styles.close} aria-label="Закрыть" onClick={onClose}>
            <IconX size={20} stroke={1.8} />
          </button>
        </div>
        {children}
      </div>
    </div>
  )
}
