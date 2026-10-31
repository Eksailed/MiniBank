import { createBrowserRouter, createMemoryRouter, type RouteObject } from 'react-router'
import { AccountPage, HomePage } from '../features/accounts'
import { EntriesPage } from '../features/entries'
import { DepositPage, TransferPage } from '../features/transfers'
import { AppShell } from '../shared/ui'

const routes: RouteObject[] = [
  {
    element: <AppShell />,
    children: [
      { path: '/', element: <HomePage /> },
      { path: '/accounts/:id', element: <AccountPage /> },
      { path: '/accounts/:id/deposit', element: <DepositPage /> },
      { path: '/accounts/:id/transfer', element: <TransferPage /> },
      { path: '/accounts/:id/entries', element: <EntriesPage /> },
    ],
  },
]

export function createAppRouter(initialEntries?: string[]) {
  return initialEntries ? createMemoryRouter(routes, { initialEntries }) : createBrowserRouter(routes)
}
