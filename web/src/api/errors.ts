export interface ProblemBody {
  title?: string
  detail?: string
  errors?: Record<string, string[]>
}

/** status = 0 — запрос не дошёл до сервера (сеть). */
export class ApiError extends Error {
  readonly status: number
  readonly title?: string
  readonly detail?: string
  readonly errors?: Record<string, string[]>

  constructor(status: number, problem: ProblemBody = {}) {
    super(problem.detail ?? problem.title ?? `HTTP ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.title = problem.title
    this.detail = problem.detail
    this.errors = problem.errors
  }
}

export interface ErrorDescription {
  title: string
  hint?: string
  retryable: boolean
  fieldErrors?: Record<string, string[]>
}

const GENERIC: ErrorDescription = {
  title: 'Что-то пошло не так. Попробуйте позже',
  retryable: false,
}

export function describeError(err: unknown): ErrorDescription {
  if (!(err instanceof ApiError)) return GENERIC

  switch (err.status) {
    case 0:
    case 502:
    case 503:
    case 504:
      return { title: 'Нет связи с сервером', hint: 'Проверьте соединение и повторите', retryable: true }
    case 400:
      return {
        title: 'Проверьте введённые данные',
        hint: err.detail,
        retryable: false,
        fieldErrors: err.errors,
      }
    case 404:
      return { title: 'Счёт не найден', retryable: false }
    case 409:
      return { title: 'Счёт занят, повторите', retryable: true }
    case 422:
      return { title: 'Операцию нельзя выполнить', hint: err.detail, retryable: false }
    default:
      return GENERIC
  }
}
