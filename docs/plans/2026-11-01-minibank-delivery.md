# MiniBank: поставка (этап 3) — план реализации

> **Для исполнителей:** план реализуется по задачам; шаги отмечены чекбоксами (`- [ ]`).

**Goal:** `docker compose up --build` поднимает весь стек (PostgreSQL + API + веб за nginx), CI на каждый push и PR прогоняет тесты, собирает образы и проверяет живой стек.

**Architecture:** В API добавляются `GET /health` (проверка БД) и флаг `Swagger:Enabled`. Два Dockerfile (API на aspnet:9.0, веб на nginx со статикой и прокси `/api/` → API). Compose связывает три сервиса. Workflow GitHub Actions из трёх job-ов: `backend`, `web`, `stack`. Дымовую проверку стека делает один скрипт `scripts/smoke.sh`, его же запускают CI и разработчик локально.

**Tech Stack:** .NET 9 / ASP.NET Core, xUnit + Testcontainers, Docker / Compose, nginx:alpine, Node 24, GitHub Actions.

**Spec:** [2026-11-01-minibank-delivery-design.md](../specs/2026-11-01-minibank-delivery-design.md)

## Global Constraints

- Новых функций и изменений API-контрактов, схемы БД и клиента нет; затрагиваются только `/health` и `Swagger:Enabled`.
- Swagger включён, если окружение Development **или** `Swagger:Enabled = true`; по умолчанию `false`.
- API-образ: `mcr.microsoft.com/dotnet/sdk:9.0` → `mcr.microsoft.com/dotnet/aspnet:9.0`; контекст сборки — корень репозитория; запуск не от root; порт 8080; конфигурация только переменными окружения (`ConnectionStrings__MiniBank`, `Database__MigrateOnStartup=true`, `Swagger__Enabled=true`).
- Веб-образ: `node:24-alpine` (`npm ci`, `npm run build`) → `nginx:alpine`; `index.html` без кеша, `assets/` на год; фолбэк SPA `try_files $uri /index.html`.
- Compose: `postgres` (`postgres:17`, 5432, том `minibank-data`), `api` (5050 → 8080, зависит от `postgres` healthy), `web` (8080 → 80, зависит от `api` started). Пароль БД учебный (`postgres`).
- CI: триггеры `push` в `main` и `pull_request`; `permissions: contents: read`; `concurrency` с отменой устаревших запусков; job-ы `backend`, `web`, `stack` на `ubuntu-latest`; действия закреплены мажорными тегами; образы не публикуются.
- Коммиты — **без** строки `Co-Authored-By` и без упоминаний Claude. **`git push` не выполнять** без явной просьбы пользователя.
- Рабочая директория — `C:\MiniBank`; команды ниже запускаются из неё (bash).

## Review Focus

1. БД недоступна → `/health` отвечает 503, а не 200 и не падение процесса (Task 1, тест).
2. Swagger по умолчанию выключен в Production и не раскрывает `/health` (Task 1, тесты).
3. Глубокая ссылка SPA (`/accounts/<guid>`) через nginx отдаёт `index.html`, а не 404 (Task 5, `smoke.sh`).
4. Прокси `/api/` снимает префикс и сохраняет query string: `/api/accounts/{id}/entries?page=1&pageSize=5` → 200 (Task 5, `smoke.sh`).
5. API-контейнер работает не от root (Task 2, проверка `id -u`).

---

### Task 1: `/health` и `Swagger:Enabled`

**Files:**
- Modify: `src/MiniBank.Api/MiniBank.Api.csproj` — пакет `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` (последняя 9.0.x, как EF Core 9.0.22)
- Modify: `src/MiniBank.Api/Program.cs`
- Modify: `tests/MiniBank.Api.IntegrationTests/ApiFactory.cs`
- Create: `tests/MiniBank.Api.IntegrationTests/HealthAndSwaggerTests.cs`

**Interfaces:**
- Produces: `GET /health` → 200 `Healthy` / 503 `Unhealthy`; конфигурационный ключ `Swagger:Enabled` (bool).
- Produces (тестовая фабрика): `ApiFactory.CreateAsync(PostgresFixture postgres, string environment = "Development", IReadOnlyDictionary<string, string?>? settings = null)` — прежние вызовы `CreateAsync(postgres)` остаются валидными; и `ApiFactory.CreateUnreachable()` — фабрика в окружении Production с недостижимой БД (`Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2`), `Database:MigrateOnStartup=false`. Метод `ConfigureWebHost` применяет окружение и `settings` через `UseSetting`.

- [ ] **Step 1: Расширить `ApiFactory`** по сигнатурам выше. Приватный конструктор принимает `connectionString`, `migrate`, `environment`, `settings`; `Database:MigrateOnStartup` равен `migrate`.

- [ ] **Step 2: Написать падающие тесты** в `HealthAndSwaggerTests` (`[Collection("postgres")]`, конструктор `(PostgresFixture postgres)`, как в `AccountsEndpointsTests`):
  - `Health_WithLiveDatabase_Returns200Healthy` — `GET /health` → 200, тело `Healthy`.
  - `Health_WithUnreachableDatabase_Returns503` — `ApiFactory.CreateUnreachable()`, `GET /health` → 503 (`HttpStatusCode.ServiceUnavailable`).
  - `Swagger_InProductionWithoutFlag_Returns404` — `CreateAsync(postgres, "Production")`, `GET /swagger/v1/swagger.json` → 404.
  - `Swagger_InProductionWithFlag_Returns200AndOmitsHealth` — `CreateAsync(postgres, "Production", new Dictionary<string, string?> { ["Swagger:Enabled"] = "true" })`, `GET /swagger/v1/swagger.json` → 200, тело содержит `"/accounts"` и не содержит `"/health"`.
  - `Swagger_InDevelopment_Returns200` — `CreateAsync(postgres)`, тот же запрос → 200 (защита от регрессии).

- [ ] **Step 3: Запустить тесты — убедиться, что падают**

Run: `dotnet test tests/MiniBank.Api.IntegrationTests --filter FullyQualifiedName~HealthAndSwaggerTests`
Expected: FAIL — `/health` отвечает 404; `Swagger_InProductionWithFlag...` получает 404 вместо 200. `Swagger_InProductionWithoutFlag` и `Swagger_InDevelopment` проходят сразу — это ожидаемо (фиксируют текущее поведение).

- [ ] **Step 4: Реализовать в `Program.cs`**: `builder.Services.AddHealthChecks().AddDbContextCheck<MiniBankDbContext>()`; `app.MapHealthChecks("/health")`; условие Swagger — `app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled")`. Регистрация `AddSwaggerGen` остаётся безусловной.

- [ ] **Step 5: Запустить тесты — убедиться, что проходят, затем весь набор**

Run: `dotnet test tests/MiniBank.Api.IntegrationTests --filter FullyQualifiedName~HealthAndSwaggerTests`, затем `dotnet test`
Expected: PASS; весь набор — 144 теста, 0 ошибок (было 139).

- [ ] **Step 6: Commit**

```bash
git add src/MiniBank.Api tests/MiniBank.Api.IntegrationTests
git commit -m "feat(api): GET /health with database check and Swagger:Enabled flag"
```

---

### Task 2: Docker-образ API

**Files:**
- Create: `src/MiniBank.Api/Dockerfile`
- Create: `.dockerignore` (в корне — контекст сборки)

**Interfaces:**
- Consumes: Task 1 (`/health`, `Swagger__Enabled`).
- Produces: образ, слушающий 8080 и читающий `ConnectionStrings__MiniBank`, `Database__MigrateOnStartup`, `Swagger__Enabled`.

- [ ] **Step 1: Написать `.dockerignore`**: `**/bin`, `**/obj`, `.git`, `web`, `tests`, `docs`, `**/.vs`, `**/.idea`.

- [ ] **Step 2: Написать `src/MiniBank.Api/Dockerfile`**: стадия `build` (`sdk:9.0`, `WORKDIR /src`, `COPY Directory.Build.props .`, `COPY src ./src`, `dotnet publish src/MiniBank.Api -c Release -o /app --no-self-contained`); стадия запуска (`aspnet:9.0`, `WORKDIR /app`, `COPY --from=build /app .`, `USER $APP_UID`, `EXPOSE 8080`, `ENTRYPOINT ["dotnet", "MiniBank.Api.dll"]`). Порт 8080 в образах .NET 9 — по умолчанию (`ASPNETCORE_HTTP_PORTS`), явно не задавать.

- [ ] **Step 3: Собрать образ**

Run: `docker build -f src/MiniBank.Api/Dockerfile -t minibank-api:test .`
Expected: успешная сборка, без ошибок restore/publish.

- [ ] **Step 4: Проверить запуск не от root и работу с БД.** Поднять `docker compose up -d postgres` (текущий compose), затем:

Run: `docker run --rm --entrypoint id minibank-api:test -u`
Expected: число, не `0`.

Run (в фоне): `docker run -d --name api-test --network host -e "ConnectionStrings__MiniBank=Host=localhost;Port=5432;Database=minibank;Username=postgres;Password=postgres" -e Swagger__Enabled=true -e ASPNETCORE_HTTP_PORTS=18080 minibank-api:test`, затем `curl -s -o /dev/null -w "%{http_code}" http://localhost:18080/health`
Expected: `200`. (На Docker Desktop `--network host` может быть недоступен — тогда заменить `Host=localhost` на `Host=host.docker.internal` и опубликовать порт `-p 18080:18080`.) После проверки `docker rm -f api-test`.

- [ ] **Step 5: Commit**

```bash
git add .dockerignore src/MiniBank.Api/Dockerfile
git commit -m "feat(docker): API image"
```

---

### Task 3: Docker-образ веба (nginx)

**Files:**
- Create: `web/Dockerfile`
- Create: `web/nginx.conf`
- Create: `web/.dockerignore` (`node_modules`, `dist`)

**Interfaces:**
- Produces: образ, слушающий 80; `/api/*` проксирует в `http://api:8080/*` (префикс снимается, query string сохраняется), всё остальное — статика SPA.

- [ ] **Step 1: Написать `web/nginx.conf`** (содержимое `server { ... }` для `conf.d/default.conf`):
  - `listen 80; root /usr/share/nginx/html;`
  - `location /api/`: `resolver 127.0.0.11 valid=10s; set $api http://api:8080; rewrite ^/api/(.*)$ /$1 break; proxy_pass $api;` плюс `proxy_set_header Host $host` и `X-Forwarded-For`. Динамическое разрешение через переменную нужно, чтобы nginx стартовал и переживал пересоздание контейнера `api` (литеральный `proxy_pass http://api:8080/` кеширует адрес при старте).
  - `location /assets/`: `expires 1y; add_header Cache-Control "public, immutable"; try_files $uri =404;`
  - `location /`: `add_header Cache-Control "no-cache"; try_files $uri /index.html;`

- [ ] **Step 2: Написать `web/Dockerfile`**: стадия `build` (`node:24-alpine`, `WORKDIR /app`, `COPY package.json package-lock.json ./`, `RUN npm ci`, `COPY . .`, `RUN npm run build`); стадия запуска (`nginx:alpine`, `COPY nginx.conf /etc/nginx/conf.d/default.conf`, `COPY --from=build /app/dist /usr/share/nginx/html`). Контекст сборки — `web/`.

- [ ] **Step 3: Собрать образ**

Run: `docker build -t minibank-web:test web`
Expected: успешная сборка (`tsc -b && vite build` проходят).

- [ ] **Step 4: Проверить статику и фолбэк без API.** `docker run -d --name web-test -p 18081:80 minibank-web:test`, затем:

Run: `curl -s -o /dev/null -w "%{http_code}" http://localhost:18081/accounts/3f2b1c0e-0000-4000-8000-000000000001` и `curl -s http://localhost:18081/ | grep -c 'id="root"'`
Expected: `200` и `1`.

Run: `curl -s -o /dev/null -w "%{http_code}" http://localhost:18081/api/health`
Expected: `502` (API нет в сети; nginx при этом не упал). После проверки `docker rm -f web-test`.

- [ ] **Step 5: Commit**

```bash
git add web/Dockerfile web/nginx.conf web/.dockerignore
git commit -m "feat(docker): web image with nginx and /api proxy"
```

---

### Task 4: Compose на весь стек

**Files:**
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: образы Task 2 и Task 3.
- Produces: `docker compose up --build` → приложение <http://localhost:8080>, API <http://localhost:5050>.

- [ ] **Step 1: Освободить порты.** Остановить фоновые процессы: `TaskStop` для `bn0ifucg0` (API) и `bphr5y2w5` (Vite), затем `docker compose down` (без `-v`: том с данными остаётся).

- [ ] **Step 2: Переписать `docker-compose.yml`.** Комментарий в шапке: полный стек; для разработки — `docker compose up -d postgres`. Сервисы:
  - `postgres` — как сейчас (образ, env, порт, том, healthcheck).
  - `api` — `build: { context: ., dockerfile: src/MiniBank.Api/Dockerfile }`; `environment`: `ConnectionStrings__MiniBank: "Host=postgres;Port=5432;Database=minibank;Username=postgres;Password=postgres"`, `Database__MigrateOnStartup: "true"`, `Swagger__Enabled: "true"`; `ports: ["5050:8080"]`; `depends_on: { postgres: { condition: service_healthy } }`; `restart: unless-stopped`.
  - `web` — `build: web`; `ports: ["8080:80"]`; `depends_on: { api: { condition: service_started } }`; `healthcheck: test: ["CMD", "wget", "-qO-", "http://127.0.0.1/api/health"]`, `interval: 5s`, `timeout: 3s`, `retries: 20`. Адрес `127.0.0.1`, а не `localhost`: busybox `wget` может резолвить `localhost` в `::1`, на котором nginx не слушает.

- [ ] **Step 3: Поднять стек**

Run: `docker compose up -d --build --wait`
Expected: команда завершается успешно, `docker compose ps` показывает `postgres` и `web` как `healthy`, `api` — `running`.

- [ ] **Step 4: Проверить вручную ключевые пути**

Run: `curl -s -o /dev/null -w "%{http_code}" http://localhost:8080/api/health` → `200`; `curl -s -o /dev/null -w "%{http_code}" http://localhost:5050/swagger/v1/swagger.json` → `200`; `docker compose exec api id -u` → не `0`.

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml
git commit -m "feat(docker): compose runs the full stack"
```

---

### Task 5: Дымовой скрипт и CI

**Files:**
- Create: `scripts/smoke.sh`
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: запущенный стек Task 4.
- Produces: `bash scripts/smoke.sh [BASE_URL]` (по умолчанию `http://localhost:8080`); код выхода 0 — все проверки прошли, иначе 1 с сообщением, какая проверка упала.

- [ ] **Step 1: Написать `scripts/smoke.sh`** (`#!/usr/bin/env bash`, `set -euo pipefail`, `curl -s`), проверки по порядку, каждая печатает `ok: <что>` либо `FAIL: <что> (got <код>)` и завершает скрипт с 1:
  1. `GET $BASE/api/health` → 200;
  2. `POST $BASE/api/accounts` с `{"owner":"Smoke","currency":"RUB"}` → 201; из тела достать `id` (без `jq`: `sed -n 's/.*"id":"\([^"]*\)".*/\1/p'`);
  3. `GET $BASE/api/accounts/$id/entries?page=1&pageSize=5` → 200 (проверяет снятие префикса и query string);
  4. `GET $BASE/` → 200 и тело содержит `id="root"`;
  5. `GET $BASE/accounts/$id` → 200 и тело содержит `id="root"` (фолбэк SPA для глубокой ссылки).

- [ ] **Step 2: Запустить скрипт на поднятом стеке**

Run: `bash scripts/smoke.sh`
Expected: пять строк `ok: ...`, код выхода 0. Затем проверить, что скрипт ловит поломку: `bash scripts/smoke.sh http://localhost:1` → `FAIL: ...`, код выхода ненулевой.

- [ ] **Step 3: Написать `.github/workflows/ci.yml`**: `name: CI`; `on: { push: { branches: [main] }, pull_request: }`; `permissions: { contents: read }`; `concurrency: { group: ci-${{ github.ref }}, cancel-in-progress: true }`. Job-ы на `ubuntu-latest`, все начинаются `actions/checkout@v4`:
  - `backend`: `actions/setup-dotnet@v4` (`dotnet-version: 9.0.x`), `dotnet test --configuration Release`.
  - `web`: `actions/setup-node@v4` (`node-version: 24`, `cache: npm`, `cache-dependency-path: web/package-lock.json`); `defaults.run.working-directory: web`; шаги `npm ci`, `npm test -- --run`, `npm run build`.
  - `stack`: `docker compose up -d --build --wait`; `bash scripts/smoke.sh`; шаг `if: failure()` → `docker compose logs`; шаг `if: always()` → `docker compose down -v`.

- [ ] **Step 4: Проверить workflow статически**

Run: `docker run --rm -v "$(pwd -W 2>/dev/null || pwd):/repo" -w /repo rhysd/actionlint:latest`
Expected: пустой вывод, код 0. Если образ недоступен — разобрать YAML любым парсером (`node -e` + `yaml` из `web/node_modules`) и сообщить в итоге, что actionlint не запускался.

- [ ] **Step 5: Commit**

```bash
git add scripts/smoke.sh .github/workflows/ci.yml
git commit -m "ci: tests, web build and compose smoke check"
```

---

### Task 6: README и итоговая проверка

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Обновить README**
  - Первая строка после заголовка проекта: бейдж `[![CI](https://github.com/Eksailed/MiniBank/actions/workflows/ci.yml/badge.svg)](https://github.com/Eksailed/MiniBank/actions/workflows/ci.yml)`.
  - «Статус»: «Этапы 1–3 готовы», пункт 3 — `✅ Качество: тесты, Docker Compose, CI, документация.`
  - «Быстрый старт»: нужен только Docker; `docker compose up --build`; приложение <http://localhost:8080>, Swagger <http://localhost:5050/swagger>; остановка `docker compose down` (данные в томе `minibank-data`, `down -v` стирает).
  - Новый подраздел «Режим разработки»: прежний сценарий (`docker compose up -d postgres`, `dotnet run --project src/MiniBank.Api`, затем `npm run dev` из раздела «Web»); .NET SDK 9 и Node нужны только здесь.
  - Раздел «API»: строка `GET /health` (проверка готовности с запросом к БД, 200/503) и абзац про `Swagger:Enabled` (Swagger включён в Development или при `Swagger:Enabled=true`; в Compose включён).
  - Новый раздел «CI»: три job-а и что проверяет каждый; локальный аналог `docker compose up -d --build --wait && bash scripts/smoke.sh`; порты 5432, 5050, 8080 должны быть свободны.
  - «Структура»: строки `scripts/`, `.github/workflows/`, `Dockerfile` в `src/MiniBank.Api` и `web/`.
  - В разделе «Web» первая строка про запуск: уточнить, что это режим разработки, а в Compose веб уже запущен.

- [ ] **Step 2: Итоговая проверка на свежем состоянии**

Run: `docker compose down && docker compose up -d --build --wait && bash scripts/smoke.sh`
Expected: `ok` по всем пяти проверкам.

Run: `dotnet test` и `cd web && npm test -- --run`
Expected: бэкенд — 144 теста, веб — 124 теста, 0 ошибок.

- [ ] **Step 3: Остановить стек и вернуть окружение разработки**

Run: `docker compose down`
Expected: контейнеры остановлены, том `minibank-data` на месте.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: README for compose stack, CI and /health"
```

После Task 6: показать пользователю итог и предложить `git push` — только по явной просьбе. После push первый запуск workflow на GitHub может потребовать правок YAML отдельным коммитом.
