import { Link } from 'react-router'
import { api } from '../../api/client'
import { AuthLogo } from './AuthLogo'
import { ResendConfirmation } from './ResendConfirmation'
import styles from './auth.module.css'

export function ForgotPasswordPage() {
  return (
    <div className={styles.screen}>
      <AuthLogo />
      <h1 className={styles.heading}>Забыли пароль?</h1>
      <p className={styles.text}>Укажите email, и мы пришлём ссылку для сброса</p>
      <ResendConfirmation
        label="Отправить ссылку"
        sentText="Если такой адрес зарегистрирован, мы отправили письмо"
        action={api.forgotPassword}
      />
      <Link to="/login" className={styles.links}>
        Вернуться ко входу
      </Link>
    </div>
  )
}
