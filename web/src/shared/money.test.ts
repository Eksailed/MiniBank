import { describe, expect, it } from 'vitest'
import { formatMoney, MAX_MINOR, moneyErrorText, parseMoney } from './money'

const normalize = (s: string) => s.replace(/[  ]/g, ' ')

describe('formatMoney', () => {
  it('formats rubles with grouping and symbol', () => {
    expect(normalize(formatMoney(1245000n, 'RUB'))).toBe('12 450,00 ₽')
  })

  it('formats small dollar amounts', () => {
    expect(normalize(formatMoney(5n, 'USD'))).toBe('0,05 $')
  })

  it('formats negative euros with a minus sign', () => {
    expect(normalize(formatMoney(-150000n, 'EUR'))).toBe('−1 500,00 €')
  })
})

describe('parseMoney', () => {
  it.each([
    ['1500', 150000n],
    ['1500,5', 150050n],
    ['1500.50', 150050n],
    ['12 345,67', 1234567n],
    ['12 345,67', 1234567n],
    ['0,01', 1n],
  ])('parses %j', (input, minor) => {
    expect(parseMoney(input)).toEqual({ ok: true, minor })
  })

  it.each([
    ['', 'empty'],
    ['   ', 'empty'],
    ['abc', 'invalid'],
    ['1e3', 'invalid'],
    ['-5', 'invalid'],
    ['1,2,3', 'invalid'],
    ['1.', 'invalid'],
    [',5', 'invalid'],
    ['0', 'not-positive'],
    ['0,00', 'not-positive'],
    ['1,234', 'too-many-decimals'],
    ['90071992547409,92', 'too-large'],
  ])('rejects %j as %s', (input, error) => {
    expect(parseMoney(input)).toEqual({ ok: false, error })
  })

  it('accepts the largest safe amount', () => {
    expect(parseMoney('90071992547409,91')).toEqual({ ok: true, minor: MAX_MINOR })
  })
})

describe('moneyErrorText', () => {
  it('has a Russian text for every error', () => {
    for (const e of ['empty', 'invalid', 'too-many-decimals', 'not-positive', 'too-large'] as const) {
      expect(moneyErrorText(e)).toMatch(/[а-яё]/i)
    }
  })
})
