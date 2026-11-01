import type { CurrencyCode } from '../shared/money'
import { CURRENCIES } from '../shared/money'
import type { components } from './schema'

type Schemas = components['schemas']

export interface Account {
  id: string
  owner: string
  currency: CurrencyCode
  balance: bigint
  status: string
  isSystem: boolean
  createdAt: string
}

export interface Entry {
  id: string
  transferId: string
  /** Со знаком: плюс — зачисление, минус — списание. */
  amount: bigint
  balanceAfter: bigint
  createdAt: string
}

export interface Limits {
  perOperation: bigint
  daily: bigint
  spentToday: bigint
  remainingToday: bigint
  /** Потолок, выше которого лимит не поднять (зависит от валюты). */
  maxPerOperation: bigint
  maxDaily: bigint
}

export interface UserProfile {
  id: string
  email: string
  displayName: string
}

export interface Transfer {
  id: string
  fromAccountId: string
  toAccountId: string
  amount: bigint
  currency: CurrencyCode
  createdAt: string
}

function required<T>(value: T | null | undefined, field: string): T {
  if (value === undefined || value === null) throw new TypeError(`Missing field in API response: ${field}`)
  return value
}

/** Суммы приходят JSON-числами; принимаем только безопасные целые. */
export function toBigInt(value: number | undefined, field: string): bigint {
  const n = required(value, field)
  if (!Number.isSafeInteger(n)) throw new RangeError(`Unsafe integer in API response: ${field}`)
  return BigInt(n)
}

export function toNumber(value: bigint): number {
  const n = Number(value)
  if (!Number.isSafeInteger(n)) throw new RangeError('Amount is outside the safe integer range')
  return n
}

function toCurrency(value: string | null | undefined): CurrencyCode {
  const code = required(value, 'currency')
  if (!(CURRENCIES as readonly string[]).includes(code)) throw new TypeError(`Unsupported currency: ${code}`)
  return code as CurrencyCode
}

export function toAccount(dto: Schemas['AccountDto']): Account {
  return {
    id: required(dto.id, 'id'),
    owner: required(dto.owner, 'owner'),
    currency: toCurrency(dto.currency),
    balance: toBigInt(dto.balance, 'balance'),
    status: required(dto.status, 'status'),
    isSystem: dto.isSystem ?? false,
    createdAt: required(dto.createdAt, 'createdAt'),
  }
}

export function toEntry(dto: Schemas['EntryDto']): Entry {
  return {
    id: required(dto.id, 'id'),
    transferId: required(dto.transferId, 'transferId'),
    amount: toBigInt(dto.amount, 'amount'),
    balanceAfter: toBigInt(dto.balanceAfter, 'balanceAfter'),
    createdAt: required(dto.createdAt, 'createdAt'),
  }
}

export function toTransfer(dto: Schemas['TransferDto']): Transfer {
  return {
    id: required(dto.id, 'id'),
    fromAccountId: required(dto.fromAccountId, 'fromAccountId'),
    toAccountId: required(dto.toAccountId, 'toAccountId'),
    amount: toBigInt(dto.amount, 'amount'),
    currency: toCurrency(dto.currency),
    createdAt: required(dto.createdAt, 'createdAt'),
  }
}

export function toLimits(dto: Schemas['LimitsDto']): Limits {
  return {
    perOperation: toBigInt(dto.perOperation, 'perOperation'),
    daily: toBigInt(dto.daily, 'daily'),
    spentToday: toBigInt(dto.spentToday, 'spentToday'),
    remainingToday: toBigInt(dto.remainingToday, 'remainingToday'),
    maxPerOperation: toBigInt(dto.maxPerOperation, 'maxPerOperation'),
    maxDaily: toBigInt(dto.maxDaily, 'maxDaily'),
  }
}

export function toUserProfile(dto: Schemas['UserProfileDto']): UserProfile {
  return {
    id: required(dto.id, 'id'),
    email: required(dto.email, 'email'),
    displayName: required(dto.displayName, 'displayName'),
  }
}
