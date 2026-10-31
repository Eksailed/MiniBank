import type { CurrencyCode } from '../shared/money'
import { ApiError, type ProblemBody } from './errors'
import type { components } from './schema'
import { toAccount, toEntry, toNumber, toTransfer, type Account, type Entry, type Transfer } from './types'

type Schemas = components['schemas']

const BASE = '/api'

export interface RequestInitLite {
  method?: 'GET' | 'POST'
  body?: unknown
  idempotencyKey?: string
  signal?: AbortSignal
}

async function readProblem(response: Response): Promise<ProblemBody> {
  try {
    const parsed: unknown = JSON.parse(await response.text())
    if (typeof parsed !== 'object' || parsed === null) return {}
    const { title, detail, errors } = parsed as Record<string, unknown>
    return {
      title: typeof title === 'string' ? title : undefined,
      detail: typeof detail === 'string' ? detail : undefined,
      errors: typeof errors === 'object' && errors !== null ? (errors as Record<string, string[]>) : undefined,
    }
  } catch {
    return {}
  }
}

export async function apiRequest<T>(
  path: string,
  init: RequestInitLite = {},
): Promise<{ data: T; replayed: boolean }> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (init.body !== undefined) headers['Content-Type'] = 'application/json'
  if (init.idempotencyKey) headers['Idempotency-Key'] = init.idempotencyKey

  let response: Response
  try {
    // Node (тесты) не принимает относительный URL в fetch — приводим к абсолютному.
    const url = new URL(`${BASE}${path}`, globalThis.location?.href).toString()
    response = await fetch(url, {
      method: init.method ?? 'GET',
      headers,
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
      signal: init.signal,
    })
  } catch (e) {
    if (init.signal?.aborted) throw e
    throw new ApiError(0)
  }

  if (!response.ok) throw new ApiError(response.status, await readProblem(response))

  try {
    const data = (await response.json()) as T
    return { data, replayed: response.headers.get('Idempotent-Replayed') === 'true' }
  } catch {
    throw new ApiError(response.status)
  }
}

export interface DepositInput {
  amount: bigint
  currency: CurrencyCode
}

export interface TransferInput {
  fromAccountId: string
  toAccountId: string
  amount: bigint
  currency: CurrencyCode
}

export const api = {
  async createAccount(input: { owner: string; currency: CurrencyCode }): Promise<Account> {
    const { data } = await apiRequest<Schemas['AccountDto']>('/accounts', { method: 'POST', body: input })
    return toAccount(data)
  },

  async getAccount(id: string): Promise<Account> {
    const { data } = await apiRequest<Schemas['AccountDto']>(`/accounts/${encodeURIComponent(id)}`)
    return toAccount(data)
  },

  async deposit(
    id: string,
    input: DepositInput,
    key: string,
  ): Promise<{ transfer: Transfer; replayed: boolean }> {
    const body = { amount: toNumber(input.amount), currency: input.currency }
    const { data, replayed } = await apiRequest<Schemas['TransferDto']>(
      `/accounts/${encodeURIComponent(id)}/deposit`,
      { method: 'POST', body, idempotencyKey: key },
    )
    return { transfer: toTransfer(data), replayed }
  },

  async transfer(input: TransferInput, key: string): Promise<{ transfer: Transfer; replayed: boolean }> {
    const body = { ...input, amount: toNumber(input.amount) }
    const { data, replayed } = await apiRequest<Schemas['TransferDto']>('/transfers', {
      method: 'POST',
      body,
      idempotencyKey: key,
    })
    return { transfer: toTransfer(data), replayed }
  },

  async getEntries(id: string, page: number, pageSize: number): Promise<Entry[]> {
    const { data } = await apiRequest<Schemas['EntryDto'][]>(
      `/accounts/${encodeURIComponent(id)}/entries?page=${page}&pageSize=${pageSize}`,
    )
    return data.map(toEntry)
  },
}
