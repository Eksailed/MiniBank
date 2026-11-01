#!/usr/bin/env bash
# Дымовая проверка поднятого стека: bash scripts/smoke.sh [BASE_URL] (по умолчанию http://localhost:8080).
set -euo pipefail

BASE="${1:-http://localhost:8080}"

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

expect_status "GET /api/health" 200 "$BASE/api/health"

expect_status "POST /api/accounts" 201 -X POST "$BASE/api/accounts" \
  -H "Content-Type: application/json" -d '{"owner":"Smoke","currency":"RUB"}'
ID="$(printf '%s' "$BODY" | sed -n 's/.*"id":"\([^"]*\)".*/\1/p')"
[ -n "$ID" ] || fail "в ответе POST /api/accounts нет id"

# Префикс /api снимается, query string доходит до API.
expect_status "GET /api/accounts/{id}/entries?page=1&pageSize=5" 200 "$BASE/api/accounts/$ID/entries?page=1&pageSize=5"

expect_status "GET /" 200 "$BASE/"
case "$BODY" in *'id="root"'*) ;; *) fail "GET / не вернул страницу приложения" ;; esac

# Глубокая ссылка SPA отдаёт index.html.
expect_status "GET /accounts/{id}" 200 "$BASE/accounts/$ID"
case "$BODY" in *'id="root"'*) ;; *) fail "GET /accounts/{id} не вернул страницу приложения" ;; esac
