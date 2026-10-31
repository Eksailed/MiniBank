import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/errors'
import { ActionTile, Button, ErrorNotice, TextField } from './index'

describe('ActionTile', () => {
  it('renders a link named by its label when `to` is given', () => {
    render(
      <MemoryRouter>
        <ActionTile icon={<span />} label="Выписка" to="/accounts/1/entries" />
      </MemoryRouter>,
    )
    expect(screen.getByRole('link', { name: 'Выписка' })).toHaveAttribute('href', '/accounts/1/entries')
  })

  it('renders a button that calls onClick when there is no `to`', async () => {
    const onClick = vi.fn()
    render(<ActionTile icon={<span />} label="Реквизиты" onClick={onClick} />)
    await userEvent.click(screen.getByRole('button', { name: 'Реквизиты' }))
    expect(onClick).toHaveBeenCalledTimes(1)
  })
})

describe('Button', () => {
  it('is disabled and busy while pending, and ignores clicks', async () => {
    const onClick = vi.fn()
    render(
      <Button pending onClick={onClick}>
        Перевести
      </Button>,
    )
    const button = screen.getByRole('button', { name: 'Перевести' })
    expect(button).toBeDisabled()
    expect(button).toHaveAttribute('aria-busy', 'true')
    await userEvent.click(button)
    expect(onClick).not.toHaveBeenCalled()
  })
})

describe('TextField', () => {
  it('links label, invalid state and error text', () => {
    render(<TextField label="Сумма" error="Введите сумму" />)
    const input = screen.getByLabelText('Сумма')
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(input).toHaveAccessibleDescription('Введите сумму')
  })

  it('is valid and has no description without an error', () => {
    render(<TextField label="Сумма" />)
    const input = screen.getByLabelText('Сумма')
    expect(input).not.toHaveAttribute('aria-invalid', 'true')
    expect(input).not.toHaveAttribute('aria-describedby')
  })
})

describe('ErrorNotice', () => {
  it('offers a retry for a retryable error', async () => {
    const onRetry = vi.fn()
    render(<ErrorNotice error={new ApiError(409)} onRetry={onRetry} />)
    expect(screen.getByRole('alert')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Повторить' }))
    expect(onRetry).toHaveBeenCalledTimes(1)
  })

  it('has no retry button for a non-retryable error', () => {
    render(<ErrorNotice error={new ApiError(404)} onRetry={() => {}} />)
    expect(screen.getByRole('alert')).toHaveTextContent('Счёт не найден')
    expect(screen.queryByRole('button', { name: 'Повторить' })).not.toBeInTheDocument()
  })

  it('has no retry button when onRetry is not provided', () => {
    render(<ErrorNotice error={new ApiError(409)} />)
    expect(screen.queryByRole('button', { name: 'Повторить' })).not.toBeInTheDocument()
  })
})
