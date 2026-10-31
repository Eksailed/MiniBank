# MiniBank Core Implementation Plan

> **Для исполнителей:** план реализуется по задачам; шаги отмечены чекбоксами (`- [ ]`).

**Goal:** Собрать ядро MiniBank (этап 1): счета, переводы с двойной записью, выписка, REST API на ASP.NET Core с PostgreSQL.

**Architecture:** Модульный монолит из четырёх проектов (Domain, Application, Infrastructure, Api) с зависимостями только внутрь. Бизнес-правила живут в Domain, сценарии — обычные классы в Application, EF Core и PostgreSQL — в Infrastructure. Инварианты дублируются ограничениями БД, интеграционные тесты идут на реальном PostgreSQL через Testcontainers.

**Tech Stack:** C#, .NET 9, ASP.NET Core (контроллеры), EF Core 9 + Npgsql + EFCore.NamingConventions, Swashbuckle, xUnit, Testcontainers.PostgreSql, Microsoft.AspNetCore.Mvc.Testing.

**Spec:** `docs/specs/2026-10-31-minibank-core-design.md`

## Global Constraints

- .NET 9, PostgreSQL, EF Core + Npgsql; тесты — xUnit и Testcontainers.
- Деньги: `bigint` в минимальных единицах (копейки) плюс код валюты; `decimal` и `float` не используются. Одна валюта на счёт; переводы только внутри одной валюты, конвертации нет.
- Зависимости: Api → Application → Domain; Infrastructure → Application, Domain. Domain без внешних пакетов.
- Контроллеры, а не minimal API. MediatR не используется; сценарии — обычные классы.
- Проводки только добавляются: UPDATE и DELETE в `ledger_entries` запрещены (триггер в БД).
- Баланс обычного счёта не ниже нуля (`CHECK` в БД плюс проверка в домене). Системные счета «Касса» (`is_system = true`) могут уходить в минус.
- Сумма всех балансов всегда равна нулю; `accounts.balance` равен сумме проводок по счёту.
- В `transfers` пишутся только выполненные переводы (`status = Completed`); неудачные не сохраняются.
- Ошибки только в формате `ProblemDetails`: 400 — невалидный ввод; 404 — счёт не найден; 422 — недостаточно средств, счёт заблокирован; 409 — конфликт версий.
- Вне рамок: мультивалютность, комиссии, отмена переводов, аутентификация.
- Работаем в ветке `main` (пользователь выбрал не создавать отдельную ветку). Каждое сообщение коммита заканчивается строкой `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Пуш в GitHub — только по просьбе пользователя.

**Решения плана сверх спецификации** (спецификация молчит): поддерживаемые валюты — `RUB`, `USD`, `EUR`, и в миграции создаётся по одному счёту «Касса» на каждую; сумма перевода и пополнения должна быть больше нуля; размер страницы выписки по умолчанию 50, максимум 100.

## Review Focus

Входы, которые спецификация подразумевает, но не описывает. Тест на каждый добавлен в задачу-владельца.

1. Сумма близка к `long.MaxValue`: переполнение баланса не должно давать 500 или «тихого» отрицательного баланса, ожидается 400. (Задача 3)
2. Клиент вызывает `POST /transfers` со счётом «Касса» как отправителем или получателем: иначе можно напечатать деньги. Ожидается 422. (Задачи 7 и 9)
3. Валюта в нижнем регистре или неподдерживаемая (`rub`, `RU`, `JPY`, пустая строка): `rub` нормализуется в `RUB`, остальное даёт 400. (Задачи 2 и 8)
4. Битый JSON, отсутствующие поля, пустой владелец при создании счёта: ожидается 400, а не 500. (Задача 8)
5. Границы выписки: `page = 0`, `pageSize = 0`, `pageSize = 101`: ожидается 400, а не пустой ответ или исключение БД. (Задачи 7 и 9)

---

### Task 1: Каркас решения

**Files:**
- Create: `MiniBank.sln`, `Directory.Build.props`, `.gitignore`
- Create: `src/MiniBank.Domain/`, `src/MiniBank.Application/`, `src/MiniBank.Infrastructure/`, `src/MiniBank.Api/` (`dotnet new classlib` ×3, `dotnet new webapi --use-controllers` ×1)
- Create: `tests/MiniBank.Domain.Tests/` (`dotnet new xunit`)

**Interfaces:**
- Produces: решение, которое собирается; ссылки между проектами Application → Domain, Infrastructure → Application, Api → Application и Infrastructure, Domain.Tests → Domain.

- [ ] **Step 1: Создать решение и проекты командами `dotnet new` и `dotnet sln add`.** Целевая платформа `net9.0`. Удалить шаблонные файлы (`Class1.cs`, `WeatherForecast*`, `UnitTest1.cs`).
- [ ] **Step 2: Написать `Directory.Build.props`** с `TargetFramework=net9.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`. Добавить стандартный .NET `.gitignore` (`bin/`, `obj/`, `.vs/`).
- [ ] **Step 3: Прописать ссылки между проектами** (`dotnet add reference`) по схеме из Interfaces.
- [ ] **Step 4: Проверить сборку.**
  Run: `dotnet build MiniBank.sln`
  Expected: `Build succeeded`, 0 предупреждений, 0 ошибок.
- [ ] **Step 5: Commit.**
  ```bash
  git add -A
  git commit -m "chore: scaffold solution and projects"
  ```

---

### Task 2: Domain — валюты, Money, исключения

**Files:**
- Create: `src/MiniBank.Domain/Currencies.cs`, `Money.cs`, `Exceptions.cs`
- Test: `tests/MiniBank.Domain.Tests/MoneyTests.cs`

**Interfaces:**
- Produces:
  - `abstract class DomainException : Exception`; `sealed class DomainValidationException : DomainException`, `InsufficientFundsException : DomainException`, `AccountBlockedException : DomainException` (у каждого конструктор `(string message)`).
  - `static class Currencies { static readonly IReadOnlyList<string> Supported; static string Normalize(string? code) }`. `Normalize` обрезает пробелы, приводит к верхнему регистру и возвращает код. Бросает `DomainValidationException`, если кода нет в `Supported` (`RUB`, `USD`, `EUR`).
  - `readonly record struct Money(long Amount, string Currency)` с фабрикой `static Money Of(long amount, string? currency)`. `Of` бросает `DomainValidationException`, если `amount <= 0`; валюта нормализуется через `Currencies.Normalize`.

- [ ] **Step 1: Написать падающие тесты в `MoneyTests.cs`:**
  ```csharp
  Of_NormalizesCurrencyToUpper:   Money.Of(100, " rub ").Currency == "RUB"
  Of_RejectsNonPositiveAmount:    Theory 0, -1 -> Assert.Throws<DomainValidationException>
  Of_RejectsUnsupportedCurrency:  Theory "JPY", "RU", "", null -> Assert.Throws<DomainValidationException>
  Of_KeepsAmountInMinorUnits:     Money.Of(12345, "USD").Amount == 12345
  ```
- [ ] **Step 2: Запустить и убедиться, что падают (не компилируется или Fail).**
  Run: `dotnet test tests/MiniBank.Domain.Tests --filter "FullyQualifiedName~MoneyTests"`
  Expected: FAIL.
- [ ] **Step 3: Реализовать `Currencies`, `Money` и исключения** по сигнатурам из Interfaces.
- [ ] **Step 4: Запустить тесты.**
  Run: та же команда. Expected: PASS, все тесты и все случаи Theory зелёные.
- [ ] **Step 5: Commit.**
  ```bash
  git add src/MiniBank.Domain tests/MiniBank.Domain.Tests
  git commit -m "feat(domain): add Money, Currencies and domain exceptions"
  ```

---

### Task 3: Domain — Account и LedgerEntry

**Files:**
- Create: `src/MiniBank.Domain/Account.cs`, `AccountStatus.cs`, `LedgerEntry.cs`
- Test: `tests/MiniBank.Domain.Tests/AccountTests.cs`

**Interfaces:**
- Consumes: `Money`, `Currencies`, `DomainValidationException`, `InsufficientFundsException`, `AccountBlockedException` (Задача 2).
- Produces:
  - `enum AccountStatus { Active, Blocked }`.
  - `sealed class LedgerEntry { Guid Id; Guid TransferId; Guid AccountId; long Amount; long BalanceAfter; DateTimeOffset CreatedAt }` (знаковая сумма: списание отрицательное).
  - `sealed class Account { Guid Id; string Owner; string Currency; long Balance; int Version; AccountStatus Status; bool IsSystem; DateTimeOffset CreatedAt; }` и методы:
    `static Account Open(string? owner, string? currency, DateTimeOffset now, bool isSystem = false, Guid? id = null)`,
    `void Block()`,
    `LedgerEntry Debit(Guid transferId, Money amount, DateTimeOffset now)`,
    `LedgerEntry Credit(Guid transferId, Money amount, DateTimeOffset now)`.
  - `Debit`/`Credit` каждый вызов увеличивают `Version` на 1 и возвращают проводку с `BalanceAfter`, равным новому балансу. Для EF Core нужен приватный конструктор без параметров.

- [ ] **Step 1: Написать падающие тесты в `AccountTests.cs`:**
  ```csharp
  Open_RejectsEmptyOwner:            Account.Open("  ", "RUB", now) -> DomainValidationException
  Open_NormalizesCurrency:           Account.Open("a", "rub", now).Currency == "RUB"; Balance == 0; Status == Active
  Debit_ReducesBalanceAndReturnsNegativeEntry: balance 1000 -> Debit(500) -> Balance 500, entry.Amount == -500, entry.BalanceAfter == 500, Version +1
  Debit_BelowZero_Throws:            balance 100, Debit(101) -> InsufficientFundsException, Balance still 100, Version unchanged
  Debit_SystemAccount_MayGoNegative: Open(isSystem: true).Debit(500) -> Balance == -500
  Credit_IncreasesBalance:           Credit(300) -> Balance 300, entry.Amount == 300
  Blocked_RejectsDebitAndCredit:     after Block() -> AccountBlockedException on both
  Currency_Mismatch_Throws:          RUB account, Money.Of(10,"USD") -> DomainValidationException
  Credit_Overflow_ThrowsValidation:  balance long.MaxValue - 1, Credit(2) -> DomainValidationException, balance unchanged   // Review Focus 1
  ```
  Начальный баланс в тестах задавать через `Credit` с другим `transferId`.
- [ ] **Step 2: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Domain.Tests --filter "FullyQualifiedName~AccountTests"`
  Expected: FAIL.
- [ ] **Step 3: Реализовать `Account`, `LedgerEntry`, `AccountStatus`.** Арифметику баланса вести через `checked` и превращать `OverflowException` в `DomainValidationException`.
- [ ] **Step 4: Запустить тесты.**
  Run: та же команда. Expected: PASS.
- [ ] **Step 5: Commit.**
  ```bash
  git add src/MiniBank.Domain tests/MiniBank.Domain.Tests
  git commit -m "feat(domain): add Account with debit/credit and ledger entries"
  ```

---

### Task 4: Domain — Transfer и системные счета

**Files:**
- Create: `src/MiniBank.Domain/Transfer.cs`, `TransferStatus.cs`, `SystemAccounts.cs`
- Test: `tests/MiniBank.Domain.Tests/TransferTests.cs`

**Interfaces:**
- Consumes: `Account`, `LedgerEntry`, `Money`, исключения (Задачи 2–3).
- Produces:
  - `enum TransferStatus { Completed }`.
  - `sealed class Transfer { Guid Id; Guid FromAccountId; Guid ToAccountId; long Amount; string Currency; TransferStatus Status; DateTimeOffset CreatedAt; IReadOnlyList<LedgerEntry> Entries }` и фабрика `static Transfer Execute(Account from, Account to, Money amount, DateTimeOffset now)`. Фабрика проверяет, что счета разные (`DomainValidationException`), вызывает `from.Debit` и `to.Credit`, собирает ровно две проводки. Приватный конструктор без параметров для EF Core.
  - `static class SystemAccounts { static Guid CashId(string currency); static bool IsCashId(Guid id) }`. Фиксированные идентификаторы касс: `RUB` = `00000000-0000-0000-0000-0000000000a1`, `USD` = `...a2`, `EUR` = `...a3`. `CashId` бросает `DomainValidationException` для неподдерживаемой валюты.

- [ ] **Step 1: Написать падающие тесты в `TransferTests.cs`:**
  ```csharp
  Execute_CreatesTwoEntriesSummingToZero: Theory amounts 1, 500, 99999 -> Entries.Count == 2 && Entries.Sum(e => e.Amount) == 0
  Execute_MovesMoney:                     from 1000, to 0, Execute(300) -> from.Balance 700, to.Balance 300
  Execute_SameAccount_Throws:             Execute(a, a, ...) -> DomainValidationException
  Execute_InsufficientFunds_ChangesNothing: from 100, Execute(200) -> InsufficientFundsException, both balances unchanged
  Execute_CurrencyMismatch_Throws:        RUB -> USD accounts -> DomainValidationException
  Execute_RecordsFromToAndStatus:         Status == Completed, FromAccountId/ToAccountId/Amount/Currency заполнены, entries имеют TransferId == Id
  CashId_IsStableAndRejectsUnknown:       CashId("RUB") == Guid("...a1"); CashId("JPY") throws; IsCashId(CashId("EUR")) is true
  ```
- [ ] **Step 2: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Domain.Tests --filter "FullyQualifiedName~TransferTests"`
  Expected: FAIL.
- [ ] **Step 3: Реализовать `Transfer`, `TransferStatus`, `SystemAccounts`.** Если `Debit` бросил исключение, состояние `to` не должно меняться (дебет выполняется первым).
- [ ] **Step 4: Запустить все тесты Domain.**
  Run: `dotnet test tests/MiniBank.Domain.Tests`
  Expected: PASS.
- [ ] **Step 5: Commit.**
  ```bash
  git add src/MiniBank.Domain tests/MiniBank.Domain.Tests
  git commit -m "feat(domain): add Transfer with double-entry and system cash accounts"
  ```

---

### Task 5: Infrastructure — схема БД, миграция, ограничения

**Files:**
- Create: `tests/MiniBank.TestSupport/MiniBank.TestSupport.csproj` (classlib), `tests/MiniBank.TestSupport/PostgresFixture.cs`
- Create: `tests/MiniBank.Infrastructure.Tests/` (`dotnet new xunit`, ссылки на Infrastructure, Domain, TestSupport)
- Create: `src/MiniBank.Infrastructure/Persistence/MiniBankDbContext.cs`, `Persistence/Configurations/*.cs`, `Persistence/Migrations/*` (генерируется)
- Test: `tests/MiniBank.Infrastructure.Tests/SchemaTests.cs`

**Interfaces:**
- Consumes: сущности Domain (Задачи 2–4).
- Produces:
  - `PostgresFixture : IAsyncLifetime` с `string ConnectionString` и `Task<string> CreateDatabaseAsync()`. Фикстура поднимает один контейнер `postgres:17` через Testcontainers на весь тестовый класс-коллекцию `[CollectionDefinition("postgres")]`. `CreateDatabaseAsync` создаёт отдельную пустую базу (`CREATE DATABASE`) и возвращает её строку подключения, чтобы тесты не влияли друг на друга.
  - `class MiniBankDbContext(DbContextOptions<MiniBankDbContext> options) : DbContext` с `DbSet<Account> Accounts`, `DbSet<Transfer> Transfers`, `DbSet<LedgerEntry> LedgerEntries`. Таблицы `accounts`, `transfers`, `ledger_entries`, колонки в snake_case (`UseSnakeCaseNamingConvention`). `Account.Version` настроен как concurrency token. `Transfer.Entries` — навигация на `LedgerEntry` (внешний ключ `transfer_id`).
  - Миграция `InitialCreate` (в ней же сырой SQL) создаёт: `CHECK ck_accounts_balance_non_negative (balance >= 0 OR is_system)`; триггер `ledger_entries_append_only`, который перед `UPDATE` и `DELETE` в `ledger_entries` вызывает `RAISE EXCEPTION`; три строки «Касса» (по одной на `RUB`, `USD`, `EUR`, идентификаторы из `SystemAccounts.CashId`, `is_system = true`, `balance = 0`).

- [ ] **Step 1: Создать `PostgresFixture` и проект `MiniBank.Infrastructure.Tests`.** Пакеты: `Testcontainers.PostgreSql`, `Npgsql`, `Microsoft.EntityFrameworkCore`.
- [ ] **Step 2: Написать падающие тесты в `SchemaTests.cs`** (каждый тест берёт чистую базу через `CreateDatabaseAsync` и применяет миграции `context.Database.MigrateAsync()`):
  ```csharp
  Migrate_SeedsOneCashAccountPerSupportedCurrency: три счёта is_system, balance 0, Id == SystemAccounts.CashId(currency)
  CheckConstraint_RejectsNegativeBalanceForRegularAccount: INSERT balance = -1, is_system = false -> PostgresException (SqlState 23514)
  CheckConstraint_AllowsNegativeBalanceForSystemAccount:   UPDATE кассы SET balance = -5 проходит
  LedgerTrigger_RejectsUpdateAndDelete: после вставки проводки UPDATE и DELETE -> PostgresException
  RoundTrip_SavesTransferWithEntries: сохранить два счёта и Transfer.Execute(...), перечитать в новом контексте -> Entries.Count == 2, суммы и Version совпадают
  ```
- [ ] **Step 3: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~SchemaTests"`
  Expected: FAIL (нет контекста). Нужен запущенный Docker.
- [ ] **Step 4: Реализовать `MiniBankDbContext` и конфигурации, добавить пакеты** `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`, `Microsoft.EntityFrameworkCore.Design`. Сгенерировать миграцию `dotnet ef migrations add InitialCreate --project src/MiniBank.Infrastructure --output-dir Persistence/Migrations` (нужна `IDesignTimeDbContextFactory<MiniBankDbContext>` с локальной строкой подключения). Вписать `CHECK`, триггер и посев касс в `Up`/`Down` миграции через `migrationBuilder.Sql` и `InsertData`.
- [ ] **Step 5: Запустить тесты.**
  Run: та же команда. Expected: PASS (первый запуск дольше из-за загрузки образа).
- [ ] **Step 6: Commit.**
  ```bash
  git add -A
  git commit -m "feat(infra): add EF Core schema, migration with constraints and cash accounts"
  ```

---

### Task 6: Application-порты и реализации репозиториев

**Files:**
- Create: `src/MiniBank.Application/Abstractions/IAccountRepository.cs`, `ITransferRepository.cs`, `ILedgerQuery.cs`, `IUnitOfWork.cs`
- Create: `src/MiniBank.Application/Exceptions.cs`
- Create: `src/MiniBank.Infrastructure/Persistence/AccountRepository.cs`, `TransferRepository.cs`, `LedgerQuery.cs`, `UnitOfWork.cs`, `DependencyInjection.cs`
- Test: `tests/MiniBank.Infrastructure.Tests/RepositoryTests.cs`

**Interfaces:**
- Consumes: `MiniBankDbContext`, `PostgresFixture` (Задача 5); сущности Domain.
- Produces:
  - `sealed class AccountNotFoundException : Exception` (конструктор `(Guid id)`), `TransferNotAllowedException : Exception`, `ConcurrencyConflictException : Exception` (конструкторы `(string message)`, кроме первого).
  - `interface IAccountRepository { Task<Account?> FindAsync(Guid id, CancellationToken ct); void Add(Account account); }`
  - `interface ITransferRepository { void Add(Transfer transfer); }`
  - `interface ILedgerQuery { Task<IReadOnlyList<LedgerEntry>> GetEntriesAsync(Guid accountId, int page, int pageSize, CancellationToken ct); }` Сортировка: `created_at` по убыванию, затем `id`; страницы с единицы.
  - `interface IUnitOfWork { Task SaveChangesAsync(CancellationToken ct); }` Реализация превращает `DbUpdateConcurrencyException` в `ConcurrencyConflictException`.
  - `static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)` регистрирует контекст, четыре реализации выше (scoped) и `TimeProvider.System` (singleton).

- [ ] **Step 1: Написать падающие тесты в `RepositoryTests.cs`** (чистая база, миграции, `ServiceProvider` через `AddInfrastructure`):
  ```csharp
  Add_ThenFind_ReturnsAccount:           Add(account) + SaveChanges, FindAsync(id) в новом scope -> те же Owner, Currency, Balance
  Find_Unknown_ReturnsNull:              FindAsync(Guid.NewGuid()) is null
  SaveChanges_OnStaleVersion_ThrowsConcurrencyConflict: два scope загрузили один счёт, оба сделали Credit, второй SaveChanges -> ConcurrencyConflictException
  GetEntries_ReturnsNewestFirstAndPages: 5 проводок, pageSize 2 -> страницы 1,2,3 содержат 2,2,1 записи в порядке убывания created_at
  ```
- [ ] **Step 2: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~RepositoryTests"`
  Expected: FAIL.
- [ ] **Step 3: Реализовать порты, исключения и классы Infrastructure** по сигнатурам из Interfaces. `FindAsync` загружает счёт без блокировок.
- [ ] **Step 4: Запустить тесты Infrastructure целиком.**
  Run: `dotnet test tests/MiniBank.Infrastructure.Tests`
  Expected: PASS.
- [ ] **Step 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat: add application ports and EF Core repositories"
  ```

---

### Task 7: Application — сценарии AccountsService и TransfersService

**Files:**
- Create: `src/MiniBank.Application/Dtos.cs`, `AccountsService.cs`, `TransfersService.cs`
- Create: `src/MiniBank.Application/DependencyInjection.cs` с `static IServiceCollection AddApplication(this IServiceCollection services)` (регистрирует оба сервиса как scoped)
- Test: `tests/MiniBank.Infrastructure.Tests/ServicesTests.cs` (сервисы проверяются на настоящей БД; ссылку Infrastructure.Tests → Application добавить)

**Interfaces:**
- Consumes: порты и исключения (Задача 6), `Transfer.Execute`, `SystemAccounts` (Задача 4).
- Produces:
  - `record AccountDto(Guid Id, string Owner, string Currency, long Balance, string Status, bool IsSystem, DateTimeOffset CreatedAt)`; `record EntryDto(Guid Id, Guid TransferId, long Amount, long BalanceAfter, DateTimeOffset CreatedAt)`; `record TransferDto(Guid Id, Guid FromAccountId, Guid ToAccountId, long Amount, string Currency, DateTimeOffset CreatedAt)`.
  - `sealed class AccountsService(IAccountRepository accounts, ILedgerQuery ledger, IUnitOfWork uow, TimeProvider clock)`:
    `Task<AccountDto> CreateAsync(string? owner, string? currency, CancellationToken ct)`,
    `Task<AccountDto> GetAsync(Guid id, CancellationToken ct)` (бросает `AccountNotFoundException`),
    `Task<IReadOnlyList<EntryDto>> GetEntriesAsync(Guid id, int page, int pageSize, CancellationToken ct)`. Допустимо `page >= 1` и `1 <= pageSize <= 100`, иначе `DomainValidationException`. Счёт должен существовать, иначе `AccountNotFoundException`.
  - `sealed class TransfersService(IAccountRepository accounts, ITransferRepository transfers, IUnitOfWork uow, TimeProvider clock)`:
    `Task<TransferDto> TransferAsync(Guid fromId, Guid toId, long amount, string? currency, CancellationToken ct)`: бросает `TransferNotAllowedException`, если любой из счетов системный;
    `Task<TransferDto> DepositAsync(Guid accountId, long amount, string? currency, CancellationToken ct)`: перевод с кассы нужной валюты (`SystemAccounts.CashId`) на счёт клиента, валюта должна совпасть с валютой счёта.
  - Оба сервиса выполняют весь сценарий в одном `SaveChangesAsync`. При любой ошибке ничего не сохраняется.

- [ ] **Step 1: Написать падающие тесты в `ServicesTests.cs`** (чистая база, миграции, сервисы из `AddApplication` + `AddInfrastructure`):
  ```csharp
  Create_ReturnsZeroBalanceAccount:       Balance == 0, Currency нормализована ("rub" -> "RUB"), Status == "Active"
  Create_UnsupportedCurrencyOrEmptyOwner_Throws: "JPY" и "" владелец -> DomainValidationException
  Get_Unknown_ThrowsNotFound:             AccountNotFoundException
  Deposit_MovesMoneyFromCash:             после Deposit(1000): баланс клиента 1000, баланс кассы RUB -1000
  Deposit_UnknownAccount_ThrowsNotFound
  Deposit_CurrencyMismatch_Throws:        счёт RUB, Deposit(..., "USD") -> DomainValidationException
  Transfer_HappyPath:                     балансы изменились, две проводки (-X у отправителя, +X у получателя), сумма всех балансов в БД == 0
  Transfer_InsufficientFunds_PersistsNothing: InsufficientFundsException, балансы и число проводок не изменились
  Transfer_ToSameAccount_Throws:          DomainValidationException
  Transfer_BlockedAccount_Throws:         после account.Block() и Save -> AccountBlockedException
  Transfer_WithCashAsSourceOrTarget_Throws: оба направления -> TransferNotAllowedException, балансы не изменились     // Review Focus 2
  Transfer_UnknownAccount_ThrowsNotFound
  GetEntries_InvalidPaging_Throws:        (0,50), (1,0), (1,101) -> DomainValidationException                         // Review Focus 5
  GetEntries_ReturnsEntriesOfAccount:     после двух операций у клиента две проводки, у чужого счёта чужие не видны
  ```
- [ ] **Step 2: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~ServicesTests"`
  Expected: FAIL.
- [ ] **Step 3: Реализовать DTO, сервисы и `AddApplication`** по сигнатурам из Interfaces. Время брать только из `TimeProvider`.
- [ ] **Step 4: Запустить тесты проекта.**
  Run: `dotnet test tests/MiniBank.Infrastructure.Tests`
  Expected: PASS.
- [ ] **Step 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat(application): add accounts and transfers services"
  ```

---

### Task 8: Api — запуск, обработка ошибок, создание и чтение счетов, Swagger

**Files:**
- Create: `tests/MiniBank.Api.IntegrationTests/` (`dotnet new xunit`, ссылки на Api, TestSupport; пакет `Microsoft.AspNetCore.Mvc.Testing`), `tests/MiniBank.Api.IntegrationTests/ApiFactory.cs`
- Create: `src/MiniBank.Api/Controllers/AccountsController.cs`, `Errors/ExceptionMapper.cs`, `Errors/ProblemDetailsExceptionHandler.cs`, `Contracts.cs`
- Modify: `src/MiniBank.Api/Program.cs`, `appsettings.json`, `appsettings.Development.json`
- Test: `tests/MiniBank.Api.IntegrationTests/ExceptionMapperTests.cs`, `AccountsEndpointsTests.cs`

**Interfaces:**
- Consumes: `AccountsService`, `AddApplication`, `AddInfrastructure`, исключения (Задачи 6–7).
- Produces:
  - `static class ExceptionMapper { static int ToStatusCode(Exception ex) }`: `DomainValidationException` → 400; `InsufficientFundsException`, `AccountBlockedException`, `TransferNotAllowedException` → 422; `AccountNotFoundException` → 404; `ConcurrencyConflictException` → 409; всё остальное → 500.
  - `ProblemDetailsExceptionHandler : IExceptionHandler` пишет `ProblemDetails` со статусом из `ExceptionMapper`. Для 500 текст ошибки не раскрывается.
  - `record CreateAccountRequest(string Owner, string Currency)`, `record DepositRequest(long Amount, string Currency)`, `record TransferRequest(Guid FromAccountId, Guid ToAccountId, long Amount, string Currency)` в `Contracts.cs` (поля нужны для следующих задач).
  - `public partial class Program` для `WebApplicationFactory`. Строка подключения `ConnectionStrings:MiniBank`; флаг `Database:MigrateOnStartup` (true в `appsettings.Development.json`) запускает `MigrateAsync` при старте. Swashbuckle включён в окружении Development: `/swagger/v1/swagger.json`.
  - `ApiFactory : WebApplicationFactory<Program>` берёт чистую базу из `PostgresFixture.CreateDatabaseAsync`, подставляет `ConnectionStrings:MiniBank` и `Database:MigrateOnStartup=true`, окружение `Development`.
  - Эндпоинты: `POST /accounts` → 201 + `Location` + `AccountDto`; `GET /accounts/{id:guid}` → 200 + `AccountDto`.

- [ ] **Step 1: Написать падающие тесты.**
  `ExceptionMapperTests.cs`:
  ```csharp
  ToStatusCode_MapsEachExceptionType: Theory — DomainValidationException 400, InsufficientFundsException 422, AccountBlockedException 422,
      TransferNotAllowedException 422, AccountNotFoundException 404, ConcurrencyConflictException 409, InvalidOperationException 500
  ```
  `AccountsEndpointsTests.cs` (через `ApiFactory` и `HttpClient`):
  ```csharp
  PostAccounts_Returns201WithLocationAndZeroBalance
  PostAccounts_LowercaseCurrency_IsNormalized:         "rub" -> Currency == "RUB"                                     // Review Focus 3
  PostAccounts_BadCurrency_Returns400ProblemDetails:   "JPY", "RU", "" -> 400, Content-Type application/problem+json    // Review Focus 3
  PostAccounts_EmptyOwner_Returns400:                  Owner "" -> 400                                                // Review Focus 4
  PostAccounts_MalformedJson_Returns400:               тело "{not json" -> 400                                       // Review Focus 4
  PostAccounts_MissingFields_Returns400:               тело "{}" -> 400                                              // Review Focus 4
  GetAccount_Returns200AndUnknownReturns404:           случайный Guid -> 404 ProblemDetails
  Swagger_JsonIsServed:                                GET /swagger/v1/swagger.json -> 200
  ```
- [ ] **Step 2: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Api.IntegrationTests`
  Expected: FAIL.
- [ ] **Step 3: Реализовать `Program.cs`** (контроллеры, `AddApplication`, `AddInfrastructure`, `AddExceptionHandler<ProblemDetailsExceptionHandler>`, `AddProblemDetails`, Swashbuckle, миграции при старте по флагу), **`AccountsController`** (создание и чтение) и классы ошибок.
- [ ] **Step 4: Запустить тесты.**
  Run: `dotnet test tests/MiniBank.Api.IntegrationTests`
  Expected: PASS.
- [ ] **Step 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat(api): add app setup, error handling and account endpoints"
  ```

---

### Task 9: Api — пополнение, переводы, выписка

**Files:**
- Modify: `src/MiniBank.Api/Controllers/AccountsController.cs` (deposit, entries)
- Create: `src/MiniBank.Api/Controllers/TransfersController.cs`
- Test: `tests/MiniBank.Api.IntegrationTests/TransfersEndpointsTests.cs`

**Interfaces:**
- Consumes: `TransfersService`, `AccountsService`, контракты и обработчик ошибок (Задача 8).
- Produces:
  - `POST /accounts/{id:guid}/deposit` с `DepositRequest` → 200 + `TransferDto`.
  - `POST /transfers` с `TransferRequest` → 200 + `TransferDto`.
  - `GET /accounts/{id:guid}/entries?page=1&pageSize=50` → 200 + `EntryDto[]`; значения по умолчанию `page = 1`, `pageSize = 50`.

- [ ] **Step 1: Написать падающие тесты в `TransfersEndpointsTests.cs`:**
  ```csharp
  Deposit_Returns200AndIncreasesBalance
  Deposit_UnknownAccount_Returns404
  Deposit_NonPositiveAmount_Returns400:        0 и -5 -> 400
  Transfer_Returns200AndMovesMoney:            пополнить A на 1000, перевести 300 на B -> GET показывает 700 и 300
  Transfer_InsufficientFunds_Returns422
  Transfer_ToSameAccount_Returns400
  Transfer_CurrencyMismatch_Returns400:        RUB -> USD счёт
  Transfer_UnknownAccount_Returns404
  Transfer_WithCashAccount_Returns422:         два случая: from = SystemAccounts.CashId("RUB") и to = обычный счёт RUB; from = обычный счёт RUB и to = SystemAccounts.CashId("RUB"); оба -> 422   // Review Focus 2
  Transfer_HugeAmount_Returns400Not500:        получатель с балансом long.MaxValue - 1, перевод 2 -> 400   // Review Focus 1
  Entries_ReturnsHistoryNewestFirst:           после пополнения и перевода у A две проводки, первая — перевод
  Entries_DefaultsAndBounds:                   без параметров -> 200; page=0, pageSize=0, pageSize=101 -> 400   // Review Focus 5
  Entries_UnknownAccount_Returns404
  ```
  Чтобы получить получателя с большим балансом, пополнить его через `Deposit` суммой `long.MaxValue - 1` (касса системная и уходит в минус); отправителю пополнить минимум 2.
- [ ] **Step 2: Запустить, убедиться, что падают.**
  Run: `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~TransfersEndpointsTests"`
  Expected: FAIL.
- [ ] **Step 3: Реализовать эндпоинты** `deposit`, `entries` (в `AccountsController`) и `POST /transfers` (`TransfersController`), делегируя сервисам. Контроллеры без бизнес-логики.
- [ ] **Step 4: Запустить тесты проекта.**
  Run: `dotnet test tests/MiniBank.Api.IntegrationTests`
  Expected: PASS.
- [ ] **Step 5: Commit.**
  ```bash
  git add -A
  git commit -m "feat(api): add deposit, transfer and statement endpoints"
  ```

---

### Task 10: Сверка инвариантов, Swagger, README и локальная БД

**Files:**
- Create: `tests/MiniBank.Api.IntegrationTests/InvariantsTests.cs`
- Create: `docker-compose.yml` (только PostgreSQL для локального запуска)
- Modify: `README.md`, `src/MiniBank.Api/appsettings.Development.json`

**Interfaces:**
- Consumes: весь API (Задачи 8–9) и `MiniBankDbContext` (для прямой сверки в БД).
- Produces: тест-сверка этапа 1, `docker-compose.yml` с сервисом `postgres` (образ `postgres:17`, порт 5432, база `minibank`), README с запуском и схемой данных.

- [ ] **Step 1: Написать падающие тесты в `InvariantsTests.cs`:**
  ```csharp
  AfterSeriesOfOperations_TotalOfAllBalancesIsZero:     3 счёта, пополнения и 20 переводов (часть с недостатком средств игнорируется), SUM(accounts.balance) == 0 по SELECT
  AfterSeriesOfOperations_BalanceEqualsSumOfEntries:    для каждого счёта balance == SUM(ledger_entries.amount) (кассы включены)
  EveryTransferHasExactlyTwoEntriesSummingToZero:       GROUP BY transfer_id: COUNT == 2 и SUM == 0
  Swagger_ListsAllFiveEndpoints:                        /swagger/v1/swagger.json содержит пути /accounts, /accounts/{id}, /accounts/{id}/deposit, /accounts/{id}/entries, /transfers
  ```
  Сверку делать SQL-запросами через отдельное подключение к базе фабрики (не через сервисы).
- [ ] **Step 2: Запустить, убедиться, что падают или проходят.**
  Run: `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~InvariantsTests"`
  Expected: тесты на инварианты должны проходить, если всё сделано верно. Если какой-то падает, это ошибка в реализации предыдущих задач: найти и исправить причину, не ослабляя тест.
- [ ] **Step 3: Добавить `docker-compose.yml` с PostgreSQL** и строку подключения `ConnectionStrings:MiniBank` в `appsettings.Development.json`, совпадающую с compose.
- [ ] **Step 4: Обновить `README.md`:** статус «этап 1 готов», команды `docker compose up -d`, `dotnet run --project src/MiniBank.Api`, `dotnet test`, адрес Swagger, ER-схема таблиц в Mermaid, список эндпоинтов.
- [ ] **Step 5: Запустить все тесты решения.**
  Run: `dotnet test MiniBank.sln`
  Expected: все тесты PASS.
- [ ] **Step 6: Commit.**
  ```bash
  git add -A
  git commit -m "test: add invariant checks; docs: README, docker-compose for local DB"
  ```
