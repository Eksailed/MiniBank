import { useInfiniteQuery, useMutation } from '@tanstack/react-query'
import { api, type EntriesFilter } from '../../api/client'
import { isGuid } from '../../shared/guid'
import { accountKeys } from '../accounts'
import { downloadCsv } from './downloadCsv'

export function useEntriesInfinite(id: string, filter: EntriesFilter, pageSize: number) {
  return useInfiniteQuery({
    // Под префиксом accountKeys.entries(id): пополнение и перевод инвалидируют и выписку.
    // Фильтр в ключе: другой фильтр — другая выписка, подгрузка начинается с первой страницы.
    queryKey: [...accountKeys.entries(id), 'infinite', pageSize, filter],
    queryFn: ({ pageParam }) => api.getEntries(id, filter, pageParam, pageSize),
    initialPageParam: 1,
    getNextPageParam: (lastPage, _pages, lastPageParam) => (lastPage.length >= pageSize ? lastPageParam + 1 : undefined),
    enabled: isGuid(id),
  })
}

/** Выписка целиком в CSV с теми же фильтрами, что и на экране. */
export function useDownloadStatement(id: string) {
  return useMutation({
    mutationFn: (filter: EntriesFilter) => api.downloadStatementCsv(id, filter),
    onSuccess: (blob) => downloadCsv(blob, `statement-${id}.csv`),
  })
}
