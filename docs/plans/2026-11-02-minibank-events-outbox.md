# MiniBank: события, outbox и RabbitMQ. План реализации

> **Для исполнителя:** используйте superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans. Шаги отмечены чекбоксами (`- [ ]`).

**Цель:** переводы и пополнения публикуют события в RabbitMQ без потерь и без событий от откатившихся операций; мини-потребитель принимает их идемпотентно.

**Архитектура:** собственный transactional outbox: `IOutbox.Add` пишет строку в `outbox_messages` в той же транзакции, что и операция; `OutboxPublisher` (`BackgroundService` в API) забирает пачки `FOR UPDATE SKIP LOCKED`, публикует в обменник `minibank.events` с publisher confirms и помечает строки. Доставка at-least-once; новый проект `MiniBank.Notifications` дедуплицирует по `eventId` в таблице `processed_events`.

**Стек:** .NET 9, EF Core 9, PostgreSQL 17, `RabbitMQ.Client` 7.x, RabbitMQ 4 (`rabbitmq:management`), xUnit + Testcontainers (`Testcontainers.PostgreSql`, `Testcontainers.RabbitMq`).

**Спецификация:** `docs/specs/2026-11-02-minibank-events-outbox-design.md`

## Глобальные ограничения

- Коммиты и тексты на русском (`feat`/`fix`/`test`/`docs`/`ci` с латинским скоупом), без `Co-Authored-By` и упоминаний Claude. Коммит после каждой задачи; `git push` только по просьбе пользователя. Работа в `main`.
- `Directory.Build.props`: `TreatWarningsAsErrors=true`, `Nullable`, `ImplicitUsings`; новые проекты наследуют его.
- Конверт события (JSON, UTF-8, `application/json`): `eventId`, `type`, `version` (=1), `occurredAt`, `data`. Типы: `transfer.completed`, `account.deposited`. Поля `data` — ровно из спецификации; без email, имён и токенов.
- Обменник `minibank.events` (topic, durable); очередь `minibank.notifications` (durable, привязка `#`, `x-dead-letter-exchange=minibank.events.dead`); очередь `minibank.notifications.dead`. Routing key = `type`, сообщения persistent, `messageId = eventId`.
- Настройки по умолчанию: `Events:Enabled=true`, `Events:PollInterval=00:00:01`, `Events:BatchSize=50`, `Events:MaxAttempts=10`, `Events:RetentionDays=7`, `Events:StaleAfter=00:05:00`; задержка повтора 5 с · 2^(attempts−1), потолок 5 мин. Невалидные значения роняют старт.
- В логах только `eventId`, `type`, номер попытки; `/health` от брокера не зависит.
- Запуск тестов: `dotnet test` (нужен Docker: PostgreSQL и RabbitMQ через Testcontainers).

## Review Focus

Случаи, которые не выводятся из спецификации прямо, но могут навредить; у каждого есть тест в указанной задаче.

1. Перевод при недоступном брокере проходит, событие остаётся в `outbox_messages` и уходит после возвращения брокера (задача 2).
2. Идемпотентный повтор перевода/пополнения с тем же `Idempotency-Key` не создаёт второе событие (задача 1).
3. Публикатор упал после подтверждения брокера, до коммита: событие уходит повторно, потребитель записывает его один раз (задачи 2 и 3).
4. Сообщение с битым JSON или неизвестным `type`/`version` не блокирует очередь потребителя (задача 3).
5. Строка, превысившая `MaxAttempts`, получает `dead_at` и больше не выбирается, а остальные строки пачки не страдают от «ядовитой» (задача 2).

---

### Задача 1: Контракт событий и запись в outbox

**Файлы:**
- Создать: `src/MiniBank.Contracts/MiniBank.Contracts.csproj` (без зависимостей), `src/MiniBank.Contracts/Events.cs`
- Создать: `src/MiniBank.Application/Abstractions/IOutbox.cs`
- Создать: `src/MiniBank.Infrastructure/Persistence/OutboxMessage.cs`, `Persistence/Configurations/OutboxMessageConfiguration.cs`, `Persistence/Outbox.cs`, миграция `AddOutbox`
- Изменить: `MiniBank.sln`, `src/MiniBank.Application/MiniBank.Application.csproj` (ссылка на Contracts), `src/MiniBank.Application/TransfersService.cs`, `src/MiniBank.Infrastructure/Persistence/MiniBankDbContext.cs`, `src/MiniBank.Infrastructure/Persistence/DependencyInjection.cs`
- Тесты: создать `tests/MiniBank.Infrastructure.Tests/OutboxWriteTests.cs`, изменить `tests/MiniBank.Infrastructure.Tests/SchemaTests.cs`

**Интерфейсы:**
- Производит (нужно задачам 2–4):
  - `Events.cs` в `MiniBank.Contracts`: `public abstract record IntegrationEvent { public abstract string Type { get; } public virtual int Version => 1; }`; `TransferCompleted(Guid TransferId, Guid FromAccountId, Guid ToAccountId, Guid FromOwnerId, Guid ToOwnerId, long Amount, string Currency)` и `AccountDeposited(Guid TransferId, Guid AccountId, Guid OwnerId, long Amount, string Currency)` с `Type` `"transfer.completed"` / `"account.deposited"`; `public sealed record EventEnvelope(Guid EventId, string Type, int Version, DateTimeOffset OccurredAt, JsonElement Data)`; `public static class EventTypes { const string TransferCompleted, AccountDeposited }`; `public static class EventJson { static readonly JsonSerializerOptions Options }` (web-defaults, camelCase).
  - `IOutbox { void Add(IntegrationEvent @event, DateTimeOffset occurredAt); }` (в Application).
  - `OutboxMessage` (все колонки из спецификации: `Id, Type, Version, Payload (string, jsonb), CreatedAt, OccurredAt, Attempts, NextAttemptAt, PublishedAt, DeadAt, LastError`), `DbSet<OutboxMessage> OutboxMessages` в `MiniBankDbContext`. `Payload` хранит только `data` (JSON события без конверта).
- Потребляет: существующие `TransfersService`, `IdempotencyExecutor`, `Transfer.Execute`, `SystemAccounts.CashId`.

- [ ] **Шаг 1: Падающие тесты** `OutboxWriteTests` (реальная БД через `PostgresFixture`, сервисы собираются как в `ServicesTests`):
  - `Transfer_AddsOneTransferCompletedEvent` — после перевода ровно одна строка `type='transfer.completed'`, `published_at IS NULL`, `attempts=0`, `payload` содержит `transferId`, `fromOwnerId`, `toOwnerId`, `amount`, `currency` с верными значениями, `occurred_at` = время часов `FakeClock`;
  - `Deposit_AddsOneAccountDepositedEvent` — одна строка `account.deposited` с `accountId`, `ownerId`, `transferId`, `amount`, `currency`;
  - `FailedTransfer_AddsNoEvent` (недостаточно средств и чужой счёт) — строк 0;
  - `IdempotentReplay_DoesNotDuplicateEvent` — два вызова с одним ключом дают одну строку (Review Focus 2);
  - `Payload_ContainsNoEmailOrNames` — payload не содержит `email` и `displayName`.
  В `SchemaTests` добавить проверку таблицы `outbox_messages` (колонки, PK, частичный индекс `ix_outbox_messages_pending` по `next_attempt_at` с условием `published_at IS NULL AND dead_at IS NULL`).
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~OutboxWriteTests|FullyQualifiedName~SchemaTests"` — ожидается ошибка компиляции (типов нет).
- [ ] **Шаг 3: Реализовать.** `MiniBank.Contracts` (net9.0) добавить в `MiniBank.sln` и в ссылки Application. `OutboxMessageConfiguration`: таблица `outbox_messages`, `Payload` — `jsonb`, `LastError` — `HasMaxLength(2000)`, частичный индекс `HasFilter("published_at IS NULL AND dead_at IS NULL")`. `Persistence/Outbox : IOutbox` (internal, scoped) создаёт `OutboxMessage` с `Id = Guid.NewGuid()`, сериализует событие в `Payload` через `EventJson.Options`, `NextAttemptAt = CreatedAt = occurredAt`, и добавляет в `MiniBankDbContext` без `SaveChanges`. Регистрация в `AddInfrastructure`. В `TransfersService` внедрить `IOutbox` и вызывать `Add` внутри операции `idempotency.ExecuteAsync(...)` до `uow.SaveChangesAsync` (для `TransferAsync` владельцы берутся из загруженных счетов, для `DepositAsync` — владелец целевого счёта). Миграция: `dotnet ef migrations add AddOutbox --project src/MiniBank.Infrastructure --startup-project src/MiniBank.Infrastructure --output-dir Persistence/Migrations`.
- [ ] **Шаг 4: Запустить** тесты из шага 2, затем `dotnet test` целиком — ожидается PASS (существующие тесты `TransfersService` обновить под новый конструктор).
- [ ] **Шаг 5: Коммит** `feat(events): контракты событий и запись в outbox в транзакции операции`.

---

### Задача 2: Публикатор outbox и RabbitMQ в Compose

**Файлы:**
- Создать: `src/MiniBank.Infrastructure/Events/EventsOptions.cs`, `RabbitMqOptions.cs`, `RabbitMqTopology.cs`, `OutboxPublisher.cs` (`BackgroundService`), `OutboxBatchProcessor.cs`, `EventsServiceCollectionExtensions.cs`
- Создать: `tests/MiniBank.TestSupport/RabbitMqFixture.cs`
- Изменить: `src/MiniBank.Infrastructure/MiniBank.Infrastructure.csproj` (пакет `RabbitMQ.Client`), `tests/MiniBank.TestSupport/MiniBank.TestSupport.csproj` (`Testcontainers.RabbitMq`), `src/MiniBank.Api/Program.cs`, `src/MiniBank.Api/appsettings.json`, `docker-compose.yml`
- Тесты: создать `tests/MiniBank.Infrastructure.Tests/OutboxPublisherTests.cs`

**Интерфейсы:**
- Потребляет: `OutboxMessage`, `MiniBankDbContext.OutboxMessages` (задача 1), `EventEnvelope`, `EventJson` (Contracts).
- Производит (нужно задачам 3–4):
  - `EventsOptions { bool Enabled; TimeSpan PollInterval; int BatchSize; int MaxAttempts; int RetentionDays; TimeSpan StaleAfter }` и `RabbitMqOptions { string Host; int Port; string User; string Password; string VirtualHost }`, оба с `static Load(IConfiguration)` и валидацией (положительные значения, непустой `Host` при `Enabled`).
  - `RabbitMqTopology` — константы `Exchange="minibank.events"`, `DeadExchange="minibank.events.dead"`, `Queue="minibank.notifications"`, `DeadQueue="minibank.notifications.dead"` и `static Task DeclareAsync(IChannel channel, CancellationToken ct)` (идемпотентно, вызывается издателем и потребителем). Файл компилируется и в Notifications (ссылка `Link` или вынос в `MiniBank.Contracts`: исполнитель решает, но топология живёт в одном месте).
  - `OutboxBatchProcessor.ProcessBatchAsync(IOutboxSink sink, CancellationToken ct): Task<int>` — одна транзакция, выборка `FOR UPDATE SKIP LOCKED`, возвращает число обработанных строк; `interface IOutboxSink { Task PublishAsync(OutboxMessage message, CancellationToken ct); }` — за ним RabbitMQ, подмена в тестах.
  - `RabbitMqFixture` (xUnit `IAsyncLifetime`, коллекция `"rabbitmq"`): `string ConnectionUri`, `Task StopAsync()`, `Task StartAsync()` (для теста недоступного брокера), `Task<uint> MessageCountAsync(string queue)`.

- [ ] **Шаг 1: Падающие тесты** `OutboxPublisherTests` (PostgreSQL + RabbitMQ, публикатор запускается через `OutboxBatchProcessor`/`OutboxPublisher` напрямую, строки создаются вставкой в БД):
  - `PublishesPendingMessagesAndMarksThem` — N строк уходят в `minibank.notifications`, у каждой `published_at` задан, `messageId` и routing key сообщения совпадают с `id` и `type`, тело — конверт с `eventId = id`;
  - `BrokerDown_KeepsRowsAndDeliversAfterRestart` — при остановленном брокере строки не помечены, `attempts` растёт, после `StartAsync` доходят (Review Focus 1);
  - `PublishFailure_IncrementsAttemptsAndPostponesRetry` — fake sink бросает исключение: `attempts=1`, `next_attempt_at ≥ now+5с`, `last_error` непустой и не длиннее 2000 символов; при втором сбое задержка 10 с;
  - `ExceedingMaxAttempts_SetsDeadAtAndIsNotSelectedAgain` — `MaxAttempts=3`, после третьей ошибки `dead_at` задан, следующий проход строку не берёт; «ядовитая» строка не мешает остальным строкам пачки (Review Focus 5);
  - `ConcurrentProcessors_DoNotPublishSameRowTwice` — два параллельных `ProcessBatchAsync` над 100 строками: каждая строка опубликована ровно один раз;
  - `CleanupDeletesOnlyOldPublishedRows` — удаляются опубликованные старше `RetentionDays`, неопубликованные, `dead` и свежие остаются;
  - `PublishedBeforeCommitCrash_IsResentAfterRestart` — sink, подтвердивший отправку, и принудительно откатываемая транзакция: строка остаётся неопубликованной и уходит повторно (Review Focus 3, сторона издателя);
  - `Disabled_DoesNotStartWorker` и `InvalidOptions_FailOnStartup` (`BatchSize=0`, `MaxAttempts=0`).
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~OutboxPublisherTests"` — ожидается ошибка компиляции.
- [ ] **Шаг 3: Реализовать.** `OutboxBatchProcessor`: транзакция, выборка сырым SQL `SELECT … FROM outbox_messages WHERE published_at IS NULL AND dead_at IS NULL AND next_attempt_at <= @now ORDER BY created_at LIMIT @n FOR UPDATE SKIP LOCKED` (`FromSqlRaw`), на каждой строке вызов sink; успех → `published_at`, ошибка → `attempts++`, `next_attempt_at = now + min(5с·2^(attempts−1), 5 мин)`, `last_error` (обрезан до 2000, без payload), при `attempts ≥ MaxAttempts` — `dead_at`; ошибки одной строки не прерывают остальные; один коммит на пачку. Реальный sink: одно соединение и канал с publisher confirms (`CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)`), `AutomaticRecoveryEnabled`, конверт собирается из строки; топология объявляется при открытии канала. `OutboxPublisher`: цикл «пачка → пауза `PollInterval`, если пачка пуста», очистка старых опубликованных пачками по 500 раз в минуту, Warning при самой старой неотправленной строке старше `StaleAfter` (с `eventId` и возрастом), все исключения цикла логируются и не завершают воркер. Регистрация воркера в `AddInfrastructure`/`Program.cs` только при `Events:Enabled`. `docker-compose.yml`: сервис `rabbitmq` (`rabbitmq:4-management`, порт `127.0.0.1:15672:15672`, `rabbitmq-diagnostics -q ping` в healthcheck), у `api` — `depends_on: rabbitmq: condition: service_healthy` и `RabbitMq__Host=rabbitmq`, учётные данные из `RABBITMQ_DEFAULT_USER/PASS` через переменные окружения с значениями по умолчанию для разработки. `appsettings.json`: секции `Events` и `RabbitMq` со значениями по умолчанию.
- [ ] **Шаг 4: Запустить** фильтр из шага 2 — ожидается PASS; затем `dotnet test` целиком и `docker compose config` без ошибок.
- [ ] **Шаг 5: Коммит** `feat(events): публикатор outbox в RabbitMQ и брокер в Compose`.

---

### Задача 3: Потребитель `MiniBank.Notifications`

**Файлы:**
- Создать: `src/MiniBank.Notifications/MiniBank.Notifications.csproj` (Worker SDK), `Program.cs`, `NotificationsOptions.cs`, `NotificationsDbContext.cs`, `ProcessedEvent.cs`, `ReceivedEvent.cs`, `EventConsumer.cs` (`BackgroundService`), `EventHandler.cs` (разбор и запись), миграция `Init`, `Dockerfile`
- Создать: `tests/MiniBank.Notifications.Tests/MiniBank.Notifications.Tests.csproj`, `EventConsumerTests.cs`
- Изменить: `MiniBank.sln`, `docker-compose.yml`

**Интерфейсы:**
- Потребляет: `EventEnvelope`, `EventJson`, `EventTypes` (Contracts), `RabbitMqTopology`, `RabbitMqOptions` (задача 2), `PostgresFixture`, `RabbitMqFixture` (TestSupport).
- Производит (нужно задаче 4): образ и сервис `notifications` в Compose; таблицы схемы `notifications`: `processed_events(event_id uuid PK, processed_at)` и `received_events(id uuid PK, event_id, type, owner_id, occurred_at, received_at)` (по записи на каждого `ownerId` события, у `transfer.completed` их два); `static Task<HandleResult> EventHandler.HandleAsync(ReadOnlyMemory<byte> body, IDictionary<string,object?>? headers, CancellationToken ct)` с `enum HandleResult { Processed, Duplicate, Ignored }`.

- [ ] **Шаг 1: Падающие тесты** `EventConsumerTests` (реальные PostgreSQL и RabbitMQ; публикация в обменник напрямую):
  - `DeliveredTwice_RecordsOnce` — одно сообщение с тем же `messageId`/`eventId` дважды: `processed_events` и `received_events` по одному разу, обе доставки подтверждены, очередь пуста (Review Focus 3);
  - `TransferCompleted_RecordsBothOwners` — две строки `received_events` с `fromOwnerId` и `toOwnerId`;
  - `UnknownTypeOrVersion_IsAckedAndIgnored` — `type="x.y"` и `version=2` подтверждаются, ничего не записывается, очередь не блокируется: следующее валидное сообщение обработано (Review Focus 4);
  - `MalformedJson_GoesToDeadLetterWithoutBlocking` — битое тело уходит в `minibank.notifications.dead`, следующее валидное обрабатывается;
  - `HandlerFailure_RetriesLimitedTimesThenDeadLetters` — сбой записи (таблица недоступна/подмена фабрики контекста) приводит к повторам по счётчику в заголовке `x-attempt` (максимум 3) и затем к сообщению в `minibank.notifications.dead`;
  - `ProcessedAndReceivedAreAtomic` — при ошибке вставки `received_events` запись в `processed_events` тоже не остаётся.
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Notifications.Tests` — ожидается ошибка компиляции.
- [ ] **Шаг 3: Реализовать.** Worker: `NotificationsDbContext` в схеме `notifications` (snake_case как в основном проекте), миграции применяются при старте (`Database:MigrateOnStartup`), строка подключения `ConnectionStrings:Notifications`. Консюмер: `BasicQos(prefetchCount: 10)`, ручные ack; успех/дубль/игнор → `ack`; ошибка обработки → если `x-attempt < 3`, переопубликовать с `x-attempt+1` и `ack` исходного, иначе `nack(requeue:false)` (уходит в dead-letter). Битый JSON → сразу `nack(requeue:false)`. Дедупликация: вставка в `processed_events` и `received_events` в одной транзакции, конфликт по PK → `Duplicate`. Логи без payload. `Dockerfile` по образцу `src/MiniBank.Api/Dockerfile` (publish `MiniBank.Notifications`, `USER $APP_UID`). Compose: сервис `notifications` (depends_on `postgres` и `rabbitmq` healthy, свой `ConnectionStrings__Notifications`, база `minibank_notifications` создаётся скриптом инициализации postgres или схема внутри основной базы с отдельным пользователем: исполнитель выбирает минимально громоздкий вариант и фиксирует в README).
- [ ] **Шаг 4: Запустить** `dotnet test tests/MiniBank.Notifications.Tests` и `dotnet test` целиком — ожидается PASS; `docker compose build notifications` без ошибок.
- [ ] **Шаг 5: Коммит** `feat(notifications): потребитель событий с дедупликацией и dead-letter очередью`.

---

### Задача 4: Сквозная проверка, smoke, CI, README

**Файлы:**
- Создать: `tests/MiniBank.Api.IntegrationTests/EventsEndToEndTests.cs`
- Изменить: `tests/MiniBank.Api.IntegrationTests/ApiFactory.cs` (настройки `Events:Enabled`, `RabbitMq:*`), `scripts/smoke.sh`, `.github/workflows/ci.yml`, `README.md`, `.gitignore` при необходимости

**Интерфейсы:**
- Потребляет: всё из задач 1–3 (публикатор в API, `EventConsumer`/`EventHandler`, `RabbitMqFixture`).
- Производит: ничего для последующих задач; результат — зелёные CI и smoke.

- [ ] **Шаг 1: Падающий тест** `EventsEndToEndTests`: `Transfer_ThroughApi_ReachesConsumer` — API (с `Events:Enabled=true` и адресом тестового брокера) делает пополнение и перевод, потребитель, запущенный в тесте, за ≤ 15 с записывает `received_events` для обоих владельцев, `processed_events` содержит два `eventId`, `minibank.notifications.dead` пуста; `ApiHealth_IgnoresBroker` — при остановленном брокере `/health` отвечает 200 и перевод проходит (Review Focus 1, уровень API).
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~EventsEndToEndTests"` — ожидается FAIL.
- [ ] **Шаг 3: Реализовать.** Расширить `ApiFactory` настройками брокера; существующие тесты работают с `Events:Enabled=false` по умолчанию. `scripts/smoke.sh`: после перевода и пополнения (до проверки лимита) опрашивать до 20 с management API `http://localhost:15672/api/queues/%2F/minibank.notifications.dead` и `…/minibank.notifications` (учётные данные guest/guest из Compose по умолчанию, URL параметром `RABBIT_URL`): очередь основной пуста, в `dead` 0 сообщений, обменник получил ≥ 2 публикаций; текст `ok: события доставлены в RabbitMQ и обработаны потребителем`. `ci.yml`: в job `backend` Testcontainers уже поднимают RabbitMQ, Docker есть; в job `stack` Compose теперь включает `rabbitmq` и `notifications`, при падении `docker compose logs` собирает и их. README: роадмап (этап 5 выполнен), раздел «События» — схема потока, типы событий, гарантии at-least-once, переменные `Events:*`/`RabbitMq:*`, порт `15672`, как смотреть очереди и dead-letter.
- [ ] **Шаг 4: Запустить** `dotnet test` целиком; `docker compose up -d --build --wait && bash scripts/smoke.sh && docker compose down -v` — ожидается PASS, финальная строка `ok: лимит запросов к /auth не обходится подделкой X-Forwarded-For`, в выводе есть строка про доставку событий.
- [ ] **Шаг 5: Коммит** `feat(events): сквозная проверка доставки, smoke и CI для RabbitMQ`.

---

## Самопроверка

- **Покрытие спецификации:** события и конверт — задача 1; outbox-таблица, запись в транзакции, идемпотентность — задача 1; воркер (пачки, `SKIP LOCKED`, confirms, повторы, `dead_at`, очистка, `Enabled`, StaleAfter, валидация настроек) — задача 2; брокер, топология, потребитель, inbox, dead-letter — задачи 2–3; `/health` независим от брокера, сквозной тест, smoke, CI — задача 4.
- **Согласованность типов:** `EventEnvelope`/`EventJson`/`EventTypes` определены в задаче 1 и используются в 2–4; `RabbitMqTopology`/`RabbitMqOptions` — в 2, используются в 3; `RabbitMqFixture` — в 2, используется в 3–4.
- **Пропорции:** четыре задачи примерно по одной зоне ответственности; код приведён только там, где его определяют сигнатуры и спецификация.
