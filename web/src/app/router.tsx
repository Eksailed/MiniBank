import { createBrowserRouter, createMemoryRouter, type RouteObject } from 'react-router'
import { AccountPage, HomePage } from '../features/accounts'
import { ConfirmEmailPage, LoginPage, RegisterPage, RequireAuth } from '../features/auth'
import { PaymentsPage } from '../features/payments'
import { ProfilePage } from '../features/profile'
import { EntriesPage } from '../features/entries'
import { DepositPage, TransferPage } from '../features/transfers'
import { AppShell } from '../shared/ui'

const routes: RouteObject[] = [
  {
    element: <AppShell />,
    children: [
      { path: '/login', element: <LoginPage /> },
      { path: '/register', element: <RegisterPage /> },
      { path: '/confirm-email', element: <ConfirmEmailPage /> },
    ],
  },
  {
    element: <RequireAuth />,
    children: [
      {
        element: <AppShell nav />,
        children: [
          { path: '/', element: <HomePage /> },
          { path: '/payments', element: <PaymentsPage /> },
          { path: '/profile', element: <ProfilePage /> },
          { path: '/accounts/:id', element: <AccountPage /> },
          { path: '/accounts/:id/deposit', element: <DepositPage /> },
          { path: '/accounts/:id/transfer', element: <TransferPage /> },
          { path: '/accounts/:id/entries', element: <EntriesPage /> },
        ],
      },
    ],
  },
]

export function createAppRouter(initialEntries?: string[]) {
  return initialEntries ? createMemoryRouter(routes, { initialEntries }) : createBrowserRouter(routes)
}
