/** Куда вернуть после входа: только путь внутри приложения, чужой адрес (open redirect) игнорируем. */
export function safeNext(raw: string | null): string {
  if (!raw || !raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return '/'
  // Браузер выбрасывает \t \n \r из URL, и «/\t/evil.com» превратился бы в «//evil.com».
  if (/[\u0000-\u001f\u007f]/.test(raw)) return '/'
  return new URL(raw, window.location.origin).origin === window.location.origin ? raw : '/'
}
