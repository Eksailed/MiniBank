export const MIN_PASSWORD_LENGTH = 8

export const isEmail = (value: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)

/** Сколько из четырёх делений индикатора заполнить; только подсказка, правила валидации — в MIN_PASSWORD_LENGTH. */
export function passwordStrength(password: string): 0 | 1 | 2 | 3 | 4 {
  if (!password) return 0
  let score = 1
  if (password.length >= MIN_PASSWORD_LENGTH) score++
  if (password.length >= 12) score++
  if (/[a-zа-я]/i.test(password) && /\d/.test(password) && /[^a-zа-я\d]|[A-ZА-Я]/.test(password)) score++
  return Math.min(score, 4) as 1 | 2 | 3 | 4
}
