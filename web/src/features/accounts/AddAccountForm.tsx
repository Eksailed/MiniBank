import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { api } from '../../api/client'
import { isGuid } from '../../shared/guid'
import { Button, ErrorNotice, TextField } from '../../shared/ui'
import { useAccountDirectory } from './DirectoryProvider'
import { accountKeys } from './queries'
import styles from './forms.module.css'

export function AddAccountForm() {
  const [value, setValue] = useState('')
  const [fieldError, setFieldError] = useState<string>()
  const directory = useAccountDirectory()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const lookup = useMutation({
    mutationFn: (id: string) => api.getAccount(id),
    onSuccess: (account) => {
      queryClient.setQueryData(accountKeys.detail(account.id), account)
      directory.add(account.id)
      navigate(`/accounts/${account.id}`)
    },
  })

  function submit(event: FormEvent) {
    event.preventDefault()
    const id = value.trim().toLowerCase()
    if (!isGuid(id)) {
      setFieldError('Это не похоже на ID счёта — он выглядит как 3f2b1c0e-0000-4000-8000-000000000001')
      return
    }
    setFieldError(undefined)
    lookup.mutate(id)
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate>
      <h2 className={styles.heading}>Добавить по ID</h2>
      <TextField
        label="ID счёта"
        value={value}
        error={fieldError}
        autoComplete="off"
        spellCheck={false}
        onChange={(e) => setValue(e.target.value)}
      />
      {lookup.isError && <ErrorNotice error={lookup.error} />}
      <Button type="submit" variant="secondary" pending={lookup.isPending}>
        Добавить
      </Button>
    </form>
  )
}
