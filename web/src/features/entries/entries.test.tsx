import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'

const ID = '3f2b1c0e-0000-4000-8000-000000000001'

const account = {
  id: ID,
  owner: 'Alice',
  currency: 'RUB',
  balance: 1000000,
  status: 'Active',
  isSystem: false,
  createdAt: '2026-10-31T10:00:00Z',
}

function entries(from: number, count: number) {
  return Array.from({ length: count }, (_, i) => ({
    id: `e${from + i}`,
    transferId: `t${from + i}`,
    amount: 100 * (from + i),
    balanceAfter: 1000000,
    createdAt: '2026-10-31T10:00:00Z',
  }))
}

function problem(status: number) {
  return HttpResponse.json(
    { title: 'Problem', status, detail: 'secret stack trace' },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

/** Подменяет /entries: `pages[n-1]` — ответ на страницу n; считает запросы с их query. */
function useEntryPages(pages: Array<unknown[] | 'network-error' | 'server-error'>) {
  const requested: string[] = []
  server.use(
    http.get(`/api/accounts/${ID}`, () => HttpResponse.json(account)),
    http.get(`/api/accounts/${ID}/entries`, ({ request }) => {
      const url = new URL(request.url)
      requested.push(`page=${url.searchParams.get('page')}&pageSize=${url.searchParams.get('pageSize')}`)
      const page = pages[Number(url.searchParams.get('page')) - 1] ?? []
      if (page === 'network-error') return HttpResponse.error()
      if (page === 'server-error') return problem(500)
      return HttpResponse.json(page)
    }),
  )
  return requested
}

const rows = () => screen.getAllByText(/Зачисление|Списание/)
const waitForRows = (count: number) => waitFor(() => expect(rows()).toHaveLength(count))

describe('entries page', () => {
  it('loads the next page on "Показать ещё" and hides the button after a short page', async () => {
    const requested = useEntryPages([entries(1, 20), entries(21, 5)])
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)

    expect(await screen.findAllByText('Зачисление')).toHaveLength(20)
    await user.click(screen.getByRole('button', { name: 'Показать ещё' }))

    await waitForRows(25)
    expect(requested).toEqual(['page=1&pageSize=20', 'page=2&pageSize=20'])
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument()
  })

  it('hides the button after an empty response following a full page', async () => {
    const requested = useEntryPages([entries(1, 20), []])
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)

    await screen.findAllByText('Зачисление')
    await user.click(screen.getByRole('button', { name: 'Показать ещё' }))

    await waitFor(() => expect(requested).toHaveLength(2))
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument())
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument()
    expect(rows()).toHaveLength(20)
    expect(screen.queryByText('Операций пока нет')).not.toBeInTheDocument()
  })

  it('shows the empty state with a deposit link', async () => {
    useEntryPages([[]])
    renderApp(`/accounts/${ID}/entries`)

    expect(await screen.findByText('Операций пока нет')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Пополнить' })).toHaveAttribute('href', `/accounts/${ID}/deposit`)
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument()
  })

  it('shows a generic error for a 500 without technical details', async () => {
    useEntryPages(['server-error'])
    renderApp(`/accounts/${ID}/entries`)

    const alert = await screen.findByRole('alert')
    expect(alert).not.toHaveTextContent('secret')
  })

  it('offers a retry after a network error', async () => {
    useEntryPages(['network-error'])
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)
    await user.click(await screen.findByRole('button', { name: 'Повторить' }))

    // Заглушка по-прежнему отвечает ошибкой: после повтора уведомление остаётся.
    expect(await screen.findByRole('alert')).toHaveTextContent('Нет связи с сервером')
  })

  it('keeps loaded entries and offers a retry when the next page fails', async () => {
    let attempt = 0
    server.use(
      http.get(`/api/accounts/${ID}`, () => HttpResponse.json(account)),
      http.get(`/api/accounts/${ID}/entries`, ({ request }) => {
        const page = Number(new URL(request.url).searchParams.get('page'))
        if (page === 1) return HttpResponse.json(entries(1, 20))
        attempt += 1
        return attempt === 1 ? HttpResponse.error() : HttpResponse.json(entries(21, 3))
      }),
    )
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)

    await screen.findAllByText('Зачисление')
    await user.click(screen.getByRole('button', { name: 'Показать ещё' }))
    const alert = await screen.findByRole('alert')
    expect(rows()).toHaveLength(20)

    await user.click(within(alert).getByRole('button', { name: 'Повторить' }))
    await waitForRows(23)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows "Счёт не найден" when the account does not exist', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => problem(404)))
    renderApp(`/accounts/${ID}/entries`)
    expect(await screen.findByText('Счёт не найден')).toBeInTheDocument()
  })
})
