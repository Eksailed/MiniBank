import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'

/**
 * Запросы, которые экраны делают всегда: шапка просит /me, главная — список счетов, страница счёта — лимиты.
 * Остальное тесты объявляют сами (server.use).
 */
export const server = setupServer(
  http.get('/api/me', () => HttpResponse.json({ id: 'u1', email: 'alice@example.com', displayName: 'Alice' })),
  http.get('/api/accounts', () => HttpResponse.json([])),
  http.get('/api/accounts/:id/limits', () =>
    HttpResponse.json({
      perOperation: 10000000,
      daily: 50000000,
      spentToday: 0,
      remainingToday: 50000000,
      maxPerOperation: 100000000,
      maxDaily: 500000000,
    }),
  ),
)
