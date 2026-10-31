import { describe, expect, it } from 'vitest'
import { ApiError, describeError } from './errors'

describe('describeError', () => {
  it('maps 400 to a non-retryable error with field errors', () => {
    const err = new ApiError(400, {
      title: 'One or more validation errors occurred.',
      detail: undefined,
      errors: { Currency: ['The Currency field is required.'] },
    })
    const d = describeError(err)
    expect(d.retryable).toBe(false)
    expect(d.fieldErrors).toEqual({ Currency: ['The Currency field is required.'] })
  })

  it('maps 404 to "Счёт не найден"', () => {
    expect(describeError(new ApiError(404, { detail: 'Account not found' })).title).toBe('Счёт не найден')
  })

  it('maps 409 to a retryable "Счёт занят"', () => {
    const d = describeError(new ApiError(409, {}))
    expect(d.title).toContain('Счёт занят')
    expect(d.retryable).toBe(true)
  })

  it('maps 422 to a business error carrying the server detail as hint', () => {
    const d = describeError(new ApiError(422, { detail: 'Insufficient funds.' }))
    expect(d.title).toBe('Операцию нельзя выполнить')
    expect(d.hint).toBe('Insufficient funds.')
    expect(d.retryable).toBe(false)
  })

  it.each([500, 502])('maps %i to a generic text without server detail', (status) => {
    const d = describeError(new ApiError(status, { detail: 'secret stack trace' }))
    expect(d.hint).toBeUndefined()
    expect(JSON.stringify(d)).not.toContain('secret')
  })

  it('maps network errors (status 0) to a retryable "Нет связи с сервером"', () => {
    const d = describeError(new ApiError(0, {}))
    expect(d.title).toBe('Нет связи с сервером')
    expect(d.retryable).toBe(true)
  })

  it('maps non-ApiError values to a generic text', () => {
    const d = describeError(new Error('boom'))
    expect(d.hint).toBeUndefined()
    expect(d.title).toMatch(/[а-яё]/i)
  })
})
