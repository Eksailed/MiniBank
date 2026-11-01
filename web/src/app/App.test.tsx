import { render, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { expect, test } from 'vitest'
import { server } from '../test/server'
import { setToken } from '../features/auth'
import App from './App'

test('renders the app shell with the home page for a signed-in user', async () => {
  setToken('test-token')
  render(<App />)
  expect(screen.getByRole('link', { name: 'Профиль' })).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Мои счета' })).toBeInTheDocument()
  expect(await screen.findByText('Откройте первый счёт')).toBeInTheDocument()
})

test('sends an anonymous visitor to the login page', () => {
  render(<App />)
  expect(screen.getByRole('heading', { name: 'Вход' })).toBeInTheDocument()
})

test('payments page lists accounts and links to the transfer form', async () => {
  server.use(
    http.get('/api/accounts', () =>
      HttpResponse.json([{ id: '11111111-1111-4111-8111-111111111111', owner: 'Alice', currency: 'RUB', balance: 100000, status: 'Active', isSystem: false, createdAt: '2026-01-01T00:00:00Z' }]),
    ),
  )
  setToken('test-token')
  window.history.pushState({}, '', '/payments')
  render(<App />)
  expect(screen.getByRole('heading', { name: 'Платежи' })).toBeInTheDocument()
  const link = await screen.findByRole('link', { name: /Alice/ })
  expect(link.getAttribute('href')).toMatch(/^\/accounts\/[^/]+\/transfer$/)
  window.history.pushState({}, '', '/')
})
