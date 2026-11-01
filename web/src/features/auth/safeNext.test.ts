import { describe, expect, it } from 'vitest'
import { safeNext } from './safeNext'

describe('safeNext', () => {
  it('keeps an in-app path with query and hash', () => {
    expect(safeNext('/accounts/42?tab=1#top')).toBe('/accounts/42?tab=1#top')
  })

  it.each([null, '', 'accounts', 'https://evil.com', '//evil.com', '/\\evil.com', '/\t/evil.com', '/\n/evil.com', '/\r/evil.com'])(
    'falls back to the root for %j',
    (raw) => {
      expect(safeNext(raw)).toBe('/')
    },
  )
})
