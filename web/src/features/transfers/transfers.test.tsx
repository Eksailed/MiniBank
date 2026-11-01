import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay, http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'

const ID = '3f2b1c0e-0000-4000-8000-000000000001'
const OTHER = '3f2b1c0e-0000-4000-8000-000000000002'

const account = (id: string, currency = 'RUB') => ({
  id,
  owner: 'Alice',
  currency,
  balance: 1000000,
  status: 'Active',
  isSystem: false,
  createdAt: '2026-10-31T10:00:00Z',
})

const transferDto = {
  id: '3f2b1c0e-0000-4000-8000-0000000000aa',
  fromAccountId: ID,
  toAccountId: OTHER,
  amount: 25000,
  currency: 'RUB',
  createdAt: '2026-10-31T10:00:00Z',
}

function problem(status: number, detail: string) {
  return HttpResponse.json(
    { title: 'Problem', status, detail },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

function useAccountHandlers(currency = 'RUB') {
  server.use(
    http.get(`/api/accounts/${ID}`, () => HttpResponse.json(account(ID, currency))),
    http.get(`/api/accounts/${OTHER}`, () => HttpResponse.json(account(OTHER, currency))),
    http.get(`/api/accounts/${ID}/entries`, () => HttpResponse.json([])),
  )
}

interface Recorded {
  body: unknown
  key: string | null
}

function recordDeposits(respond: (call: number) => Response | Promise<Response> = () => HttpResponse.json(transferDto)) {
  const calls: Recorded[] = []
  server.use(
    http.post(`/api/accounts/${ID}/deposit`, async ({ request }) => {
      calls.push({ body: await request.json(), key: request.headers.get('Idempotency-Key') })
      return respond(calls.length)
    }),
  )
  return calls
}

async function openDeposit() {
  const user = userEvent.setup()
  const app = renderApp(`/accounts/${ID}/deposit`)
  const amount = await screen.findByLabelText('Сумма')
  return { user, amount, ...app }
}

describe('deposit page', () => {
  it('sends one deposit in minor units with an Idempotency-Key and returns to the account', async () => {
    useAccountHandlers()
    const calls = recordDeposits()
    const { user, amount } = await openDeposit()

    expect(screen.getByText(/RUB/)).toBeInTheDocument()
    await user.type(amount, '250')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(calls).toHaveLength(1)
    expect(calls[0].body).toEqual({ amount: 25000, currency: 'RUB' })
    expect(calls[0].key).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('sends exactly one request on a double click while the request is in flight', async () => {
    useAccountHandlers()
    const calls = recordDeposits(async () => {
      await delay(50)
      return HttpResponse.json(transferDto)
    })
    const { user, amount } = await openDeposit()

    await user.type(amount, '250')
    await user.dblClick(screen.getByRole('button', { name: 'Пополнить' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(calls).toHaveLength(1)
  })

  it('reuses the key when retrying after a network error', async () => {
    useAccountHandlers()
    const calls = recordDeposits((call) => (call === 1 ? HttpResponse.error() : HttpResponse.json(transferDto)))
    const { user, amount } = await openDeposit()

    await user.type(amount, '250')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Нет связи с сервером')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(calls).toHaveLength(2)
    expect(calls[1].key).toBe(calls[0].key)
  })

  it('uses a new key after the amount is edited following an error', async () => {
    useAccountHandlers()
    const calls = recordDeposits((call) => (call === 1 ? HttpResponse.error() : HttpResponse.json(transferDto)))
    const { user, amount } = await openDeposit()

    await user.type(amount, '250')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))
    await screen.findByRole('alert')
    await user.type(amount, '0')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(calls).toHaveLength(2)
    expect(calls[1].body).toEqual({ amount: 250000, currency: 'RUB' })
    expect(calls[1].key).not.toBe(calls[0].key)
  })

  it('uses a new key for a new operation after a successful one', async () => {
    useAccountHandlers()
    const calls = recordDeposits()
    const { user, amount } = await openDeposit()

    await user.type(amount, '250')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))
    await screen.findByText('Последние операции')

    await user.click(screen.getByRole('link', { name: 'Пополнить' }))
    await user.type(await screen.findByLabelText('Сумма'), '250')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))

    await waitFor(() => expect(calls).toHaveLength(2))
    expect(calls[1].key).not.toBe(calls[0].key)
  })

  it.each(['1,234', '', '0', 'abc'])('rejects the amount %j without a request', async (text) => {
    useAccountHandlers()
    const calls = recordDeposits()
    const { user, amount } = await openDeposit()

    if (text) await user.type(amount, text)
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))

    expect(amount).toBeInvalid()
    expect(calls).toHaveLength(0)
  })

  it('offers a retry on 409 and resends with the same key', async () => {
    useAccountHandlers()
    const calls = recordDeposits((call) => (call === 1 ? problem(409, 'busy') : HttpResponse.json(transferDto)))
    const { user, amount } = await openDeposit()

    await user.type(amount, '250')
    await user.click(screen.getByRole('button', { name: 'Пополнить' }))
    await user.click(await screen.findByRole('button', { name: 'Повторить' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(calls).toHaveLength(2)
    expect(calls[1].key).toBe(calls[0].key)
  })

  it('shows "Счёт не найден" when the account does not exist', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => problem(404, 'Account was not found.')))
    renderApp(`/accounts/${ID}/deposit`)
    expect(await screen.findByText('Счёт не найден')).toBeInTheDocument()
  })
})

function recordTransfers(respond: () => Response = () => HttpResponse.json(transferDto)) {
  const calls: Recorded[] = []
  server.use(
    http.post('/api/transfers', async ({ request }) => {
      calls.push({ body: await request.json(), key: request.headers.get('Idempotency-Key') })
      return respond()
    }),
  )
  return calls
}

async function openTransfer(directoryIds: string[] = []) {
  if (directoryIds.length) localStorage.setItem('minibank.accounts', JSON.stringify(directoryIds))
  const user = userEvent.setup()
  const app = renderApp(`/accounts/${ID}/transfer`)
  const recipient = await screen.findByLabelText('ID получателя')
  return { user, recipient, amount: screen.getByLabelText('Сумма'), ...app }
}

describe('transfer page', () => {
  it('rejects a recipient that is not a GUID without a request', async () => {
    useAccountHandlers()
    const calls = recordTransfers()
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, 'not-a-guid')
    await user.type(amount, '100')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    expect(recipient).toBeInvalid()
    expect(calls).toHaveLength(0)
  })

  it('rejects a transfer to the same account without a request', async () => {
    useAccountHandlers()
    const calls = recordTransfers()
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, ID)
    await user.type(amount, '100')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    expect(screen.getByText('Нельзя перевести на тот же счёт')).toBeInTheDocument()
    expect(calls).toHaveLength(0)
  })

  it('sends the transfer with the currency of the source account', async () => {
    useAccountHandlers('USD')
    const calls = recordTransfers()
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, ` ${OTHER.toUpperCase()} `)
    await user.type(amount, '50')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(calls).toHaveLength(1)
    expect(calls[0].body).toEqual({ fromAccountId: ID, toAccountId: OTHER, amount: 5000, currency: 'USD' })
    expect(calls[0].key).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('shows the 422 reason next to the form in Russian', async () => {
    useAccountHandlers()
    recordTransfers(() => problem(422, `Account ${ID} has insufficient funds.`) as Response)
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, OTHER)
    await user.type(amount, '50')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Операцию нельзя выполнить')
    expect(alert).toHaveTextContent('Недостаточно средств на счёте')
    expect(alert).not.toHaveTextContent('insufficient')
  })

  it('explains a currency mismatch returned by the API as 400', async () => {
    useAccountHandlers()
    recordTransfers(() => problem(400, 'Transfer requires accounts and amount in the same currency.') as Response)
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, OTHER)
    await user.type(amount, '50')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Счета в разных валютах')
  })

  it('reports an unknown recipient next to the recipient field', async () => {
    useAccountHandlers()
    recordTransfers(() => problem(404, `Account ${OTHER} was not found.`) as Response)
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, OTHER)
    await user.type(amount, '50')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    expect(await screen.findByText('Счёт получателя не найден')).toBeInTheDocument()
    expect(recipient).toBeInvalid()
  })

  it('treats a replayed response as success', async () => {
    useAccountHandlers()
    recordTransfers(() => HttpResponse.json(transferDto, { headers: { 'Idempotent-Replayed': 'true' } }))
    const { user, recipient, amount } = await openTransfer()

    await user.type(recipient, OTHER)
    await user.type(amount, '50')
    await user.click(screen.getByRole('button', { name: 'Перевести' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
  })

  it('suggests my other accounts as recipients, without the current one', async () => {
    useAccountHandlers()
    await openTransfer([ID, OTHER])

    const options = Array.from(document.querySelectorAll('datalist option')).map((o) => o.getAttribute('value'))
    expect(options).toEqual([OTHER])
  })

  it('shows "Счёт не найден" when the source account does not exist', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => problem(404, 'Account was not found.')))
    renderApp(`/accounts/${ID}/transfer`)
    expect(await screen.findByText('Счёт не найден')).toBeInTheDocument()
  })
})
