#!/usr/bin/env bash
# Дымовая проверка поднятого стека: bash scripts/smoke.sh [BASE_URL] [MAILPIT_URL].
# По умолчанию http://localhost:8080 (веб + API за nginx) и http://localhost:8025 (Mailpit).
# Адрес RabbitMQ management задаётся RABBIT_URL (по умолчанию http://localhost:15672).
# Проверка лимита запросов в конце исчерпывает окно входа/регистрации для вашего IP на минуту:
# повторный запуск в течение минуты упрётся в 429 на регистрации.
set -euo pipefail

BASE="${1:-http://localhost:8080}"
MAILPIT="${2:-http://localhost:8025}"
# Management API RabbitMQ (Compose публикует его только на 127.0.0.1) и учётные данные Compose по умолчанию.
RABBIT="${RABBIT_URL:-http://localhost:15672}"
RABBIT_USER="${RABBITMQ_USER:-minibank}"
RABBIT_PASSWORD="${RABBITMQ_PASSWORD:-minibank-local-only}"

TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

fail() {
  echo "FAIL: $1" >&2
  exit 1
}

# expect_status <описание> <ожидаемый код> <curl-аргументы...>; тело ответа остаётся в $BODY.
expect_status() {
  local what="$1" expected="$2"
  shift 2
  local out code
  out="$(curl -s -w $'\n%{http_code}' "$@")" || fail "$what (curl не смог выполнить запрос)"
  code="${out##*$'\n'}"
  BODY="${out%$'\n'*}"
  [ "$code" = "$expected" ] || fail "$what (got $code, expected $expected)"
  echo "ok: $what"
}

# json_field <имя>: строковое значение поля из JSON со stdin (хватает для плоских ответов API).
json_field() {
  sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p"
}

RUN="$$-$RANDOM"
EMAIL="smoke-$RUN@example.com"
PASSWORD="smoke-password-$RUN"

expect_status "GET /api/health" 200 "$BASE/api/health"

expect_status "GET /api/accounts без токена" 401 "$BASE/api/accounts"

expect_status "POST /api/auth/register" 202 -X POST "$BASE/api/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\",\"displayName\":\"Smoke\"}"

# До подтверждения email войти нельзя.
expect_status "POST /api/auth/login до подтверждения email" 403 -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}"

# Письмо приходит в Mailpit; даём SMTP до 15 секунд.
MESSAGE_ID=""
for _ in $(seq 1 30); do
  MESSAGE_ID="$(curl -s -G "$MAILPIT/api/v1/search" --data-urlencode "query=to:$EMAIL" | sed -n 's/.*"ID":"\([^"]*\)".*/\1/p')"
  [ -n "$MESSAGE_ID" ] && break
  sleep 0.5
done
[ -n "$MESSAGE_ID" ] || fail "письмо для $EMAIL не пришло в Mailpit"
echo "ok: письмо подтверждения пришло в Mailpit"

# Ссылка из письма: <Web__BaseUrl>/confirm-email#token=<base64url> (токен во фрагменте, до сервера не доходит).
TOKEN="$(curl -s "$MAILPIT/api/v1/message/$MESSAGE_ID" | sed -n 's/.*confirm-email#token=\([A-Za-z0-9_-]*\).*/\1/p')"
[ -n "$TOKEN" ] || fail "в письме нет ссылки с токеном подтверждения"

expect_status "POST /api/auth/confirm-email" 204 -X POST "$BASE/api/auth/confirm-email" \
  -H "Content-Type: application/json" -d "{\"token\":\"$TOKEN\"}"

expect_status "POST /api/auth/login" 200 -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}"
ACCESS="$(printf '%s' "$BODY" | json_field accessToken)"
[ -n "$ACCESS" ] || fail "в ответе POST /api/auth/login нет accessToken"
AUTH=(-H "Authorization: Bearer $ACCESS")

expect_status "GET /api/me" 200 "${AUTH[@]}" "$BASE/api/me"

expect_status "POST /api/accounts (первый)" 201 "${AUTH[@]}" -X POST "$BASE/api/accounts" \
  -H "Content-Type: application/json" -d '{"currency":"RUB"}'
FROM="$(printf '%s' "$BODY" | json_field id)"
[ -n "$FROM" ] || fail "в ответе POST /api/accounts нет id"

expect_status "POST /api/accounts (второй)" 201 "${AUTH[@]}" -X POST "$BASE/api/accounts" \
  -H "Content-Type: application/json" -d '{"currency":"RUB"}'
TO="$(printf '%s' "$BODY" | json_field id)"
[ -n "$TO" ] || fail "в ответе второго POST /api/accounts нет id"

expect_status "GET /api/accounts" 200 "${AUTH[@]}" "$BASE/api/accounts"
case "$BODY" in *"$FROM"*"$TO"* | *"$TO"*"$FROM"*) ;; *) fail "GET /api/accounts не вернул оба счёта" ;; esac

expect_status "POST /api/accounts/{id}/deposit" 200 "${AUTH[@]}" -X POST "$BASE/api/accounts/$FROM/deposit" \
  -H "Content-Type: application/json" -H "Idempotency-Key: smoke-deposit-$RUN" \
  -d '{"amount":100000,"currency":"RUB"}'

expect_status "POST /api/transfers" 200 "${AUTH[@]}" -X POST "$BASE/api/transfers" \
  -H "Content-Type: application/json" -H "Idempotency-Key: smoke-transfer-$RUN" \
  -d "{\"fromAccountId\":\"$FROM\",\"toAccountId\":\"$TO\",\"amount\":30000,\"currency\":\"RUB\"}"

# События пополнения и перевода: публикатор отправляет их в RabbitMQ, оба оказываются в очереди потребителя
# (потребитель живёт в профиле telegram и без него не запущен; его работу проверяют интеграционные тесты),
# в dead-letter ничего нет. Статистика management API обновляется раз в ~5 секунд, ждём до 30.
queue_field() { # <очередь> <поле>: число из JSON очереди management API
  curl -s -u "$RABBIT_USER:$RABBIT_PASSWORD" "$RABBIT/api/queues/%2F/$1" | { grep -o "\"$2\":[0-9]*" || true; } | head -n 1 | cut -d: -f2
}
DELIVERED=0
for _ in $(seq 1 60); do
  ACKED="$(queue_field minibank.notifications ack)"
  PENDING="$(queue_field minibank.notifications messages)"
  if [ $(( ${ACKED:-0} + ${PENDING:-0} )) -ge 2 ]; then
    DELIVERED=1
    break
  fi
  sleep 0.5
done
[ "$DELIVERED" = "1" ] || fail "события не дошли до очереди (ack=${ACKED:-?}, в очереди=${PENDING:-?})"
[ "$(queue_field minibank.notifications.dead messages)" = "0" ] || fail "в dead-letter очереди есть сообщения"
echo "ok: события доставлены в очередь RabbitMQ"

# Префикс /api снимается, query string доходит до API.
expect_status "GET /api/accounts/{id}/entries?page=1&pageSize=5" 200 "${AUTH[@]}" \
  "$BASE/api/accounts/$FROM/entries?page=1&pageSize=5"

# Выписка: UTF-8 с BOM, заголовок и ровно две проводки (пополнение и списание по переводу).
expect_status "GET /api/accounts/{id}/entries.csv" 200 "${AUTH[@]}" -o "$TMP" "$BASE/api/accounts/$FROM/entries.csv"
[ "$(head -c 3 "$TMP" | od -An -tx1 | tr -d ' \n')" = "efbbbf" ] || fail "в CSV нет BOM"
[ "$(tail -c +4 "$TMP" | head -n 1 | tr -d '\r')" = "Дата (UTC);ID перевода;Сумма;Валюта;Баланс после" ] || fail "в CSV неверный заголовок"
[ "$(wc -l < "$TMP" | tr -d ' ')" = "3" ] || fail "в CSV должно быть 3 строки (заголовок и 2 проводки)"
echo "ok: CSV содержит BOM, заголовок и 2 проводки"

expect_status "GET /" 200 "$BASE/"
case "$BODY" in *'id="root"'*) ;; *) fail "GET / не вернул страницу приложения" ;; esac

# Глубокие ссылки SPA отдают index.html, в том числе ссылка из письма.
expect_status "GET /accounts/{id}" 200 "$BASE/accounts/$FROM"
case "$BODY" in *'id="root"'*) ;; *) fail "GET /accounts/{id} не вернул страницу приложения" ;; esac
expect_status "GET /confirm-email" 200 "$BASE/confirm-email"
case "$BODY" in *'id="root"'*) ;; *) fail "GET /confirm-email не вернул страницу приложения" ;; esac

# Сброс пароля: запрос письма, новый пароль, отзыв прежних токенов входа.
expect_status "POST /api/auth/forgot-password" 202 -X POST "$BASE/api/auth/forgot-password" \
  -H "Content-Type: application/json" -d "{\"email\":\"$EMAIL\"}"

# Письмо со ссылкой сброса отличаем от письма подтверждения по адресу ссылки: <Web__BaseUrl>/reset-password#token=…
RESET_TOKEN=""
for _ in $(seq 1 30); do
  for ID in $(curl -s -G "$MAILPIT/api/v1/search" --data-urlencode "query=to:$EMAIL" | grep -o '"ID":"[^"]*"' | cut -d'"' -f4); do
    RESET_TOKEN="$(curl -s "$MAILPIT/api/v1/message/$ID" | sed -n 's/.*reset-password#token=\([A-Za-z0-9_-]*\).*/\1/p')"
    [ -n "$RESET_TOKEN" ] && break
  done
  [ -n "$RESET_TOKEN" ] && break
  sleep 0.5
done
[ -n "$RESET_TOKEN" ] || fail "письмо со ссылкой сброса пароля не пришло в Mailpit"
echo "ok: письмо сброса пароля пришло в Mailpit"

# Токен входа хранит время выдачи с точностью до секунды: ждём, чтобы смена пароля была строго позже.
sleep 2
NEW_PASSWORD="new-$PASSWORD"
expect_status "POST /api/auth/reset-password" 204 -X POST "$BASE/api/auth/reset-password" \
  -H "Content-Type: application/json" -d "{\"token\":\"$RESET_TOKEN\",\"newPassword\":\"$NEW_PASSWORD\"}"
expect_status "POST /api/auth/reset-password повторно тем же токеном" 400 -X POST "$BASE/api/auth/reset-password" \
  -H "Content-Type: application/json" -d "{\"token\":\"$RESET_TOKEN\",\"newPassword\":\"$NEW_PASSWORD\"}"
expect_status "POST /api/auth/login со старым паролем" 401 -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" -d "{\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\"}"
expect_status "POST /api/auth/login с новым паролем" 200 -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" -d "{\"email\":\"$EMAIL\",\"password\":\"$NEW_PASSWORD\"}"
NEW_ACCESS="$(printf '%s' "$BODY" | json_field accessToken)"
[ -n "$NEW_ACCESS" ] || fail "в ответе входа с новым паролем нет accessToken"
expect_status "GET /api/me со старым токеном после смены пароля" 401 "${AUTH[@]}" "$BASE/api/me"
expect_status "GET /api/me с новым токеном" 200 -H "Authorization: Bearer $NEW_ACCESS" "$BASE/api/me"
expect_status "GET /reset-password" 200 "$BASE/reset-password"
case "$BODY" in *'id="root"'*) ;; *) fail "GET /reset-password не вернул страницу приложения" ;; esac

# Уведомления в Telegram живут в отдельном профиле Compose: без него эндпоинт честно отвечает 503, а остальной API работает.
expect_status "GET /api/me/notifications без профиля telegram" 503 -H "Authorization: Bearer $NEW_ACCESS" "$BASE/api/me/notifications"
case "$BODY" in *notifications_unavailable*) ;; *) fail "503 уведомлений без кода notifications_unavailable" ;; esac
expect_status "GET /api/me после 503 уведомлений" 200 -H "Authorization: Bearer $NEW_ACCESS" "$BASE/api/me"

# Лимит запросов считается по адресу клиента: подделанный X-Forwarded-For извне ничего не меняет
# (nginx дописывает настоящий адрес, API учитывает только его), поэтому 429 приходит, несмотря на разные значения.
LIMITED=0
for i in $(seq 1 40); do
  CODE="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/auth/login" \
    -H "Content-Type: application/json" -H "X-Forwarded-For: 203.0.113.$i" \
    -d "{\"email\":\"$EMAIL\",\"password\":\"wrong-password-$i\"}")"
  if [ "$CODE" = "429" ]; then
    LIMITED=1
    break
  fi
done
[ "$LIMITED" = "1" ] || fail "лимит запросов к /auth не сработал: подделанный X-Forwarded-For обходит его"
echo "ok: лимит запросов к /auth не обходится подделкой X-Forwarded-For"
