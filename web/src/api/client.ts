import type { CurrencyCode } from '../shared/money'
import { ApiError, type ProblemBody } from './errors'
import { clearToken, getToken } from '../features/auth/session'
import type { components } from './schema'
import {
  toAccount,
  toEntry,
  toNumber,
  toTransfer,
  toUserProfile,
  type Account,
  type Entry,
  type Transfer,
  type UserProfile,
} from './types'

type Schemas = components['schemas']

const BASE = '/api'

export interface RequestInitLite {
  method?: 'GET' | 'POST' | 'PUT'
  body?: unknown
  idempotencyKey?: string
  signal?: AbortSignal
}

async function readProblem(response: Response): Promise<ProblemBody> {
  try {
    const parsed: unknown = JSON.parse(await response.text())
    if (typeof parsed !== 'object' || parsed === null) return {}
    const { title, detail, errors, code } = parsed as Record<string, unknown>
    return {
      title: typeof title === 'string' ? title : undefined,
      detail: typeof detail === 'string' ? detail : undefined,
      code: typeof code === 'string' ? code : undefined,
      errors: typeof errors === 'object' && errors !== null ? (errors as Record<string, string[]>) : undefined,
    }
  } catch {
    return {}
  }
}

/** Вход и регистрация идут без токена: устаревший токен не должен мешать анонимному запросу и сам по 401 не сбрасывается. */
const isAuthPath = (path: string) => path.startsWith('/auth/')

function bearer(path: string): Record<string, string> {
  const token = isAuthPath(path) ? null : getToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

/** Протухший или чужой токен: сбрасываем, интерфейс сам уведёт на /login. */
function dropTokenOn401(path: string, status: number) {
  if (status === 401 && !isAuthPath(path)) clearToken()
}

export async function apiRequest<T>(
  path: string,
  init: RequestInitLite = {},
): Promise<{ data: T; replayed: boolean }> {
  const headers: Record<string, string> = { Accept: 'application/json', ...bearer(path) }
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

  if (!response.ok) {
    dropTokenOn401(path, response.status)
    throw new ApiError(response.status, await readProblem(response))
  }

  // 202 и 204 приходят без тела (регистрация, подтверждение email).
  if (response.status === 202 || response.status === 204) {
    return { data: undefined as T, replayed: false }
  }

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
  async register(input: { email: string; password: string; displayName: string }): Promise<void> {
    await apiRequest<void>('/auth/register', { method: 'POST', body: input })
  },

  async login(input: { email: string; password: string }): Promise<{ accessToken: string; expiresAt: string }> {
    const { data } = await apiRequest<Schemas['LoginResponse']>('/auth/login', { method: 'POST', body: input })
    if (!data.accessToken || !data.expiresAt) throw new TypeError('Missing fields in login response')
    return { accessToken: data.accessToken, expiresAt: data.expiresAt }
  },

  async confirmEmail(token: string): Promise<void> {
    await apiRequest<void>('/auth/confirm-email', { method: 'POST', body: { token } })
  },

  async resendConfirmation(email: string): Promise<void> {
    await apiRequest<void>('/auth/resend-confirmation', { method: 'POST', body: { email } })
  },

  async getMe(): Promise<UserProfile> {
    const { data } = await apiRequest<Schemas['UserProfileDto']>('/me')
    return toUserProfile(data)
  },

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
