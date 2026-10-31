import { createContext, useContext, useMemo, useSyncExternalStore, type ReactNode } from 'react'
import { createLocalStorageDirectory, type AccountDirectory } from './directory'

const DirectoryContext = createContext<AccountDirectory | null>(null)

export function DirectoryProvider({ directory, children }: { directory?: AccountDirectory; children: ReactNode }) {
  const value = useMemo(() => directory ?? createLocalStorageDirectory(), [directory])
  return <DirectoryContext.Provider value={value}>{children}</DirectoryContext.Provider>
}

export function useAccountDirectory(): { ids: string[]; add(id: string): void; remove(id: string): void } {
  const directory = useContext(DirectoryContext)
  if (!directory) throw new Error('useAccountDirectory must be used inside DirectoryProvider')

  const ids = useSyncExternalStore(directory.subscribe, directory.list)
  return useMemo(
    () => ({ ids, add: (id: string) => directory.add(id), remove: (id: string) => directory.remove(id) }),
    [ids, directory],
  )
}
