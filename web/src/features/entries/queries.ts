import { useInfiniteQuery } from '@tanstack/react-query'
import { api } from '../../api/client'
import { isGuid } from '../../shared/guid'
import { accountKeys } from '../accounts'

export function useEntriesInfinite(id: string, pageSize: number) {
  return useInfiniteQuery({
    // Под префиксом accountKeys.entries(id): пополнение и перевод инвалидируют и выписку.
    queryKey: [...accountKeys.entries(id), 'infinite', pageSize],
    queryFn: ({ pageParam }) => api.getEntries(id, pageParam, pageSize),
    initialPageParam: 1,
    getNextPageParam: (lastPage, _pages, lastPageParam) => (lastPage.length >= pageSize ? lastPageParam + 1 : undefined),
    enabled: isGuid(id),
  })
}
