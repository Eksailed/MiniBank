import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, type DepositInput, type TransferInput } from '../../api/client'
import { accountKeys } from '../accounts'

export function useDeposit(accountId: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ input, key }: { input: DepositInput; key: string }) => api.deposit(accountId, input, key),
    // detail-ключ — префикс и для записей счёта; ждём обновления, чтобы баланс на экране счёта был свежим.
    onSuccess: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: accountKeys.detail(accountId) }),
        queryClient.invalidateQueries({ queryKey: accountKeys.list() }),
      ]),
  })
}

export function useTransfer() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ input, key }: { input: TransferInput; key: string }) => api.transfer(input, key),
    onSuccess: (_, { input }) =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: accountKeys.detail(input.fromAccountId) }),
        queryClient.invalidateQueries({ queryKey: accountKeys.detail(input.toAccountId) }),
        queryClient.invalidateQueries({ queryKey: accountKeys.list() }),
      ]),
  })
}
