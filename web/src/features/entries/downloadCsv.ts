/** Отдаёт файл браузеру: временная ссылка на Blob и клик по `<a download>`. */
export function downloadCsv(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.append(link)
  link.click()
  link.remove()
  // Скачивание уже стартовало; ссылку освобождаем чуть позже, чтобы не оборвать его в Safari.
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}
