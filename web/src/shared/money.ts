export type CurrencyCode = 'RUB' | 'USD' | 'EUR'

export const CURRENCIES: readonly CurrencyCode[] = ['RUB', 'USD', 'EUR']

export const MAX_MINOR = BigInt(Number.MAX_SAFE_INTEGER)

const formatters = new Map<CurrencyCode, Intl.NumberFormat>()

function formatterFor(currency: CurrencyCode): Intl.NumberFormat {
  let formatter = formatters.get(currency)
  if (!formatter) {
    formatter = new Intl.NumberFormat('ru-RU', { style: 'currency', currency })
    formatters.set(currency, formatter)
  }
  return formatter
}

export function formatMoney(minor: bigint, currency: CurrencyCode): string {
  // ICU в Node отдаёт «-», в браузерах — «−» (U+2212); приводим к одному виду.
  return formatterFor(currency).format(Number(minor) / 100).replace('-', '−')
}

export type ParseMoneyError = 'empty' | 'invalid' | 'too-many-decimals' | 'not-positive' | 'too-large'

export type ParseMoneyResult = { ok: true; minor: bigint } | { ok: false; error: ParseMoneyError }

const AMOUNT = /^(\d+)(?:[.,](\d*))?$/

export function parseMoney(input: string): ParseMoneyResult {
  const text = input.replace(/\s/g, '')
  if (text === '') return { ok: false, error: 'empty' }

  const match = AMOUNT.exec(text)
  if (!match || (text.includes('.') || text.includes(',')) && !match[2]) {
    return { ok: false, error: 'invalid' }
  }

  const whole = match[1]
  const fraction = match[2] ?? ''
  if (fraction.length > 2) return { ok: false, error: 'too-many-decimals' }

  const minor = BigInt(whole) * 100n + BigInt(fraction.padEnd(2, '0'))
  if (minor <= 0n) return { ok: false, error: 'not-positive' }
  if (minor > MAX_MINOR) return { ok: false, error: 'too-large' }
  return { ok: true, minor }
}

const ERROR_TEXTS: Record<ParseMoneyError, string> = {
  empty: 'Введите сумму',
  invalid: 'Введите сумму цифрами, например 1500 или 1500,50',
  'too-many-decimals': 'Не больше двух знаков после запятой',
  'not-positive': 'Сумма должна быть больше нуля',
  'too-large': 'Слишком большая сумма',
}

export function moneyErrorText(error: ParseMoneyError): string {
  return ERROR_TEXTS[error]
}
