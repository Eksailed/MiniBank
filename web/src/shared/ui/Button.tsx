import type { ButtonHTMLAttributes } from 'react'
import styles from './Button.module.css'

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  pending?: boolean
  variant?: 'primary' | 'secondary'
}

export function Button({
  pending = false,
  variant = 'primary',
  type = 'button',
  disabled,
  className,
  children,
  ...rest
}: ButtonProps) {
  return (
    <button
      {...rest}
      type={type}
      disabled={disabled || pending}
      aria-busy={pending || undefined}
      className={`${styles.button} ${styles[variant]} ${className ?? ''}`}
    >
      {children}
    </button>
  )
}
