import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'
import { downloadCsv } from './downloadCsv'

vi.mock('./downloadCsv', () => ({ downloadCsv: vi.fn() }))

beforeEach(() => vi.mocked(downloadCsv).mockClear())

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

function problem(status: number, detail = 'secret stack trace') {
  return HttpResponse.json(
    { title: 'Problem', status, detail },
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

describe('statement filter and export', () => {
  /** Отдаёт зачисления по фильтру direction=in, иначе обычные страницы; запоминает query каждого запроса. */
  function recordEntryQueries() {
    const queries: URLSearchParams[] = []
    server.use(
      http.get(`/api/accounts/${ID}`, () => HttpResponse.json(account)),
      http.get(`/api/accounts/${ID}/entries`, ({ request }) => {
        const params = new URL(request.url).searchParams
        queries.push(params)
        if (params.get('direction') === 'in') return HttpResponse.json(entries(100, 2))
        return HttpResponse.json(params.get('page') === '1' ? entries(1, 20) : entries(21, 5))
      }),
    )
    return queries
  }

  const csvBody = () => new HttpResponse('a,b\r\n1,2\r\n', { headers: { 'Content-Type': 'text/csv' } })

  function recordCsv(respond: () => Response = csvBody) {
    const queries: URLSearchParams[] = []
    server.use(
      http.get(`/api/accounts/${ID}/entries.csv`, ({ request }) => {
        queries.push(new URL(request.url).searchParams)
        return respond()
      }),
    )
    return queries
  }

  const setDate = (label: string, value: string) =>
    fireEvent.change(screen.getByLabelText(label), { target: { value } })

  it('sends no filter parameters by default', async () => {
    const queries = recordEntryQueries()
    renderApp(`/accounts/${ID}/entries`)

    await screen.findAllByText('Зачисление')
    expect(queries[0].has('from')).toBe(false)
    expect(queries[0].has('to')).toBe(false)
    expect(queries[0].has('direction')).toBe(false)
  })

  it('sends from, to (the day after the chosen one, since "По" is inclusive) and direction', async () => {
    const queries = recordEntryQueries()
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)
    await screen.findAllByText('Зачисление')

    setDate('С', '2026-10-01')
    setDate('По', '2026-10-31')
    await user.selectOptions(screen.getByLabelText('Операции'), 'Зачисления')

    await waitFor(() => expect(queries.at(-1)?.get('direction')).toBe('in'))
    const last = queries.at(-1)!
    expect(last.get('from')).toBe('2026-10-01')
    expect(last.get('to')).toBe('2026-11-01')
    expect(last.get('page')).toBe('1')
  })

  it('moves "По" over the end of the month and the year', async () => {
    const queries = recordEntryQueries()
    renderApp(`/accounts/${ID}/entries`)
    await screen.findAllByText('Зачисление')

    setDate('По', '2026-12-31')

    await waitFor(() => expect(queries.at(-1)?.get('to')).toBe('2027-01-01'))
  })

  it('starts over from the first page when the filter changes', async () => {
    const queries = recordEntryQueries()
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)

    await screen.findAllByText('Зачисление')
    await user.click(screen.getByRole('button', { name: 'Показать ещё' }))
    await waitForRows(25)

    await user.selectOptions(screen.getByLabelText('Операции'), 'Зачисления')

    await waitForRows(2)
    expect(queries.at(-1)?.get('page')).toBe('1')
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument()
  })

  it('explains an inverted period next to the field without a request', async () => {
    const queries = recordEntryQueries()
    renderApp(`/accounts/${ID}/entries`)
    await screen.findAllByText('Зачисление')

    setDate('С', '2026-10-31')
    await waitFor(() => expect(queries).toHaveLength(2))
    setDate('По', '2026-10-01')

    expect(await screen.findByText('Начало периода позже конца')).toBeInTheDocument()
    expect(screen.getByLabelText('С')).toBeInvalid()
    expect(queries).toHaveLength(2)
  })

  it('downloads the CSV with the same filters', async () => {
    recordEntryQueries()
    const csv = recordCsv()
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)
    await screen.findAllByText('Зачисление')

    setDate('С', '2026-10-01')
    setDate('По', '2026-10-31')
    await user.selectOptions(screen.getByLabelText('Операции'), 'Списания')
    await user.click(screen.getByRole('button', { name: 'Скачать CSV' }))

    await waitFor(() => expect(downloadCsv).toHaveBeenCalledTimes(1))
    expect(csv).toHaveLength(1)
    expect(csv[0].get('from')).toBe('2026-10-01')
    expect(csv[0].get('to')).toBe('2026-11-01')
    expect(csv[0].get('direction')).toBe('out')
    const [blob, filename] = vi.mocked(downloadCsv).mock.calls[0]
    expect(filename).toBe(`statement-${ID}.csv`)
    expect(await blob.text()).toBe('a,b\r\n1,2\r\n')
  })

  it('sends the token with the CSV request', async () => {
    recordEntryQueries()
    let header: string | null = null
    server.use(
      http.get(`/api/accounts/${ID}/entries.csv`, ({ request }) => {
        header = request.headers.get('Authorization')
        return csvBody()
      }),
    )
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`, { token: 'jwt-abc' })
    await screen.findAllByText('Зачисление')

    await user.click(screen.getByRole('button', { name: 'Скачать CSV' }))

    await waitFor(() => expect(header).toBe('Bearer jwt-abc'))
  })

  it('does not request the CSV for an inverted period', async () => {
    recordEntryQueries()
    const csv = recordCsv()
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)
    await screen.findAllByText('Зачисление')

    setDate('С', '2026-10-31')
    setDate('По', '2026-10-01')
    await user.click(screen.getByRole('button', { name: 'Скачать CSV' }))

    expect(csv).toHaveLength(0)
    expect(downloadCsv).not.toHaveBeenCalled()
  })

  it('tells the user to narrow the period when the statement is too long', async () => {
    recordEntryQueries()
    recordCsv(() => problem(400, 'Statement has more than 10000 rows; narrow the period or direction.'))
    const user = userEvent.setup()
    renderApp(`/accounts/${ID}/entries`)
    await screen.findAllByText('Зачисление')

    await user.click(screen.getByRole('button', { name: 'Скачать CSV' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Слишком много строк')
    expect(alert).not.toHaveTextContent('Statement has more')
    expect(downloadCsv).not.toHaveBeenCalled()
  })
})
