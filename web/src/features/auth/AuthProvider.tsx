import { useQueryClient } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useEffect, useMemo, useSyncExternalStore, type ReactNode } from 'react'
import { clearToken, getToken, setToken, subscribe } from './session'

interface AuthState {
  isAuthenticated: boolean
  signIn: (token: string) => void
  signOut: () => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const token = useSyncExternalStore(subscribe, getToken, () => null)

  // Любой выход (кнопка, 401 от сервера) стирает кэш: данные прежнего пользователя не должны достаться следующему.
  useEffect(
    () =>
      subscribe(() => {
        if (getToken() === null) queryClient.clear()
      }),
    [queryClient],
  )

  const signIn = useCallback((value: string) => setToken(value), [])
  const signOut = useCallback(() => clearToken(), [])
  const value = useMemo(
    () => ({ isAuthenticated: token !== null, signIn, signOut }),
    [token, signIn, signOut],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>')
  return value
}
