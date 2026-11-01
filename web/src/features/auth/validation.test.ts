import { describe, expect, it } from 'vitest'
import { passwordStrength } from './validation'

describe('passwordStrength', () => {
  it.each([
    ['', 0],
    ['abc', 1],
    ['abcdefgh', 2],
    ['abcdefghijkl', 3],
    ['Abcdefgh12!x', 4],
  ])('%j → %i', (password, expected) => {
    expect(passwordStrength(password)).toBe(expected)
  })
})
