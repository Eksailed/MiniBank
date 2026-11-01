import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'
import { getToken, setToken } from './session'

const TOKEN_KEY = 'minibank.token'

function problem(status: number, code?: string) {
  return HttpResponse.json(
    { title: 'Problem', status, detail: 'internal detail', ...(code ? { code } : {}) },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

const session = { accessToken: 'jwt-123', expiresAt: '2030-01-01T00:00:00Z' }

function useLogin(respond: () => Response = () => HttpResponse.json(session)) {
  const bodies: unknown[] = []
  server.use(
    http.post('/api/auth/login', async ({ request }) => {
      bodies.push(await request.json())
      return respond()
    }),
  )
  return bodies
}

async function fillLogin(user: ReturnType<typeof userEvent.setup>, email = 'alice@example.com', password = 'secret-pass-1') {
  await user.type(screen.getByLabelText('Email'), email)
  await user.type(screen.getByLabelText('Пароль'), password)
  await user.click(screen.getByRole('button', { name: 'Войти' }))
}

describe('route protection', () => {
  it('redirects an anonymous visitor to /login and remembers where they were going', async () => {
    const { router } = renderApp('/accounts/abc', { token: null })

    expect(await screen.findByRole('button', { name: 'Войти' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(router.state.location.search).toBe('?next=%2Faccounts%2Fabc')
  })

  it('sends the visitor back to the requested page after signing in', async () => {
    useLogin()
    server.use(http.get('/api/accounts', () => HttpResponse.json([])))
    const user = userEvent.setup()
    const { router } = renderApp('/login?next=%2Faccounts%2Fabc%2Fentries', { token: null })

    await fillLogin(user)

    await waitFor(() => expect(router.state.location.pathname).toBe('/accounts/abc/entries'))
  })

  it.each(['https://evil.example', '//evil.example', '/\\evil.example', 'javascript:alert(1)'])(
    'ignores the foreign redirect target %s and goes home',
    async (target) => {
      useLogin()
      const user = userEvent.setup()
      const { router } = renderApp(`/login?next=${encodeURIComponent(target)}`, { token: null })

      await fillLogin(user)

      await waitFor(() => expect(router.state.location.pathname).toBe('/'))
    },
  )

  it('lets /register and /confirm-email open without a token', () => {
    const { unmount } = renderApp('/register', { token: null })
    expect(screen.getByRole('button', { name: 'Зарегистрироваться' })).toBeInTheDocument()
    unmount()

    server.use(http.post('/api/auth/confirm-email', () => new HttpResponse(null, { status: 204 })))
    renderApp('/confirm-email', { token: null })
    expect(screen.queryByRole('button', { name: 'Войти' })).not.toBeInTheDocument()
  })
})

describe('login', () => {
  it('stores the token in sessionStorage and shows the home page', async () => {
    const bodies = useLogin()
    const user = userEvent.setup()
    renderApp('/login', { token: null })

    await fillLogin(user)

    expect(await screen.findByRole('heading', { name: 'Мои счета' })).toBeInTheDocument()
    expect(bodies).toEqual([{ email: 'alice@example.com', password: 'secret-pass-1' }])
    expect(sessionStorage.getItem(TOKEN_KEY)).toBe('jwt-123')
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull()
  })

  it('does not send a request for empty fields', async () => {
    const bodies = useLogin()
    const user = userEvent.setup()
    renderApp('/login', { token: null })

    await user.click(screen.getByRole('button', { name: 'Войти' }))

    expect(screen.getByLabelText('Email')).toBeInvalid()
    expect(screen.getByLabelText('Пароль')).toBeInvalid()
    expect(bodies).toEqual([])
  })

  it('shows an error and stays on the page for a wrong email/password pair', async () => {
    useLogin(() => problem(401, 'invalid_credentials'))
    const user = userEvent.setup()
    const { router } = renderApp('/login', { token: null })

    await fillLogin(user, 'alice@example.com', 'wrong-password')

    expect(await screen.findByRole('alert')).toHaveTextContent('Неверный email или пароль')
    expect(router.state.location.pathname).toBe('/login')
    expect(getToken()).toBeNull()
  })

  it('suggests resending the letter when the email is not confirmed', async () => {
    useLogin(() => problem(403, 'email_not_confirmed'))
    const resent: unknown[] = []
    server.use(
      http.post('/api/auth/resend-confirmation', async ({ request }) => {
        resent.push(await request.json())
        return new HttpResponse(null, { status: 202 })
      }),
    )
    const user = userEvent.setup()
    renderApp('/login', { token: null })

    await fillLogin(user)
    expect(await screen.findByRole('alert')).toHaveTextContent('Email не подтверждён')
    await user.click(screen.getByRole('button', { name: 'Отправить письмо ещё раз' }))

    expect(await screen.findByText('Письмо отправлено')).toBeInTheDocument()
    expect(resent).toEqual([{ email: 'alice@example.com' }])
  })

  it('tells the user to wait on 429 without a retry loop', async () => {
    useLogin(() => problem(429))
    const user = userEvent.setup()
    renderApp('/login', { token: null })

    await fillLogin(user)

    expect(await screen.findByRole('alert')).toHaveTextContent('Слишком много попыток')
  })
})

describe('registration', () => {
  it('validates email, password length and name before any request', async () => {
    const calls: unknown[] = []
    server.use(
      http.post('/api/auth/register', async ({ request }) => {
        calls.push(await request.json())
        return new HttpResponse(null, { status: 202 })
      }),
    )
    const user = userEvent.setup()
    renderApp('/register', { token: null })

    await user.type(screen.getByLabelText('Email'), 'not-an-email')
    await user.type(screen.getByLabelText('Пароль'), 'short')
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }))

    expect(screen.getByLabelText('Email')).toBeInvalid()
    expect(screen.getByLabelText('Пароль')).toBeInvalid()
    expect(screen.getByLabelText('Имя')).toBeInvalid()
    expect(calls).toEqual([])
  })

  it('asks to check the mailbox after a successful registration', async () => {
    const calls: unknown[] = []
    server.use(
      http.post('/api/auth/register', async ({ request }) => {
        calls.push(await request.json())
        return new HttpResponse(null, { status: 202 })
      }),
    )
    const user = userEvent.setup()
    renderApp('/register', { token: null })

    await user.type(screen.getByLabelText('Email'), ' alice@example.com ')
    await user.type(screen.getByLabelText('Пароль'), 'secret-pass-1')
    await user.type(screen.getByLabelText('Имя'), ' Alice ')
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }))

    expect(await screen.findByText('Проверьте почту')).toBeInTheDocument()
    expect(screen.getByText(/alice@example\.com/)).toBeInTheDocument()
    expect(calls).toEqual([{ email: 'alice@example.com', password: 'secret-pass-1', displayName: 'Alice' }])
  })

  it('lets the user resend the letter right after registration', async () => {
    const resent: unknown[] = []
    server.use(
      http.post('/api/auth/register', () => new HttpResponse(null, { status: 202 })),
      http.post('/api/auth/resend-confirmation', async ({ request }) => {
        resent.push(await request.json())
        return new HttpResponse(null, { status: 202 })
      }),
    )
    const user = userEvent.setup()
    renderApp('/register', { token: null })

    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Пароль'), 'secret-pass-1')
    await user.type(screen.getByLabelText('Имя'), 'Alice')
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }))
    await user.click(await screen.findByRole('button', { name: 'Отправить письмо ещё раз' }))

    expect(await screen.findByText('Письмо отправлено')).toBeInTheDocument()
    expect(resent).toEqual([{ email: 'alice@example.com' }])
  })

  it('limits the name to what the server accepts', () => {
    renderApp('/register', { token: null })

    expect(screen.getByLabelText('Имя')).toHaveAttribute('maxlength', '100')
  })

  it('shows "email taken" next to the email field', async () => {
    server.use(http.post('/api/auth/register', () => problem(409, 'email_taken')))
    const user = userEvent.setup()
    renderApp('/register', { token: null })

    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.type(screen.getByLabelText('Пароль'), 'secret-pass-1')
    await user.type(screen.getByLabelText('Имя'), 'Alice')
    await user.click(screen.getByRole('button', { name: 'Зарегистрироваться' }))

    const email = screen.getByLabelText('Email')
    await waitFor(() => expect(email).toBeInvalid())
    expect(email).toHaveAccessibleDescription('Этот email уже зарегистрирован')
  })
})

describe('email confirmation', () => {
  it('confirms the token from the link and offers to sign in', async () => {
    const tokens: unknown[] = []
    server.use(
      http.post('/api/auth/confirm-email', async ({ request }) => {
        tokens.push(await request.json())
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/confirm-email#token=abc123', { token: null })

    expect(await screen.findByText('Email подтверждён')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Войти' })).toHaveAttribute('href', '/login')
    expect(tokens).toEqual([{ token: 'abc123' }])
  })

  it('explains an invalid token and lets the user request a new letter', async () => {
    server.use(
      http.post('/api/auth/confirm-email', () => problem(400, 'invalid_confirmation_token')),
      http.post('/api/auth/resend-confirmation', () => new HttpResponse(null, { status: 202 })),
    )
    const user = userEvent.setup()
    renderApp('/confirm-email#token=stale', { token: null })

    expect(await screen.findByRole('alert')).toHaveTextContent('Ссылка недействительна или устарела')
    await user.type(screen.getByLabelText('Email'), 'alice@example.com')
    await user.click(screen.getByRole('button', { name: 'Отправить письмо ещё раз' }))

    expect(await screen.findByText('Письмо отправлено')).toBeInTheDocument()
  })

  it('treats a link without a token as invalid without a request', async () => {
    renderApp('/confirm-email', { token: null })

    expect(await screen.findByRole('alert')).toHaveTextContent('Ссылка недействительна или устарела')
  })
})

describe('session', () => {
  it('sends the token in the Authorization header', async () => {
    let header: string | null = null
    server.use(
      http.get('/api/me', ({ request }) => {
        header = request.headers.get('Authorization')
        return HttpResponse.json({ id: 'u1', email: 'alice@example.com', displayName: 'Alice' })
      }),
    )
    renderApp('/', { token: 'jwt-abc' })

    await waitFor(() => expect(header).toBe('Bearer jwt-abc'))
  })

  it('does not send the token to /auth endpoints', async () => {
    let header: string | null = 'unset'
    server.use(
      http.post('/api/auth/login', ({ request }) => {
        header = request.headers.get('Authorization')
        return HttpResponse.json(session)
      }),
    )
    setToken('stale-token')
    const { api } = await import('../../api/client')

    await api.login({ email: 'a@b.co', password: 'secret-pass-1' })

    expect(header).toBeNull()
  })

  it('drops the token on a 401 and leads to /login', async () => {
    server.use(http.get('/api/me', () => problem(401)))
    const { router } = renderApp('/', { token: 'expired' })

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
    expect(getToken()).toBeNull()
    expect(sessionStorage.getItem(TOKEN_KEY)).toBeNull()
  })

  it('shows the name from /me in the header', async () => {
    server.use(http.get('/api/me', () => HttpResponse.json({ id: 'u1', email: 'alice@example.com', displayName: 'Alice' })))
    renderApp('/', { token: 'jwt-abc' })

    expect(await screen.findByText('Alice')).toBeInTheDocument()
  })

  it('"Выйти" clears the token and shows the login page', async () => {
    const user = userEvent.setup()
    const { router } = renderApp('/', { token: 'jwt-abc' })

    await user.click(await screen.findByRole('button', { name: 'Выйти' }))

    await waitFor(() => expect(router.state.location.pathname).toBe('/login'))
    expect(getToken()).toBeNull()
  })
})
