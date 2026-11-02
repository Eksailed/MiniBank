import type { CurrencyCode } from '../shared/money'
import { ApiError, type ProblemBody } from './errors'
import { clearToken, getToken } from '../features/auth/session'
import type { components } from './schema'
import {
  toAccount,
  toEntry,
  toLimits,
  toNumber,
  toTransfer,
  toTelegramLink,
  toTelegramStatus,
  toUserProfile,
  type Account,
  type Entry,
  type Limits,
  type NotificationSettings,
  type TelegramLink,
  type TelegramStatus,
  type Transfer,
  type UserProfile,
} from './types'

type Schemas = components['schemas']

const BASE = '/api'

export interface RequestInitLite {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  idempotencyKey?: string
  signal?: AbortSignal
  /** Заголовок Accept; по умолчанию JSON. */
  accept?: string
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

/** Отправляет запрос и возвращает успешный ответ; любая ошибка — ApiError. */
async function send(path: string, init: RequestInitLite): Promise<Response> {
  const headers: Record<string, string> = { Accept: init.accept ?? 'application/json', ...bearer(path) }
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
  return response
}

export async function apiRequest<T>(
  path: string,
  init: RequestInitLite = {},
): Promise<{ data: T; replayed: boolean }> {
  const response = await send(path, init)

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

/** Фильтр выписки: from включительно, to не включительно (даты ISO 8601, без времени — UTC). */
export interface EntriesFilter {
  from?: string
  to?: string
  direction?: 'in' | 'out'
}

function filterQuery(filter: EntriesFilter): URLSearchParams {
  const params = new URLSearchParams()
  if (filter.from) params.set('from', filter.from)
  if (filter.to) params.set('to', filter.to)
  if (filter.direction) params.set('direction', filter.direction)
  return params
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

  async forgotPassword(email: string): Promise<void> {
    await apiRequest<void>('/auth/forgot-password', { method: 'POST', body: { email } })
  },

  async resetPassword(token: string, newPassword: string): Promise<void> {
    await apiRequest<void>('/auth/reset-password', { method: 'POST', body: { token, newPassword } })
  },

  async getMe(): Promise<UserProfile> {
    const { data } = await apiRequest<Schemas['UserProfileDto']>('/me')
    return toUserProfile(data)
  },

  async getNotifications(): Promise<TelegramStatus> {
    const { data } = await apiRequest<Schemas['TelegramStatusDto']>('/me/notifications')
    return toTelegramStatus(data)
  },

  async createTelegramLink(): Promise<TelegramLink> {
    const { data } = await apiRequest<Schemas['LinkDto']>('/me/notifications/telegram/link', { method: 'POST' })
    return toTelegramLink(data)
  },

  async unlinkTelegram(): Promise<void> {
    await apiRequest<void>('/me/notifications/telegram', { method: 'DELETE' })
  },

  async updateNotificationSettings(settings: NotificationSettings): Promise<void> {
    await apiRequest<void>('/me/notifications/settings', { method: 'PUT', body: settings })
  },

  async listAccounts(): Promise<Account[]> {
    const { data } = await apiRequest<Schemas['AccountDto'][]>('/accounts')
    return data.map(toAccount)
  },

  async createAccount(input: { currency: CurrencyCode }): Promise<Account> {
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

  async getLimits(id: string): Promise<Limits> {
    const { data } = await apiRequest<Schemas['LimitsDto']>(`/accounts/${encodeURIComponent(id)}/limits`)
    return toLimits(data)
  },

  async putLimits(id: string, input: { perOperation: bigint; daily: bigint }): Promise<Limits> {
    const body = { perOperation: toNumber(input.perOperation), daily: toNumber(input.daily) }
    const { data } = await apiRequest<Schemas['LimitsDto']>(`/accounts/${encodeURIComponent(id)}/limits`, {
      method: 'PUT',
      body,
    })
    return toLimits(data)
  },

  async getEntries(id: string, filter: EntriesFilter, page: number, pageSize: number): Promise<Entry[]> {
    const params = filterQuery(filter)
    params.set('page', String(page))
    params.set('pageSize', String(pageSize))
    const { data } = await apiRequest<Schemas['EntryDto'][]>(
      `/accounts/${encodeURIComponent(id)}/entries?${params}`,
    )
    return data.map(toEntry)
  },

  /** Выписка целиком в CSV (запрос с токеном, поэтому не ссылка на скачивание). */
  async downloadStatementCsv(id: string, filter: EntriesFilter): Promise<Blob> {
    const query = filterQuery(filter).toString()
    const response = await send(`/accounts/${encodeURIComponent(id)}/entries.csv${query ? `?${query}` : ''}`, {
      accept: 'text/csv',
    })
    return response.blob()
  },
}
