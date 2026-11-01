# MiniBank

[![CI](https://github.com/Eksailed/MiniBank/actions/workflows/ci.yml/badge.svg)](https://github.com/Eksailed/MiniBank/actions/workflows/ci.yml)

Учебный банковский бэкенд на .NET: счета, переводы между ними, леджер с двойной записью, идемпотентность запросов и корректность при параллельных операциях.

Проект делается как портфолио для стажировки .NET-разработчика. Деньги в нём не настоящие.

## Стек

C#, ASP.NET Core (.NET 9), PostgreSQL 17, EF Core 9, Docker. Веб-клиент: React, TypeScript, Vite.

## Статус

Этапы 1–3 готовы. Реализация идёт этапами, каждый из которых заканчивается рабочим состоянием:

1. ✅ Ядро: счета, переводы, двойная запись, баланс из проводок.
2. ✅ Корректность: идемпотентность, защита от гонок и двойного списания.
3. ✅ Качество: тесты, Docker Compose, CI, документация.
4. Безопасность и функции: JWT, выписки, лимиты.
5. События: outbox-паттерн и брокер сообщений.
6. Сервис уведомлений и Telegram-бот.

## Быстрый старт

Нужен только Docker.

```bash
docker compose up --build
```

Поднимется весь стек: PostgreSQL, API и веб-клиент за nginx. Миграции применяются при старте API.

- Приложение: <http://localhost:8080>
- API и Swagger: <http://localhost:5050>, <http://localhost:5050/swagger>

Остановка: `docker compose down`. Данные лежат в томе `minibank-data` и сохраняются между запусками; `docker compose down -v` их стирает. Порты 5432, 5050 и 8080 на хосте должны быть свободны.

### Режим разработки

Нужны .NET SDK 9 и Node.js 22.22+ (или 24). В Compose поднимается только база, API и веб запускаются из исходников:

```bash
docker compose up -d postgres
dotnet run --project src/MiniBank.Api
```

В окружении Development миграции применяются при старте, Swagger включён. Веб-клиент — см. раздел «Web».

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

- Экраны: «Мои счета», счёт (баланс, последние операции), пополнение, перевод, выписка с подгрузкой страниц. Светлая и тёмная тема, выбор запоминается.
- API не отдаёт список счетов и не требует входа, поэтому «мои счета» клиент хранит в `localStorage` браузера (счёт можно открыть или добавить по ID).
- Каждая операция отправляется с `Idempotency-Key`: повтор после сбоя сети идёт с тем же ключом, правка суммы или получателя даёт новый.
- Типы запросов генерируются из Swagger работающего API: `npm run gen:api`.
- Тесты (Vitest, Testing Library, MSW; бэкенд не нужен): `npm test`. Сборка: `npm run build`.

## API

| Метод и путь | Что делает | Успех |
| --- | --- | --- |
| `POST /accounts` | Открыть счёт `{owner, currency}` (RUB, USD, EUR) | 201 |
| `GET /accounts/{id}` | Счёт и его баланс | 200 |
| `POST /accounts/{id}/deposit` | Пополнить с кассы `{amount, currency}`; нужен заголовок `Idempotency-Key` | 200 |
| `POST /transfers` | Перевод `{fromAccountId, toAccountId, amount, currency}`; нужен заголовок `Idempotency-Key` | 200 |
| `GET /accounts/{id}/entries?page=1&pageSize=50` | Проводки счёта, новые первыми | 200 |
| `GET /health` | Проверка готовности: выполняет запрос к БД; не входит в Swagger | 200, при недоступной БД 503 |

Суммы задаются целым числом в минимальных единицах валюты (копейки, центы).

Swagger включён в окружении Development или при настройке `Swagger:Enabled=true` (переменная окружения `Swagger__Enabled`); по умолчанию выключен. В Compose включён.

```bash
curl -X POST http://localhost:5050/transfers   -H "Content-Type: application/json"   -H "Idempotency-Key: 7c9e6679-7425-40de-944b-e07fc1f90ae7"   -d '{"fromAccountId":"<id>","toAccountId":"<id>","amount":30000,"currency":"RUB"}'
```

Ошибки приходят как `ProblemDetails`:

| Код | Когда |
| --- | --- |
| 400 | неверный ввод: валюта, сумма, пустой владелец, перевод на тот же счёт, переполнение баланса; нет заголовка `Idempotency-Key` или он некорректен (пустой, длиннее 200 символов, управляющие символы) |
| 404 | счёт не найден |
| 409 | редкий случай: операция не прошла после нескольких автоматических повторов из-за гонки; запрос можно отправить заново с тем же ключом |
| 422 | недостаточно средств, счёт заблокирован, перевод с кассой или на кассу; `Idempotency-Key` уже использован с другим запросом |

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
    accounts ||--o{ transfers : "from_account_id"
    accounts ||--o{ transfers : "to_account_id"
    accounts ||--o{ ledger_entries : "account_id"
    transfers ||--|{ ledger_entries : "transfer_id"

    accounts {
        uuid id PK
        text owner
        text currency
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
- **stack** — `docker compose up -d --build --wait` собирает оба образа и поднимает стек, затем `scripts/smoke.sh` проверяет его: `/api/health`, открытие счёта, выписку через прокси, страницу приложения и глубокую ссылку SPA.

Образы никуда не публикуются. То же, что делает job `stack`, запускается локально:

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
scripts/                     smoke.sh — дымовая проверка поднятого стека
.github/workflows/           CI
docker-compose.yml           весь стек: PostgreSQL + API + веб
docs/                        спецификации и планы этапов
```
