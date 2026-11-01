/** Куда вернуть после входа: только путь внутри приложения, чужой адрес (open redirect) игнорируем. */
export function safeNext(raw: string | null): string {
  if (!raw || !raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return '/'
  return raw
}
