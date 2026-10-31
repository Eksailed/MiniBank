import type { ReactNode } from 'react'
import { Link } from 'react-router'
import styles from './ActionTile.module.css'

export interface ActionTileProps {
  icon: ReactNode
  label: string
  to?: string
  onClick?: () => void
  primary?: boolean
}

export function ActionTile({ icon, label, to, onClick, primary = false }: ActionTileProps) {
  const className = `${styles.tile} ${primary ? styles.primary : ''}`
  const content = (
    <>
      <span className={styles.icon} aria-hidden="true">
        {icon}
      </span>
      <span className={styles.label}>{label}</span>
    </>
  )

  if (to) {
    return (
      <Link to={to} className={className}>
        {content}
      </Link>
    )
  }
  return (
    <button type="button" className={className} onClick={onClick}>
      {content}
    </button>
  )
}
