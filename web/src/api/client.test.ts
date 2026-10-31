import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '../test/server'
import { api } from './client'
import { ApiError } from './errors'

const ID = '3f2b1c0e-0000-4000-8000-000000000001'
const OTHER = '3f2b1c0e-0000-4000-8000-000000000002'

const accountDto = {
  id: ID,
  owner: 'Alice',
  currency: 'RUB',
  balance: 1245000,
  status: 'Active',
  isSystem: false,
  createdAt: '2026-10-31T10:00:00Z',
}

const transferDto = {
  id: '3f2b1c0e-0000-4000-8000-0000000000aa',
  fromAccountId: ID,
  toAccountId: OTHER,
  amount: 30000,
  currency: 'RUB',
  createdAt: '2026-10-31T10:05:00Z',
}

async function rejection(promise: Promise<unknown>): Promise<unknown> {
  try {
    await promise
  } catch (e) {
    return e
  }
  throw new Error('expected promise to reject')
}

describe('api client', () => {
  it('getAccount converts balance to bigint', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => HttpResponse.json(accountDto)))

    const account = await api.getAccount(ID)

    expect(account.balance).toBe(1245000n)
    expect(account.owner).toBe('Alice')
    expect(account.currency).toBe('RUB')
  })

  it('createAccount posts owner and currency', async () => {
    let body: unknown
    server.use(
      http.post('/api/accounts', async ({ request }) => {
        body = await request.json()
        return HttpResponse.json(accountDto, { status: 201 })
      }),
    )

    const account = await api.createAccount({ owner: 'Alice', currency: 'RUB' })

    expect(body).toEqual({ owner: 'Alice', currency: 'RUB' })
    expect(account.id).toBe(ID)
  })

  it('deposit sends Idempotency-Key and a numeric amount, and reads the replay header', async () => {
    let key: string | null = null
    let body: unknown
    server.use(
      http.post(`/api/accounts/${ID}/deposit`, async ({ request }) => {
        key = request.headers.get('Idempotency-Key')
        body = await request.json()
        return HttpResponse.json(transferDto, { headers: { 'Idempotent-Replayed': 'true' } })
      }),
    )

    const result = await api.deposit(ID, { amount: 30000n, currency: 'RUB' }, 'key-1')

    expect(key).toBe('key-1')
    expect(body).toEqual({ amount: 30000, currency: 'RUB' })
    expect(result.replayed).toBe(true)
    expect(result.transfer.amount).toBe(30000n)
  })

  it('transfer without the replay header reports replayed false', async () => {
    server.use(http.post('/api/transfers', () => HttpResponse.json(transferDto)))

    const result = await api.transfer(
      { fromAccountId: ID, toAccountId: OTHER, amount: 30000n, currency: 'RUB' },
      'key-2',
    )

    expect(result.replayed).toBe(false)
  })

  it('getEntries passes paging and converts amounts', async () => {
    let search = ''
    server.use(
      http.get(`/api/accounts/${ID}/entries`, ({ request }) => {
        search = new URL(request.url).search
        return HttpResponse.json([
          { id: ID, transferId: OTHER, amount: -500, balanceAfter: 700, createdAt: '2026-10-31T10:05:00Z' },
        ])
      }),
    )

    const entries = await api.getEntries(ID, 2, 20)

    expect(search).toBe('?page=2&pageSize=20')
    expect(entries[0].amount).toBe(-500n)
    expect(entries[0].balanceAfter).toBe(700n)
  })

  it('maps a 404 problem+json response to ApiError with status and detail', async () => {
    server.use(
      http.get(`/api/accounts/${ID}`, () =>
        HttpResponse.json(
          { title: 'Not Found', status: 404, detail: 'Account was not found.' },
          { status: 404, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    )

    const err = await rejection(api.getAccount(ID))

    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(404)
    expect((err as ApiError).detail).toBe('Account was not found.')
  })

  it('maps a 502 HTML response to ApiError, not SyntaxError', async () => {
    server.use(
      http.get(`/api/accounts/${ID}`, () =>
        HttpResponse.text('<html>Bad Gateway</html>', { status: 502, headers: { 'Content-Type': 'text/html' } }),
      ),
    )

    const err = await rejection(api.getAccount(ID))

    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(502)
  })

  it('maps an empty 500 response to ApiError with status 500', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => new HttpResponse(null, { status: 500 })))

    const err = await rejection(api.getAccount(ID))

    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(500)
  })

  it('maps a network failure to ApiError with status 0', async () => {
    server.use(http.get(`/api/accounts/${ID}`, () => HttpResponse.error()))

    const err = await rejection(api.getAccount(ID))

    expect(err).toBeInstanceOf(ApiError)
    expect((err as ApiError).status).toBe(0)
  })

  it('rejects an amount above the safe maximum before sending a request', async () => {
    // Нет обработчика: любой запрос провалил бы тест через onUnhandledFrame: 'error'.
    const tooBig = BigInt(Number.MAX_SAFE_INTEGER) + 1n

    const err = await rejection(api.deposit(ID, { amount: tooBig, currency: 'RUB' }, 'key-3'))

    expect(err).toBeInstanceOf(RangeError)
  })
})
