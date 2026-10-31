import { act, renderHook } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { isGuid } from '../../shared/guid'
import { useIdempotentKey } from './useIdempotentKey'

describe('useIdempotentKey', () => {
  it('returns a UUID', () => {
    const { result } = renderHook(() => useIdempotentKey(['a']))
    expect(isGuid(result.current.key)).toBe(true)
  })

  it('keeps the key stable between renders while deps are unchanged', () => {
    const { result, rerender } = renderHook(({ deps }) => useIdempotentKey(deps), { initialProps: { deps: ['a', '1'] } })
    const first = result.current.key
    rerender({ deps: ['a', '1'] })
    rerender({ deps: ['a', '1'] })
    expect(result.current.key).toBe(first)
  })

  it('changes the key when a dep changes', () => {
    const { result, rerender } = renderHook(({ deps }) => useIdempotentKey(deps), { initialProps: { deps: ['a', '1'] } })
    const first = result.current.key
    rerender({ deps: ['a', '2'] })
    expect(result.current.key).not.toBe(first)
    expect(isGuid(result.current.key)).toBe(true)
  })

  it('changes the key on rotate() and keeps the new one afterwards', () => {
    const { result, rerender } = renderHook(() => useIdempotentKey(['a']))
    const first = result.current.key
    act(() => result.current.rotate())
    const second = result.current.key
    expect(second).not.toBe(first)
    rerender()
    expect(result.current.key).toBe(second)
  })
})
