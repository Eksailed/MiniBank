import { createBrowserRouter, createMemoryRouter, type RouteObject } from 'react-router'
import { AccountPage, HomePage } from '../features/accounts'
import { AppShell } from '../shared/ui'

// Заглушки заменяются настоящими страницами в следующих задачах.
const Placeholder = () => null

const routes: RouteObject[] = [
  {
    element: <AppShell />,
    children: [
      { path: '/', element: <HomePage /> },
      { path: '/accounts/:id', element: <AccountPage /> },
      { path: '/accounts/:id/deposit', element: <Placeholder /> },
      { path: '/accounts/:id/transfer', element: <Placeholder /> },
      { path: '/accounts/:id/entries', element: <Placeholder /> },
    ],
  },
]

export function createAppRouter(initialEntries?: string[]) {
  return initialEntries ? createMemoryRouter(routes, { initialEntries }) : createBrowserRouter(routes)
}
