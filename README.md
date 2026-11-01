# MiniBank

[![CI](https://github.com/Eksailed/MiniBank/actions/workflows/ci.yml/badge.svg)](https://github.com/Eksailed/MiniBank/actions/workflows/ci.yml)

Учебный банковский бэкенд на .NET: счета, переводы между ними, леджер с двойной записью, идемпотентность запросов и корректность при параллельных операциях.

Проект делается как портфолио для стажировки .NET-разработчика. Деньги в нём не настоящие.

## Стек

C#, ASP.NET Core (.NET 9), PostgreSQL 17, EF Core 9, Docker. Веб-клиент: React, TypeScript, Vite.

## Статус

Этапы 1–4 готовы. Реализация идёт этапами, каждый из которых заканчивается рабочим состоянием:

1. ✅ Ядро: счета, переводы, двойная запись, баланс из проводок.
2. ✅ Корректность: идемпотентность, защита от гонок и двойного списания.
3. ✅ Качество: тесты, Docker Compose, CI, документация.
4. ✅ Безопасность и функции: вход по email и паролю (JWT), подтверждение email, владение счетами, лимиты, выписки с CSV.
5. События: outbox-паттерн и брокер сообщений.
6. Сервис уведомлений и Telegram-бот.

## Быстрый старт

Нужен только Docker.

```bash
docker compose up --build
```

Поднимется весь стек: PostgreSQL, API, веб-клиент за nginx и Mailpit. Миграции применяются при старте API.

- Приложение: <http://localhost:8080>
- API и Swagger: <http://localhost:5050>, <http://localhost:5050/swagger>
- Почта (Mailpit): <http://localhost:8025>

Настоящих писем стек не отправляет: Mailpit перехватывает всё, что шлёт API. Зарегистрируйтесь в приложении, откройте <http://localhost:8025>, найдите письмо «Подтвердите email в MiniBank» и перейдите по ссылке из него — после этого можно войти.

Остановка: `docker compose down`. Данные лежат в томе `minibank-data` и сохраняются между запусками; `docker compose down -v` их стирает. Порты 5432, 5050, 8025 и 8080 на хосте должны быть свободны.

Ключ подписи JWT в Compose — демонстрационный и публичный (только для локального запуска). Для любой другой среды задайте свой, не короче 32 байт: `JWT_KEY=... docker compose up`.

### Режим разработки

Нужны .NET SDK 9 и Node.js 22.22+ (или 24). В Compose поднимаются база и Mailpit, API и веб запускаются из исходников:

```bash
docker compose up -d postgres mailpit
dotnet run --project src/MiniBank.Api
```

В окружении Development миграции применяются при старте, Swagger включён, используются dev-ключ JWT (`development-only-signing-key-…` из `appsettings.Development.json`) и SMTP на `localhost:1025` (Mailpit); ссылка в письме ведёт на `http://localhost:5173` (Vite). Письма — в <http://localhost:8025>. Для запуска вне Development обязательны `Jwt__Key` (от 32 байт) и `Web__BaseUrl`. Веб-клиент — см. раздел «Web».

Тесты (интеграционные поднимают PostgreSQL через Testcontainers, нужен запущенный Docker):

```bash
dotnet test
```

## Web

Веб-клиент — React + TypeScript + Vite, лежит в `web/`. В Compose он уже запущен (<http://localhost:8080>); ниже — режим разработки с горячей перезагрузкой. Нужен Node.js 22.22+ (или 24).

```bash
cd web
npm install
npm run dev
```

Откройте <http://localhost:5173>. Клиент ходит в API на `localhost:5050` через прокси Vite (`/api` → API, префикс снимается), поэтому сначала запустите API (см. «Режим разработки»).

- Экраны: вход, регистрация, подтверждение email (с повторной отправкой письма), «Мои счета», счёт (баланс, лимиты, последние операции), пополнение, перевод, выписка с фильтром по датам и виду операций, подгрузкой страниц и скачиванием CSV. Светлая и тёмная тема, выбор запоминается.
- Список счетов приходит с сервера (`GET /accounts`), в браузере счета не хранятся. Токен входа лежит в `sessionStorage`: закрыли вкладку — вошли заново; ответ 401 разлогинивает.
- Лимиты счёта (на операцию и в сутки) редактируются в карточке на странице счёта; там же видно, сколько потрачено и осталось сегодня.
- Каждая операция отправляется с `Idempotency-Key`: повтор после сбоя сети идёт с тем же ключом, правка суммы или получателя даёт новый.
- Типы запросов генерируются из Swagger работающего API: `npm run gen:api`.
- Тесты (Vitest, Testing Library, MSW; бэкенд не нужен): `npm test`. Сборка: `npm run build`.

## API

Всё, кроме `/auth/*` и `/health`, требует заголовок `Authorization: Bearer <токен>`; без него — 401.

| Метод и путь | Что делает | Успех |
| --- | --- | --- |
| `POST /auth/register` | Регистрация `{email, password, displayName}` (пароль 8–128 символов); на email уходит письмо со ссылкой | 202 |
| `POST /auth/confirm-email` | Подтвердить email `{token}` из ссылки в письме (токен живёт 24 часа) | 204 |
| `POST /auth/resend-confirmation` | Отправить письмо ещё раз `{email}`; всегда 202, ответ не выдаёт, есть ли такой email | 202 |
| `POST /auth/login` | Вход `{email, password}` → `{accessToken, expiresAt}`; только для подтверждённого email | 200 |
| `GET /me` | Профиль текущего пользователя | 200 |
| `GET /accounts` | Счета пользователя | 200 |
| `POST /accounts` | Открыть счёт `{currency}` (RUB, USD, EUR); не больше 50 счетов на пользователя | 201 |
| `GET /accounts/{id}` | Счёт и его баланс | 200 |
| `GET /accounts/{id}/limits` | Лимиты счёта, потрачено и осталось сегодня, потолки | 200 |
| `PUT /accounts/{id}/limits` | Изменить лимиты `{perOperation, daily}` (не больше потолка; дневной не меньше лимита на операцию) | 200 |
| `POST /accounts/{id}/deposit` | Пополнить с кассы `{amount, currency}`; нужен заголовок `Idempotency-Key` | 200 |
| `POST /transfers` | Перевод `{fromAccountId, toAccountId, amount, currency}` со своего счёта; нужен заголовок `Idempotency-Key` | 200 |
| `GET /accounts/{id}/entries?from&to&direction&page=1&pageSize=50` | Проводки счёта, новые первыми; фильтры — период и `direction=in\|out` | 200 |
| `GET /accounts/{id}/entries.csv?from&to&direction` | Выписка в CSV: UTF-8 с BOM, разделитель `;`, те же фильтры, без пагинации | 200 |
| `GET /health` | Проверка готовности: выполняет запрос к БД; не входит в Swagger | 200, при недоступной БД 503 |

Суммы в API задаются целым числом в минимальных единицах валюты (копейки, центы); в CSV — в основных единицах с точкой и двумя знаками. Даты в фильтрах — ISO 8601, без смещения считаются UTC; `from` включительно, `to` не включительно.

Swagger включён в окружении Development или при настройке `Swagger:Enabled=true` (переменная окружения `Swagger__Enabled`); по умолчанию выключен. В Compose включён; кнопка «Authorize» принимает токен из `POST /auth/login`.

```bash
curl -X POST http://localhost:5050/transfers   -H "Authorization: Bearer <токен>"   -H "Content-Type: application/json"   -H "Idempotency-Key: 7c9e6679-7425-40de-944b-e07fc1f90ae7"   -d '{"fromAccountId":"<id>","toAccountId":"<id>","amount":30000,"currency":"RUB"}'
```

Ошибки приходят как `ProblemDetails`; для части из них есть машинный код в поле `code`:

| Код | Когда |
| --- | --- |
| 400 | неверный ввод: валюта, сумма, пароль, email, лимиты, период и `direction` в выписке, перевод на тот же счёт, переполнение баланса; нет заголовка `Idempotency-Key` или он некорректен; `invalid_confirmation_token` — токен из письма неверный, просрочен или уже использован; CSV длиннее потолка строк (`Statements:MaxCsvRows`, по умолчанию 50 000) |
| 401 | нет или недействителен токен; `invalid_credentials` — неверный email или пароль |
| 403 | `email_not_confirmed` — пароль верный, но email ещё не подтверждён |
| 404 | счёт не найден или принадлежит другому пользователю (чужие счета неотличимы от несуществующих) |
| 409 | `email_taken` — email уже зарегистрирован; либо операция не прошла после нескольких автоматических повторов из-за гонки — запрос можно отправить заново с тем же ключом |
| 422 | недостаточно средств, счёт заблокирован, перевод с кассой или на кассу; `Idempotency-Key` уже использован с другим запросом; `account_limit_reached` — 50 счетов; `per_operation_limit_exceeded`, `daily_limit_exceeded` — превышен лимит счёта |
| 429 | слишком много запросов к `/auth/*` с одного адреса (по умолчанию 10 в минуту, `Auth:RateLimitPerMinute`) |

## Безопасность

- **Вход.** Пароль хранится как PBKDF2-хеш; вход по email и паролю выдаёт JWT (HS256, срок жизни `Jwt:LifetimeMinutes`, по умолчанию 60 минут). Ответ на неверный email и неверный пароль одинаковый, а время проверки не выдаёт, существует ли пользователь. Ключ `Jwt:Key` обязателен (от 32 байт), без него API не стартует.
- **Подтверждение email.** После регистрации войти нельзя, пока не открыта ссылка из письма. В БД хранится только SHA-256 от токена, токен одноразовый и живёт 24 часа; повторное письмо (не чаще раза в минуту) делает прежние токены недействительными. Письма уходят по SMTP (`Smtp:*`, в Compose — Mailpit), адрес веб-клиента для ссылки — `Web:BaseUrl`.
- **Владение счетами.** Каждый счёт принадлежит пользователю. Читать, пополнять и переводить можно только со своих счетов; чужой счёт отвечает 404. Переводить можно на любой существующий счёт по ID. Всё закрыто по умолчанию: анонимно доступны только `/auth/*` и `/health`.
- **Лимиты.** У счёта есть лимит на одну операцию и лимит в сутки (суммы списаний за календарные сутки UTC); значения по умолчанию и потолки зависят от валюты (`Limits:Default`, `Limits:Max`). Лимиты проверяются в той же транзакции, что и списание, поэтому параллельные запросы их не обходят. Пополнение лимитами не ограничено.
- **Лимит запросов.** `/auth/*` — не больше 10 запросов в минуту с одного адреса. Адрес клиента берётся из `X-Forwarded-For` только от доверенных прокси (`ForwardedHeaders:TrustedNetworks`, CIDR; в Compose — сеть Docker, где работает nginx). Без доверенных сетей заголовок игнорируется, иначе его подделка обходила бы лимит.
- **Выписки.** Период и направление операций фильтруются на сервере; CSV пишется потоком, а потолок строк проверяется до первого байта ответа.

## Как устроены деньги

- Сумма хранится как `bigint` в минимальных единицах, рядом код валюты. Один счёт — одна валюта.
- Каждый перевод — две проводки с суммой 0 в сумме: списание (минус) и зачисление (плюс). Пополнение — перевод с системного счёта «Касса» нужной валюты, поэтому сумма балансов всех счетов всегда равна нулю.
- `accounts.balance` — кэш суммы проводок. Баланс клиента не уходит ниже нуля (`CHECK`), кассы — могут.
- Проводки только добавляются: триггеры в БД запрещают `UPDATE`, `DELETE` и `TRUNCATE`.
- Одновременные изменения счёта разрешают пессимистичные блокировки (см. ниже); `accounts.version` остаётся страховкой.

## Идемпотентность и параллельность

**Идемпотентность.** `POST /transfers` и `POST /accounts/{id}/deposit` требуют заголовок `Idempotency-Key` (до 200 символов, без управляющих символов). Ключ резервируется в таблице `idempotency_keys` в той же транзакции, что и сама операция:

- повтор с тем же ключом и тем же запросом возвращает сохранённый ответ (200) с заголовком `Idempotent-Replayed: true`, деньги второй раз не двигаются;
- тот же ключ с другим запросом (другая сумма, счёт, операция) — 422;
- запрос, завершившийся ошибкой, ключ не занимает: после исправления причины его можно повторить с тем же ключом;
- параллельные запросы с одним ключом выстраиваются на уникальном индексе: выполняется один, остальные получают его ответ.

Ключ в запросе сравнивается по SHA-256 от нормализованного содержимого (валюта в верхнем регистре). TTL ключей не реализован: записи хранятся бессрочно.

**Параллельность.** Операция блокирует оба счёта одним запросом `SELECT ... WHERE id = ANY(...) ORDER BY id FOR UPDATE`: порядок блокировок всегда одинаковый, поэтому встречные переводы не создают взаимоблокировок. Если PostgreSQL всё же прервал транзакцию (SQLSTATE 40001 или 40P01), операция автоматически повторяется до 3 раз; после этого клиент получает 409.

```mermaid
erDiagram
    users ||--o{ accounts : "user_id"
    users ||--o{ email_confirmation_tokens : "user_id"
    accounts ||--o{ transfers : "from_account_id"
    accounts ||--o{ transfers : "to_account_id"
    accounts ||--o{ ledger_entries : "account_id"
    transfers ||--|{ ledger_entries : "transfer_id"

    users {
        uuid id PK
        text email UK
        text password_hash
        text display_name
        timestamptz created_at
    }
    email_confirmation_tokens {
        uuid id PK
        uuid user_id FK
        text token_hash UK
        timestamptz created_at
    }
    accounts {
        uuid id PK
        uuid user_id FK
        text owner
        text currency
        bigint per_operation_limit
        bigint daily_limit
        bigint balance
        int version
        int status
        bool is_system
        timestamptz created_at
    }
    transfers {
        uuid id PK
        uuid from_account_id FK
        uuid to_account_id FK
        bigint amount
        text currency
        int status
        timestamptz created_at
    }
    ledger_entries {
        uuid id PK
        uuid transfer_id FK
        uuid account_id FK
        bigint amount
        bigint balance_after
        timestamptz created_at
    }
    idempotency_keys {
        varchar key PK
        varchar request_hash
        int status_code
        text response_body
        timestamptz created_at
    }
```

## CI

Workflow [`.github/workflows/ci.yml`](.github/workflows/ci.yml) запускается на каждый push в `main` и на каждый pull request. Три независимых job-а:

- **backend** — `dotnet test` (интеграционные тесты поднимают PostgreSQL через Testcontainers);
- **web** — `npm ci`, тесты Vitest, `npm run build`;
- **stack** — `docker compose up -d --build --wait` собирает оба образа и поднимает стек, затем `scripts/smoke.sh` проходит весь сценарий через nginx: регистрация, письмо из Mailpit, подтверждение email, вход, два счёта, пополнение, перевод, выписка и CSV (BOM, заголовок, число строк), страница приложения и глубокие ссылки SPA, 401 без токена, а также то, что подделанный `X-Forwarded-For` не обходит лимит запросов. При падении job печатает `docker compose logs`.

Образы никуда не публикуются. То же, что делает job `stack`, запускается локально (повторный запуск смоука раньше чем через минуту упрётся в лимит запросов к `/auth`):

```bash
docker compose up -d --build --wait
bash scripts/smoke.sh
```

## Структура

```
src/MiniBank.Domain          сущности и правила: Account, Transfer, Money
src/MiniBank.Application     сценарии (AccountsService, TransfersService) и порты
src/MiniBank.Infrastructure  EF Core, миграции, репозитории
src/MiniBank.Api             контроллеры, обработка ошибок, Swagger, Dockerfile
web/                         веб-клиент: React + TypeScript + Vite, Dockerfile и nginx.conf
tests/                       юнит-, БД- и API-тесты
scripts/                     smoke.sh — сквозная проверка поднятого стека
.github/workflows/           CI
docker-compose.yml           весь стек: PostgreSQL + API + веб + Mailpit
docs/                        спецификации и планы этапов
```
