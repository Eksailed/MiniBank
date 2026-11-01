import { QueryClient } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import { RouterProvider } from 'react-router/dom'
import { AppProviders } from '../app/providers'
import { createAppRouter } from '../app/router'
import { setToken } from '../features/auth'

/** По умолчанию пользователь уже вошёл; `token: null` — анонимный посетитель. */
export function renderApp(path = '/', options: { token?: string | null } = {}) {
  const { token = 'test-token' } = options
  if (token !== null) setToken(token)
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  const router = createAppRouter([path])
  const utils = render(
    <AppProviders queryClient={queryClient}>
      <RouterProvider router={router} />
    </AppProviders>,
  )
  return { ...utils, router, queryClient }
}
