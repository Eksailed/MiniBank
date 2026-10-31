import { isGuid } from '../../shared/guid'

/** «Мои счета». В этапе 4 реализация заменяется запросом к API по JWT. */
export interface AccountDirectory {
  list(): string[]
  add(id: string): void
  remove(id: string): void
  subscribe(listener: () => void): () => void
}

export const DIRECTORY_KEY = 'minibank.accounts'

function parseIds(raw: string | null): string[] {
  if (raw === null) return []
  try {
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((item): item is string => typeof item === 'string' && isGuid(item))
  } catch {
    return []
  }
}

function defaultStorage(): Storage | undefined {
  try {
    return globalThis.localStorage
  } catch {
    return undefined
  }
}

export function createLocalStorageDirectory(
  storage: Storage | undefined = defaultStorage(),
  key: string = DIRECTORY_KEY,
): AccountDirectory {
  const listeners = new Set<() => void>()
  // Если хранилище недоступно, счета живут до перезагрузки страницы.
  let memoryRaw: string | null = null
  let cache: { raw: string | null; ids: string[] } = { raw: null, ids: [] }

  function readRaw(): string | null {
    try {
      return storage ? storage.getItem(key) : memoryRaw
    } catch {
      return memoryRaw
    }
  }

  function list(): string[] {
    const raw = readRaw()
    // Одна и та же ссылка, пока данные не изменились (нужно для useSyncExternalStore).
    if (raw !== cache.raw) cache = { raw, ids: parseIds(raw) }
    return cache.ids
  }

  function write(ids: string[]): void {
    const raw = JSON.stringify(ids)
    memoryRaw = raw
    try {
      storage?.setItem(key, raw)
    } catch {
      // нет места или доступ запрещён — остаёмся в памяти
    }
    listeners.forEach((listener) => listener())
  }

  return {
    list,
    add(id) {
      if (!isGuid(id)) return
      const ids = list()
      if (ids.includes(id)) return
      write([...ids, id])
    },
    remove(id) {
      const ids = list()
      if (!ids.includes(id)) return
      write(ids.filter((existing) => existing !== id))
    },
    subscribe(listener) {
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
      }
    },
  }
}
