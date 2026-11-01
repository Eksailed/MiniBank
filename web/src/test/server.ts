import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

/** Шапка приложения всегда просит /me; остальные запросы тесты объявляют сами (server.use). */
export const server = setupServer(
  http.get('/api/me', () => HttpResponse.json({ id: 'u1', email: 'alice@example.com', displayName: 'Alice' })),
)
