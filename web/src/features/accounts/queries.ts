import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../api/client'
import { ApiError } from '../../api/errors'
import { isGuid } from '../../shared/guid'
import type { CurrencyCode } from '../../shared/money'

export const accountKeys = {
  all: ['accounts'] as const,
  detail: (id: string) => ['accounts', id] as const,
  entries: (id: string) => ['accounts', id, 'entries'] as const,
  entriesPage: (id: string, page: number, pageSize: number) => ['accounts', id, 'entries', page, pageSize] as const,
}

/** Повторяем только сбои сети и 5xx: 4xx (в том числе 404) от повтора не изменится. */
export function shouldRetry(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false
  return failureCount < 2
}

export function useAccount(id: string | undefined) {
  return useQuery({
    queryKey: accountKeys.detail(id ?? ''),
    queryFn: () => api.getAccount(id!),
    enabled: id !== undefined && isGuid(id),
    // Данные, только что полученные при создании/добавлении счёта, не перезапрашиваем сразу же.
    staleTime: 30_000,
  })
}

export function useEntries(id: string, page: number, pageSize: number) {
  return useQuery({
    queryKey: accountKeys.entriesPage(id, page, pageSize),
    queryFn: () => api.getEntries(id, page, pageSize),
    enabled: isGuid(id),
  })
}

export function useCreateAccount() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (input: { owner: string; currency: CurrencyCode }) => api.createAccount(input),
    onSuccess: (account) => queryClient.setQueryData(accountKeys.detail(account.id), account),
  })
}
