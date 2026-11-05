# Уведомления в Telegram: план реализации

Статус: выполнено (5 задач, этап 6), зелёный полный набор тестов. Исправления по финальному ревью внесены отдельными коммитами.
Связано: [спецификация](../specs/уведомления-в-telegram.md).

> **Для исполнителя:** используйте superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans. Шаги отмечены чекбоксами (`- [ ]`).

**Цель:** пользователь привязывает Telegram в профиле и получает сообщения о переводах и пополнениях; настройки включают и выключают типы уведомлений.

**Архитектура:** `MiniBank.Notifications` становится Web SDK: потребитель событий ставит доставки в `telegram_deliveries` (в одной транзакции с `received_events`), фоновый отправитель шлёт их в Telegram Bot API, фоновый поллер принимает `/start <код>`, внутренний HTTP API (секрет в `X-Internal-Secret`) обслуживает API. Веб ходит только в API (`/me/notifications/*`), API вызывает `notifications` по внутренней сети.

**Стек:** .NET 9, ASP.NET Core minimal API (в Notifications), EF Core 9, `HttpClient` для Telegram Bot API (без сторонних библиотек), React 19 + TanStack Query + Vitest + msw.

**Спецификация:** `docs/specs/уведомления-в-telegram.md`

## Глобальные ограничения

- Коммиты и тексты на русском (`feat`/`fix`/`test`/`docs`/`ci` с латинским скоупом), без `Co-Authored-By` и упоминаний Claude. Коммит после каждой задачи; `git push` только по просьбе пользователя. Работа в `main`.
- `Directory.Build.props`: `TreatWarningsAsErrors=true`; файлы в репозитории со смешанными CRLF/LF: правки csproj делать инструментом Edit, большие файлы создавать через Write.
- Telegram Bot API: `https://api.telegram.org/bot<token>/<method>`, методы `getUpdates` (long polling, `timeout=25`), `sendMessage`. Базовый адрес настраивается (`Telegram:ApiBaseUrl`, по умолчанию `https://api.telegram.org`) ради тестовой заглушки. Токен бота никогда не попадает в логи, сообщения исключений и ответы: в логах и `last_error` URL запроса не используется, пишется только метод и код ответа.
- Настройки сервиса: `Telegram:BotToken` и `Telegram:BotUsername` (обязательны, без них старт падает), `Telegram:ApiBaseUrl`, `Notifications:InternalSecret` (обязателен, не короче 16 символов), `Notifications:DeliveryPollInterval=00:00:01`, `Notifications:DeliveryBatchSize=20`, `Notifications:DeliveryMaxAttempts=10`, `Notifications:RetentionDays=30`. Невалидные значения роняют старт.
- Настройки API: `Notifications:BaseUrl` (адрес внутреннего API) и `Notifications:InternalSecret`; если `BaseUrl` не задан, эндпоинты `/me/notifications/*` отвечают `503`.
- Код привязки: 16 случайных байт (`RandomNumberGenerator`), Base64Url без заполнения; в БД только SHA-256 (hex), TTL 10 минут, у пользователя не больше одного активного кода.
- Тексты сообщений (русские): `Перевод: −{сумма}`, `Поступление: +{сумма}`, `Пополнение: +{сумма}`, где сумма — `2 500,00 ₽` (минорные единицы /100, пробел-разделитель тысяч — неразрывный U+00A0, запятая, код валюты → знак: RUB `₽`, USD `$`, EUR `€`, иначе код валюты после суммы через пробел). В тексте нет имён, email, id.
- Внутренние эндпоинты не опубликованы наружу; сравнение секрета через `CryptographicOperations.FixedTimeEquals`.
- Запуск тестов: `dotnet test` (нужен Docker); веб: `cd web && npm test -- --run`.

## Review Focus

1. Один `chat_id` не получает уведомления двух пользователей; перепривязка не оставляет прежнего владельца (задача 3).
2. Код привязки не работает повторно и после истечения; неверный код не раскрывает, существует ли он (задача 3).
3. Недоступность Telegram не теряет события и не тратит попытки; после восстановления доставка уходит (задача 2).
4. `403` от Telegram снимает привязку, а не крутит повторы (задача 2).
5. Токен бота и секрет не попадают в логи, `last_error` и ответы API (задачи 2, 3, 4).

---

### Задача 1: Модель данных, настройки и постановка доставок

**Файлы:**
- Изменить: `src/MiniBank.Notifications/NotificationsDbContext.cs`, `EventHandler.cs`
- Создать: `src/MiniBank.Notifications/Entities.cs` (сущности ниже), `MessageText.cs`, миграция `AddTelegram` (`dotnet ef migrations add AddTelegram --project src/MiniBank.Notifications --startup-project src/MiniBank.Notifications --output-dir Migrations`)
- Тест: `tests/MiniBank.Notifications.Tests/MessageTextTests.cs`, `DeliveryEnqueueTests.cs`

**Интерфейсы:**
- Производит (используют задачи 2–4):
  - `TelegramLink { Guid UserId; long ChatId; DateTimeOffset LinkedAt }`;
  - `LinkCode { string CodeHash; Guid UserId; DateTimeOffset ExpiresAt }`;
  - `NotificationSettings { Guid UserId; bool Enabled = true; bool OnTransferSent = true; bool OnTransferReceived = true; bool OnDeposit = true }`;
  - `TelegramDelivery { Guid Id; Guid ReceivedEventId; long ChatId; string Text; DeliveryStatus Status; int Attempts; DateTimeOffset NextAttemptAt; DateTimeOffset? SentAt; string? LastError }`, `enum DeliveryStatus { Pending, Sent, Failed }` (в БД строкой);
  - `static class MessageText { string TransferSent(long amount, string currency); string TransferReceived(long amount, string currency); string Deposit(long amount, string currency); string FormatAmount(long amount, string currency) }`;
  - `NotificationsDbContext` получает `DbSet` для четырёх сущностей. Таблицы `telegram_links` (PK `user_id`, уникальный индекс `chat_id`), `link_codes` (PK `code_hash`, индекс `user_id`), `notification_settings` (PK `user_id`), `telegram_deliveries` (уникальный индекс `received_event_id`, частичный индекс `ix_telegram_deliveries_pending` по `next_attempt_at` где `status = 'pending'`); `ReceivedEvent` получает неизменяемое поле `Direction` (`sent`/`received`/`deposit`, строка до 20 символов) для выбора настройки.

- [ ] **Шаг 1: Написать падающие тесты.**
  - `MessageTextTests`: `FormatAmount(250000, "RUB") == "2 500,00 ₽"` (разделитель U+00A0), `FormatAmount(5, "USD") == "0,05 $"`, `FormatAmount(100, "XYZ") == "1,00 XYZ"`; `TransferSent(250000, "RUB") == "Перевод: −2 500,00 ₽"` (знак минус U+2212), `TransferReceived(...) == "Поступление: +2 500,00 ₽"`, `Deposit(1000000, "RUB") == "Пополнение: +10 000,00 ₽"`.
  - `DeliveryEnqueueTests` (БД через `PostgresFixture`, `Collection("postgres")`, `EventHandler` из задачи этапа 5): при наличии привязки и настроек по умолчанию перевод между двумя привязанными владельцами даёт две доставки (отправителю «Перевод», получателю «Поступление»), пополнение — одну; владелец без привязки доставку не получает; `Enabled=false` или выключенный флаг типа доставку подавляет; повторная доставка события (`Duplicate`) не создаёт вторых доставок; запись `received_events` и доставки появляются в одной транзакции (при падении `SaveChanges` нет ни того, ни другого — проверить подменой таблицы `telegram_deliveries` на DROP, как в `ProcessedAndReceivedAreAtomic`).
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Notifications.Tests --filter "FullyQualifiedName~MessageTextTests|FullyQualifiedName~DeliveryEnqueueTests"` — ожидается ошибка компиляции (типов нет).
- [ ] **Шаг 3: Реализовать** сущности, конфигурацию, миграцию и `MessageText` (`CultureInfo` не использовать: формат собирается вручную). В `EventHandler.HandleAsync` после добавления `ReceivedEvent` загрузить привязки и настройки владельцев одним запросом и для каждого владельца, у которого есть привязка, `Enabled` и включён флаг по `Direction`, добавить `TelegramDelivery` (`Status=Pending`, `NextAttemptAt=now`, текст из `MessageText` по данным события). Для `TransferCompleted` отправителю — `sent`, получателю — `received`; для `AccountDeposited` — `deposit`.
- [ ] **Шаг 4: Запустить** тесты из шага 2, затем `dotnet test tests/MiniBank.Notifications.Tests` — ожидается PASS (существующие тесты потребителя не меняются по поведению).
- [ ] **Шаг 5: Коммит** `feat(notifications): привязки, настройки и очередь доставок Telegram`.

---

### Задача 2: Клиент Telegram и отправитель доставок

**Файлы:**
- Создать: `src/MiniBank.Notifications/Telegram/ITelegramClient.cs`, `TelegramClient.cs`, `TelegramOptions.cs`, `DeliverySender.cs` (`BackgroundService`), `DeliveryBatchProcessor.cs`
- Изменить: `NotificationsServiceCollectionExtensions.cs` (регистрация), `appsettings.json`
- Тест: `tests/MiniBank.Notifications.Tests/TelegramClientTests.cs`, `DeliverySenderTests.cs`, заглушка `FakeTelegramServer` (Kestrel на свободном порту, `TestSupport` не нужен)

**Интерфейсы:**
- Потребляет: сущности и `NotificationsDbContext` из задачи 1.
- Производит:
  - `interface ITelegramClient { Task SendMessageAsync(long chatId, string text, CancellationToken ct); Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, TimeSpan timeout, CancellationToken ct); }` и `record TelegramUpdate(long UpdateId, long ChatId, string? Text)`;
  - исключения `TelegramUnavailableException` (сеть, таймаут, 5xx), `TelegramRateLimitedException(TimeSpan RetryAfter)` (429), `TelegramForbiddenException` (403), `TelegramRejectedException` (прочие 4xx);
  - `DeliveryBatchProcessor(IDbContextFactory<NotificationsDbContext>, TelegramOptions/DeliveryOptions, TimeProvider, ITelegramClient, ILogger)` с `Task<int> ProcessBatchAsync(CancellationToken ct)` (число обработанных строк), по образцу `OutboxBatchProcessor`: транзакция, `FOR UPDATE SKIP LOCKED`, пачка `DeliveryBatchSize`;
  - `DeliverySender` вызывает процессор раз в `DeliveryPollInterval` и раз в минуту чистит `sent`/`failed` старше `RetentionDays`.

- [ ] **Шаг 1: Написать падающие тесты.**
  - `TelegramClientTests` (заглушка): `sendMessage` шлёт POST `/bot<token>/sendMessage` с `chat_id` и `text`; ответ `429` с `parameters.retry_after=7` → `TelegramRateLimitedException` с `RetryAfter=7с`; `403` → `TelegramForbiddenException`; `500` и обрыв соединения → `TelegramUnavailableException`; `400` → `TelegramRejectedException`; `getUpdates` разбирает `message.chat.id` и `message.text`, пропускает апдейты без `message`; сообщения всех исключений не содержат токена (`DoesNotContain(token)`).
  - `DeliverySenderTests` (БД и заглушка клиента `ITelegramClient`): успех ставит `Sent` и `SentAt`; `TelegramRateLimited` откладывает `NextAttemptAt` на `max(RetryAfter, 5с·2^(n−1))` и тратит попытку; `TelegramUnavailable` прерывает пачку без траты попыток и без `failed` (повторить 15 раз при `DeliveryMaxAttempts=3`: статус остаётся `Pending`, `Attempts=0`) (Review Focus 3); `TelegramRejected` ставит `Failed` после `MaxAttempts` попыток с растущей паузой; `TelegramForbidden` удаляет привязку этого `chat_id`, ставит `Failed` этой доставке и остальным `Pending` доставкам того же `chat_id` (Review Focus 4); `last_error` не длиннее 2000 символов и не содержит токена (Review Focus 5); очистка удаляет старые `Sent`/`Failed`.
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Notifications.Tests --filter "FullyQualifiedName~TelegramClientTests|FullyQualifiedName~DeliverySenderTests"` — ожидается ошибка компиляции.
- [ ] **Шаг 3: Реализовать.** `TelegramClient` на `HttpClient` (`IHttpClientFactory`, тайм-аут запроса 10 с для `sendMessage` и `timeout+10с` для `getUpdates`); ошибки собираются без URL. Регистрация: `TelegramOptions.Load(IConfiguration)` с проверкой (токен, имя бота, непустой секрет), `AddHttpClient<ITelegramClient, TelegramClient>`, `AddSingleton`/`AddHostedService` для отправителя. Пауза повтора и обработка пачки — по образцу `OutboxBatchProcessor` (учесть исправление этапа 5: недоступность внешнего сервиса попытки не тратит).
- [ ] **Шаг 4: Запустить** тесты из шага 2, затем `dotnet test tests/MiniBank.Notifications.Tests` — ожидается PASS.
- [ ] **Шаг 5: Коммит** `feat(notifications): клиент Telegram и отправитель доставок с повторами`.

---

### Задача 3: Привязка по коду, бот и внутренний API

**Файлы:**
- Создать: `src/MiniBank.Notifications/Linking/LinkService.cs`, `Telegram/BotPoller.cs` (`BackgroundService`), `Internal/InternalApi.cs` (`MapInternalApi`), `Internal/InternalSecretFilter.cs`
- Изменить: `MiniBank.Notifications.csproj` (SDK → `Microsoft.NET.Sdk.Web`), `Program.cs` (WebApplication, `ASPNETCORE_URLS=http://+:8080`), `Dockerfile` (образ `mcr.microsoft.com/dotnet/aspnet:9.0`, `EXPOSE 8080`), `NotificationsServiceCollectionExtensions.cs`
- Тест: `tests/MiniBank.Notifications.Tests/LinkServiceTests.cs`, `BotPollerTests.cs`, `InternalApiTests.cs`

**Интерфейсы:**
- Потребляет: сущности (задача 1), `ITelegramClient` (задача 2).
- Производит:
  - `LinkService(IDbContextFactory<NotificationsDbContext>, TimeProvider)`:
    - `Task<LinkCodeResult> CreateCodeAsync(Guid userId, CancellationToken ct)` — `record LinkCodeResult(string Code, DateTimeOffset ExpiresAt)`;
    - `Task<bool> RedeemAsync(string code, long chatId, CancellationToken ct)` — `true`, если код принят;
    - `Task<TelegramStatus> GetStatusAsync(Guid userId, CancellationToken ct)` — `record TelegramStatus(bool Linked, DateTimeOffset? LinkedAt, NotificationSettingsDto Settings)`;
    - `Task UnlinkAsync(Guid userId, CancellationToken ct)`;
    - `Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct)`;
    - `record NotificationSettingsDto(bool Enabled, bool OnTransferSent, bool OnTransferReceived, bool OnDeposit)`;
  - `BotPoller`: цикл `GetUpdatesAsync(offset, 25с)`; `/start <код>` → `RedeemAsync` → ответ «Готово. Уведомления включены.» или «Код недействителен или устарел.»; любое иное сообщение → короткая справка; ошибки Telegram не роняют цикл (пауза 5 с);
  - внутренний API (`X-Internal-Secret`, иначе `401`): `GET /internal/users/{userId}/telegram` → `{ linked, linkedAt, settings }`; `POST /internal/users/{userId}/telegram/link-code` → `{ url: "https://t.me/<BotUsername>?start=<код>", expiresAt }`; `DELETE /internal/users/{userId}/telegram` → `204`; `PUT /internal/users/{userId}/telegram/settings` с телом `NotificationSettingsDto` → `204`; `GET /internal/health` → `200` без секрета не отдаётся (тоже `401`).

- [ ] **Шаг 1: Написать падающие тесты.**
  - `LinkServiceTests`: выпущенный код хранится только как SHA-256 (в таблице нет самого кода); `RedeemAsync` с верным кодом создаёт привязку и удаляет код; повторный `RedeemAsync` тем же кодом → `false` (Review Focus 2); просроченный (после `Advance(11 мин)`) → `false`, неизвестный → `false` без исключения; второй `CreateCodeAsync` делает первый код недействительным; `chat_id`, уже привязанный к другому пользователю, переходит к новому: у прежнего привязки нет, у нового есть (Review Focus 1); новый `chat_id` того же пользователя заменяет прежний; `UnlinkAsync` и `UpdateSettingsAsync` (создание строки при первом изменении) работают, `GetStatusAsync` без строк возвращает значения по умолчанию.
  - `BotPollerTests` (клиент-заглушка): `/start <верный код>` → привязка и ответ «Готово…»; неверный код и произвольный текст → соответствующие ответы без привязки; ошибка `GetUpdatesAsync` не останавливает поллер, `offset` растёт на `UpdateId + 1`.
  - `InternalApiTests` (`WebApplicationFactory` или `TestServer` поверх `MapInternalApi`): без заголовка и с неверным секретом → `401` на каждом маршруте; полный цикл «создать код → вернуть ссылку с именем бота → status не привязан → `RedeemAsync` → status привязан → настройки → отвязка»; ответы и логи не содержат секрета и токена (Review Focus 5).
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Notifications.Tests --filter "FullyQualifiedName~LinkServiceTests|FullyQualifiedName~BotPollerTests|FullyQualifiedName~InternalApiTests"` — ожидается ошибка компиляции.
- [ ] **Шаг 3: Реализовать.** `RedeemAsync` — одна транзакция: найти код по хешу и сроку, удалить `link_codes`, удалить привязки с этим `chat_id` и этим `user_id`, добавить новую. `InternalSecretFilter` — `IEndpointFilter` с `FixedTimeEquals` по UTF-8 байтам. Поддержка `UseUrls`/порта через `ASPNETCORE_HTTP_PORTS=8080` в `Dockerfile`. `Program.cs`: `WebApplication.CreateBuilder`, миграции по флагу как раньше, `app.MapInternalApi()`.
- [ ] **Шаг 4: Запустить** тесты из шага 2, затем `dotnet test tests/MiniBank.Notifications.Tests` и `docker compose build notifications` (при наличии Docker) — ожидается PASS.
- [ ] **Шаг 5: Коммит** `feat(notifications): привязка Telegram по коду, бот и внутренний API`.

---

### Задача 4: Эндпоинты API и Compose

**Файлы:**
- Создать: `src/MiniBank.Application/Abstractions/INotificationsGateway.cs` (порт), `src/MiniBank.Infrastructure/Notifications/NotificationsHttpGateway.cs`, `NotificationsGatewayOptions.cs`, `src/MiniBank.Api/Controllers/NotificationsController.cs`
- Изменить: `src/MiniBank.Api/Errors/ExceptionMapper.cs` (503), `Contracts.cs`, регистрация в `AddInfrastructure`, `appsettings.json`, `docker-compose.yml`, `.env`-пример в README
- Тест: `tests/MiniBank.Api.IntegrationTests/NotificationsEndpointsTests.cs`

**Интерфейсы:**
- Производит:
  - `INotificationsGateway { Task<TelegramStatusDto> GetAsync(Guid userId, CancellationToken ct); Task<LinkDto> CreateLinkAsync(Guid userId, CancellationToken ct); Task UnlinkAsync(Guid userId, CancellationToken ct); Task UpdateSettingsAsync(Guid userId, NotificationSettingsDto settings, CancellationToken ct); }` и `NotificationsUnavailableException` (в `Application`, маппится в `503`, код `notifications_unavailable`);
  - маршруты API (`[Authorize]`, `userId = User.GetUserId()`): `GET /me/notifications` → `{ linked, linkedAt, settings }`; `POST /me/notifications/telegram/link` → `{ url, expiresAt }`; `DELETE /me/notifications/telegram` → `204`; `PUT /me/notifications/settings` → `204`.
  - Compose: сервис `notifications` под `profiles: ["telegram"]`, переменные `Telegram__BotToken: "${TELEGRAM_BOT_TOKEN:?...}"`, `Telegram__BotUsername: "${TELEGRAM_BOT_USERNAME:?...}"`, `Notifications__InternalSecret: "${NOTIFICATIONS_SECRET:-local-demo-only-internal-secret}"`; у `api`: `Notifications__BaseUrl: "http://notifications:8080"` и тот же секрет. В обязательных `${...:?}` — только переменные профиля `telegram` (без профиля Compose их не проверяет).

- [ ] **Шаг 1: Написать падающие тесты.** Заглушка `INotificationsGateway` подменяется в `ApiFactory`; проверить: без токена все четыре маршрута → `401`; `userId` передаётся из токена (шлём запрос чужого `userId` в теле — игнорируется); `GET` отдаёт статус шлюза; `POST link` → ссылка; `PUT settings` с телом, отличным от четырёх булевых полей (null, лишнее), → `400`; шлюз бросает `NotificationsUnavailableException` → `503` с ProblemDetails (`code=notifications_unavailable`), при этом `GET /health` и `GET /accounts` продолжают отвечать `200`; без `Notifications:BaseUrl` в настройках все маршруты → `503`; тест `NotificationsHttpGateway` с заглушкой HTTP: передаёт `X-Internal-Secret`, переводит сетевую ошибку и `5xx` в `NotificationsUnavailableException`, секрет не попадает в текст исключения.
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~NotificationsEndpointsTests"` — ожидается ошибка компиляции.
- [ ] **Шаг 3: Реализовать** шлюз на `HttpClient` с тайм-аутом 5 с (регистрация только если задан `Notifications:BaseUrl`, иначе заглушка шлюза, бросающая `NotificationsUnavailableException`), контроллер и маппинг ошибок. Обновить `docker-compose.yml`, проверить `docker compose config -q` и `docker compose --profile telegram config -q` (со значениями переменных).
- [ ] **Шаг 4: Запустить** тесты из шага 2, затем `dotnet test` целиком — ожидается PASS.
- [ ] **Шаг 5: Коммит** `feat(api): эндпоинты уведомлений Telegram и профиль Compose`.

---

### Задача 5: Веб-клиент, документация, сквозная проверка

**Файлы:**
- Создать: `web/src/features/profile/TelegramCard.tsx`, `TelegramCard.module.css`, `TelegramCard.test.tsx`
- Изменить: `web/src/api/client.ts` (методы `getNotifications`, `createTelegramLink`, `unlinkTelegram`, `updateNotificationSettings`), `types.ts`, `schema.d.ts` (типы вручную по контракту задачи 4, если OpenAPI-генерация недоступна), `ProfilePage.tsx`, `web/src/test/server.ts` (обработчик `GET /api/me/notifications` по умолчанию: не привязано)
- Изменить: `README.md` (статус «Этапы 1–6 готовы», раздел «Уведомления в Telegram»: как создать бота у BotFather, переменные, запуск `docker compose --profile telegram up`, привязка, настройки, ограничения), `docs/` (при необходимости)
- Создать: `tests/MiniBank.Api.IntegrationTests/TelegramEndToEndTests.cs`

**Интерфейсы:**
- Потребляет: маршруты API из задачи 4; `FakeTelegramServer` и сервисы Notifications из задач 2–3.
- Производит: карточка «Уведомления в Telegram» в профиле.

- [ ] **Шаг 1: Написать падающие тесты.**
  - `TelegramCard.test.tsx` (msw): не привязано → кнопка «Привязать Telegram», по клику `POST` и показывается ссылка `t.me/…` и подсказка, статус опрашивается раз в 3 с, пока окно открыто (fake timers), после появления `linked` окно сменяется настройками; привязано → переключатели «Уведомления», «Исходящие переводы», «Входящие переводы», «Пополнения» (изменение шлёт `PUT` с полным объектом), при выключенном «Уведомления» остальные недоступны, «Отвязать» → `DELETE` и возврат в состояние «не привязано»; `503` → «Сервис уведомлений временно недоступен», без падения страницы; ошибки `PUT` откатывают переключатель.
  - `TelegramEndToEndTests` (Testcontainers PostgreSQL и RabbitMQ, `FakeTelegramServer`): API (`Events:Enabled=true`) + `EventConsumer` + `DeliverySender` + `BotPoller` с заглушкой клиента; пользователь через API получает код привязки, заглушка выдаёт апдейт `/start <код>`, затем перевод через API приводит к `sendMessage` с текстом «Поступление: +25,00 ₽» получателю и «Перевод: −25,00 ₽» отправителю; выключенный флаг типа подавляет сообщение.
- [ ] **Шаг 2: Запустить** `cd web && npm test -- --run src/features/profile` и `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~TelegramEndToEndTests"` — ожидается FAIL (компонента и клиента нет).
- [ ] **Шаг 3: Реализовать** карточку (стили по токенам `tokens.css`, как соседние карточки профиля), API-клиент и типы; обновить README (раздел с инструкцией и пометкой, что бот проверяется вручную с настоящим токеном); `scripts/smoke.sh` не меняется (стек без профиля `telegram`), но добавить в smoke проверку, что `GET /me/notifications` отвечает `503` с `code=notifications_unavailable`, пока профиль не поднят, а остальной API работает.
- [ ] **Шаг 4: Запустить** оба набора из шага 2, затем `cd web && npm test -- --run && npm run build` и `dotnet test` целиком; поднять стек `docker compose up -d --build --wait && bash scripts/smoke.sh && docker compose down -v` — ожидается PASS.
- [ ] **Шаг 5: Коммит** `feat(web): привязка Telegram и настройки уведомлений в профиле`.
