import { useCallback, useState } from 'react'

function sameDeps(a: readonly unknown[], b: readonly unknown[]): boolean {
  return a.length === b.length && a.every((value, i) => Object.is(value, b[i]))
}

/**
 * Ключ идемпотентности операции: стабилен, пока не менялись `deps` (повтор после сбоя шлёт тот же ключ),
 * и меняется при правке `deps` или после `rotate()` (новая операция).
 */
export function useIdempotentKey(deps: readonly unknown[]): { key: string; rotate(): void } {
  const [state, setState] = useState(() => ({ key: crypto.randomUUID(), deps }))

  let current = state
  if (!sameDeps(state.deps, deps)) {
    // Производное состояние: пересчитываем прямо при рендере, чтобы старый ключ не успел уйти в запрос.
    current = { key: crypto.randomUUID(), deps }
    setState(current)
  }

  const rotate = useCallback(() => setState((s) => ({ ...s, key: crypto.randomUUID() })), [])
  return { key: current.key, rotate }
}
