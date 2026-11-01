import { render, screen } from '@testing-library/react'
import { expect, test } from 'vitest'
import { setToken } from '../features/auth'
import App from './App'

test('renders the app shell with the home page for a signed-in user', async () => {
  setToken('test-token')
  render(<App />)
  expect(screen.getByRole('link', { name: 'MiniBank' })).toBeInTheDocument()
  expect(screen.getByRole('heading', { name: 'Мои счета' })).toBeInTheDocument()
  expect(await screen.findByText('Alice')).toBeInTheDocument()
})

test('sends an anonymous visitor to the login page', () => {
  render(<App />)
  expect(screen.getByRole('heading', { name: 'Вход' })).toBeInTheDocument()
})
