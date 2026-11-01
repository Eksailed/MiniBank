import { afterEach, expect, it, vi } from 'vitest'
import { downloadCsv } from './downloadCsv'

afterEach(() => vi.restoreAllMocks())

it('clicks a temporary download link with the file name and then releases the URL', () => {
  vi.useFakeTimers()
  const create = vi.fn(() => 'blob:fake-url')
  const revoke = vi.fn()
  vi.stubGlobal('URL', Object.assign(URL, { createObjectURL: create, revokeObjectURL: revoke }))
  let clicked: HTMLAnchorElement | undefined
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    clicked = this
  })

  downloadCsv(new Blob(['a,b']), 'statement-1.csv')

  expect(create).toHaveBeenCalledTimes(1)
  expect(clicked?.download).toBe('statement-1.csv')
  expect(clicked?.getAttribute('href')).toBe('blob:fake-url')
  expect(document.querySelector('a[download]')).toBeNull()
  expect(revoke).not.toHaveBeenCalled()
  vi.runAllTimers()
  expect(revoke).toHaveBeenCalledWith('blob:fake-url')
  vi.useRealTimers()
})
