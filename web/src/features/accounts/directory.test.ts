import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createLocalStorageDirectory } from './directory'

const A = '3f2b1c0e-0000-4000-8000-00000000000a'
const B = '3f2b1c0e-0000-4000-8000-00000000000b'
const KEY = 'minibank.accounts'

beforeEach(() => localStorage.clear())

describe('createLocalStorageDirectory', () => {
  it('stores added ids and keeps insertion order', () => {
    const dir = createLocalStorageDirectory()
    dir.add(A)
    dir.add(B)
    expect(dir.list()).toEqual([A, B])
    expect(JSON.parse(localStorage.getItem(KEY)!)).toEqual([A, B])
  })

  it('does not duplicate an id added twice', () => {
    const dir = createLocalStorageDirectory()
    dir.add(A)
    dir.add(A)
    expect(dir.list()).toEqual([A])
  })

  it('removes an id', () => {
    const dir = createLocalStorageDirectory()
    dir.add(A)
    dir.add(B)
    dir.remove(A)
    expect(dir.list()).toEqual([B])
  })

  it('ignores a non-GUID id', () => {
    const dir = createLocalStorageDirectory()
    dir.add('not-a-guid')
    expect(dir.list()).toEqual([])
  })

  it('returns [] for broken JSON', () => {
    localStorage.setItem(KEY, '{broken')
    expect(createLocalStorageDirectory().list()).toEqual([])
  })

  it('returns [] when the stored JSON is not an array', () => {
    localStorage.setItem(KEY, JSON.stringify({ a: 1 }))
    expect(createLocalStorageDirectory().list()).toEqual([])
  })

  it('filters out non-string and non-GUID items', () => {
    localStorage.setItem(KEY, JSON.stringify([A, 5, null, 'nope', B]))
    expect(createLocalStorageDirectory().list()).toEqual([A, B])
  })

  it('notifies subscribers after add and remove, and stops after unsubscribe', () => {
    const dir = createLocalStorageDirectory()
    const listener = vi.fn()
    const unsubscribe = dir.subscribe(listener)
    dir.add(A)
    dir.remove(A)
    expect(listener).toHaveBeenCalledTimes(2)
    unsubscribe()
    dir.add(B)
    expect(listener).toHaveBeenCalledTimes(2)
  })

  it('returns the same array reference while data is unchanged', () => {
    const dir = createLocalStorageDirectory()
    dir.add(A)
    expect(dir.list()).toBe(dir.list())
  })

  it('does not throw when storage is unavailable and keeps ids in memory', () => {
    const broken = {
      getItem: () => {
        throw new Error('denied')
      },
      setItem: () => {
        throw new Error('denied')
      },
    } as unknown as Storage
    const dir = createLocalStorageDirectory(broken)
    expect(() => dir.add(A)).not.toThrow()
    expect(dir.list()).toEqual([A])
  })
})
