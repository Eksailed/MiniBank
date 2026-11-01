import { QueryClient } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { RouterProvider } from 'react-router/dom'
import { AppProviders } from '../app/providers'
import { createAppRouter } from '../app/router'
import { createLocalStorageDirectory, type AccountDirectory } from '../features/accounts'
import { setToken } from '../features/auth'

/** По умолчанию пользователь уже вошёл; `token: null` — анонимный посетитель. */
export function renderApp(
  path = '/',
  options: { directory?: AccountDirectory; token?: string | null } = {},
) {
  const { token = 'test-token' } = options
  if (token !== null) setToken(token)
  const directory = options.directory ?? createLocalStorageDirectory()
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const router = createAppRouter([path])
  const utils = render(
    <AppProviders directory={directory} queryClient={queryClient}>
      <RouterProvider router={router} />
    </AppProviders>,
  )
  return { ...utils, directory, router, queryClient }
}
