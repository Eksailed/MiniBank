import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { server } from '../../test/server'
import { renderApp } from '../../test/render'

const ID = '3f2b1c0e-0000-4000-8000-000000000001'
const OTHER = '3f2b1c0e-0000-4000-8000-000000000002'
const UNKNOWN = '3f2b1c0e-0000-4000-8000-0000000000ff'

const account = (id: string, owner: string, balance: number, currency = 'RUB') => ({
  id,
  owner,
  currency,
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

const limits = {
  perOperation: 10000000,
  daily: 50000000,
  spentToday: 1500000,
  remainingToday: 48500000,
  maxPerOperation: 100000000,
  maxDaily: 500000000,
}

function problem(status: number, detail: string, code?: string) {
  return HttpResponse.json(
    { title: 'Problem', status, detail, ...(code ? { code } : {}) },
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
  it('offers to open the first account when the user has no accounts', async () => {
    renderApp('/')
    expect(await screen.findByText('Откройте первый счёт')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Новый счёт' })).toBeInTheDocument()
  })

  it('has no local directory: no "add by id" form', async () => {
    renderApp('/')
    await screen.findByText('Откройте первый счёт')
    expect(screen.queryByLabelText('ID счёта')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Добавить' })).not.toBeInTheDocument()
  })

  it('lists the accounts returned by the server with their balances', async () => {
    server.use(
      http.get('/api/accounts', () =>
        HttpResponse.json([account(ID, 'Alice', 1245000), account(OTHER, 'Bob', 5000)]),
      ),
    )
    renderApp('/')

    const first = await screen.findByRole('link', { name: /Alice/ })
    const second = await screen.findByRole('link', { name: /Bob/ })
    expect(first).toHaveAttribute('href', `/accounts/${ID}`)
    expect(within(first).getByText(/12\s450,00/)).toBeInTheDocument()
    expect(within(second).getByText(/50,00/)).toBeInTheDocument()
    expect(screen.queryByText('Откройте первый счёт')).not.toBeInTheDocument()
  })

  it('shows account cards with a mask and action tiles for the first account', async () => {
    server.use(
      http.get('/api/accounts', () => HttpResponse.json([account(ID, 'Alice', 1245000), account(OTHER, 'Bob', 5000)])),
    )
    renderApp('/')

    const first = await screen.findByRole('link', { name: /Alice/ })
    expect(within(first).getByText(`•• ${ID.slice(-4).toUpperCase()}`)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Пополнить' })).toHaveAttribute('href', `/accounts/${ID}/deposit`)
    expect(screen.getByRole('link', { name: 'Перевести' })).toHaveAttribute('href', `/accounts/${ID}/transfer`)
    expect(await screen.findByRole('progressbar', { name: 'Дневной лимит' })).toBeInTheDocument()
  })

  it('shows an error with a retry when the list cannot be loaded', async () => {
    server.use(http.get('/api/accounts', () => HttpResponse.error()))
    renderApp('/')
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Нет связи с сервером')
    expect(within(alert).getByRole('button', { name: 'Повторить' })).toBeInTheDocument()
  })

  it('creates an account with the chosen currency only and shows the account screen', async () => {
    let body: unknown
    server.use(
      http.post('/api/accounts', async ({ request }) => {
        body = await request.json()
        return HttpResponse.json(account(ID, 'Alice', 0, 'USD'), { status: 201 })
      }),
      http.get(`/api/accounts/${ID}/entries`, () => HttpResponse.json([])),
    )
    const user = userEvent.setup()
    renderApp('/')

    await user.click(await screen.findByRole('button', { name: 'Новый счёт' }))
    expect(screen.queryByLabelText('Владелец')).not.toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Валюта'), 'USD')
    await user.click(screen.getByRole('button', { name: 'Открыть счёт' }))

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    expect(body).toEqual({ currency: 'USD' })
  })

  it('refreshes the list after an account is created', async () => {
    let created = false
    server.use(
      http.get('/api/accounts', () => HttpResponse.json(created ? [account(ID, 'Alice', 0)] : [])),
      http.post('/api/accounts', () => {
        created = true
        return HttpResponse.json(account(ID, 'Alice', 0), { status: 201 })
      }),
      http.get(`/api/accounts/${ID}/entries`, () => HttpResponse.json([])),
    )
    const user = userEvent.setup()
    const { router } = renderApp('/')

    await user.click(await screen.findByRole('button', { name: 'Новый счёт' }))
    await user.click(screen.getByRole('button', { name: 'Открыть счёт' }))
    await screen.findByText('Последние операции')
    await user.click(screen.getByRole('link', { name: /Мои счета/ }))

    expect(router.state.location.pathname).toBe('/')
    expect(await screen.findByRole('link', { name: /Alice/ })).toBeInTheDocument()
  })

  it('explains that the account limit is reached', async () => {
    server.use(http.post('/api/accounts', () => problem(422, 'Account limit reached.', 'account_limit_reached')))
    const user = userEvent.setup()
    renderApp('/')

    await user.click(await screen.findByRole('button', { name: 'Новый счёт' }))
    await user.click(screen.getByRole('button', { name: 'Открыть счёт' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Достигнут максимум счетов')
    expect(alert).not.toHaveTextContent('Account limit')
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

  it('shows "Счёт не найден" on 404 and only leads back to my accounts', async () => {
    server.use(http.get(`/api/accounts/${UNKNOWN}`, () => problem(404, 'Account was not found.')))
    renderApp(`/accounts/${UNKNOWN}`)

    expect(await screen.findByText('Счёт не найден')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Убрать из списка' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'К моим счетам' })).toHaveAttribute('href', '/')
  })

  it('shows a generic error for a 500 without technical details', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => problem(500, 'secret stack trace')))
    renderApp(`/accounts/${ID}`)

    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('secret')
  })
})

describe('limits card', () => {
  function useLimitsHandlers(overrides: Partial<typeof limits> = {}) {
    const puts: unknown[] = []
    server.use(
      http.get(`/api/accounts/${ID}/limits`, () => HttpResponse.json({ ...limits, ...overrides })),
      http.put(`/api/accounts/${ID}/limits`, async ({ request }) => {
        const body = (await request.json()) as { perOperation: number; daily: number }
        puts.push(body)
        return HttpResponse.json({ ...limits, ...overrides, ...body })
      }),
    )
    useAccountHandlers()
    return puts
  }

  async function openCard() {
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}`)
    await user.click(await screen.findByRole('button', { name: 'Изменить лимиты' }))
    const perOperation = await screen.findByLabelText('Лимит на операцию')
    return { user, perOperation, daily: screen.getByLabelText('Лимит в сутки') }
  }

  async function replace(user: ReturnType<typeof userEvent.setup>, field: HTMLElement, text: string) {
    await user.clear(field)
    await user.type(field, text)
  }

  it('shows the limits, what was spent today and what is left', async () => {
    useLimitsHandlers()
    const { perOperation, daily } = await openCard()

    expect(perOperation).toHaveValue('100000')
    expect(daily).toHaveValue('500000')
    expect(screen.getByText('Потрачено сегодня')).toBeInTheDocument()
    expect(screen.getByText(/15\s000,00/)).toBeInTheDocument()
    expect(screen.getByText('Осталось сегодня')).toBeInTheDocument()
    expect(screen.getByText(/485\s000,00/)).toBeInTheDocument()
  })

  it('closes the limits sheet with Escape', async () => {
    useLimitsHandlers()
    const { user } = await openCard()

    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps the kopecks in the prefilled value', async () => {
    useLimitsHandlers({ perOperation: 150050, daily: 150050 })
    const { perOperation, daily } = await openCard()

    expect(perOperation).toHaveValue('1500,50')
    expect(daily).toHaveValue('1500,50')
  })

  it('saves the limits in minor units', async () => {
    const puts = useLimitsHandlers()
    const { user, perOperation, daily } = await openCard()

    await replace(user, perOperation, '500')
    await replace(user, daily, '2 000,5')
    await user.click(screen.getByRole('button', { name: 'Сохранить лимиты' }))

    expect(await screen.findByText('Лимиты сохранены')).toBeInTheDocument()
    expect(puts).toEqual([{ perOperation: 50000, daily: 200050 }])
  })

  it('rejects a value above the ceiling next to the field without a request', async () => {
    const puts = useLimitsHandlers()
    const { user, perOperation } = await openCard()

    await replace(user, perOperation, '1000001')
    await user.click(screen.getByRole('button', { name: 'Сохранить лимиты' }))

    expect(perOperation).toBeInvalid()
    expect(perOperation).toHaveAccessibleDescription(/Не больше 1\s000\s000,00/)
    expect(puts).toEqual([])
  })

  it('rejects a daily limit below the per-operation limit without a request', async () => {
    const puts = useLimitsHandlers()
    const { user, perOperation, daily } = await openCard()

    await replace(user, perOperation, '1000')
    await replace(user, daily, '500')
    await user.click(screen.getByRole('button', { name: 'Сохранить лимиты' }))

    expect(daily).toBeInvalid()
    expect(daily).toHaveAccessibleDescription('Дневной лимит не может быть меньше лимита на операцию')
    expect(puts).toEqual([])
  })

  it.each(['0', '', 'abc'])('rejects the value %j without a request', async (text) => {
    const puts = useLimitsHandlers()
    const { user, perOperation } = await openCard()

    await user.clear(perOperation)
    if (text) await user.type(perOperation, text)
    await user.click(screen.getByRole('button', { name: 'Сохранить лимиты' }))

    expect(perOperation).toBeInvalid()
    expect(puts).toEqual([])
  })

  it('shows the 400 returned by the server in Russian', async () => {
    useLimitsHandlers()
    server.use(
      http.put(`/api/accounts/${ID}/limits`, () => problem(400, 'Daily limit must not exceed 500000.')),
    )
    const { user } = await openCard()

    await user.click(screen.getByRole('button', { name: 'Сохранить лимиты' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Проверьте введённые данные')
    expect(alert).toHaveTextContent('Лимит больше допустимого')
    expect(alert).not.toHaveTextContent('Daily limit')
  })

  it('does not break the account page when the limits cannot be loaded', async () => {
    useAccountHandlers()
    server.use(http.get(`/api/accounts/${ID}/limits`, () => problem(500, 'secret stack trace')))
    renderApp(`/accounts/${ID}`)

    expect(await screen.findByText('Последние операции')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('alert')).not.toHaveTextContent('secret'))
  })
})
