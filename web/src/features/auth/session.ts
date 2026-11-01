const KEY = 'minibank.token'

const listeners = new Set<() => void>()

function storage(): Storage | undefined {
  try {
    return globalThis.sessionStorage
  } catch {
    return undefined
  }
}

/** Токен живёт в sessionStorage: закрыл вкладку — вышел; localStorage не используем. */
export function getToken(): string | null {
  return storage()?.getItem(KEY) ?? null
}

export function setToken(token: string): void {
  storage()?.setItem(KEY, token)
  listeners.forEach((notify) => notify())
}

export function clearToken(): void {
  storage()?.removeItem(KEY)
  listeners.forEach((notify) => notify())
}

export function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}
