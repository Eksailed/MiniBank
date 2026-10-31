import { describe, expect, it } from 'vitest'
import { isGuid } from './guid'

describe('isGuid', () => {
  it('accepts lower- and upper-case GUIDs', () => {
    expect(isGuid('3f2b1c0e-0000-4000-8000-00000000000a')).toBe(true)
    expect(isGuid('3F2B1C0E-0000-4000-8000-00000000000A')).toBe(true)
  })

  it.each(['', 'abc', '3f2b1c0e-0000-4000-8000-00000000000ab', ' 3f2b1c0e-0000-4000-8000-00000000000a'])(
    'rejects %j',
    (value) => {
      expect(isGuid(value)).toBe(false)
    },
  )
})
