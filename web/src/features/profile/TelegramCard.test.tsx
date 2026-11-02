import { act, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { server } from '../../test/server'
import { renderApp } from '../../test/render'

const settings = { enabled: true, onTransferSent: true, onTransferReceived: true, onDeposit: true }
const unlinked = { linked: false, linkedAt: null, settings: null }
const linked = { linked: true, linkedAt: '2026-11-02T10:00:00Z', settings }

function unavailable() {
  return HttpResponse.json(
    { title: 'Service Unavailable', status: 503, code: 'notifications_unavailable' },
    { status: 503, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

afterEach(() => {
  vi.useRealTimers()
})

describe('TelegramCard', () => {
  it('не привязано: показывает кнопку привязки, создаёт ссылку на бота', async () => {
    let posted = 0
    server.use(
      http.get('/api/me/notifications', () => HttpResponse.json(unlinked)),
      http.post('/api/me/notifications/telegram/link', () => {
        posted++
        return HttpResponse.json({ url: 'https://t.me/minibank_bot?start=abc', expiresAt: '2026-11-02T10:10:00Z' })
      }),
    )
    const user = userEvent.setup()
    renderApp('/profile')

    await user.click(await screen.findByRole('button', { name: 'Привязать Telegram' }))

    const link = await screen.findByRole('link', { name: /Открыть бота/ })
    expect(link).toHaveAttribute('href', 'https://t.me/minibank_bot?start=abc')
    expect(posted).toBe(1)
  })

  it('после ссылки опрашивает статус каждые 3 секунды и показывает настройки, когда чат привязан', async () => {
    let status: typeof unlinked | typeof linked = unlinked
    let gets = 0
    server.use(
      http.get('/api/me/notifications', () => {
        gets++
        return HttpResponse.json(status)
      }),
      http.post('/api/me/notifications/telegram/link', () =>
        HttpResponse.json({ url: 'https://t.me/minibank_bot?start=abc', expiresAt: '2026-11-02T10:10:00Z' }),
      ),
    )
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime })
    renderApp('/profile')
    await user.click(await screen.findByRole('button', { name: 'Привязать Telegram' }))
    await screen.findByRole('link', { name: /Открыть бота/ })

    const before = gets
    status = linked
    await act(async () => {
      await vi.advanceTimersByTimeAsync(3100)
    })

    expect(await screen.findByRole('switch', { name: 'Уведомления' })).toBeChecked()
    expect(gets).toBeGreaterThan(before)
  })

  it('привязано: переключатель отправляет полный объект настроек', async () => {
    let body: unknown
    let saved = settings
    server.use(
      // Сервер хранит сохранённое: после PUT повторный GET (invalidate) должен вернуть уже новые настройки.
      http.get('/api/me/notifications', () => HttpResponse.json({ ...linked, settings: saved })),
      http.put('/api/me/notifications/settings', async ({ request }) => {
        body = await request.json()
        saved = body as typeof settings
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const user = userEvent.setup()
    renderApp('/profile')

    await user.click(await screen.findByRole('switch', { name: 'Входящие переводы' }))

    await waitFor(() =>
      expect(body).toEqual({ enabled: true, onTransferSent: true, onTransferReceived: false, onDeposit: true }),
    )
    expect(screen.getByRole('switch', { name: 'Входящие переводы' })).not.toBeChecked()
    expect(screen.getByRole('switch', { name: 'Исходящие переводы' })).toBeChecked()
    expect(screen.getByRole('switch', { name: 'Пополнения' })).toBeChecked()
  })

  it('при выключенных «Уведомления» остальные переключатели недоступны', async () => {
    server.use(
      http.get('/api/me/notifications', () =>
        HttpResponse.json({ ...linked, settings: { ...settings, enabled: false } }),
      ),
    )
    renderApp('/profile')

    expect(await screen.findByRole('switch', { name: 'Уведомления' })).not.toBeChecked()
    expect(screen.getByRole('switch', { name: 'Исходящие переводы' })).toBeDisabled()
    expect(screen.getByRole('switch', { name: 'Входящие переводы' })).toBeDisabled()
    expect(screen.getByRole('switch', { name: 'Пополнения' })).toBeDisabled()
  })

  it('«Отвязать» шлёт DELETE и возвращает состояние «не привязано»', async () => {
    let deleted = 0
    let status: typeof unlinked | typeof linked = linked
    server.use(
      http.get('/api/me/notifications', () => HttpResponse.json(status)),
      http.delete('/api/me/notifications/telegram', () => {
        deleted++
        status = unlinked
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const user = userEvent.setup()
    renderApp('/profile')

    await user.click(await screen.findByRole('button', { name: 'Отвязать' }))

    expect(await screen.findByRole('button', { name: 'Привязать Telegram' })).toBeInTheDocument()
    expect(deleted).toBe(1)
  })

  it('503: показывает сообщение, остальной профиль работает', async () => {
    server.use(http.get('/api/me/notifications', unavailable))
    renderApp('/profile')

    expect(await screen.findByText('Сервис уведомлений временно недоступен')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Профиль' })).toBeInTheDocument()
    expect(await screen.findByText('alice@example.com')).toBeInTheDocument()
  })

  it('ошибка сохранения откатывает переключатель', async () => {
    server.use(
      http.get('/api/me/notifications', () => HttpResponse.json(linked)),
      http.put('/api/me/notifications/settings', unavailable),
    )
    const user = userEvent.setup()
    renderApp('/profile')

    const sent = await screen.findByRole('switch', { name: 'Исходящие переводы' })
    await user.click(sent)

    expect(await screen.findByText('Сервис уведомлений временно недоступен')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Исходящие переводы' })).toBeChecked())
  })
})
