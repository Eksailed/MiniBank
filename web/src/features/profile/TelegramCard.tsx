import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../api/client'
import { ApiError } from '../../api/errors'
import type { NotificationSettings, TelegramLink, TelegramStatus } from '../../api/types'
import { Button, ErrorNotice } from '../../shared/ui'
import styles from './TelegramCard.module.css'

const KEY = ['notifications'] as const
const POLL_MS = 3000

const TOGGLES: { field: keyof NotificationSettings; label: string }[] = [
  { field: 'onTransferSent', label: 'Исходящие переводы' },
  { field: 'onTransferReceived', label: 'Входящие переводы' },
  { field: 'onDeposit', label: 'Пополнения' },
]

function Failure({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  if (error instanceof ApiError && error.status === 503) {
    return (
      <p role="alert" className={styles.hint}>
        Сервис уведомлений временно недоступен
      </p>
    )
  }
  return <ErrorNotice error={error} onRetry={onRetry} />
}

function Switch({
  label,
  checked,
  disabled,
  onChange,
}: {
  label: string
  checked: boolean
  disabled?: boolean
  onChange: (value: boolean) => void
}) {
  return (
    <div className={styles.toggleRow}>
      <span>{label}</span>
      <button
        type="button"
        role="switch"
        aria-label={label}
        aria-checked={checked}
        disabled={disabled}
        className={styles.switch}
        onClick={() => onChange(!checked)}
      />
    </div>
  )
}

function Settings({ settings }: { settings: NotificationSettings }) {
  const queryClient = useQueryClient()
  const save = useMutation({
    mutationFn: api.updateNotificationSettings,
    onMutate: async (next: NotificationSettings) => {
      await queryClient.cancelQueries({ queryKey: KEY })
      const previous = queryClient.getQueryData<TelegramStatus>(KEY)
      queryClient.setQueryData<TelegramStatus>(KEY, (old) => (old ? { ...old, settings: next } : old))
      return { previous }
    },
    onError: (_error, _next, context) => {
      if (context?.previous) queryClient.setQueryData(KEY, context.previous)
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: KEY }),
  })

  const change = (patch: Partial<NotificationSettings>) => save.mutate({ ...settings, ...patch })

  return (
    <div className={styles.toggles}>
      <Switch label="Уведомления" checked={settings.enabled} onChange={(enabled) => change({ enabled })} />
      {TOGGLES.map(({ field, label }) => (
        <Switch
          key={field}
          label={label}
          checked={settings[field]}
          disabled={!settings.enabled}
          onChange={(value) => change({ [field]: value })}
        />
      ))}
      {save.isError && <Failure error={save.error} />}
    </div>
  )
}

export function TelegramCard() {
  const queryClient = useQueryClient()
  const [link, setLink] = useState<TelegramLink | null>(null)

  const status = useQuery({
    queryKey: KEY,
    queryFn: api.getNotifications,
    // Пока пользователь привязывает чат в Telegram, спрашиваем статус, чтобы показать настройки без перезагрузки.
    refetchInterval: (query) => (link && !query.state.data?.linked ? POLL_MS : false),
    retry: false,
  })

  const createLink = useMutation({
    mutationFn: api.createTelegramLink,
    onSuccess: setLink,
  })
  const unlink = useMutation({
    mutationFn: api.unlinkTelegram,
    onSuccess: async () => {
      setLink(null)
      await queryClient.invalidateQueries({ queryKey: KEY })
    },
  })

  const data = status.data

  return (
    <section className={styles.card} aria-label="Уведомления в Telegram">
      <h2 className={styles.heading}>Telegram</h2>
      {status.isError && <Failure error={status.error} onRetry={() => void status.refetch()} />}
      {data?.linked && data.settings && (
        <>
          <Settings settings={data.settings} />
          <Button variant="secondary" pending={unlink.isPending} onClick={() => unlink.mutate()}>
            Отвязать
          </Button>
          {unlink.isError && <Failure error={unlink.error} />}
        </>
      )}
      {data && !data.linked && (
        <>
          <p className={styles.hint}>Получайте сообщения о переводах и пополнениях в Telegram.</p>
          {link ? (
            <a className={styles.link} href={link.url} target="_blank" rel="noreferrer">
              Открыть бота в Telegram
            </a>
          ) : (
            <Button pending={createLink.isPending} onClick={() => createLink.mutate()}>
              Привязать Telegram
            </Button>
          )}
          {createLink.isError && <Failure error={createLink.error} />}
        </>
      )}
    </section>
  )
}
