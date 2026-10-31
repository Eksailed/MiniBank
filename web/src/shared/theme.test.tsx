import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resolveInitialTheme, THEME_KEY } from './theme'
import { ThemeToggle } from './ThemeToggle'

function stubPrefersDark(prefersDark: boolean) {
  vi.stubGlobal(
    'matchMedia',
    vi.fn().mockImplementation((query: string) => ({
      matches: query.includes('dark') ? prefersDark : false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    })),
  )
}

describe('resolveInitialTheme', () => {
  it.each([
    ['dark', false, 'dark'],
    ['light', true, 'light'],
    [null, true, 'dark'],
    [null, false, 'light'],
    ['мусор', true, 'dark'],
  ] as const)('stored=%j prefersDark=%s -> %s', (stored, prefersDark, expected) => {
    expect(resolveInitialTheme(stored, prefersDark)).toBe(expected)
  })
})

describe('ThemeToggle', () => {
  beforeEach(() => {
    delete document.documentElement.dataset.theme
    stubPrefersDark(false)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('starts from the system theme, toggles and persists the choice', async () => {
    const user = userEvent.setup()
    render(<ThemeToggle />)
    expect(document.documentElement.dataset.theme).toBe('light')

    await user.click(screen.getByRole('button', { name: 'Переключить тему' }))
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(localStorage.getItem(THEME_KEY)).toBe('dark')

    await user.click(screen.getByRole('button', { name: 'Переключить тему' }))
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(localStorage.getItem(THEME_KEY)).toBe('light')
  })

  it('uses the stored theme over the system one', () => {
    stubPrefersDark(true)
    localStorage.setItem(THEME_KEY, 'light')
    render(<ThemeToggle />)
    expect(document.documentElement.dataset.theme).toBe('light')
  })
})
