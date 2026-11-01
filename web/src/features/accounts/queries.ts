import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type EntriesFilter } from '../../api/client'
import { ApiError } from '../../api/errors'
import { isGuid } from '../../shared/guid'
import type { CurrencyCode } from '../../shared/money'

export const accountKeys = {
  all: ['accounts'] as const,
  list: () => ['accounts', 'list'] as const,
  detail: (id: string) => ['accounts', id] as const,
  // Лимиты и записи лежат под detail-префиксом: пополнение и перевод инвалидируют и их.
  limits: (id: string) => ['accounts', id, 'limits'] as const,
  entries: (id: string) => ['accounts', id, 'entries'] as const,
  entriesPage: (id: string, page: number, pageSize: number) => ['accounts', id, 'entries', page, pageSize] as const,
}

/** Повторяем только сбои сети и 5xx: 4xx (в том числе 404) от повтора не изменится. */
export function shouldRetry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false
  return failureCount < 2
}

const NO_FILTER: EntriesFilter = {}

export function useAccounts() {
  return useQuery({ queryKey: accountKeys.list(), queryFn: api.listAccounts })
}

export function useAccount(id: string | undefined) {
  return useQuery({
    queryKey: accountKeys.detail(id ?? ''),
    queryFn: () => api.getAccount(id!),
    enabled: id !== undefined && isGuid(id),
    // Данные, только что полученные при создании счёта, не перезапрашиваем сразу же.
    staleTime: 30_000,
  })
}

export function useLimits(id: string) {
  return useQuery({
    queryKey: accountKeys.limits(id),
    queryFn: () => api.getLimits(id),
    enabled: isGuid(id),
  })
}

export function usePutLimits(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { perOperation: bigint; daily: bigint }) => api.putLimits(id, input),
    onSuccess: (limits) => queryClient.setQueryData(accountKeys.limits(id), limits),
  })
}

export function useEntries(id: string, page: number, pageSize: number) {
  return useQuery({
    queryKey: accountKeys.entriesPage(id, page, pageSize),
    queryFn: () => api.getEntries(id, NO_FILTER, page, pageSize),
    enabled: isGuid(id),
  })
}

export function useCreateAccount() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { currency: CurrencyCode }) => api.createAccount(input),
    onSuccess: (account) => {
      queryClient.setQueryData(accountKeys.detail(account.id), account)
      return queryClient.invalidateQueries({ queryKey: accountKeys.list() })
    },
  })
}
