export interface ProblemBody {
  title?: string
  detail?: string
  /** Машинный код ошибки (поле `code` в ProblemDetails), например `email_taken`. */
  code?: string
  errors?: Record<string, string[]>
}

/** status = 0 — запрос не дошёл до сервера (сеть). */
export class ApiError extends Error {
  readonly status: number
  readonly title?: string
  readonly detail?: string
  readonly code?: string
  readonly errors?: Record<string, string[]>

  constructor(status: number, problem: ProblemBody = {}) {
    super(problem.detail ?? problem.title ?? `HTTP ${status}`)
    this.name = 'ApiError'
    this.status = status
    this.title = problem.title
    this.detail = problem.detail
    this.code = problem.code
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

/** Тексты ProblemDetails бэкенда английские и могут содержать ID счетов — показываем только известные, по-русски. */
const KNOWN_DETAILS: Array<[RegExp, string]> = [
  [/insufficient funds/i, 'Недостаточно средств на счёте'],
  [/same currency|holds \w+, not \w+/i, 'Счета в разных валютах: перевод возможен только в валюте счёта'],
  [/is blocked/i, 'Счёт заблокирован'],
  [/system accounts/i, 'Операции с системным счётом недоступны'],
  [/balance overflow/i, 'Сумма слишком велика для счёта'],
  [/must be positive/i, 'Лимит должен быть больше нуля'],
  [/less than the per-operation limit/i, 'Дневной лимит не может быть меньше лимита на операцию'],
  [/must not exceed/i, 'Лимит больше допустимого для этой валюты'],
  [/Statement has more than/i, 'Слишком много строк в выписке: сузьте период или выберите вид операций'],
]

/** Ошибки с машинным кодом точнее, чем статус: тексты по-русски, без деталей бэкенда. */
const CODE_DESCRIPTIONS: Record<string, ErrorDescription> = {
  email_taken: { title: 'Этот email уже зарегистрирован', retryable: false },
  invalid_confirmation_token: { title: 'Ссылка недействительна или устарела', retryable: false },
  invalid_credentials: { title: 'Неверный email или пароль', retryable: false },
  account_limit_reached: {
    title: 'Достигнут максимум счетов',
    hint: 'Новый счёт можно будет открыть, когда число счетов станет меньше',
    retryable: false,
  },
  per_operation_limit_exceeded: {
    title: 'Превышен лимит на одну операцию',
    hint: 'Уменьшите сумму или поднимите лимит на странице счёта',
    retryable: false,
  },
  daily_limit_exceeded: {
    title: 'Превышен дневной лимит',
    hint: 'Уменьшите сумму, дождитесь завтра или поднимите лимит на странице счёта',
    retryable: false,
  },
  email_not_confirmed: {
    title: 'Email не подтверждён',
    hint: 'Перейдите по ссылке из письма или запросите письмо ещё раз',
    retryable: false,
  },
}

function localizeDetail(detail?: string): string | undefined {
  if (!detail) return undefined
  return KNOWN_DETAILS.find(([pattern]) => pattern.test(detail))?.[1]
}

export function describeError(err: unknown): ErrorDescription {
  if (!(err instanceof ApiError)) return GENERIC

  const byCode = err.code ? CODE_DESCRIPTIONS[err.code] : undefined
  if (byCode) return byCode

  switch (err.status) {
    case 0:
    case 502:
    case 503:
    case 504:
      return { title: 'Нет связи с сервером', hint: 'Проверьте соединение и повторите', retryable: true }
    case 400:
      return {
        title: 'Проверьте введённые данные',
        hint: localizeDetail(err.detail),
        retryable: false,
        fieldErrors: err.errors,
      }
    case 401:
      return { title: 'Сеанс истёк', hint: 'Войдите снова', retryable: false }
    case 404:
      return { title: 'Счёт не найден', retryable: false }
    case 409:
      return { title: 'Счёт занят, повторите', retryable: true }
    case 422:
      return { title: 'Операцию нельзя выполнить', hint: localizeDetail(err.detail), retryable: false }
    case 429:
      return { title: 'Слишком много попыток', hint: 'Подождите минуту и повторите', retryable: false }
    default:
      return GENERIC
  }
}
