export const MIN_PASSWORD_LENGTH = 8

export const isEmail = (value: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(value)
