# План реализации: безопасность и функции MiniBank (этап 4)

> **Для исполнителя:** ОБЯЗАТЕЛЬНЫЙ САБ-СКИЛЛ — superpowers:subagent-driven-development (рекомендуется) или superpowers:executing-plans. Шаги отмечены чекбоксами (`- [ ]`).

**Цель:** вход по email и паролю с подтверждением почты, владение счетами, индивидуальные лимиты на счёт, выписки с фильтрами и экспортом CSV — в API и в веб-клиенте.

**Архитектура:** слоистая, как в этапах 1–3. Домен получает `User`, токен подтверждения и лимиты счёта; Application — `AuthService`, порты (`IUserRepository`, `IPasswordHasher`, `IAccessTokenIssuer`, `IEmailSender`) и проверку владения/лимитов в `AccountsService` и `TransfersService`; Infrastructure — EF-конфигурации, миграции, PBKDF2 и MailKit; Api — JwtBearer, глобальная политика авторизации, rate limiter, forwarded headers. Веб получает слой аутентификации (токен в `sessionStorage`) и переходит со справочника счетов в `localStorage` на `GET /accounts`.

**Стек:** .NET 9, ASP.NET Core, EF Core 9 + Npgsql, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.Extensions.Identity.Core` (только `PasswordHasher`), MailKit, Mailpit (Compose); React 19, TypeScript, Vite, TanStack Query, React Router, Vitest + MSW.

**Спека:** [`docs/specs/2026-11-01-minibank-security-design.md`](../specs/2026-11-01-minibank-security-design.md) — при расхождении плана и спеки прав спека.

## Общие ограничения

Относятся к каждой задаче.

- Сообщения коммитов — **на русском**, префикс и скоуп латиницей (`feat(auth): …`). **Без** строки `Co-Authored-By` и без упоминаний Claude/ИИ в коммитах и файлах. Push не выполнять.
- `TreatWarningsAsErrors` включён (`Directory.Build.props`); xUnit 2.9.2; интеграционные тесты — Testcontainers `postgres:17` через `PostgresFixture`.
- Email: нормализация `trim` + `ToLowerInvariant`, ≤ 254 символов, `unique`. Пароль 8–128 символов, хэш PBKDF2 через `PasswordHasher<TUser>` (без полного Identity).
- Токен подтверждения: 32 случайных байта, base64url; в БД только hex SHA-256 (`token_hash varchar(64)` unique); срок жизни 24 ч; одноразовый. Повторное письмо — не чаще 1/мин на пользователя, ответ всегда 202.
- JWT: HS256, `Jwt:Key` ≥ 32 байт и обязателен (без него API не стартует), `Jwt:Issuer`=`minibank`, `Jwt:Audience`=`minibank-web`, `Jwt:LifetimeMinutes`=60, ClockSkew 30 с, claim `sub` = id пользователя.
- Коды ошибок (`code` в `ProblemDetails`): `email_taken` 409, `invalid_confirmation_token` 400, `invalid_credentials` 401, `email_not_confirmed` 403, `account_limit_reached` 422, `per_operation_limit_exceeded` 422, `daily_limit_exceeded` 422.
- Чужой счёт → **404** во всех эндпоинтах (чтение, выписка, CSV, лимиты, пополнение, списание). Получатель перевода — любой существующий счёт.
- Не более 50 счетов на пользователя (иначе 422 `account_limit_reached`). Валюты RUB/USD/EUR, суммы — целые минимальные единицы (÷100).
- Лимиты: `bigint > 0`; `daily ≥ perOperation`; ≤ потолка `Limits:Max:{валюта}:{PerOperation|Daily}`; начальные значения — `Limits:Default:…`; действуют только на исходящие переводы; сутки — UTC с 00:00; при превышении обоих — `per_operation_limit_exceeded`; идемпотентный повтор лимиты не перепроверяет.
- Выписки: `from` включительно, `to` не включительно, ISO 8601 UTC; CSV — `text/csv; charset=utf-8` с BOM, разделитель `;`, колонки «Дата (UTC)», «ID перевода», «Сумма», «Валюта», «Баланс после», десятичная точка, потоково, потолок 50 000 строк (иначе 400), `Content-Disposition: attachment; filename="statement-<id>.csv"`.
- Rate limit на `/auth/*`: `Auth:RateLimitPerMinute`=10 на IP → 429; IP из `X-Forwarded-For` только от сетей из `ForwardedHeaders:TrustedNetworks`.
- Письма: `IEmailSender` в Application, MailKit в Infrastructure, отправка **после** коммита; сбой SMTP не ломает регистрацию, в лог — без токена и ссылки. Настройки `Smtp:Host/Port/Username/Password/From/UseStartTls`, `Web:BaseUrl`; Mailpit: SMTP `mailpit:1025`, UI `http://localhost:8025`.
- Не входит: refresh-токены, смена/сброс пароля, роли, 2FA, outbox, смена email.

## Что проверить отдельно (Review Focus)

Каждый пункт закреплён тестом в указанной задаче.

1. Ключ идемпотентности чужого пользователя с тем же телом не должен вернуть чужой сохранённый ответ — отпечаток запроса включает `userId` (задача 4).
2. Старые счета без владельца (`user_id IS NULL`, не кассы): с них нельзя списывать (404), получателем они остаются допустимыми, в `GET /accounts` не попадают (задача 4).
3. Граница суток для дневного лимита: проводка в `23:59:59.999` предыдущих суток не считается, в `00:00:00` — считается; параллельные переводы у границы лимита — проходит ровно столько, сколько помещается (задача 5).
4. JWT: просроченный, с чужим `iss`/`aud`, с неверной подписью и `alg=none` — 401; `Jwt:Key` короче 32 байт — API не стартует (задача 3).
5. CSV и фильтры: пустой результат (только заголовок и BOM), больше 50 000 строк → 400, `from ≥ to` → 400, чужой счёт → 404, экранирование не нужно (в строках нет пользовательского текста) (задача 6).

---

## Карта файлов

Новые файлы по слоям (существующие правятся по месту в задачах).

- Domain: `User.cs`, `EmailAddress.cs`, `PasswordPolicy.cs`, `EmailConfirmationToken.cs`, `AccountLimits.cs`; исключения — в `Exceptions.cs`.
- Application: `Auth/AuthService.cs`, `Auth/AuthDtos.cs`, `Auth/ConfirmationTokens.cs`, `Auth/AuthOptions.cs`, `Abstractions/IUserRepository.cs`, `IPasswordHasher.cs`, `IAccessTokenIssuer.cs`, `IEmailSender.cs`; `Limits/LimitPolicy.cs`, `Limits/LimitsOptions.cs`; `Statements/StatementCsv.cs`.
- Infrastructure: `Persistence/UserRepository.cs`, `Persistence/Configurations/UserConfiguration.cs`, `EmailConfirmationTokenConfiguration.cs`, `Security/Pbkdf2PasswordHasher.cs`, `Email/MailKitEmailSender.cs`, `Email/SmtpOptions.cs`, миграции.
- Api: `Auth/JwtOptions.cs`, `Auth/JwtTokenIssuer.cs`, `Auth/ClaimsPrincipalExtensions.cs`, `Controllers/AuthController.cs`, `Controllers/MeController.cs`.
- Тесты: `tests/MiniBank.TestSupport/TestData.cs`, `tests/MiniBank.Api.IntegrationTests/{FakeEmailSender,TestUsers,FakeClock}.cs`.
- Веб: `web/src/features/auth/*`, `web/src/features/accounts/{LimitsCard,useAccounts}.tsx`, `web/src/features/entries/{StatementFilter,downloadCsv}.ts(x)`.

---

### Задача 1: Пользователи в домене и БД

**Файлы:**
- Создать: `src/MiniBank.Domain/{User,EmailAddress,PasswordPolicy,EmailConfirmationToken}.cs`; `src/MiniBank.Infrastructure/Persistence/Configurations/{User,EmailConfirmationToken}Configuration.cs`; миграция `AddUsers`.
- Изменить: `MiniBankDbContext.cs` (добавить `DbSet<User> Users`, `DbSet<EmailConfirmationToken> EmailConfirmationTokens`), `Domain/Exceptions.cs`.
- Тесты: `tests/MiniBank.Domain.Tests/{EmailAddressTests,PasswordPolicyTests,UserTests,EmailConfirmationTokenTests}.cs`, `tests/MiniBank.Infrastructure.Tests/SchemaTests.cs`.

**Интерфейсы:**
- Производит (Domain):
  - `static string EmailAddress.Normalize(string? email)` — бросает `DomainValidationException`.
  - `static void PasswordPolicy.Validate(string? password)` — бросает `DomainValidationException`.
  - `User.Register(string? email, string passwordHash, string? displayName, DateTimeOffset now)`; свойства `Id, Email, PasswordHash, DisplayName, EmailConfirmedAt, CreatedAt, IsEmailConfirmed`; `void ConfirmEmail(DateTimeOffset now)`; `const int MaxDisplayNameLength = 100`.
  - `EmailConfirmationToken.Create(Guid userId, string tokenHash, DateTimeOffset now)` (срок = `now + Lifetime`, `static readonly TimeSpan Lifetime = 24 ч`); свойства `Id, UserId, TokenHash, CreatedAt, ExpiresAt, UsedAt`; `bool IsUsableAt(DateTimeOffset now)`; `void MarkUsed(DateTimeOffset now)`.
- Таблицы: `users` (`email` unique `ix_users_email`), `email_confirmation_tokens` (`token_hash` unique, FK `user_id` → `users` cascade).

- [ ] **Шаг 1: Падающие юнит-тесты домена.**
  - `EmailAddressTests`: `" Alice@Example.COM "` → `"alice@example.com"`; недопустимые (пустая строка, `null`, `a@`, `@b.c`, `a@b`, `a b@c.d`, `a@@b.c`, 255 символов) → `DomainValidationException`; 254 символа — допустимо.
  - `PasswordPolicyTests`: 7 символов → исключение; 8 и 128 — ок; 129 → исключение; пробелы по краям не обрезаются (пароль из 8 пробелов допустим).
  - `UserTests`: `Register` нормализует email и обрезает `displayName`; пустое имя и имя длиннее 100 → исключение; `ConfirmEmail` выставляет `EmailConfirmedAt`.
  - `EmailConfirmationTokenTests`: `ExpiresAt == now + 24 ч`; `IsUsableAt` — `true` до срока, `false` в момент `ExpiresAt` и после, `false` после `MarkUsed`.
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Domain.Tests` — ожидаемо: ошибка компиляции (типов нет).
- [ ] **Шаг 3: Реализовать** сущности по сигнатурам выше. Валидация email — ручная (ровно один `@`, непустые части, точка в домене, без пробелов и управляющих символов, ≤ 254), без `MailAddress`.
- [ ] **Шаг 4: Падающие БД-тесты** в `SchemaTests` (по образцу существующих): вставка двух пользователей с одним email → `PostgresException` с `ConstraintName == "ix_users_email"`; дубль `token_hash` → нарушение уникальности; удаление пользователя каскадно удаляет его токены.
- [ ] **Шаг 5: Реализовать** конфигурации EF (длины: `email` 254, `display_name` 100, `password_hash` 512, `token_hash` 64) и добавить миграцию:
  `dotnet tool restore` и `dotnet ef migrations add AddUsers --project src/MiniBank.Infrastructure --startup-project src/MiniBank.Infrastructure --output-dir Persistence/Migrations`.
- [ ] **Шаг 6: Запустить** `dotnet test tests/MiniBank.Domain.Tests tests/MiniBank.Infrastructure.Tests` — ожидаемо: всё зелёное.
- [ ] **Шаг 7: Коммит** `feat(auth): пользователи и токены подтверждения email в домене и БД`.

---

### Задача 2: Сервис аутентификации и отправка писем

**Файлы:**
- Создать: `Application/Auth/{AuthService,AuthDtos,ConfirmationTokens,AuthOptions}.cs`, `Application/Abstractions/{IUserRepository,IPasswordHasher,IAccessTokenIssuer,IEmailSender}.cs`; `Infrastructure/Persistence/UserRepository.cs`; `Infrastructure/Security/Pbkdf2PasswordHasher.cs`; `Infrastructure/Email/{MailKitEmailSender,SmtpOptions}.cs`.
- Изменить: `Application/Exceptions.cs`, `Application/Abstractions/IUnitOfWork.cs` + `Infrastructure/Persistence/UnitOfWork.cs` (перевод SQLSTATE 23505 в `UniqueViolationException(string constraintName)`), оба `DependencyInjection.cs`, пакеты Infrastructure (`MailKit`, `Microsoft.Extensions.Identity.Core`).
- Тесты: `tests/MiniBank.Infrastructure.Tests/AuthServiceTests.cs` (БД + фейки), юнит-тесты `ConfirmationTokens`.

**Интерфейсы:**
- Потребляет: задача 1 (`User`, `EmailConfirmationToken`, `EmailAddress`, `PasswordPolicy`), `IUnitOfWork.ExecuteInTransactionAsync`.
- Производит (Application):
  - `record AccessToken(string Value, DateTimeOffset ExpiresAt)`; `interface IAccessTokenIssuer { AccessToken Issue(User user, DateTimeOffset now); }`
  - `interface IPasswordHasher { string Hash(string password); bool Verify(string hash, string password); string DummyHash { get; } }`
  - `record EmailMessage(string To, string Subject, string Body)`; `interface IEmailSender { Task SendAsync(EmailMessage message, CancellationToken ct); }`
  - `interface IUserRepository { Task<User?> FindByIdAsync(Guid, ct); Task<User?> FindByEmailAsync(string normalizedEmail, ct); Task<User?> FindByEmailForUpdateAsync(string normalizedEmail, ct); Task<EmailConfirmationToken?> FindTokenByHashAsync(string hash, ct); Task<EmailConfirmationToken?> FindLatestTokenAsync(Guid userId, ct); Task InvalidateOpenTokensAsync(Guid userId, DateTimeOffset now, ct); void Add(User); void Add(EmailConfirmationToken); }`
  - `static (string Raw, string Hash) ConfirmationTokens.Generate()`; `static string ConfirmationTokens.Hash(string raw)` (hex SHA-256 нижним регистром).
  - `class AuthOptions { string WebBaseUrl }` (из `Web:BaseUrl`).
  - `AuthService`: `Task RegisterAsync(string? email, string? password, string? displayName, ct)`, `Task ConfirmEmailAsync(string? token, ct)`, `Task ResendConfirmationAsync(string? email, ct)`, `Task<AccessToken> LoginAsync(string? email, string? password, ct)`.
  - Исключения: `EmailTakenException`, `InvalidConfirmationTokenException`, `InvalidCredentialsException`, `EmailNotConfirmedException`, `UniqueViolationException(string constraintName)`.
  - Письмо: тема «Подтвердите email в MiniBank», тело — обычный текст с ссылкой `{WebBaseUrl}/confirm-email?token={raw}`.

- [ ] **Шаг 1: Падающие тесты `AuthServiceTests`** (реальная БД, `FakeEmailSender` и счётчик-обёртка над хэшером, `FakeClock`):
  - `Register_CreatesUnconfirmedUser_AndSendsOneEmailWithLink`; в БД хранится хэш токена, а не сам токен;
  - `Register_DuplicateEmailDifferentCaseAndSpaces_ThrowsEmailTaken`;
  - `Register_ParallelSameEmail_ExactlyOneSucceeds` (10 параллельных вызовов, остальные — `EmailTakenException`);
  - `Register_SmtpFails_StillSucceeds_AndLogsWithoutToken` (логгер собирает сообщения: ни токена, ни ссылки);
  - `Register_InvalidInput_ThrowsDomainValidation` (плохой email, пароль 7 символов, пустое имя);
  - `ConfirmEmail_ValidToken_Confirms_AndTokenIsSingleUse` (второй вызов → `InvalidConfirmationTokenException`); истёкший (после 24 ч), неизвестный, пустой токен → то же исключение;
  - `Login_Confirmed_ReturnsToken` (фейк-эмиттер возвращает фиксированный `AccessToken`);
  - `Login_WrongPassword_And_UnknownEmail_ThrowSameException`, при неизвестном email `Verify` всё равно вызывается (по `DummyHash`);
  - `Login_Unconfirmed_ThrowsEmailNotConfirmed` (только при верном пароле; при неверном — `InvalidCredentials`);
  - `Resend_Unconfirmed_InvalidatesOldTokens_AndSendsNew` (старый токен после повтора не подтверждает);
  - `Resend_WithinOneMinute_SendsNothing`; `Resend_UnknownOrConfirmed_SendsNothingAndDoesNotThrow`.
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Infrastructure.Tests --filter "FullyQualifiedName~AuthServiceTests"` — ожидаемо: ошибка компиляции.
- [ ] **Шаг 3: Реализовать** порты, `AuthService`, `UserRepository`, `Pbkdf2PasswordHasher` (обёртка над `PasswordHasher<object>`; `DummyHash` вычисляется один раз при создании), перевод 23505 в `UniqueViolationException` в `UnitOfWork`.
  - `RegisterAsync`: проверка ввода → хэш пароля → в транзакции `Add(User)` + `Add(token)`; `UniqueViolationException` с `ix_users_email` → `EmailTakenException`; письмо — после коммита в `try/catch`, ошибка пишется в лог без токена.
  - `ResendConfirmationAsync`: в транзакции `FindByEmailForUpdateAsync` (сериализует повторные запросы), проверки «есть, не подтверждён, последний токен старше 60 с», затем `InvalidateOpenTokensAsync` и новый токен.
  - `LoginAsync`: нормализация email; для неизвестного email проверка по `DummyHash`.
- [ ] **Шаг 4: Реализовать `MailKitEmailSender`** (`SmtpClient`, `SecureSocketOptions.StartTls` при `UseStartTls`, иначе `None`; аутентификация только если задан `Username`; текстовое тело UTF-8) и `SmtpOptions`. Зарегистрировать в `AddInfrastructure(connectionString, configuration)`: `Smtp` и `Web` берутся из конфигурации, `IPasswordHasher` и `IEmailSender` — scoped/singleton. Отдельного теста на MailKit нет — он проверяется смоуком в задаче 9.
- [ ] **Шаг 5: Запустить** `dotnet test tests/MiniBank.Infrastructure.Tests tests/MiniBank.Domain.Tests` — ожидаемо: всё зелёное.
- [ ] **Шаг 6: Коммит** `feat(auth): регистрация, подтверждение email, повторное письмо и вход в сервисе`.

---

### Задача 3: HTTP-слой аутентификации: JWT, `/auth/*`, `/me`, rate limiter

**Файлы:**
- Создать: `Api/Auth/{JwtOptions,JwtTokenIssuer,ClaimsPrincipalExtensions}.cs`, `Api/Controllers/{AuthController,MeController}.cs`; `tests/MiniBank.Api.IntegrationTests/{FakeEmailSender,TestUsers}.cs`, `AuthEndpointsTests.cs`.
- Изменить: `Api/Program.cs`, `Api/Contracts.cs`, `Errors/ExceptionMapper.cs`, `ProblemDetailsExceptionHandler.cs`, `appsettings.json`, `appsettings.Development.json`, `ApiFactory.cs`, `MiniBank.Api.csproj` (`Microsoft.AspNetCore.Authentication.JwtBearer`), `README.md` не трогать (задача 9).

**Интерфейсы:**
- Потребляет: `AuthService` и `IAccessTokenIssuer` (задача 2).
- Производит:
  - `static Guid ClaimsPrincipalExtensions.GetUserId(this ClaimsPrincipal)` (claim `sub`; `MapInboundClaims = false`).
  - `ExceptionMapper.ToErrorCode(Exception) : string?` и поле `code` в `ProblemDetails.Extensions`; соответствие статусов — по списку кодов из «Общих ограничений».
  - Тестовая инфраструктура: `FakeEmailSender : IEmailSender` (синглтон в фабрике; `IReadOnlyList<EmailMessage> Sent`, `string LastConfirmationToken(string email)` вытаскивает токен из ссылки); `ApiFactory` по умолчанию подставляет `Jwt:Key` (32+ байт), `Web:BaseUrl`, `Auth:RateLimitPerMinute=1000` и `FakeEmailSender`; `static class TestUsers { Task<string> RegisterAndConfirmAsync(ApiFactory, string email, string password = "correct-horse-1", string name = "Alice"); Task<HttpClient> SignInAsync(ApiFactory, string email = null, …) }` — клиент с заголовком `Authorization: Bearer`.
  - Эндпоинты из спеки: `POST /auth/register` (202), `POST /auth/confirm-email` (204), `POST /auth/resend-confirmation` (202), `POST /auth/login` (200 `{accessToken, expiresAt}`), `GET /me` (`{id, email, displayName}`); все `/auth/*` — `[AllowAnonymous]`, `/me` — `[Authorize]`.
  - Порядок middleware: `UseForwardedHeaders` → `UseExceptionHandler` → `UseStatusCodePages` (чтобы 401/403/429 были `application/problem+json`) → `UseRouting` → `UseRateLimiter` → `UseAuthentication` → `UseAuthorization`.
  - Rate limiter: политика `"auth"`, окно 1 мин, лимит `Auth:RateLimitPerMinute` (по умолчанию 10) на IP, статус 429; `ForwardedHeadersOptions`: `KnownNetworks` — из `ForwardedHeaders:TrustedNetworks` (CIDR), `KnownProxies` очищены; без доверенных сетей заголовок игнорируется.
  - Fallback-политика пока **не** включается (задача 4).

- [ ] **Шаг 1: Падающие тесты `AuthEndpointsTests`:**
  - полный сценарий: register → 202 и одно письмо → login до подтверждения 403 `email_not_confirmed` → confirm-email 204 → login 200 с `accessToken` → `GET /me` с токеном возвращает email и имя;
  - `Register_DuplicateEmail_Returns409WithCode`; `Register_BadInput_Returns400` (email, пароль 7, пустое имя);
  - `ConfirmEmail_BadToken_Returns400WithCode`; повторное использование токена — тот же ответ;
  - `Login_WrongPassword_And_UnknownEmail_ReturnIdentical401` (одинаковые статус, `code`, `title`, `detail`);
  - `Resend_AlwaysReturns202` (для существующего, неизвестного и подтверждённого email; письмо уходит только первому);
  - `Me_WithoutToken_Returns401ProblemJson`;
  - JWT (Review Focus 4): токены, собранные в тесте вручную тем же ключом (`JsonWebTokenHandler`): с `exp` на минуту в прошлом (больше `ClockSkew`), с чужими `iss` и `aud`, подписанные другим ключом, и с `alg=none` → 401;
  - `Startup_WithoutJwtKey_Throws` и `Startup_WithShortJwtKey_Throws` (`ApiFactory` с пустым/коротким ключом — создание клиента бросает исключение);
  - rate limit: фабрика с `Auth:RateLimitPerMinute=3`, четвёртый `POST /auth/login` → 429 `problem+json`; смена `X-Forwarded-For` без доверенной сети лимит не обходит; с доверенной сетью (`IStartupFilter`, выставляющий `RemoteIpAddress=10.0.0.1`, `ForwardedHeaders:TrustedNetworks=10.0.0.0/8`) разные `X-Forwarded-For` считаются разными клиентами.
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~AuthEndpointsTests"` — ожидаемо: FAIL (эндпоинтов нет).
- [ ] **Шаг 3: Реализовать** `JwtOptions` с валидацией при старте (`Key` обязателен, `Encoding.UTF8.GetByteCount ≥ 32`), `JwtTokenIssuer : IAccessTokenIssuer` (HS256, claim `sub`, `iss`, `aud`, `exp` = `now + LifetimeMinutes`), контроллеры, регистрацию `AddAuthentication().AddJwtBearer(...)` (`ValidateIssuer/Audience/Lifetime/IssuerSigningKey`, `ClockSkew=30 с`, `RequireSignedTokens`, `MapInboundClaims=false`), rate limiter и forwarded headers, `ExceptionMapper`/handler с кодами. `appsettings.json` — значения по умолчанию (`Jwt:Issuer`, `Audience`, `LifetimeMinutes`, `Auth:RateLimitPerMinute`); `appsettings.Development.json` — dev-ключ JWT и Mailpit (`localhost:1025`, `Web:BaseUrl=http://localhost:5173`).
- [ ] **Шаг 4: Swagger:** схема безопасности `Bearer` (описание по документации Swashbuckle 10), чтобы Swagger UI мог подставлять токен.
- [ ] **Шаг 5: Запустить** `dotnet test` целиком — ожидаемо: всё зелёное (старые тесты не затронуты — fallback-политики ещё нет).
- [ ] **Шаг 6: Коммит** `feat(auth): JWT, эндпоинты /auth и /me, rate limiter и forwarded headers`.

---

### Задача 4: Владение счетами и глобальная авторизация

**Файлы:**
- Создать: миграция `AddAccountOwner`; `tests/MiniBank.TestSupport/TestData.cs`; `tests/MiniBank.Api.IntegrationTests/OwnershipTests.cs`.
- Изменить: `Domain/Account.cs`, `Infrastructure/.../AccountConfiguration.cs`, `AccountRepository.cs`, `Application/{AccountsService,TransfersService,Dtos}.cs`, `Abstractions/IAccountRepository.cs`, `Api/Controllers/{AccountsController,TransfersController}.cs`, `Api/Contracts.cs`, `Api/Program.cs`; все существующие тесты, открывающие счета или вызывающие API.

**Интерфейсы:**
- Потребляет: `GetUserId()` (задача 3), `IUserRepository.FindByIdAsync` (для имени владельца).
- Производит:
  - `Account.UserId: Guid?`; `Account.Open(string? owner, string? currency, DateTimeOffset now, bool isSystem = false, Guid? id = null, Guid? userId = null)`.
  - БД: `accounts.user_id uuid NULL` FK → `users` (restrict) и `CHECK (is_system OR user_id IS NOT NULL)` как `NOT VALID` — правится вручную в миграции (`migrationBuilder.Sql("ALTER TABLE accounts ADD CONSTRAINT ck_accounts_owner_required CHECK (is_system OR user_id IS NOT NULL) NOT VALID")` вместо сгенерированного `AddCheckConstraint`; в модели ограничение остаётся, чтобы следующий `migrations add` не давал diff).
  - `IAccountRepository`: `Task<IReadOnlyList<Account>> ListByUserAsync(Guid userId, ct)` (старые первыми), `Task<int> CountByUserAsync(Guid userId, ct)`; `IUserRepository` дополняется `Task<User?> FindByIdForUpdateAsync(Guid id, ct)` (`SELECT … FOR UPDATE`).
  - `AccountsService`: `CreateAsync(Guid userId, string? currency, ct)` (владелец = `DisplayName` пользователя; в транзакции с блокировкой строки пользователя `FindByIdForUpdate`, проверка ≤ 50, иначе `AccountLimitReachedException`), `ListAsync(Guid userId, ct)`, `GetAsync(Guid userId, Guid id, ct)`, `GetEntriesAsync(Guid userId, Guid id, page, pageSize, ct)`; недоступный счёт → `AccountNotFoundException`.
  - `TransfersService.TransferAsync(Guid userId, string? key, Guid fromId, Guid toId, …)`, `DepositAsync(Guid userId, string? key, Guid accountId, …)`; отпечаток запроса включает `userId`: `$"transfer|{userId}|{fromId}|…"`, `$"deposit|{userId}|…"`; счёт-источник и счёт пополнения проверяются на владельца после блокировки (404, если `UserId != userId`).
  - `TestData` (TestSupport; добавить ссылки на Domain и Infrastructure): `Task<User> SeedUserAsync(MiniBankDbContext db, string? email = null)`, `Account OpenFor(Guid userId, string currency = "RUB", string owner = "Alice")` — **единая точка** создания счетов в тестах (в задаче 5 сюда добавятся лимиты).
  - API: `POST /accounts {currency}` → 201; `GET /accounts` → `AccountDto[]`; всё остальное — под `FallbackPolicy = RequireAuthenticatedUser`, `[AllowAnonymous]` явно только на `/auth/*` (кроме `/me`) и `.AllowAnonymous()` на `MapHealthChecks("/health")`.

- [ ] **Шаг 1: Падающие тесты `OwnershipTests`** (два пользователя Alice и Bob через `TestUsers`):
  - без токена `GET /accounts`, `POST /accounts`, `POST /transfers`, `GET /accounts/{id}` → 401; `/health` и `/auth/login` без токена доступны; Swagger при `Swagger:Enabled` доступен без токена;
  - `POST /accounts {currency}` создаёт счёт с `owner == displayName`; `owner` в теле игнорируется;
  - `GET /accounts` отдаёт только свои счета; счёт Боба в списке Алисы отсутствует;
  - по одному тесту на каждый эндпоинт, где счёт чужой → 404: `GET /accounts/{id}`, `GET /accounts/{id}/entries`, `POST /accounts/{id}/deposit`, `POST /transfers` (чужой источник); получатель чужого счёта — 200;
  - `Create_51stAccount_Returns422AccountLimitReached`; 50 параллельных создания не превышают лимит (60 параллельных → ровно 50 успешных);
  - Review Focus 1: Боб, повторивший `Idempotency-Key` Алисы с тем же телом (он знает её `fromAccountId`), получает 404, а не её ответ;
  - Review Focus 2: старый счёт без владельца (в тесте: `ALTER TABLE accounts DROP CONSTRAINT ck_accounts_owner_required`, затем `UPDATE accounts SET user_id = NULL` для счёта Алисы): списание с него → 404, перевод на него — 200, в `GET /accounts` не попадает;
  - `Database_RejectsNewUserlessNonSystemAccount` в `SchemaTests` (вставка не-системного счёта без `user_id` → нарушение `ck_accounts_owner_required`).
- [ ] **Шаг 2: Запустить** `dotnet test tests/MiniBank.Api.IntegrationTests --filter "FullyQualifiedName~OwnershipTests"` — ожидаемо: FAIL.
- [ ] **Шаг 3: Реализовать** изменения домена, репозитория, сервисов, контроллеров (каждый берёт `User.GetUserId()` и передаёт в сервис), миграции и fallback-политики. `AccountLimitReachedException` → 422 `account_limit_reached`. Контракт `CreateAccountRequest(string Currency)`.
- [ ] **Шаг 4: Обновить существующие тесты:** все места создания счетов в `tests/*` перевести на `TestData.OpenFor` (для БД-тестов — после `SeedUserAsync`); API-тесты (`AccountsEndpointsTests`, `TransfersEndpointsTests`, `IdempotencyEndpointsTests`, `ConcurrencyTests`, `InvariantsTests`, `ErrorLoggingTests`, `HealthAndSwaggerTests`) — на авторизованный клиент `TestUsers.SignInAsync`; тесты, где владелец задавался телом `{owner, currency}`, — на `{currency}` и ожидание `owner == displayName`.
- [ ] **Шаг 5: Запустить** `dotnet test` целиком — ожидаемо: всё зелёное.
- [ ] **Шаг 6: Коммит** `feat(accounts): владелец счёта, список своих счетов и глобальная авторизация`.

---

### Задача 5: Лимиты счёта

**Файлы:**
- Создать: `Domain/AccountLimits.cs`, `Application/Limits/{LimitPolicy,LimitsOptions}.cs`, миграция `AddAccountLimits`; `tests/MiniBank.Api.IntegrationTests/{LimitsEndpointsTests,FakeClock}.cs`; юнит-тесты `LimitPolicyTests`, `AccountLimitsTests`.
- Изменить: `Domain/{Account,Exceptions}.cs`, `AccountConfiguration.cs`, `Application/{AccountsService,TransfersService,Dtos}.cs`, `Abstractions/ILedgerQuery.cs`, `LedgerQuery.cs`, `Api/Controllers/AccountsController.cs`, `Api/Contracts.cs`, `appsettings.json`, `ExceptionMapper.cs`, `TestData.cs`.

**Интерфейсы:**
- Потребляет: `AccountsService`/`TransfersService` с `userId` (задача 4), `TimeProvider`.
- Производит:
  - `record AccountLimits(long PerOperation, long Daily)` с проверкой `> 0` и `Daily ≥ PerOperation` (`DomainValidationException`).
  - `Account.PerOperationLimit: long?`, `Account.DailyLimit: long?`; `Account.Open(…, AccountLimits? limits = null)` (для не-кассы лимиты обязательны); `void Account.SetLimits(AccountLimits limits)`; `void Account.EnsureCanSpend(long amount, long spentToday)` — бросает `PerOperationLimitExceededException`, затем `DailyLimitExceededException` (оба — `DomainException`; у касс проверка пропускается).
  - `LimitPolicy` (из `Limits:Default:{валюта}:{PerOperation|Daily}` и `Limits:Max:…`; проверка при старте: `Default ≤ Max` и `Default.Daily ≥ Default.PerOperation`): `AccountLimits Default(string currency)`, `AccountLimits Max(string currency)`, `void EnsureWithinMax(string currency, AccountLimits limits)`.
  - `ILedgerQuery.GetSpentSinceAsync(Guid accountId, DateTimeOffset since, ct) : Task<long>` — сумма модулей отрицательных проводок счёта с `created_at >= since`.
  - `record LimitsDto(long PerOperation, long Daily, long SpentToday, long RemainingToday, long MaxPerOperation, long MaxDaily)`; `AccountsService.GetLimitsAsync(Guid userId, Guid id, ct)`, `SetLimitsAsync(Guid userId, Guid id, long perOperation, long daily, ct)`.
  - API: `GET /accounts/{id}/limits`, `PUT /accounts/{id}/limits` (`{perOperation, daily}`); значения по умолчанию в `appsettings.json` — RUB: PerOperation 10 000 000, Daily 30 000 000, Max 100 000 000 / 300 000 000; USD и EUR: 100 000 / 300 000, Max 1 000 000 / 3 000 000.
  - БД: `per_operation_limit bigint NULL`, `daily_limit bigint NULL`, `CHECK (per_operation_limit > 0)`, `CHECK (daily_limit > 0)`, `CHECK (is_system OR (per_operation_limit IS NOT NULL AND daily_limit IS NOT NULL))` — последний `NOT VALID` вручную, как в задаче 4.
  - `FakeClock : TimeProvider` в тестах (`SetUtcNow`), подставляется в `ApiFactory` через `ConfigureTestServices`.

- [ ] **Шаг 1: Падающие юнит-тесты:** `AccountLimitsTests` (0, отрицательные, `Daily < PerOperation`); `Account.EnsureCanSpend` — граничные значения (сумма ровно на лимите проходит, на 1 больше — нет; `spentToday + amount == Daily` проходит, на 1 больше — `DailyLimitExceeded`; превышены оба → `PerOperationLimitExceeded`; касса не проверяется); `LimitPolicyTests` (значения по умолчанию, `EnsureWithinMax` отвергает превышение потолка, конфигурация с `Default > Max` бросает при старте).
- [ ] **Шаг 2: Падающие тесты `LimitsEndpointsTests`:**
  - новый счёт получает значения по умолчанию своей валюты; `GET /limits` возвращает `spentToday`, `remainingToday`, потолки;
  - `PUT /limits` обновляет; невалидные (`0`, `-1`, больше потолка, `daily < perOperation`) → 400; чужой счёт → 404 (и для `GET`, и для `PUT`);
  - перевод выше `perOperation` → 422 `per_operation_limit_exceeded`; несколько переводов, превышающих `daily` → 422 `daily_limit_exceeded`; оба превышены → `per_operation_limit_exceeded`;
  - лимиты действуют только на исходящие: пополнение и приём переводов их не расходуют; пополнение больше `perOperation` проходит;
  - идемпотентный повтор успешного перевода после исчерпания лимита возвращает сохранённый ответ (200 + `Idempotent-Replayed`);
  - Review Focus 3: с `FakeClock` — проводка в `23:59:59.999` вчерашних суток не входит в `spentToday`, в `00:00:00` сегодняшних — входит; 20 параллельных переводов по `L/10 + 1` при `daily = L` → проходят ровно столько, сколько помещается, остальные 422 `daily_limit_exceeded`, итоговый расход ≤ `daily`;
  - `Database_RejectsNonPositiveLimits` в `SchemaTests`.
- [ ] **Шаг 3: Запустить** `dotnet test --filter "FullyQualifiedName~Limit"` — ожидаемо: FAIL.
- [ ] **Шаг 4: Реализовать.** Проверка в `TransfersService.TransferAsync` внутри колбэка идемпотентности **после** `FindForUpdateAsync` и до `Transfer.Execute`: `since = начало текущих суток UTC по clock`, `spent = ledger.GetSpentSinceAsync(fromId, since)`, `from.EnsureCanSpend(amount, spent)`. `ExceptionMapper`: оба исключения → 422 с кодами из «Общих ограничений». `CreateAsync` задаёт `LimitPolicy.Default(currency)`. В `TestData.OpenFor` добавить лимиты по умолчанию.
- [ ] **Шаг 5: Миграция** `AddAccountLimits` (для существующих несистемных счетов лимиты остаются `NULL`, ограничение — `NOT VALID`); `dotnet test` целиком — ожидаемо: зелёное.
- [ ] **Шаг 6: Коммит** `feat(limits): индивидуальные лимиты счёта и проверка при переводе`.

---

### Задача 6: Выписки с фильтрами и экспорт CSV

**Файлы:**
- Создать: `Application/Statements/{EntryFilter,StatementCsv}.cs`; `tests/MiniBank.Api.IntegrationTests/StatementEndpointsTests.cs`; юнит-тест `StatementCsvTests`.
- Изменить: `Abstractions/ILedgerQuery.cs`, `Infrastructure/Persistence/LedgerQuery.cs`, `Application/AccountsService.cs`, `Api/Controllers/AccountsController.cs`.

**Интерфейсы:**
- Потребляет: `AccountsService.GetAsync(userId, id)` как проверку владения.
- Производит:
  - `enum EntryDirection { In, Out }`; `record EntryFilter(DateTimeOffset? From, DateTimeOffset? To, EntryDirection? Direction)`; валидация: `From < To` при обоих заданных, иначе `DomainValidationException`.
  - `ILedgerQuery.GetEntriesAsync(Guid accountId, EntryFilter filter, int page, int pageSize, ct)` (замена старой сигнатуры), `Task<int> CountAsync(Guid accountId, EntryFilter filter, int cap, ct)` (считает не более `cap + 1`), `IAsyncEnumerable<LedgerEntry> StreamAsync(Guid accountId, EntryFilter filter, ct)` (порядок как у постраничной выдачи: новые первыми, затем `id`).
  - `const int StatementCsv.MaxRows = 50_000`; `static string StatementCsv.FormatAmount(long minorUnits)` (`123456` → `1234.56`, `-5` → `-0.05`, `0` → `0.00`); `static async Task StatementCsv.WriteAsync(TextWriter, string currency, IAsyncEnumerable<LedgerEntry>, ct)` — заголовок и строки через `;`.
  - `AccountsService.GetEntriesAsync(Guid userId, Guid id, EntryFilter filter, int page, int pageSize, ct)`; `AccountsService.OpenStatementAsync(Guid userId, Guid id, EntryFilter filter, ct) : Task<(string Currency, IAsyncEnumerable<LedgerEntry> Rows)>` — проверка владения, фильтра и потолка 50 000 **до** начала ответа (иначе нельзя вернуть 400).
  - API: `GET /accounts/{id}/entries?from&to&direction=in|out&page&pageSize`; `GET /accounts/{id}/entries.csv?from&to&direction`; ошибки разбора `from`/`to`/`direction` → 400; ответ CSV: `Content-Type: text/csv; charset=utf-8`, BOM `EF BB BF`, `Content-Disposition: attachment; filename="statement-<id>.csv"`; пишется потоком в `Response.Body`.

- [ ] **Шаг 1: Падающие тесты.**
  - `StatementCsvTests`: `FormatAmount` (`123456`, `-5`, `0`, `100`, `long.MinValue + 1` без исключений); `WriteAsync` — заголовок «Дата (UTC);ID перевода;Сумма;Валюта;Баланс после» и строка с датой в формате `yyyy-MM-dd HH:mm:ss`.
  - `StatementEndpointsTests`: фильтр `from` включительно / `to` не включительно (проводки ровно на границах); `direction=in` только зачисления, `out` только списания; сочетание фильтров с пагинацией; неверные `from`/`to`/`direction` и `from ≥ to` → 400 (Review Focus 5); чужой счёт → 404 для `entries` и `entries.csv`;
  - CSV: первые три байта тела — BOM; заголовок на русском; число строк равно числу проводок; сумма отрицательна для списаний; `Content-Disposition` с `statement-<id>.csv`; пустая выписка — только заголовок; фильтр применяется и к CSV;
  - потолок: фабрика с тестовым переопределением `MaxRows` (параметр конфигурации `Statements:MaxCsvRows`, по умолчанию 50 000) = 3 — при 4 проводках 400, при 3 — 200.
- [ ] **Шаг 2: Запустить** `dotnet test --filter "FullyQualifiedName~Statement"` — ожидаемо: FAIL.
- [ ] **Шаг 3: Реализовать** фильтр в `LedgerQuery` (`created_at >= from`, `created_at < to`, `amount > 0` / `amount < 0`; индекс `(account_id, created_at)` уже есть), CSV-вывод и контроллер. Все остальные вызовы старой сигнатуры `GetEntriesAsync` перевести на новую.
- [ ] **Шаг 4: Запустить** `dotnet test` целиком — ожидаемо: зелёное.
- [ ] **Шаг 5: Коммит** `feat(statements): фильтры выписки и экспорт в CSV`.

---

### Задача 7: Веб — вход, регистрация, подтверждение email

**Файлы:**
- Создать: `web/src/features/auth/{session.ts,AuthProvider.tsx,RequireAuth.tsx,LoginPage.tsx,RegisterPage.tsx,ConfirmEmailPage.tsx,index.ts,auth.test.tsx}`.
- Изменить: `web/src/api/{client.ts,errors.ts,types.ts}`, `web/src/app/{router,providers}.tsx`, `web/src/shared/ui/AppShell.tsx`, `web/src/test/render.tsx`, `web/src/api/schema.d.ts` (`npm run gen:api` при запущенном API).

**Интерфейсы:**
- Потребляет: эндпоинты `/auth/*` и `/me` (задача 3).
- Производит:
  - `session.ts`: `getToken(): string | null`, `setToken(token: string): void`, `clearToken(): void`, `subscribe(listener): () => void` (хранилище — `sessionStorage`, ключ `minibank.token`).
  - `useAuth(): { isAuthenticated: boolean; signIn(token: string): void; signOut(): void }`; `<AuthProvider>`.
  - `api.register({email, password, displayName})`, `api.login({email, password}): Promise<{accessToken, expiresAt}>`, `api.confirmEmail(token)`, `api.resendConfirmation(email)`, `api.getMe(): Promise<{id, email, displayName}>`.
  - `apiRequest` добавляет `Authorization: Bearer <token>`, если токен есть; на 401 (кроме запросов `/auth/*`) вызывает `clearToken()`; `ProblemBody.code?: string` читается из ответа и сохраняется в `ApiError`.
  - Маршруты: `/login`, `/register`, `/confirm-email` — публичные; остальные — внутри `<RequireAuth>` (редирект на `/login?next=<путь>`); после входа переход на `next` (только относительный путь внутри приложения).
  - `describeError` знает коды `email_taken`, `invalid_confirmation_token`, `invalid_credentials`, `email_not_confirmed` и 429 («Слишком много попыток, подождите минуту»).

- [ ] **Шаг 1: Падающие тесты `auth.test.tsx`** (Vitest + MSW, `renderApp`):
  - без токена переход на `/` ведёт на `/login?next=%2F`; после входа — возврат на исходную страницу;
  - успешный вход сохраняет токен в `sessionStorage` и показывает «Мои счета»; неверная пара — сообщение об ошибке без перехода; `email_not_confirmed` — подсказка и кнопка «Отправить письмо ещё раз» (вызывает `resend-confirmation`);
  - регистрация: валидация на клиенте (email, пароль < 8, пустое имя), успех показывает «Проверьте почту»; `email_taken` — ошибка у поля email;
  - `/confirm-email?token=…`: успех → «Email подтверждён» и ссылка на вход; `invalid_confirmation_token` → сообщение и форма повторной отправки;
  - любой запрос с 401 сбрасывает токен и ведёт на `/login`; запрос получает заголовок `Authorization`;
  - «Выйти» в шапке чистит токен; в шапке — имя из `/me`; `next` с внешним адресом (`https://evil.example`) игнорируется.
- [ ] **Шаг 2: Запустить** `cd web && npm test -- --run src/features/auth` — ожидаемо: FAIL.
- [ ] **Шаг 3: Реализовать** сессию, провайдер, страницы (компоненты `TextField`, `Button`, `ErrorNotice` из `shared/ui`; стили — CSS-модули в духе существующих), маршрутизацию, обновление `describeError`.
- [ ] **Шаг 4: Перегенерировать типы** (`npm run gen:api`, API запущен локально) и убедиться, что `npm run build` проходит.
- [ ] **Шаг 5: Запустить** `npm test -- --run` и `npm run build` — ожидаемо: зелёное.
- [ ] **Шаг 6: Коммит** `feat(web): вход, регистрация и подтверждение email`.

---

### Задача 8: Веб — счета с сервера, лимиты, фильтр выписки, CSV

**Файлы:**
- Создать: `web/src/features/accounts/{LimitsCard.tsx,LimitsCard.module.css,useAccounts.ts}`, `web/src/features/entries/{StatementFilter.tsx,downloadCsv.ts}`.
- Изменить: `web/src/features/accounts/{HomePage,AccountGate,CreateAccountForm,AccountPage,index,queries}.ts(x)`, `web/src/features/entries/{EntriesPage,queries}.ts(x)`, `web/src/api/{client,types}.ts`, `web/src/app/providers.tsx`, `web/src/test/render.tsx`, схема типов; тесты `accounts.test.tsx`, `entries.test.tsx`, `App.test.tsx`.
- Удалить: `directory.ts`, `directory.test.ts`, `DirectoryProvider.tsx`, `AddAccountForm.tsx`.

**Интерфейсы:**
- Потребляет: `GET /accounts`, `POST /accounts {currency}`, `GET/PUT /accounts/{id}/limits`, `GET /accounts/{id}/entries` с фильтрами и `entries.csv` (задачи 4–6), токен из задачи 7.
- Производит:
  - `api.listAccounts(): Promise<Account[]>`; `api.createAccount({ currency })`; `api.getLimits(id)`, `api.putLimits(id, { perOperation: bigint, daily: bigint })`; тип `Limits { perOperation, daily, spentToday, remainingToday, maxPerOperation, maxDaily: bigint }`.
  - `api.getEntries(id, { from?, to?, direction? }, page, pageSize)`; `api.downloadStatementCsv(id, filter): Promise<Blob>` (fetch с заголовком `Authorization`; ответ — blob).
  - `accountKeys.list()` и `useAccounts()`; `useLimits(id)`, `usePutLimits(id)`; создание счёта инвалидирует `accountKeys.list()`.
  - `downloadCsv(blob: Blob, filename: string): void` (временная ссылка `URL.createObjectURL` и `<a download>`).
  - Фильтр выписки: поля «С» и «По» (`input type="date"`, даты по UTC; «По» включительно → в запрос уходит начало следующего дня), выбор «Все / Зачисления / Списания», кнопка «Скачать CSV» (файл `statement-<id>.csv`), недопустимый диапазон (С > По) — сообщение у поля без запроса.

- [ ] **Шаг 1: Падающие тесты:**
  - `HomePage` показывает счета из `GET /accounts`; пусто — «Откройте первый счёт»; localStorage-справочника и формы «Добавить по ID» больше нет;
  - `CreateAccountForm`: только выбор валюты, отправляет `{currency}`; ошибка `account_limit_reached` показывается понятным текстом;
  - `AccountGate`: 404 — «Счёт не найден» без кнопки «Убрать из списка»;
  - `LimitsCard`: показывает лимиты и «Потрачено сегодня / Осталось»; сохранение вызывает `PUT` с суммами в минимальных единицах; значение больше потолка — ошибка у поля без запроса; ответ 400 показывается;
  - перевод с 422 `per_operation_limit_exceeded` / `daily_limit_exceeded` показывает понятный текст на странице перевода;
  - `EntriesPage`: фильтры попадают в запрос (`from`, `to`, `direction`), смена фильтра сбрасывает постраничную подгрузку; «Скачать CSV» запрашивает `entries.csv` с теми же фильтрами и вызывает `downloadCsv`; ошибка 400 (потолок) показывается уведомлением.
- [ ] **Шаг 2: Запустить** `cd web && npm test -- --run` — ожидаемо: FAIL в затронутых тестах.
- [ ] **Шаг 3: Реализовать**, удалив справочник и всё, что от него зависело (`DirectoryProvider` из `AppProviders`, параметр `directory` из `renderApp`).
- [ ] **Шаг 4: Запустить** `npm test -- --run` и `npm run build` — ожидаемо: зелёное.
- [ ] **Шаг 5: Коммит** `feat(web): счета с сервера, лимиты, фильтр выписки и скачивание CSV`.

---

### Задача 9: Compose, Mailpit, nginx, смоук, CI и документация

**Файлы:**
- Изменить: `docker-compose.yml`, `web/nginx.conf`, `scripts/smoke.sh`, `.github/workflows/ci.yml`, `README.md`, `src/MiniBank.Api/appsettings.json` (только если не хватает значений по умолчанию).

**Интерфейсы:**
- Потребляет: всё предыдущее.
- Производит (Compose):
  - сервис `mailpit` (`axllent/mailpit`, UI `8025:8025`, SMTP `1025` только внутри сети);
  - `api`: `Jwt__Key` (`${JWT_KEY:-…}` с демо-значением не короче 32 байт и комментарием «только для локального запуска»), `Smtp__Host=mailpit`, `Smtp__Port=1025`, `Smtp__UseStartTls=false`, `Smtp__From=noreply@minibank.local`, `Web__BaseUrl=http://localhost:8080`, `ForwardedHeaders__TrustedNetworks__0=172.16.0.0/12`; `depends_on: mailpit: service_started`;
  - nginx: `proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;` и `X-Forwarded-Proto $scheme`.
  - Смоук: регистрация → письмо из `GET http://localhost:8025/api/v1/messages` и `/api/v1/message/{id}` (токен вытаскивается из текста) → `confirm-email` → вход → счёт `{currency}` → пополнение → перевод между двумя счетами → `GET /accounts/{id}/entries.csv` (BOM, заголовок, ровно ожидаемое число строк) → запрос без токена даёт 401; `X-Forwarded-For` извне не влияет на rate limit.

- [ ] **Шаг 1: Обновить** `docker-compose.yml`, `nginx.conf` и README (разделы «Быстрый старт» — порт 8025 и пояснение про Mailpit, «Режим разработки» — Mailpit и dev-ключ, «Web», «API» — таблица эндпоинтов и коды ошибок, раздел «Безопасность»: вход, владение, лимиты, выписки, статус этапа 4 ✅).
- [ ] **Шаг 2: Переписать `scripts/smoke.sh`** по сценарию выше (функции `expect_status`, `fail` сохранить; вспомогательный `json_field` на `sed`, как в текущем скрипте).
- [ ] **Шаг 3: Проверить локально** `docker compose up -d --build --wait` и `bash scripts/smoke.sh` — ожидаемо: все проверки `ok:`, код возврата 0; затем `docker compose down -v`.
- [ ] **Шаг 4: CI:** в job `stack` уже выполняются `up --wait` и `smoke.sh`; убедиться, что нужные порты (8025) не конфликтуют, при падении `docker compose logs` сохраняется; при необходимости добавить логи `mailpit`.
- [ ] **Шаг 5: Полная проверка:** `dotnet test` и `cd web && npm test -- --run && npm run build` — ожидаемо: зелёное.
- [ ] **Шаг 6: Коммит** `feat(docker): Mailpit, заголовки прокси и смоук-сценарий входа`; отдельным коммитом `docs: README про вход, лимиты и выписки (этап 4)`.

---

## Самопроверка плана по спеке

- Покрытие: пользователи, токены и письма — задачи 1–2; JWT, `/auth/*`, `/me`, rate limiter, forwarded headers, глобальная авторизация, Swagger Bearer — 3–4; владение, `GET /accounts`, лимит 50 счетов, изменение `POST /accounts` — 4; лимиты — 5; выписки и CSV — 6; веб — 7–8; Mailpit, nginx, смоук, CI, README — 9.
- Поправка к спеке: индекс `(account_id, created_at)` на `ledger_entries` уже существует — новая миграция для него не нужна.
- Решения, не зафиксированные спекой: порядок строк CSV такой же, как у выписки (новые первыми); дневные значения лимитов по умолчанию и потолки заданы в задаче 5; блокировка строки пользователя сериализует создание счетов и повторную отправку письма.
