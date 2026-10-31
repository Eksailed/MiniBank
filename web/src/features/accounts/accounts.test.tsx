import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { server } from '../../test/server'
import { renderApp } from '../../test/render'

const ID = '3f2b1c0e-0000-4000-8000-000000000001'
const OTHER = '3f2b1c0e-0000-4000-8000-000000000002'
const UNKNOWN = '3f2b1c0e-0000-4000-8000-0000000000ff'

const account = (id: string, owner: string, balance: number) => ({
  id,
  owner,
  currency: 'RUB',
  balance,
  status: 'Active',
  isSystem: false,
  createdAt: '2026-10-31T10:00:00Z',
})

const entries = [
  { id: 'e1', transferId: 't1', amount: 150000, balanceAfter: 1245000, createdAt: '2026-10-31T10:05:00Z' },
  { id: 'e2', transferId: 't2', amount: -30000, balanceAfter: 1095000, createdAt: '2026-10-30T09:00:00Z' },
  { id: 'e3', transferId: 't3', amount: 1125000, balanceAfter: 1125000, createdAt: '2026-10-29T08:00:00Z' },
]

function problem(status: number, detail: string) {
  return HttpResponse.json(
    { title: 'Problem', status, detail },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

function useAccountHandlers(id = ID, balance = 1245000) {
  server.use(
    http.get(`/api/accounts/${id}`, () => HttpResponse.json(account(id, 'Alice', balance))),
    http.get(`/api/accounts/${id}/entries`, () => HttpResponse.json(entries)),
  )
}

describe('home page', () => {
  it('offers to open the first account when the directory is empty', () => {
    renderApp('/')
    expect(screen.getByText('Откройте первый счёт')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Открыть счёт' })).toBeInTheDocument()
  })

  it('creates an account, remembers it and shows the account screen', async () => {
    let body: unknown
    server.use(
      http.post('/api/accounts', async ({ request }) => {
        body = await request.json()
        return HttpResponse.json(account(ID, 'Alice', 0), { status: 201 })
      }),
      http.get(`/api/accounts/${ID}/entries`, () => HttpResponse.json([])),
    )
    const user = userEvent.setup()
    const { directory } = renderApp('/')

    await user.type(screen.getByLabelText('Владелец'), 'Alice')
    await user.click(screen.getByRole('button', { name: 'Открыть счёт' }))

    expect(await screen.findByText('0,00 ₽', { normalizer: (s) => s.replace(/[  ]/g, ' ') })).toBeInTheDocument()
    expect(body).toEqual({ owner: 'Alice', currency: 'RUB' })
    expect(directory.list()).toEqual([ID])
  })

  it('does not send a request for an empty owner name', async () => {
    const user = userEvent.setup()
    renderApp('/')
    await user.click(screen.getByRole('button', { name: 'Открыть счёт' }))
    expect(screen.getByLabelText('Владелец')).toBeInvalid()
  })

  it('rejects a non-GUID id without a request', async () => {
    const user = userEvent.setup()
    const { directory } = renderApp('/')

    await user.type(screen.getByLabelText('ID счёта'), 'not-a-guid')
    await user.click(screen.getByRole('button', { name: 'Добавить' }))

    expect(screen.getByLabelText('ID счёта')).toBeInvalid()
    expect(directory.list()).toEqual([])
  })

  it('shows an error and does not add an id when the account does not exist', async () => {
    server.use(http.get(`/api/accounts/${UNKNOWN}`, () => problem(404, 'Account was not found.')))
    const user = userEvent.setup()
    const { directory } = renderApp('/')

    await user.type(screen.getByLabelText('ID счёта'), UNKNOWN)
    await user.click(screen.getByRole('button', { name: 'Добавить' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Счёт не найден')
    expect(directory.list()).toEqual([])
  })

  it('adds an existing account by id and opens it', async () => {
    useAccountHandlers()
    const user = userEvent.setup()
    const { directory } = renderApp('/')

    await user.type(screen.getByLabelText('ID счёта'), ` ${ID.toUpperCase()} `)
    await user.click(screen.getByRole('button', { name: 'Добавить' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(directory.list()).toEqual([ID])
  })

  it('lists the accounts of the directory with their balances', async () => {
    server.use(
      http.get(`/api/accounts/${ID}`, () => HttpResponse.json(account(ID, 'Alice', 1245000))),
      http.get(`/api/accounts/${OTHER}`, () => HttpResponse.json(account(OTHER, 'Bob', 5000))),
    )
    localStorage.setItem('minibank.accounts', JSON.stringify([ID, OTHER]))
    renderApp('/')

    const alice = await screen.findByRole('link', { name: /Alice/ })
    const bob = await screen.findByRole('link', { name: /Bob/ })
    expect(alice).toHaveAttribute('href', `/accounts/${ID}`)
    expect(within(alice).getByText(/12\s450,00/)).toBeInTheDocument()
    expect(within(bob).getByText(/50,00/)).toBeInTheDocument()
  })

  it('shows the empty state instead of crashing on broken localStorage JSON', () => {
    localStorage.setItem('minibank.accounts', '{broken')
    renderApp('/')
    expect(screen.getByText('Откройте первый счёт')).toBeInTheDocument()
  })
})

describe('account page', () => {
  it('shows the balance and the latest entries with direction and sign', async () => {
    useAccountHandlers()
    renderApp(`/accounts/${ID}`)

    expect(await screen.findByText(/12\s450,00/, { selector: '[data-testid="balance"]' })).toBeInTheDocument()
    expect(await screen.findAllByText('Зачисление')).toHaveLength(2)
    expect(screen.getAllByText('Списание')).toHaveLength(1)
    expect(screen.getByText(/\+1\s500,00/)).toBeInTheDocument()
    expect(screen.getByText(/−300,00/)).toBeInTheDocument()
  })

  it('links the deposit tile to the deposit page', async () => {
    useAccountHandlers()
    renderApp(`/accounts/${ID}`)
    expect(await screen.findByRole('link', { name: 'Пополнить' })).toHaveAttribute(
      'href',
      `/accounts/${ID}/deposit`,
    )
  })

  it('copies the account id when requisites are clicked', async () => {
    useAccountHandlers()
    const user = userEvent.setup()
    const writeText = vi.spyOn(navigator.clipboard, 'writeText')
    renderApp(`/accounts/${ID}`)

    await user.click(await screen.findByRole('button', { name: 'Реквизиты' }))

    expect(writeText).toHaveBeenCalledWith(ID)
    expect(await screen.findByText('Скопировано')).toBeInTheDocument()
  })

  it('shows "Счёт не найден" for a non-GUID id without any request', () => {
    renderApp('/accounts/not-a-guid')
    expect(screen.getByText('Счёт не найден')).toBeInTheDocument()
  })

  it('shows "Счёт не найден" on 404 and can remove the id from the list', async () => {
    server.use(http.get(`/api/accounts/${UNKNOWN}`, () => problem(404, 'Account was not found.')))
    localStorage.setItem('minibank.accounts', JSON.stringify([UNKNOWN]))
    const user = userEvent.setup()
    const { directory } = renderApp(`/accounts/${UNKNOWN}`)

    expect(await screen.findByText('Счёт не найден')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Убрать из списка' }))

    expect(directory.list()).toEqual([])
    await waitFor(() => expect(screen.getByText('Откройте первый счёт')).toBeInTheDocument())
  })

  it('shows a generic error for a 500 without technical details', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => problem(500, 'secret stack trace')))
    renderApp(`/accounts/${ID}`)

    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('secret')
  })
})
