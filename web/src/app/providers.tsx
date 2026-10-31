import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { DirectoryProvider, shouldRetry, type AccountDirectory } from '../features/accounts'

function createQueryClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: shouldRetry, refetchOnWindowFocus: false } } })
}

export function AppProviders({
  directory,
  queryClient,
  children,
}: {
  directory?: AccountDirectory
  queryClient?: QueryClient
  children: ReactNode
}) {
  const [client] = useState(() => queryClient ?? createQueryClient())

  return (
    <QueryClientProvider client={client}>
      <DirectoryProvider directory={directory}>{children}</DirectoryProvider>
    </QueryClientProvider>
  )
}
