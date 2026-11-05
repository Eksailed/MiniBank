# Корректность переводов: план реализации (этап 2)

Статус: выполнено.

Связано: спецификация [2026-10-31-minibank-correctness-design.md](../specs/2026-10-31-minibank-correctness-design.md).

> **Для исполнителей:** план реализуется по задачам; шаги отмечены чекбоксами (`- [ ]`).

**Цель:** Сделать переводы и пополнения безопасными при повторных и параллельных запросах (этап 2): обязательный `Idempotency-Key`, блокировки строк `FOR UPDATE` в фиксированном порядке, ретраи взаимных блокировок, плюс мелочи из ревью этапа 1.

**Архитектура:** Операции перевода и пополнения выполняются в явной транзакции (`IUnitOfWork.ExecuteInTransactionAsync` с ретраем при SQLSTATE `40001`/`40P01`). Счета блокируются одним запросом `SELECT ... ORDER BY id FOR UPDATE`. Идемпотентность: `IdempotencyExecutor` в Application резервирует ключ в таблице `idempotency_keys` внутри той же транзакции, что и перевод; повтор отдаёт сохранённый ответ.

**Стек:** C#, .NET 9, ASP.NET Core, EF Core 9 + Npgsql (raw SQL для блокировок и ключей), xUnit, Testcontainers.PostgreSql.

**Спецификация:** `docs/specs/2026-10-31-minibank-correctness-design.md` (продолжает `2026-10-31-minibank-core-design.md`; инварианты 1–5 этапа 1 не меняются).

## Глобальные ограничения

- Зависимости слоёв прежние: Api → Application → Domain; Infrastructure → Application, Domain. Domain без внешних пакетов.
- `Idempotency-Key` обязателен для `POST /transfers` и `POST /accounts/{id}/deposit`: без заголовка или с пустым значением — 400. Ключ: 1–200 символов, без управляющих символов, пробельные значения недопустимы. Остальные эндпоинты ключ не принимают.
- Тот же ключ с другим запросом — 422. Успешный повтор — тот же ответ и заголовок `Idempotent-Replayed: true`, новый перевод не создаётся. Сохраняются только успешные ответы (код 200).
- Блокировка счетов: один запрос `ORDER BY id FOR UPDATE`; повтор операции целиком до 3 попыток при SQLSTATE `40001` и `40P01`; после исчерпания попыток — `ConcurrencyConflictException` (409).
- Оптимистичная `Version` остаётся как страховка. Уже опубликованные миграции (`InitialCreate`) не редактируются: изменения схемы — только новыми миграциями.
- Ошибки только в формате `ProblemDetails`; 500 не раскрывает текст исключения.
- Тесты: сначала красный (подтверждённый запуском), потом зелёный; весь набор `dotnet test MiniBank.sln` зелёный перед каждым коммитом.
- Работаем в ветке `main`, локально, в GitHub не пушим. Каждое сообщение коммита заканчивается строкой `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Вне рамок: TTL и очистка ключей; ключ на `POST /accounts`; JWT, лимиты, курсорная пагинация.

**Решения плана сверх спецификации:**
- Хеш запроса считается в Application из канонического вида запроса `"<операция>|<параметры>"` (валюта в верхнем регистре), а не из сырого тела: запросы `rub` и `RUB` считаются одинаковыми. Операции `transfer` и `deposit` различаются префиксом.
- Строка ключа резервируется `INSERT ... ON CONFLICT DO NOTHING` с заглушкой `status_code = 0`, `response_body = ''`, затем обновляется `UPDATE`; другие транзакции видят только завершённые строки, потому что резерв и обновление идут в одной транзакции.
- Ответ хранится как JSON (`JsonSerializerDefaults.Web`) от `TransferDto`; код ответа всегда 200.
- Новый тип `IdempotentResult<T>(T Value, bool Replayed)` возвращается сервисами; заголовок `Idempotent-Replayed` ставит контроллер.

## Review Focus: на что смотреть при ревью

1. Ключ с краевыми значениями: пустая строка, только пробелы, 201 символ, управляющий символ — всегда 400, не 500.
2. Один и тот же ключ на разных операциях (`/deposit` и `/transfers`) или с разными параметрами — 422, а не повтор чужого ответа.
3. Два параллельных запроса с одним ключом, оба неуспешные по бизнес-правилам (недостаточно средств) — оба 422, ключ не занят, 500 нет.
4. Перевод на тот же счёт, несуществующие счета, пополнение системного счёта под блокировкой — 400/404/422, а не 500 и не зависание.
5. Параллельные встречные переводы A→B и B→A и пополнения через общую кассу — все 200/422, без 409 и без взаимных блокировок; инварианты сходятся.

---

### Задача 1: Закрыть `Account.Debit` и `Account.Credit`

**Файлы:**
- Изменить: `src/MiniBank.Domain/Account.cs`, `src/MiniBank.Domain/MiniBank.Domain.csproj`
- Тест: `tests/MiniBank.Domain.Tests/AccountTests.cs`

**Интерфейсы:**
- Использует: ничего.
- Даёт: `Account.Debit(Guid, Money, DateTimeOffset)` и `Account.Credit(...)` становятся `internal`; `InternalsVisibleTo` для `MiniBank.Domain.Tests` и `MiniBank.Infrastructure.Tests` (тесты используют их для подготовки данных).

- [ ] **Шаг 1: Написать падающий тест в `AccountTests.cs`:**
  ```csharp
  Debit_And_Credit_AreNotPublic:  typeof(Account).GetMethod("Debit") и GetMethod("Credit") (публичные) равны null  // проводки создаёт только Transfer
  ```
- [ ] **Шаг 2: Запустить, убедиться, что падает.**
  Запустить: `dotnet test tests/MiniBank.Domain.Tests --filter "FullyQualifiedName~AccountTests"`
  Ожидается: FAIL (метод найден).
- [ ] **Шаг 3: Сделать методы `internal`** в `Account.cs`; в `MiniBank.Domain.csproj` добавить `<ItemGroup><InternalsVisibleTo Include="MiniBank.Domain.Tests" /><InternalsVisibleTo Include="MiniBank.Infrastructure.Tests" /></ItemGroup>`.
- [ ] **Шаг 4: Запустить весь набор.**
  Запустить: `dotnet test MiniBank.sln`
  Ожидается: PASS (все существующие тесты, использующие `Credit`, продолжают компилироваться через `InternalsVisibleTo`).
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "refactor(domain): make Account.Debit/Credit internal"
  ```

---

### Задача 2: Триггер append-only защищает и от `TRUNCATE`

**Файлы:**
- Создать: `src/MiniBank.Infrastructure/Persistence/Migrations/<timestamp>_ProtectLedgerFromTruncate.cs` (через `dotnet ef`)
- Тест: `tests/MiniBank.Infrastructure.Tests/SchemaTests.cs`

**Интерфейсы:**
- Использует: функция и триггер `ledger_entries_append_only` из `InitialCreate`.
- Даёт: триггер срабатывает на `UPDATE OR DELETE OR TRUNCATE`; `TRUNCATE ledger_entries` отклоняется с SQLSTATE `23001` (`restrict_violation`).

- [ ] **Шаг 1: Написать падающий тест в `SchemaTests.cs`:**
  ```csharp
  LedgerTrigger_RejectsTruncate:  после вставки проводки команда "TRUNCATE ledger_entries" бросает PostgresException с SqlState "23001"; проводка осталась на месте
  ```
- [ ] **Шаг 2: Запустить, убедиться, что падает.**
  Запустить: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~LedgerTrigger_RejectsTruncate"`
  Ожидается: FAIL (исключения нет).
- [ ] **Шаг 3: Создать миграцию.**
  Запустить: `dotnet ef migrations add ProtectLedgerFromTruncate --project src/MiniBank.Infrastructure --startup-project src/MiniBank.Infrastructure`
  В `Up` (изменений модели нет) через `migrationBuilder.Sql` пересоздать триггер: `DROP TRIGGER ledger_entries_append_only ON ledger_entries;` затем `CREATE TRIGGER ledger_entries_append_only BEFORE UPDATE OR DELETE OR TRUNCATE ON ledger_entries FOR EACH STATEMENT EXECUTE FUNCTION ledger_entries_append_only();`. В `Down` вернуть прежнее определение (`BEFORE UPDATE OR DELETE`).
- [ ] **Шаг 4: Запустить весь набор.**
  Запустить: `dotnet test MiniBank.sln`
  Ожидается: PASS.
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat(db): block TRUNCATE on append-only ledger_entries"
  ```

---

### Задача 3: Примитивы блокировки: `FindForUpdateAsync` и `ExecuteInTransactionAsync`

**Файлы:**
- Изменить: `src/MiniBank.Application/Abstractions/IAccountRepository.cs`, `src/MiniBank.Application/Abstractions/IUnitOfWork.cs`, `src/MiniBank.Infrastructure/Persistence/AccountRepository.cs`, `src/MiniBank.Infrastructure/Persistence/UnitOfWork.cs`
- Тест: `tests/MiniBank.Infrastructure.Tests/LockingTests.cs` (новый)

**Интерфейсы:**
- Использует: `MiniBankDbContext`, `Account`.
- Даёт:
  - `IAccountRepository.FindForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)` → `Task<IReadOnlyDictionary<Guid, Account>>`: загружает существующие счета запросом `SELECT * FROM accounts WHERE id = ANY(@ids) ORDER BY id FOR UPDATE` (через `FromSqlRaw`, с отслеживанием); дубликаты в `ids` допустимы; отсутствующие id в словарь не попадают. Вызывать только внутри транзакции.
  - `IUnitOfWork.ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)` → `Task<T>`: открывает транзакцию `db.Database.BeginTransactionAsync`, выполняет `operation`, фиксирует. Если транзакция уже открыта, просто выполняет `operation`. Если `operation` бросает исключение, у которого в цепочке `InnerException` есть `PostgresException` с `SqlState` `40001` или `40P01`, транзакция откатывается, `db.ChangeTracker.Clear()`, операция повторяется; всего до 3 попыток; после третьей неудачи бросается `ConcurrencyConflictException`. Остальные исключения пробрасываются после отката.

- [ ] **Шаг 1: Написать падающие тесты в `LockingTests.cs`** (провайдер DI как в `ServicesTests`: `AddInfrastructure` + `AddApplication`, база из `PostgresFixture`):
  ```csharp
  FindForUpdate_ReturnsExistingAccountsAndSkipsMissing:  запрос [a, неизвестный Guid, a] возвращает словарь из одного счёта a
  FindForUpdate_BlocksSecondTransactionUntilFirstCommits:  транзакция 1 берёт блокировку счёта и ждёт; транзакция 2 (другой scope) запрашивает тот же счёт — задача не завершается за 500 мс; после коммита транзакции 1 завершается
  ExecuteInTransaction_RollsBackOnException:  в операции создаётся счёт и бросается InvalidOperationException; исключение проброшено, счёт в базе отсутствует
  ExecuteInTransaction_RetriesAfterDeadlock:  два параллельных scope берут блокировки в противоположном порядке (счета X и Y) с синхронизацией через TaskCompletionSource на первой попытке; в итоге обе операции завершаются успешно, суммарное число запусков операции равно 3 (одна жертва взаимной блокировки повторилась один раз)
  ExecuteInTransaction_ThrowsConcurrencyConflictAfterThreeAttempts:  операция всегда бросает new PostgresException("deadlock", "ERROR", "ERROR", "40P01") — счётчик запусков равен 3, наружу вылетает ConcurrencyConflictException
  ExecuteInTransaction_DoesNotRetryOtherExceptions:  операция бросает InvalidOperationException — счётчик запусков равен 1, исключение проброшено как есть
  ```
- [ ] **Шаг 2: Запустить, убедиться, что падают (ошибка компиляции — методов нет).**
  Запустить: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~LockingTests"`
  Ожидается: FAIL.
- [ ] **Шаг 3: Реализовать** оба метода в интерфейсах и классах `AccountRepository`, `UnitOfWork` по описанию в Interfaces. Проверка «транзакционная ошибка» — приватный статический метод в `UnitOfWork`.
- [ ] **Шаг 4: Запустить тесты проекта.**
  Запустить: `dotnet test tests/MiniBank.Infrastructure.Tests`
  Ожидается: PASS.
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat(infra): add FOR UPDATE account lookup and retrying transaction executor"
  ```

---

### Задача 4: Переводы и пополнения работают под блокировками

**Файлы:**
- Изменить: `src/MiniBank.Application/TransfersService.cs`
- Тест: `tests/MiniBank.Api.IntegrationTests/ConcurrencyTests.cs` (новый), `tests/MiniBank.Api.IntegrationTests/InvariantsTests.cs`

**Интерфейсы:**
- Использует: `IAccountRepository.FindForUpdateAsync`, `IUnitOfWork.ExecuteInTransactionAsync` (Задача 3).
- Даёт: `TransfersService.TransferAsync` и `DepositAsync` с прежними сигнатурами выполняются в `ExecuteInTransactionAsync`; счета (для перевода — оба, для пополнения — счёт клиента и касса валюты) берутся одним вызовом `FindForUpdateAsync`. Порядок проверок и коды ошибок прежние: сначала валидация и `Money.Of` (до транзакции), затем счёт-отправитель/клиент → 404, получатель → 404, системный счёт → 422, прочее — как в `Transfer.Execute`.

- [ ] **Шаг 1: Написать падающие тесты.** В `ConcurrencyTests.cs` (хелперы создания счёта и отправки запросов — как в `TransfersEndpointsTests`; БД без пулинга, поэтому параллелизм ограничить: гонка — 50 задач, шторм — не более 16 одновременно через `SemaphoreSlim`):
  ```csharp
  ParallelDepositsToDifferentAccounts_AllSucceed:  50 счетов RUB, 50 параллельных POST /deposit на 100 каждому: все ответы 200; GET каждого счёта показывает 100; баланс кассы равен -5000
  ParallelDepositsToSameAccount_AllSucceed:  50 параллельных пополнений одного счёта по 10: все 200, баланс 500, у счёта 50 проводок
  OppositeTransfersDoNotDeadlock:  счета A и B по 1000; 100 параллельных переводов A→B и B→A по 1: все ответы 200, тест завершается за разумное время (общий таймаут 60 с через CancellationTokenSource), суммарный баланс A+B равен 2000
  ```
  В `InvariantsTests.cs` добавить:
  ```csharp
  ConcurrentStorm_KeepsAllInvariants:  5 счетов, пополнить каждый на 10000; 300 параллельных переводов между случайными парами (Random(7), сумма 1..500, не более 16 одновременно); каждый ответ 200 или 422 (иных нет); затем SQL-сверка как в остальных тестах: сумма балансов 0, баланс = сумма проводок для каждого счёта, у каждого перевода ровно две проводки с суммой 0, клиентские балансы не отрицательны
  ```
- [ ] **Шаг 2: Запустить, убедиться, что падают.**
  Запустить: `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~ConcurrencyTests|FullyQualifiedName~ConcurrentStorm"`
  Ожидается: FAIL (ответы 409 вместо 200).
- [ ] **Шаг 3: Переписать `TransfersService`:** тело операции (поиск счетов через `FindForUpdateAsync`, проверки, `Transfer.Execute`, `transfers.Add`, `uow.SaveChangesAsync`) выполняется внутри `uow.ExecuteInTransactionAsync`; приватный метод `ExecuteAsync` остаётся общим. Для `Guid.Empty` и `Money.Of` проверки остаются до транзакции.
- [ ] **Шаг 4: Запустить весь набор.**
  Запустить: `dotnet test MiniBank.sln`
  Ожидается: PASS (включая все тесты этапа 1).
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat: run transfers and deposits under ordered row locks"
  ```

---

### Задача 5: Хранилище ключей идемпотентности

**Файлы:**
- Создать: `src/MiniBank.Application/Abstractions/IIdempotencyStore.cs`, `src/MiniBank.Infrastructure/Persistence/IdempotencyKeyRecord.cs`, `src/MiniBank.Infrastructure/Persistence/Configurations/IdempotencyKeyConfiguration.cs`, `src/MiniBank.Infrastructure/Persistence/IdempotencyStore.cs`, миграция `AddIdempotencyKeys`
- Изменить: `src/MiniBank.Infrastructure/Persistence/DependencyInjection.cs`
- Тест: `tests/MiniBank.Infrastructure.Tests/IdempotencyStoreTests.cs` (новый)

**Интерфейсы:**
- Использует: `IUnitOfWork.ExecuteInTransactionAsync` (Задача 3), `TimeProvider`.
- Даёт:
  - `public sealed record StoredResponse(string RequestHash, int StatusCode, string Body);`
  - `IIdempotencyStore.TryReserveAsync(string key, string requestHash, CancellationToken ct)` → `Task<StoredResponse?>`: внутри открытой транзакции выполняет `INSERT INTO idempotency_keys (key, request_hash, status_code, response_body, created_at) VALUES (...,0,'',now) ON CONFLICT (key) DO NOTHING` (через `Database.ExecuteSqlAsync`); при вставке (затронута 1 строка) возвращает `null`; иначе читает и возвращает существующую строку (`StoredResponse`).
  - `IIdempotencyStore.CompleteAsync(string key, int statusCode, string body, CancellationToken ct)` → `Task`: `UPDATE idempotency_keys SET status_code=@, response_body=@ WHERE key=@`.
  - Таблица `idempotency_keys`: `key` text PK (макс. 200), `request_hash` text NOT NULL (макс. 64), `status_code` int NOT NULL, `response_body` text NOT NULL, `created_at` timestamptz NOT NULL. Класс `IdempotencyKeyRecord` — `public` в Infrastructure (поля `Key`, `RequestHash`, `StatusCode`, `ResponseBody`, `CreatedAt`), доступ через `db.Set<IdempotencyKeyRecord>()`.

- [ ] **Шаг 1: Написать падающие тесты в `IdempotencyStoreTests.cs`** (каждая операция в `ExecuteInTransactionAsync`):
  ```csharp
  TryReserve_NewKey_ReturnsNull
  TryReserve_AfterCommittedComplete_ReturnsStoredResponse:  первая транзакция: Reserve, Complete(200, "{\"a\":1}"), коммит; вторая: Reserve того же ключа возвращает StoredResponse с тем же хешем, кодом 200 и телом
  TryReserve_SameKeyInParallelTransaction_WaitsForFirstToFinish:  транзакция 1 резервирует и держит открытой; транзакция 2 вызывает Reserve того же ключа — не завершается за 500 мс; после коммита транзакции 1 (с Complete) получает StoredResponse
  TryReserve_AfterRollback_ReservesAgain:  транзакция 1 резервирует и откатывается (исключением); транзакция 2 резервирует тот же ключ и получает null
  ```
- [ ] **Шаг 2: Запустить, убедиться, что падают (ошибка компиляции).**
  Запустить: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~IdempotencyStoreTests"`
  Ожидается: FAIL.
- [ ] **Шаг 3: Реализовать** запись, конфигурацию (имена колонок — snake_case по соглашению), хранилище, регистрацию `services.AddScoped<IIdempotencyStore, IdempotencyStore>()`. Создать миграцию.
  Запустить: `dotnet ef migrations add AddIdempotencyKeys --project src/MiniBank.Infrastructure --startup-project src/MiniBank.Infrastructure`
  Проверить, что в миграции только `CreateTable idempotency_keys`.
- [ ] **Шаг 4: Запустить весь набор и проверку модели.**
  Запустить: `dotnet test MiniBank.sln` и `dotnet ef migrations has-pending-model-changes --project src/MiniBank.Infrastructure --startup-project src/MiniBank.Infrastructure`
  Ожидается: PASS; «No changes have been made to the model since the last migration».
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat(infra): add idempotency_keys table and store"
  ```

---

### Задача 6: `IdempotencyExecutor` и сервисы с ключом

**Файлы:**
- Создать: `src/MiniBank.Application/Idempotency.cs` (содержит `IdempotentResult<T>`, `IdempotencyKeyReuseException`, `IdempotencyExecutor`)
- Изменить: `src/MiniBank.Application/TransfersService.cs`, `src/MiniBank.Application/DependencyInjection.cs`, `src/MiniBank.Api/Errors/ExceptionMapper.cs`, `src/MiniBank.Api/Controllers/AccountsController.cs`, `src/MiniBank.Api/Controllers/TransfersController.cs`
- Тест: `tests/MiniBank.Infrastructure.Tests/ServicesTests.cs`, `tests/MiniBank.Api.IntegrationTests/ExceptionMapperTests.cs`; обновить помощники во всех существующих тестах, которые вызывают перевод или пополнение (`ServicesTests`, `TransfersEndpointsTests`, `InvariantsTests`, `ConcurrencyTests`)

**Интерфейсы:**
- Использует: `IIdempotencyStore` (Задача 5), `IUnitOfWork.ExecuteInTransactionAsync`.
- Даёт:
  - `public sealed record IdempotentResult<T>(T Value, bool Replayed);`
  - `public sealed class IdempotencyKeyReuseException(string message) : Exception(message);` → `ExceptionMapper` отдаёт 422.
  - `IdempotencyExecutor.ExecuteAsync<T>(string? key, string requestHash, Func<CancellationToken, Task<T>> operation, CancellationToken ct)` → `Task<IdempotentResult<T>>`: проверяет ключ (null, пусто, только пробелы, длиннее 200, управляющие символы → `DomainValidationException`); хеш запроса получает готовым; в `uow.ExecuteInTransactionAsync`: `TryReserveAsync`; если запись есть — хеш другой → `IdempotencyKeyReuseException`, хеш тот же → десериализованный `T` и `Replayed = true`; иначе `operation`, затем `CompleteAsync(key, 200, JSON)`, результат с `Replayed = false`. JSON — `JsonSerializerDefaults.Web`. Регистрация в `AddApplication` (scoped).
  - `TransfersService.TransferAsync(string? idempotencyKey, Guid fromId, Guid toId, long amount, string? currency, CancellationToken ct)` и `DepositAsync(string? idempotencyKey, Guid accountId, long amount, string? currency, CancellationToken ct)` возвращают `Task<IdempotentResult<TransferDto>>`. Хеш: SHA-256 hex (нижний регистр) от строки `transfer|{fromId}|{toId}|{amount}|{CURRENCY}` и `deposit|{accountId}|{amount}|{CURRENCY}`; `CURRENCY` — нормализованная валюта (`Currencies.Normalize`, как в `Money.Of`). Валидация ключа и ввода идёт до транзакции.
  - Контроллеры получают заголовок: параметр `[FromHeader(Name = "Idempotency-Key")] string? idempotencyKey` (nullable, чтобы обязательность проверял сервис и ответ был 400 ProblemDetails), при `Replayed` добавляют заголовок ответа `Idempotent-Replayed: true`; возвращают `TransferDto` с кодом 200.

- [ ] **Шаг 1: Написать падающие тесты.** В `ServicesTests.cs` (хелперы обновить: свежий `Guid.NewGuid().ToString()` как ключ, возвращают `.Value`; новые тесты используют сервис напрямую):
  ```csharp
  Transfer_SameKeyTwice_ReturnsSameResultAndMovesMoneyOnce:  второй результат Replayed == true, Value равен первому, баланс изменён один раз, у счёта две проводки
  Transfer_SameKeyDifferentAmount_ThrowsKeyReuse
  Deposit_AndTransferWithSameKey_ThrowsKeyReuse:  один ключ на пополнение и затем на перевод с теми же счетами и суммой
  Transfer_LowercaseCurrencyIsSameRequest:  "rub" и "RUB" с одним ключом — Replayed == true
  Transfer_FailedByBusinessRule_DoesNotHoldKey:  недостаточно средств (InsufficientFundsException), затем пополнение счёта и тот же запрос с тем же ключом проходит
  Transfer_ParallelSameKey_CreatesExactlyOneTransfer:  20 параллельных вызовов с одним ключом (каждый в своём scope): все возвращают одинаковый Value, ровно один с Replayed == false, в transfers одна строка
  Transfer_ParallelSameKeyBothFail_BothThrowInsufficientFunds:  два параллельных вызова без средств на счёте — оба InsufficientFundsException, строк в idempotency_keys нет
  Key_Invalid_ThrowsValidation:  Theory: null, "", "   ", строка 201 символа, "a\u0001b" — DomainValidationException (для TransferAsync и DepositAsync)
  ```
  В `ExceptionMapperTests.cs`: `IdempotencyKeyReuseException` → 422.
  Обновить помощники в `TransfersEndpointsTests`, `InvariantsTests`, `ConcurrencyTests`: каждый POST на `/transfers` и `/accounts/{id}/deposit` отправляет заголовок `Idempotency-Key` со свежим `Guid` (общий метод-расширение `HttpClient` в `tests/MiniBank.Api.IntegrationTests/Requests.cs`: `PostWithKeyAsync(url, body, key = null)`). Сырые запросы в тестах `Transfer_MissingFieldsOrBrokenBody_Returns400` и `Deposit_MissingFieldsOrBrokenBody_Returns400` тоже отправляют заголовок, чтобы они по-прежнему проверяли именно тело.
- [ ] **Шаг 2: Запустить, убедиться, что падают (ошибка компиляции).**
  Запустить: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~ServicesTests"`
  Ожидается: FAIL.
- [ ] **Шаг 3: Реализовать** `Idempotency.cs`, переписать `TransfersService` под `IdempotencyExecutor` (поиск счетов и `Transfer.Execute` внутри `operation`; `ExecuteInTransactionAsync` из Задачи 4 заменяется вызовом исполнителя — транзакция теперь одна), обновить `ExceptionMapper` и контроллеры.
- [ ] **Шаг 4: Запустить весь набор.**
  Запустить: `dotnet test MiniBank.sln`
  Ожидается: PASS.
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat: require Idempotency-Key on transfers and deposits"
  ```

---

### Задача 7: API-тесты идемпотентности

**Файлы:**
- Тест: `tests/MiniBank.Api.IntegrationTests/IdempotencyEndpointsTests.cs` (новый)

**Интерфейсы:**
- Использует: контроллеры и `Requests.PostWithKeyAsync` (Задача 6).
- Даёт: тесты на HTTP-контракт идемпотентности (код менять не требуется; если какой-то тест красный — это дефект Задач 4–6, исправить причину, не ослабляя тест).

- [ ] **Шаг 1: Написать тесты:**
  ```csharp
  Deposit_WithoutKey_Returns400ProblemDetails:  нет заголовка → 400, Content-Type application/problem+json, баланс 0
  Transfer_WithoutKey_Returns400
  Key_BlankOrTooLong_Returns400:  Theory: "   ", строка 201 символа
  Deposit_RepeatedKey_ReturnsSameBodyAndReplayedHeader:  второй ответ 200, тело идентично, заголовок Idempotent-Replayed: true (у первого ответа заголовка нет), баланс изменён один раз
  Transfer_RepeatedKey_MovesMoneyOnce:  то же для /transfers, у обоих счетов нужное число проводок
  Transfer_SameKeyDifferentBody_Returns422
  Key_SharedBetweenDepositAndTransfer_Returns422
  Transfer_ParallelSameKey_CreatesOneTransfer:  20 параллельных запросов с одним ключом: все 200, одинаковое тело, ровно один без заголовка Idempotent-Replayed, баланс получателя равен одной сумме
  Transfer_FailedRequest_DoesNotHoldKey:  422 недостаточно средств, пополнение, тот же запрос с тем же ключом → 200
  ```
- [ ] **Шаг 2: Запустить.**
  Запустить: `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~IdempotencyEndpointsTests"`
  Ожидается: PASS (поведение реализовано в Задачах 4–6). Тест, который проходит до реализации, нужно усилить, а не оставить.
- [ ] **Шаг 3: Запустить весь набор.**
  Запустить: `dotnet test MiniBank.sln`
  Ожидается: PASS.
- [ ] **Шаг 4: Commit.**
  ```bash
  git add -A
  git commit -m "test(api): cover idempotency contract end to end"
  ```

---

### Задача 8: Логи ошибок и закреплённые версии пакетов

**Файлы:**
- Изменить: `src/MiniBank.Api/appsettings.json`, `src/MiniBank.Infrastructure/MiniBank.Infrastructure.csproj`, `tests/MiniBank.TestSupport/MiniBank.TestSupport.csproj`
- Тест: `tests/MiniBank.Api.IntegrationTests/ErrorLoggingTests.cs` (новый)

**Интерфейсы:**
- Использует: `ProblemDetailsExceptionHandler` (пишет в лог только 500).
- Даёт: бизнес-ошибки (400/404/409/422) не пишутся с уровнем Error; 500 пишется ровно один раз; в зависимостях нет плавающих версий (`9.*`).

- [ ] **Шаг 1: Написать падающие тесты в `ErrorLoggingTests.cs`.** Фабрика: `ApiFactory` с дополнительным `ILoggerProvider`, собирающим записи (`WithWebHostBuilder` + `ConfigureLogging`). Хелпер подмены `IAccountRepository` на бросающую `InvalidOperationException("secret db detail")` реализацию через `ConfigureServices`.
  ```csharp
  BusinessErrors_AreNotLoggedAsError:  GET несуществующего счёта (404) и POST /accounts с пустым владельцем (400) — среди записей нет уровня Error
  UnhandledException_Returns500WithoutDetails_AndIsLoggedOnce:  GET /accounts/{id} при бросающем репозитории → 500, Content-Type application/problem+json, detail "An unexpected error occurred.", тело не содержит "secret db detail"; ровно одна запись уровня Error
  ```
- [ ] **Шаг 2: Запустить, убедиться, что падают.**
  Запустить: `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~ErrorLoggingTests"`
  Ожидается: FAIL (стандартное middleware пишет Error на каждое исключение).
- [ ] **Шаг 3: В `appsettings.json` в `Logging:LogLevel` добавить** `"Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware": "None"`. Закрепить версии: выполнить `dotnet list package` для Infrastructure и TestSupport, заменить `9.*` у `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions` и `Npgsql` на точные установленные версии.
- [ ] **Шаг 4: Запустить весь набор.**
  Запустить: `dotnet test MiniBank.sln`
  Ожидается: PASS.
- [ ] **Шаг 5: Commit.**
  ```bash
  git add -A
  git commit -m "fix: log unhandled errors once, keep business errors quiet; pin package versions"
  ```

---

### Задача 9: README и сверка этапа

**Файлы:**
- Изменить: `README.md`

**Интерфейсы:**
- Использует: всё реализованное в Задачах 1–8.
- Даёт: описание для пользователей и обновлённый статус этапа.

- [ ] **Шаг 1: Обновить `README.md`:** статус «Этап 2 готов» (отметить ✅ в списке этапов); в таблице API указать заголовок `Idempotency-Key` для `POST /transfers` и `POST /accounts/{id}/deposit`, добавить в таблицу ошибок 422 «ключ идемпотентности уже использован с другим запросом»; в 400 добавить «нет заголовка `Idempotency-Key` или он некорректен»; убрать из строки 409 упоминание «повторить запрос» как штатной ситуации — теперь она редка (исчерпаны ретраи); добавить раздел «Идемпотентность и параллельность» (как работает ключ, повтор и заголовок `Idempotent-Replayed`, блокировки `FOR UPDATE` в порядке `id`, ретраи), добавить таблицу `idempotency_keys` в Mermaid-схему; пример запроса с `curl -H "Idempotency-Key: ..."`; упомянуть, что TTL ключей не реализован.
- [ ] **Шаг 2: Запустить всё и проверить миграции.**
  Запустить: `dotnet test MiniBank.sln` и `dotnet ef migrations has-pending-model-changes --project src/MiniBank.Infrastructure --startup-project src/MiniBank.Infrastructure`
  Ожидается: PASS; «No changes have been made to the model since the last migration».
- [ ] **Шаг 3: Commit.**
  ```bash
  git add -A
  git commit -m "docs: README for stage 2 (idempotency and concurrency)"
  ```
