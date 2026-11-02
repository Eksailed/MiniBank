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

  it('describes an invalid reset link', () => {
    const d = describeError(new ApiError(400, { code: 'invalid_reset_token' }))
    expect(d.title).toBe('Ссылка недействительна или устарела')
    expect(d.retryable).toBe(false)
  })

  it('maps 404 to "Счёт не найден"', () => {
    expect(describeError(new ApiError(404, { detail: 'Account not found' })).title).toBe('Счёт не найден')
  })

  it('maps 409 to a retryable "Счёт занят"', () => {
    const d = describeError(new ApiError(409, {}))
    expect(d.title).toContain('Счёт занят')
    expect(d.retryable).toBe(true)
  })

  it('maps 422 insufficient funds to a Russian hint without the account id', () => {
    const d = describeError(new ApiError(422, { detail: 'Account 3f2b1c0e-0000-4000-8000-000000000001 has insufficient funds.' }))
    expect(d.title).toBe('Операцию нельзя выполнить')
    expect(d.hint).toBe('Недостаточно средств на счёте')
    expect(JSON.stringify(d)).not.toContain('3f2b1c0e')
    expect(d.retryable).toBe(false)
  })

  it('maps 400 currency mismatch to a Russian hint', () => {
    const d = describeError(new ApiError(400, { detail: 'Transfer requires accounts and amount in the same currency.' }))
    expect(d.title).toBe('Проверьте введённые данные')
    expect(d.hint).toBe('Счета в разных валютах: перевод возможен только в валюте счёта')
  })

  it.each([
    [422, 'Account 3f2b1c0e-0000-4000-8000-000000000001 is blocked.', 'Счёт заблокирован'],
    [422, 'System accounts cannot take part in client transfers.', 'Операции с системным счётом недоступны'],
    [400, 'Balance overflow.', 'Сумма слишком велика для счёта'],
  ])('maps %i "%s" to a Russian hint', (status, detail, hint) => {
    expect(describeError(new ApiError(status, { detail })).hint).toBe(hint)
  })

  it('drops an unknown English server detail instead of showing it', () => {
    const d = describeError(new ApiError(422, { detail: 'Something unexpected happened.' }))
    expect(d.hint).toBeUndefined()
  })

  it('maps 500 to a generic text without server detail', () => {
    const d = describeError(new ApiError(500, { detail: 'secret stack trace' }))
    expect(d.hint).toBeUndefined()
    expect(JSON.stringify(d)).not.toContain('secret')
  })

  it.each([502, 503, 504])('maps gateway error %i to a retryable "Нет связи с сервером"', (status) => {
    const d = describeError(new ApiError(status, { detail: 'secret stack trace' }))
    expect(d.title).toBe('Нет связи с сервером')
    expect(d.retryable).toBe(true)
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
