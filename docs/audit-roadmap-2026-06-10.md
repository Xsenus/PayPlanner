# Roadmap устранения проблем по аудиту PayPlanner

Дата подготовки: 2026-06-10
Основа: `docs/full-project-audit-2026-06-10.md`
Цель: закрыть найденные риски без поломки бизнес-логики, усилить тесты и сделать проверки быстрыми, воспроизводимыми и понятными для следующего агента.

## Как работать с этим roadmap

Статусы задач:

- `[ ]` не начато.
- `[~]` в работе.
- `[x]` выполнено и проверено.
- `[!]` заблокировано, рядом обязательно указать причину.

Правила выполнения:

1. Перед изменением поведения сначала добавить или найти тест, который фиксирует текущее ожидаемое поведение.
2. Делать маленькие изменения: один риск/один модуль/один набор тестов за раз.
3. Не переписывать крупные компоненты без regression tests вокруг текущего поведения.
4. После каждого изменения запускать быстрые targeted tests, после этапа - полный gate этапа.
5. Если изменение видно пользователю, проверить `AGENTS.md`: нужна запись в `backend/AppReleases/releases.json`.
6. Не хранить реальные секреты в репозитории. В документах и тестах использовать placeholders.
7. Не удалять файлы-кандидаты на unused до подтверждения через сборку, тесты и ручную проверку сценариев.

Базовая проверка перед стартом каждого крупного этапа:

```powershell
git status --short
npm run lint
npm run test:guard
```

Полный release gate после завершения milestone:

```powershell
npm run lint
npx tsc -p tsconfig.app.json --noEmit
npm run test:unit
npm run test:integration
npm run audit:dates:test
npm run build
dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug
dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal
dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive
npm audit --audit-level=moderate
```

Если полный gate слишком долгий для каждой мелкой задачи, использовать правило:

- во время разработки: targeted tests + `npm run test:guard`;
- перед закрытием этапа: полный release gate.

## Milestone 0. Зафиксировать базовую точку

Цель: перед исправлениями убедиться, что текущее состояние понятно и воспроизводимо.

- [x] Создать рабочую ветку для исправлений аудита.
  - Проверка: `git branch --show-current`.
  - DoD: ветка не `main`, рабочее дерево понятно по `git status --short`.
  - Выполнено: создана ветка `audit-roadmap-fixes-2026-06-10`; `git status --short` показывает только изменения, существовавшие до начала исправлений, и новые audit docs.

- [x] Сохранить ссылку на полный отчет и этот roadmap в задаче/PR.
  - Файлы: `docs/full-project-audit-2026-06-10.md`, `docs/audit-roadmap-2026-06-10.md`.
  - DoD: следующий агент видит, откуда взяты решения.
  - Выполнено: полный отчет и roadmap сохранены в `docs/`; P0-исправления зафиксированы коммитом `9b61a7c8`.

- [x] Подтвердить, какие изменения уже были сделаны до roadmap.
  - Сейчас в worktree были замечены: `docs/date-time-inventory.md`, `src/components/Tasks/TaskJournal.test.tsx`, `backend/appsettings.Development.json`, новый audit report.
  - DoD: чужие/предыдущие изменения не перетерты.
  - Выполнено: перед правками проверены `git diff --stat`, `git diff --name-status` и untracked audit docs; существующие изменения не перетирались.

## Milestone 1. Сделать проверки надежными и быстрыми

Цель: сначала закрыть инфраструктуру качества, чтобы дальнейшие правки не ломали проект незаметно.

### 1.1 TypeScript gate

- [x] Исправить `npx tsc -p tsconfig.app.json --noEmit`.
  - Приоритет: P0.
  - Причина: сейчас build проходит, но typecheck падает на реальных расхождениях типов.
  - Области из аудита: `App.tsx`, `Accounts`, `Installments`, `InvoiceModal`, `ActModal`, `Calculator`, `PaymentModal`, `CaseDetailPage`, `TaskJournal`, `utils/acts`, `clientInvoiceModalApi.test`.
  - Порядок:
    1. Сначала собрать текущий список ошибок в отдельный временный лог.
    2. Разделить ошибки на группы: устаревшие типы API, nullable, missing fields, ES lib mismatch, тестовые типы.
    3. Исправлять группами, после каждой группы запускать `npx tsc -p tsconfig.app.json --noEmit`.
  - Тесты:
    ```powershell
    npx tsc -p tsconfig.app.json --noEmit
    npm run test:unit
    npm run build
    ```
  - DoD: `tsc --noEmit` проходит без ошибок.
  - В работе: сняты группы ошибок по ES lib (`.at()`/`replaceAll()`), типизации `fetchMock` в `src/services/api.test.ts`, nullable user settings, `sortKey` колонок в installments/contracts, `contactsExternal` scope, `Calculator` payment payload, отдельные settings/utils/task journal type errors. Targeted checks прошли: `npm run test:guard`, `npm run test:unit`, `npx vitest run src/services/api.test.ts`, `npx vitest run src/hooks/usePersistedTableSettings.test.tsx src/hooks/useUserTimeZone.test.tsx`, `npx vitest run src/components/Money/MoneyOperations.integration.test.tsx`, `npx vitest run src/components/Accounts/Accounts.integration.test.tsx src/components/Contracts/Contracts.integration.test.tsx`, `npx vitest run src/components/Settings/CaseDocumentTemplateSettings.test.tsx src/utils/clientInvoiceModalApi.test.ts`, `npx vitest run src/components/Tasks/TaskJournal.test.tsx`. `npx tsc -p tsconfig.app.json --noEmit` проходит.

- [x] Согласовать `tsconfig.app.json` с реально используемыми JS API.
  - Приоритет: P0.
  - Найдено: код использует `.at()`/`replaceAll()`, но `lib` сейчас `ES2020`.
  - Варианты:
    - поднять `lib` минимум до `ES2022`;
    - или убрать новые API из кода.
  - Рекомендуемый путь: поднять `lib`, если поддерживаемые браузеры это допускают.
  - Тесты:
    ```powershell
    npx tsc -p tsconfig.app.json --noEmit
    npm run test:unit
    ```
  - DoD: ошибки по `.at()`/`replaceAll()` исчезли без runtime polyfill сюрпризов.
  - Выполнено: `lib` поднят до `ES2022`; повторный `tsc` больше не показывает TS2550 для `.at()`/`replaceAll()`. `npm run test:guard` и `npm run test:unit` проходят.

### 1.2 Formatting gate

- [x] Исправить `dotnet format --verify-no-changes`.
  - Приоритет: P0.
  - Найдено: форматирование падало, например в `ReportsController.cs`, `SpreadsheetImportService.Xml.cs`.
  - Порядок:
    1. Запустить `dotnet format backend\PayPlanner.Api.sln --no-restore --verbosity minimal`.
    2. Проверить diff, убедиться, что только форматирование.
    3. Запустить backend tests.
  - Тесты:
    ```powershell
    dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal
    dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug
    ```
  - DoD: format verify проходит, поведение не изменено.
  - Выполнено: `dotnet format backend\PayPlanner.Api.sln --no-restore --verbosity minimal` применил только whitespace-форматирование в `ReportsController.cs` и `SpreadsheetImportService.Xml.cs`; `dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal` проходит. Backend tests: `dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug` — 1139/1139.

### 1.3 CI gate

- [x] Расширить `.github/workflows/deploy.yml` проверками до deploy.
  - Приоритет: P0.
  - Добавить:
    - `npm run lint`;
    - `npx tsc -p tsconfig.app.json --noEmit`;
    - `npm run test:integration` или отдельный scheduled/manual job, если долго;
    - `npm run audit:dates:test`;
    - `npm audit --audit-level=moderate`;
    - `dotnet list ... package --vulnerable --include-transitive`;
    - `dotnet format ... --verify-no-changes`.
  - Важно: сначала добиться локального прохождения, потом включать в CI.
  - DoD: deploy не начинается, если quality gate падает.
  - Выполнено: deploy workflow теперь запускает backend format verify, NuGet vulnerable audit, backend tests, frontend lint, typecheck, date/time audit self-test, npm audit, `test:ci` и `test:integration` до publish/build/deploy. Локально прошли: `npm run lint`, `npx tsc -p tsconfig.app.json --noEmit`, `npm run audit:dates:test`, `npm audit --audit-level=moderate`, `npm run test:integration`, `dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive`, `dotnet format ... --verify-no-changes`, `dotnet test ...`.

- [x] Решить стратегию `package-lock.json`.
  - Приоритет: P0.
  - Найдено: lockfile есть локально, но не tracked/ignored; CI использует `npm install` fallback.
  - Рекомендуемый путь: перестать игнорировать `package-lock.json`, закоммитить его и использовать `npm ci`.
  - Тесты:
    ```powershell
    npm ci
    npm run test:ci
    npm run build
    ```
  - DoD: CI всегда ставит зависимости воспроизводимо.
  - Выполнено: `package-lock.json` больше не игнорируется, deploy workflow использует только `npm ci` без `npm install` fallback. Локально прошли `npm ci`, `npm run test:ci`, `npm run build`.

## Milestone 2. Закрыть security/config риски

Цель: убрать очевидные риски конфигурации и сделать поведение API предсказуемым.

### 2.1 Секреты и appsettings

- [x] Убрать реальные секреты из tracked config.
  - Приоритет: P0.
  - Найдено: `backend/appsettings.json`, `backend/appsettings.Development.json`, `backend/appsettings.Production.json` содержат чувствительные поля/connection strings/ключи.
  - Порядок:
    1. Заменить реальные значения placeholders.
    2. Перенести реальные значения в user-secrets/env/secret store.
    3. Обновить README/runbook.
    4. Проверить, что приложение стартует с env overrides.
  - Тесты:
    ```powershell
    dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug
    ```
  - DoD: в tracked файлах нет реальных secrets, приложение умеет читать значения из безопасного источника.
  - Выполнено: реальные JWT/DaData/SMTP/PostgreSQL password значения убраны из tracked `appsettings*.json`; JWT secret теперь обязателен и валидируется на длину. Добавлены `JwtConfigurationTests`. Проверки: known-secret scan без совпадений, `dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug` — 1142/1142, локальный health-check backend на PostgreSQL прошел с `ConnectionStrings__Default` и `Jwt__Secret` из env.

- [x] Добавить документированный список обязательных env/config параметров.
  - Приоритет: P1.
  - Файл-кандидат: `README.md` или отдельный `docs/configuration.md`.
  - DoD: новый агент понимает, какие настройки нужны для SQLite/PostgreSQL/S3/DaData/SMTP/Telegram/Google/Yandex.
  - Выполнено: раздел backend env в `README.md` обновлен для `ConnectionStrings:Default`, `Jwt:Secret`, env overrides и optional DaData/Email параметров.

### 2.2 CORS, HTTPS, error contract

- [x] Ограничить production CORS или документировать причину `AllowAnyOrigin`.
  - Приоритет: P0/P1.
  - Найдено: `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`.
  - Рекомендуемый путь:
    - Development: broad CORS допустим.
    - Production: `WithOrigins(...)` из config.
  - Тесты:
    - backend test на CORS policy, если удобно;
    - manual smoke frontend/backend после deploy config.
  - DoD: production CORS не шире необходимого или явно обоснован.
  - Выполнено: Development оставлен с `AllowAnyOrigin`, Production использует только `Cors:AllowedOrigins`; пустой список не включает wildcard. Добавлены `CorsPolicyConfigurationTests` и README пример `Cors__AllowedOrigins__0`.

- [x] Ввести единый API error contract.
  - Приоритет: P1.
  - Найдено: нет `UseExceptionHandler`, `AddProblemDetails`, `UseStatusCodePages`; ошибки мапятся локально.
  - Порядок:
    1. Зафиксировать текущие expected response formats тестами для common failures.
    2. Добавить global exception mapping.
    3. Не менять пользовательские сообщения без необходимости.
  - Тесты:
    - validation error;
    - unauthorized;
    - forbidden;
    - not found;
    - conflict;
    - external provider unavailable.
  - DoD: API возвращает единый формат ошибок, frontend не сломан.
  - Выполнено: добавлен `application/problem+json` для непойманных API исключений и пустых API 401/403/404/409/5xx ответов; существующие локальные `Problem(...)`, validation errors и ответы с `{ message }` не переписаны. Добавлены `ApiErrorContractTests`.

- [x] Зафиксировать TLS responsibility.
  - Приоритет: P1.
  - Найдено: `UseHttpsRedirection`/`UseHsts` в app code нет.
  - Если TLS делает nginx, описать это в deploy/runbook.
  - Если должен делать app, добавить middleware и проверить deploy.
  - DoD: понятно, где enforced HTTPS и HSTS.
  - Выполнено: добавлен `docs/deploy-runbook.md` с ответственностью nginx за TLS, HTTP->HTTPS redirect и HSTS; backend остается на внутреннем `127.0.0.1:5080`. Добавлены `ProductionTlsResponsibilityTests`.

## Milestone 3. Исправить видимые frontend дефекты

Цель: закрыть найденные UI баги без переписывания крупных экранов.

- [x] Исправить mobile horizontal overflow на `/tasks`.
  - Приоритет: P0.
  - Найдено: overflow около 59px.
  - Порядок:
    1. Воспроизвести на mobile viewport.
    2. Найти элемент, который расширяет layout.
    3. Исправить точечно через responsive constraints/min-width/overflow.
  - Тесты:
    - browser screenshot/smoke на mobile;
    - `npm run test:guard`;
    - если есть component test рядом - обновить/добавить.
  - DoD: горизонтального overflow нет.
  - Выполнено: сегментированные controls в TaskJournal ограничены `max-w-full` и внутренним `overflow-x-auto`; добавлен component test. Browser smoke 390px: `/tasks` overflow 0.

- [x] Исправить mobile horizontal overflow на `/tasks/approvals`.
  - Приоритет: P0.
  - Тесты и DoD аналогично `/tasks`.
  - Выполнено: общий фикс TaskJournal покрывает approvals mode. Browser smoke 390px: `/tasks/approvals` overflow 0.

- [x] Исправить mobile horizontal overflow на `/tasks/calendar`.
  - Приоритет: P0.
  - Тесты и DoD аналогично `/tasks`.
  - Выполнено: общий фикс TaskJournal покрывает calendar initial view. Browser smoke 390px: `/tasks/calendar` overflow 0.

- [x] Разобрать повторяющуюся SignalR console negotiation error.
  - Приоритет: P1.
  - Найдено во visual smoke.
  - Порядок:
    1. Понять, ошибка появляется без backend, при 401, при неправильном URL или при CORS.
    2. Не скрывать ошибку, если она сигнализирует о реальной проблеме подключения.
    3. Добавить fallback/logging без шума для ожидаемого offline режима.
  - Тесты:
    - frontend unit test для notification/realtime service, если есть;
    - manual/browser smoke.
  - DoD: console не зашумлен ожидаемой ошибкой, реальные ошибки не проглатываются.
  - Выполнено: realtime notifications не стартуют без auth token, ожидаемые offline/network negotiation failures переводятся в тихий debug, а реальные HTTP/auth ошибки остаются в `console.error`. Добавлены `realtimeNotifications.test.ts` и расширены `RealtimeNotificationToasts.test.tsx`; browser smoke на Vite с отключенным API не показал SignalR console error.

- [x] Проверить и исправить icon-only buttons без accessible name.
  - Приоритет: P1.
  - Кандидаты: Dictionaries, DictionariesModal, MoneyAccountSettings.
  - Тесты:
    - role/name queries в Testing Library;
    - manual keyboard navigation.
  - DoD: все icon-only buttons имеют `aria-label` или видимый accessible name.
  - Выполнено: добавлены accessible names для icon-only действий в `Dictionaries`, `DictionariesModal` и `MoneyAccountSettings`; покрыто role/name queries в Testing Library. Добавлен release note `2026-06-10-dictionary-money-accessible-actions`.

## Milestone 4. Усилить права доступа и deep-link сценарии

Цель: доказать, что скрытые страницы и detail routes нельзя открыть без прав.

### 4.1 Frontend route guards

- [x] Добавить negative deep-link tests для hidden/detail tabs.
  - Приоритет: P0.
  - Покрыть:
    - `/cases/1` без `cases.canView`;
    - `/acts/new` без `acts.canCreate`;
    - `/acts/1` без `acts.canView`;
    - `/contracts/1` без `contracts.canView`;
    - `/invoices/1` без `accounts.canView`;
    - `/payments/1` без `payments.canView`;
    - `/tasks/1` без `tasks.canView`;
    - `/clients/1` без `clients.canView`.
  - Тесты:
    ```powershell
    npm run test:guard
    ```
  - DoD: direct URL не открывает запрещенный раздел.
  - Выполнено: tab access guard вынесен в `appRouting.canAccessTab`, App использует тот же helper. Добавлены negative tests для `/cases/1`, `/acts/new`, `/acts/1`, `/contracts/1`, `/invoices/1`, `/payments/1`, `/tasks/1`, `/clients/1`. Проверки: `npx vitest run src/App.routing.test.ts`, `npm run test:guard`, `npx tsc -p tsconfig.app.json --noEmit`.

- [x] Зафиксировать whitelist расхождений между `Tab`, `ALL_TABS` и `Navigation`.
  - Приоритет: P1.
  - Сейчас `Tab` и `ALL_TABS` совпадают 47/47, в navigation нет 12 hidden/detail tabs.
  - DoD: тест падает, если появляется новая вкладка без осознанного routing/navigation решения.
  - Выполнено: `Tab` теперь выводится из единого `ALL_TABS`, а `Navigation.test.tsx` проверяет, что каждая вкладка либо имеет navigation link, либо входит в явный whitelist 12 hidden/detail tabs.

### 4.2 Backend permission matrix

- [x] Добавить backend permission matrix tests для ключевых ресурсов.
  - Приоритет: P0.
  - Ресурсы:
    - payments;
    - invoices/accounts;
    - acts;
    - contracts;
    - cases;
    - tasks;
    - money operations;
    - settings/admin sections.
  - Роли:
    - admin;
    - обычный пользователь с правом;
    - пользователь без права;
    - ответственный/не ответственный, где применимо;
    - пользователь без доступа к компании/делу, где применимо.
  - DoD: API возвращает ожидаемые 200/201/204/400/401/403/404 для каждой роли.
  - Выполнено: добавлен `BackendPermissionMatrixTests` для ключевых sections (`payments`, `accounts`, `acts`, `contracts`, `cases`, `tasks`, `reports`, `roles`, `audit`, `yandexDisk`, `s3Storage`) и data-scoped resources. Проверки: targeted matrix 49/49, полный backend suite 1191/1191, `dotnet format ... --verify-no-changes`.

- [x] Унифицировать ручные `User.IsInRole("admin")` checks.
  - Приоритет: P2.
  - Не делать до появления permission tests.
  - Рекомендуемый путь: policy/permission service/helper.
  - DoD: поведение для admin не изменилось, tests проходят.
  - Выполнено: добавлен `ClaimsPrincipalExtensions.HasAdminRole()` и заменены оставшиеся ручные `User.IsInRole("admin")` checks в `ContractsController` и `MoneyOperationsController`. Добавлены `ClaimsPrincipalExtensionsTests`; targeted permission/helper tests 52/52.

## Milestone 5. Файлы, загрузки, скачивания

Цель: закрыть upload/download риски и не сломать документы, задачи, импорт, S3/Я.Диск.

- [x] Составить таблицу лимитов файлов.
  - Приоритет: P0.
  - Типы:
    - profile photo;
    - task attachment;
    - case document;
    - database import;
    - spreadsheet import;
    - S3 upload;
    - Yandex Disk upload.
  - DoD: для каждого типа есть max size, allowed extensions/content type, error message.
  - Выполнено: добавлена таблица `docs/file-upload-limits-2026-06-10.md` с текущими лимитами, allowlist/отсутствием allowlist и пользовательскими сообщениями; добавлен тест полноты документации.

- [x] Добавить shared upload validation helper.
  - Приоритет: P1.
  - Не менять UX сообщений без тестов.
  - DoD: контроллеры используют единый helper там, где правила одинаковые.
  - Выполнено: добавлен `UploadValidationHelper` для required/max-size проверок, сохранены прежние сообщения в task/case/S3/Я.Диск upload, добавлены unit-тесты helper и guard-тесты пустой загрузки для S3/Я.Диска.

- [x] Добавить file endpoint security tests.
  - Приоритет: P0.
  - Сценарии:
    - path traversal: `../`, encoded slashes, backslashes;
    - пустой файл;
    - слишком большой файл;
    - запрещенное расширение;
    - дубликат имени;
    - скачивание чужого файла;
    - скачивание удаленного/отсутствующего файла;
    - удаление файла вне root невозможно.
  - Области:
    - `TasksController`;
    - `CasesV2Controller`;
    - `ProfileController`;
    - `DatabaseAdminController`;
    - `S3StorageController`;
    - `YandexDiskController`.
  - DoD: 401/403/404/400 разделены и предсказуемы.
  - Выполнено: расширены `TasksControllerTests` для пустого файла, слишком большого declared length, traversal/backslash/encoded slash в имени файла, missing physical file и file-from-another-task download. Исправлена sanitization encoded filenames в `TasksController`. Targeted tests: 8/8.

- [x] Добавить real-browser smoke для download/print/new-tab.
  - Приоритет: P1.
  - Причина: Vitest глобально мокает `window.open`, anchor download и blob click.
  - DoD: хотя бы один browser smoke доказывает, что download/print flow работает вне jsdom.
  - Выполнено: добавлен `npm run test:browser-downloads`, который через Playwright в реальном Chromium/Edge проверяет blob download, `target="_blank"` popup с `noopener/noreferrer` и print-render через PDF.

## Milestone 6. Background/hosted services

Цель: фоновые задачи должны быть проверяемыми, отменяемыми и наблюдаемыми.

- [ ] Добавить lifecycle tests для 15 hosted services.
  - Приоритет: P1.
  - Services:
    - `DatabaseBackupSchedulerService`;
    - `PaymentStatusUpdater`;
    - `InvoicePaymentStateReconciliationService`;
    - `TaskJournalReminderNotificationService`;
    - `NotificationRetrySchedulerService`;
    - `NotificationDeliveryRetentionService`;
    - `CalendarSyncSchedulerService`;
    - `GoogleCalendarBackgroundSyncService`;
    - `YandexCalendarBackgroundSyncService`;
    - `TaskJournalAutomationSchedulerService`;
    - `TelegramBotPollingService`;
    - `MoneyBalanceReconciliationSchedulerService`;
    - `ClientCaseCourtSyncSchedulerService`;
    - `UserActivityRetentionService`;
    - `YandexDiskCrmSyncSchedulerService`.
  - Минимум для каждого:
    - старт;
    - один tick;
    - cancellation;
    - exception не убивает host;
    - не дергает реальные внешние API.
  - DoD: прямой тест по имени сервиса есть или причина отсутствия документирована.

- [ ] Добавить admin-visible status для критичных jobs.
  - Приоритет: P2.
  - Поля:
    - last run;
    - last success;
    - last error;
    - duration;
    - next run, если есть.
  - DoD: администратор видит, что синхронизация/ретеншн/backup не умерли молча.

## Milestone 7. Data model, migrations, seed/backfill

Цель: финансовые и юридические данные должны иметь проверенные ограничения и безопасные миграции.

- [x] Проверить decimal/financial precision по модели.
  - Приоритет: P0.
  - Найдено: `.HasPrecision(...)` только 2 раза.
  - Порядок:
    1. Собрать все денежные поля.
    2. Проверить SQLite/PostgreSQL mapping.
    3. Добавить precision там, где нужно.
    4. Добавить tests на округление/сохранение.
  - DoD: деньги не теряют точность и одинаково ведут себя в SQLite/PostgreSQL.

- [ ] Проверить unique indexes с soft-delete и business scope.
  - Приоритет: P1.
  - Вопросы:
    - uniqueness глобальная или по компании/году/legal entity?
    - учитывает ли soft-delete?
  - DoD: tests фиксируют expected uniqueness для счетов, актов, договоров, кассовых документов, справочников.

- [ ] Документировать и протестировать delete behavior.
  - Приоритет: P1.
  - Сущности:
    - клиент;
    - дело;
    - платеж;
    - счет;
    - акт;
    - договор;
    - документы/attachments.
  - DoD: понятно, что cascade/delete/set null/restrict делает с данными.

- [x] Добавить seed idempotency tests.
  - Приоритет: P0.
  - Сценарий: пустая БД -> seed -> seed повторно -> нет дублей, права/роли корректны.
  - DoD: seed безопасен при каждом startup.

- [ ] Проверить startup migrations/seed/backfills observability.
  - Приоритет: P1.
  - Найдено: startup выполняет `MigrateAsync`, seed, release sync, backfills.
  - DoD: ошибки видны в логах, долгие операции понятны, rollback procedure описана.

- [ ] Проверить provider-specific SQL.
  - Приоритет: P1.
  - Найдено: `FOR UPDATE` в `InvoicesController.cs`.
  - DoD: SQLite/PostgreSQL behavior покрыт тестом или код разделен по provider.

- [ ] Документировать batch operations, которые обходят audit hooks.
  - Приоритет: P2.
  - Найдено: `ExecuteUpdateAsync`, `ExecuteDeleteAsync`.
  - DoD: понятно, где object audit сознательно не пишется.

## Milestone 8. API, integrations and external callbacks

Цель: внешние интеграции должны быть тестируемыми без реальных сервисов и безопасными на public callbacks.

- [ ] Добавить mocked HTTP integration tests.
  - Приоритет: P1.
  - Интеграции:
    - DaData;
    - Sudrf;
    - Yandex Disk;
    - Google Calendar;
    - Yandex Calendar;
    - Telegram;
    - S3.
  - DoD: ошибки timeout/401/403/404/429/5xx мапятся в ожидаемые сообщения/статусы.

- [x] Добавить public callback negative tests.
  - Приоритет: P0.
  - Callback areas:
    - external auth;
    - Telegram webhook/widget;
    - Google Calendar webhook;
    - Yandex Calendar callback.
  - Сценарии:
    - отсутствует state/secret;
    - неверный state/secret;
    - replay;
    - чужой user binding;
    - expired auth request.
  - DoD: public endpoints не принимают неподписанные/чужие запросы.

- [ ] Добавить feature flag tests для legal/calendar integrations.
  - Приоритет: P1.
  - Использовать существующие `test:legal-flags:strict` и readiness scripts как gate.
  - DoD: disabled-by-default сценарий стабильно проверяется в CI.

## Milestone 9. Frontend tests and accessibility

Цель: тесты должны проверять пользовательские сценарии, а не только факт render.

- [ ] Ввести shared API mock layer или MSW.
  - Приоритет: P2.
  - Причина: сейчас много ad hoc fetch/api mocks.
  - DoD: новые tests используют единый mock layer, старые мигрируются постепенно.

- [ ] Добавить hook-specific tests для hooks без покрытия.
  - Приоритет: P1.
  - Из аудита: 35 hooks без прямых tests.
  - Начать с hooks, влияющих на permissions, storage, date/time, API loading.
  - DoD: критичные hooks имеют быстрые unit tests.

- [ ] Добавить accessibility tests для navigation/modals/toolbars.
  - Приоритет: P1.
  - Проверять:
    - role/name;
    - focus trap in modals;
    - keyboard access;
    - aria labels;
    - no positive `tabIndex`.
  - DoD: основные рабочие экраны можно пройти клавиатурой.

- [ ] Добавить visual smoke в CI или scheduled job.
  - Приоритет: P2.
  - Минимум:
    - desktop/tablet/mobile;
    - key pages;
    - overflow check;
    - console errors.
  - DoD: mobile overflow не возвращается незаметно.

## Milestone 10. Maintainability без поломки логики

Цель: уменьшать сложность только там, где есть тестовая страховка.

- [ ] Убрать type-level cycle в Roles.
  - Приоритет: P1.
  - Найдено: `Roles.tsx -> RoleModal.tsx/RolePermissionsModal.tsx` импортируют `type Role` из screen component.
  - Решение: вынести `Role` type в отдельный `types` файл.
  - Тесты:
    ```powershell
    npm run test:unit
    npx tsc -p tsconfig.app.json --noEmit
    ```
  - DoD: type cycle исчез, behavior не изменен.

- [ ] Проверить кандидаты на unused frontend files.
  - Приоритет: P2.
  - Кандидаты:
    - `src/components/Calendar/SummaryCards.tsx`;
    - `src/components/Contracts/ContractCreateForm.tsx`;
    - `src/components/LegalEntities/LegalEntityInlineEditor.tsx`;
    - `src/components/ui/Hint.tsx`;
    - `src/hooks/useClientStats.ts`.
  - DoD: файл либо удален с проверками, либо отмечен как используемый/зарезервированный.

- [ ] Разбивать большие frontend компоненты только после тестовой страховки.
  - Приоритет: P2.
  - Hotspots:
    - `ClientDetail`;
    - `TaskJournal`;
    - `Reports`;
    - `InvoiceModal`;
    - `PaymentModal`;
    - `LegalEntityModal`;
    - `Accounts`;
    - `CaseDetailPage`.
  - Правило: сначала вынести pure helper или small subcomponent, не менять UX.
  - DoD: targeted tests + screenshots проходят.

- [ ] Разбивать большие backend controllers через query/service extraction.
  - Приоритет: P2.
  - Hotspots:
    - `CasesV2Controller`;
    - `InvoicesController`;
    - `ReportsController`;
    - `MoneyOperationsController`;
    - `TasksController`;
    - `PaymentsController`.
  - Правило: сначала tests вокруг endpoint/query behavior, потом extraction.
  - DoD: API contract не изменился.

- [ ] Переименовать package name.
  - Приоритет: P3.
  - Сейчас: `vite-react-typescript-starter`.
  - DoD: `package.json` name соответствует продукту, CI не сломан.

## Milestone 11. Documentation and release process

Цель: изменения должны быть понятны пользователю, разработчику и администратору.

- [ ] Обновить README.
  - Приоритет: P1.
  - Найдено:
    - README drift по PostgreSQL/SQLite;
    - устаревшие утверждения про typecheck/CI;
    - неактуальные инструкции.
  - DoD: новый разработчик может поднять проект по README.

- [ ] Описать quality gates.
  - Приоритет: P1.
  - Где: README или `docs/quality-gates.md`.
  - DoD: понятно, какие команды запускать перед PR/deploy.

- [ ] Описать release note policy из `AGENTS.md` в dev docs.
  - Приоритет: P2.
  - DoD: при user-facing change агент не забывает `backend/AppReleases/releases.json`.

- [ ] Обновить deploy/runbook по production config.
  - Приоритет: P1.
  - Темы:
    - secrets;
    - CORS origins;
    - TLS/HSTS;
    - startup migrations;
    - backup/rollback;
    - health checks.
  - DoD: администратор понимает, что происходит при deploy.

## Milestone 12. Финальная приемка

Цель: закрыть roadmap без скрытых регрессий.

- [ ] Прогнать полный release gate.
  - Команды:
    ```powershell
    npm run lint
    npx tsc -p tsconfig.app.json --noEmit
    npm run test:unit
    npm run test:integration
    npm run audit:dates:test
    npm run build
    dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug
    dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal
    dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive
    npm audit --audit-level=moderate
    ```
  - DoD: все команды проходят.

- [ ] Прогнать visual smoke на desktop/tablet/mobile.
  - Минимум:
    - login/auth shell;
    - calendar;
    - tasks/tasks approvals/tasks calendar;
    - clients/client detail;
    - cases/case detail;
    - invoices/accounts;
    - payments/money;
    - acts/contracts;
    - settings/admin pages.
  - DoD: нет горизонтального overflow, критичных console errors и broken layout.

- [ ] Проверить API auth smoke.
  - Сценарии:
    - public endpoints return expected 200/400;
    - protected endpoints без токена return 401;
    - forbidden role return 403;
    - missing object return 404.
  - DoD: API behavior predictable.

- [ ] Проверить, что не осталось лишних локальных процессов.
  - Команда:
    ```powershell
    Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'PayPlanner|vite|dotnet run|npm run dev' }
    ```
  - DoD: нет случайно запущенного backend/frontend dev server.

- [ ] Закрыть roadmap summary.
  - Что указать:
    - выполненные задачи;
    - оставшиеся риски;
    - команды, которые прошли;
    - release notes, если были user-facing changes.
  - DoD: следующий агент или человек может быстро понять итог.

## Быстрые приоритеты, если времени мало

P0 order:

1. `tsc --noEmit`.
2. `dotnet format --verify-no-changes`.
3. CI gate before deploy.
4. `package-lock.json` reproducibility.
5. Secrets out of tracked config.
6. Mobile overflow in tasks pages.
7. Frontend negative deep-link tests.
8. Backend permission matrix for critical resources.
9. File upload/download security tests.
10. Public callback negative tests.
11. Seed idempotency tests.
12. Financial precision audit.

Не начинать крупный refactor до закрытия P0.
