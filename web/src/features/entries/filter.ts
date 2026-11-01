import type { EntriesFilter } from '../../api/client'

/** Фильтр в том виде, как его вводит пользователь: даты из `<input type="date">` (YYYY-MM-DD, пусто — не задано). */
export interface EntryFilter {
  from: string
  to: string
  direction: '' | 'in' | 'out'
}

export const EMPTY_FILTER: EntryFilter = { from: '', to: '', direction: '' }

export function filterError(filter: EntryFilter): string | undefined {
  // ISO-даты одинаковой длины сравниваются как строки.
  return filter.from && filter.to && filter.from > filter.to ? 'Начало периода позже конца' : undefined
}

/** Сервер считает `to` не включительно, а пользователь выбирает последний день периода — отправляем следующий день (UTC). */
function dayAfter(date: string): string | undefined {
  const day = new Date(`${date}T00:00:00Z`)
  if (Number.isNaN(day.getTime())) return undefined
  day.setUTCDate(day.getUTCDate() + 1)
  return day.toISOString().slice(0, 10)
}

export function toRequestFilter(filter: EntryFilter): EntriesFilter {
  const request: EntriesFilter = {}
  if (filter.from) request.from = filter.from
  const to = filter.to ? dayAfter(filter.to) : undefined
  if (to) request.to = to
  if (filter.direction) request.direction = filter.direction
  return request
}
