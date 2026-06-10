# Полный аудит проекта PayPlanner от 10.06.2026

## 1. Область проверки

Проверка выполнена для локального проекта `C:\Users\ilel\Desktop\PayPlanner`.

Покрытие аудита:

- frontend: Vite, React, TypeScript, маршруты, компоненты, адаптивность, lint, unit/integration tests;
- backend: .NET 9 API, контроллеры, авторизация, тесты, локальный runtime, health endpoint;
- локальная БД: приложение успешно подключилось к локальной PostgreSQL из development-конфигурации;
- визуальная проверка: 47 вкладок/маршрутов на 3 viewport: mobile 390x844, tablet 768x1024, desktop 1440x900;
- права доступа: проверены frontend route guards, навигация, backend `[Authorize]`/`[AllowAnonymous]`, тесты ролей и permissions;
- отчетность по тестам: собрана карта файлов с прямыми или косвенными тестовыми связями.

Ограничение аудита: destructive CRUD-сценарии не выполнялись. Я не создавал, не изменял и не удалял рабочие записи через UI/API, чтобы не повредить локальные данные. Проверка runtime была read-only/smoke: открытие страниц, чтение состояния DOM, health-check API, тесты и статический анализ.

## 2. Итог

Общее состояние проекта хорошее: сборка проходит, frontend и backend тесты проходят, основные рабочие разделы открываются, backend подключается к локальной БД и отвечает на health-check.

Найденные важные замечания:

1. На mobile 390px есть горизонтальный overflow около 59px в разделах задач:
   - `tasks` (`/tasks`);
   - `tasksApprovals` (`/tasks/approvals`);
   - `tasksCalendar` (`/tasks/calendar`).

2. В браузерной консоли повторяется ошибка SignalR:
   - `Failed to start the connection: The connection was stopped during negotiation`.

3. `npm run lint` проходит без ошибок, но оставляет 18 предупреждений по React hooks/Fast Refresh.

4. Есть крупные frontend-компоненты без прямых тестов. Самые заметные: `NotificationSettings`, `LegalEntityFinanceSection`, `AuthProviderSettings`, `RolePermissionsModal`, `InvoiceDocumentTemplateSettings`, `SpreadsheetImportSettings`.

5. Есть backend-контроллеры без явно найденных тестов. Самые значимые по количеству endpoints: `NotificationsController`, `DictionariesReadController`, `ContactsController`, `RolesController`, V1-контроллеры.

6. Production build предупреждает о крупном чанке `AppSettings` больше 500 kB.

## 3. Запущенные проверки

| Проверка | Результат | Детали |
|---|---:|---|
| `npm run lint` | Пройдено | 0 errors, 18 warnings |
| `npm run build` | Пройдено | Vite build успешен, есть warning по чанку больше 500 kB |
| `npm run test:unit` | Пройдено | 71 test files, 497 tests |
| `npm run test:integration` | Пройдено | 13 test files, 224 tests |
| `npm run audit:dates` | Пройдено | просканировано 1311 файлов, найдено 7313 date/time matches |
| `dotnet test backend\PayPlanner.Api.sln` | Пройдено | 1139 backend tests |
| `GET /api/health` | Пройдено | `200 {"status":"ok"}` |
| `GET /health` | 404 | В проекте health endpoint расположен на `/api/health` |
| Поиск `TODO/FIXME/HACK/debugger/console.log` | Чисто | Совпадений не найдено |

## 4. Состояние локального runtime

Поднимались локальные процессы:

- backend: `http://127.0.0.1:5080`;
- frontend: `http://127.0.0.1:5173`;
- PostgreSQL был доступен на локальном порту development-конфигурации.

Backend успешно стартовал, выполнял EF-запросы и отвечал на `/api/health`.

Frontend успешно стартовал через Vite и отдавал `index.html`.

Визуальная проверка выполнялась в уже авторизованной локальной браузерной сессии. Это позволило проверить реальные рабочие разделы CRM, а не только форму входа.

## 5. Frontend: структура и покрытие

Статистика:

- frontend source files без тестов: 240;
- frontend test files: 84;
- component/support files в `src/components`: 127;
- компоненты с найденной тестовой связью: 94;
- компоненты без найденной тестовой связи: 33;
- крупные компоненты без прямых тестов: 19.

### Крупные frontend-файлы без прямых тестов

| Файл | Строк | Риск |
|---|---:|---|
| `src/components/Settings/NotificationSettings.tsx` | 2148 | Высокий: настройки уведомлений, пользовательские предпочтения |
| `src/components/LegalEntities/LegalEntityFinanceSection.tsx` | 1386 | Высокий: финансы юрлиц |
| `src/components/Settings/AuthProviderSettings.tsx` | 1147 | Высокий: внешняя авторизация |
| `src/components/Roles/RolePermissionsModal.tsx` | 1002 | Высокий: права доступа |
| `src/components/Settings/InvoiceDocumentTemplateSettings.tsx` | 960 | Средний/высокий: шаблоны документов |
| `src/components/Settings/SpreadsheetImportSettings.tsx` | 906 | Высокий: импорт данных |
| `src/components/Settings/SpreadsheetImportPreviewModal.tsx` | 785 | Высокий: предпросмотр импорта |
| `src/components/Users/UserDetailPage.tsx` | 675 | Высокий: управление пользователями |
| `src/components/Auth/ExternalAuthPanel.tsx` | 605 | Средний/высокий: привязка внешней авторизации |
| `src/components/ObjectAudit/ObjectAuditPage.tsx` | 569 | Средний: аудит объектов |
| `src/components/Contracts/ContractCreatePage.tsx` | 503 | Высокий: создание договоров |
| `src/components/Contracts/ContractCreateForm.tsx` | 433 | Высокий: форма договора |
| `src/components/Settings/UserMergeSettings.tsx` | 411 | Высокий: слияние пользователей |
| `src/components/Contacts/ContactEditorModal.tsx` | 407 | Средний: редактирование контактов |
| `src/components/Settings/AppVersionSettings.tsx` | 403 | Средний: публикация версий |
| `src/components/ui/ObjectAuditHistoryCard.tsx` | 328 | Средний: отображение истории |
| `src/components/AppVersion/AppVersionModal.tsx` | 295 | Низкий/средний: отображение релизов |
| `src/components/Statistics/TypeStatsBlock.tsx` | 289 | Низкий/средний: статистика |
| `src/components/Accounts/InvoiceLineItemLookupDialog.tsx` | 282 | Средний: выбор позиций счета |

Рекомендация: первым слоем покрыть тестами не все 33 файла, а 6 критичных областей: уведомления, роли/права, импорт, внешняя авторизация, финансы юрлиц, создание договоров.

## 6. Frontend: lint warnings

`npm run lint` проходит, но оставляет 18 предупреждений:

| Файл | Предупреждение |
|---|---|
| `src/components/Accounts/InvoiceModal.tsx` | `useEffect` missing dependencies: `defaultDate`, `profileTimeZoneId` |
| `src/components/Acts/ActModal.tsx` | `useEffect` missing dependencies: `defaultDate`, `profileTimeZoneId` |
| `src/components/Calendar/PaymentModal.tsx` | `useEffect` missing dependency: `defaultDate` |
| `src/components/Clients/Clients.tsx` | Fast Refresh: файл экспортирует не только компонент |
| `src/components/Contacts/UnifiedContacts.tsx` | Fast Refresh: файл экспортирует не только компонент |
| `src/components/Contracts/ContractModal.tsx` | `useEffect` missing dependencies: `defaultDate`, `profileTimeZoneId` |
| `src/components/Money/MoneyOperations.tsx` | Fast Refresh: файл экспортирует не только компонент |
| `src/components/Payments/Payments.tsx` | `useCallback` missing dependencies: `defaultFrom`, `defaultTo` |
| `src/components/Tasks/TaskJournal.tsx` | 10 warnings по лишним dependencies `effectiveTaskJournalTimeZoneId`/`timeZoneId` |

Рекомендация: warnings по датам/таймзонам стоит разобрать отдельно. Это не ломает сборку, но такие места часто дают плавающие баги при смене профиля, часового пояса или default date.

## 7. Страницы и маршруты

Проверено 47 вкладок/маршрутов на mobile/tablet/desktop. Всего 141 visual smoke check.

| Tab | URL/route | Компонент | Право доступа | Тестовое покрытие/замечание |
|---|---|---|---|---|
| `calendar` | `/?ppTab=calendar` | `Calendar` | `calendar.canView` | Есть integration test календаря; desktop/tablet/mobile открывается |
| `notifications` | `/notifications` | `NotificationCenter` | доступен авторизованному пользователю | Есть `NotificationCenter.test.tsx`; виден SignalR console error |
| `tasks` | `/tasks` | `TaskJournal` | `tasks.canView` | Очень хорошее покрытие `TaskJournal.test.tsx`; mobile overflow 59px |
| `tasksApprovals` | `/tasks/approvals` | `TaskJournal mode="approvals"` | `tasks.canView` | Покрыто тестами задач; mobile overflow 59px |
| `tasksCalendar` | `/tasks/calendar` | `TaskJournal initialView="calendar"` | `tasks.canView` | Покрыто тестами задач; mobile overflow 59px |
| `reports` | `/?ppTab=reports` | `Reports` | `reports.canView` | Backend reports tests есть; прямого component test не видно |
| `calculator` | `/?ppTab=calculator` | `Calculator` | `calculator.canView` | Прямого теста компонента не видно |
| `legalEntities` | `/?ppTab=legalEntities` | `LegalEntities` | `legalEntities.canView` | Есть тесты modal/utils; finance-section без прямого теста |
| `cases` | `/cases` | `CasesRegistry` | `cases.canView` | Есть `CasesRegistry.test.tsx` и backend V2 tests |
| `caseDetail` | `/cases/{id}` / fallback | `CaseDetailPage` | `cases.canView` | Есть `CaseDetailPage.test.tsx`; прямой `ppTab=caseDetail` без id уводит в список |
| `clients` | `/clients` | `Clients` | `clients.canView` | Есть набор tests по клиентам и detail |
| `contacts` | `/?ppTab=contacts` | `UnifiedContacts` | `clients.canView || legalEntities.canView` | Есть `UnifiedContacts.test.tsx` |
| `contactsClients` | `/?ppTab=contactsClients` | `UnifiedContacts` | `clients.canView` | Покрыто через contacts tests частично |
| `contactsLegal` | `/?ppTab=contactsLegal` | `UnifiedContacts` | `legalEntities.canView` | Покрыто через contacts tests частично |
| `contactsExternal` | `/?ppTab=contactsExternal` | `Contacts` | `clients.canView` | Прямой test на старый contacts UI не найден |
| `clientDetail` | `/clients/{id}` / fallback | `ClientDetail` | `clients.canView` | Есть integration tests; без id fallback в список |
| `accounts` | `/invoices` | `Accounts` | `accounts.canView` | Есть `Accounts.integration.test.tsx` |
| `accountsIncome` | `/invoices?ppTab=accountsIncome` | `Accounts lockType Income` | `accounts.canView` | Есть routing/navigation tests |
| `accountsExpense` | `/invoices?ppTab=accountsExpense` | `Accounts lockType Expense` | `accounts.canView` | Есть routing tests |
| `installments` | `/?ppTab=installments` | `Installments` | `accounts.canView` | Есть indirect invoice/installment tests |
| `acts` | `/acts` | `Acts` | `acts.canView` | Есть tests для modal/detail, backend acts tests |
| `actsIncome` | `/acts?ppTab=actsIncome` | `Acts lockType Income` | `acts.canView` | Покрыто routing tests |
| `actsExpense` | `/acts?ppTab=actsExpense` | `Acts lockType Expense` | `acts.canView` | Покрыто routing tests |
| `actCreate` | `/acts/new` | `ActCreatePage` | `acts.canCreate` | Прямого test для страницы создания не видно |
| `actDetail` | `/acts/{id}` | `ActDetailPage` | `acts.canView` | Есть `ActDetailPage.test.tsx`; без id показывает ограничение/fallback |
| `payments` | `/payments` | `Payments` | `payments.canView` | Есть `Payments.integration.test.tsx` |
| `paymentsIncome` | `/payments?ppTab=paymentsIncome` | `Payments lockType Income` | `payments.canView` | Покрыто routing/integration частично |
| `paymentsExpense` | `/payments?ppTab=paymentsExpense` | `Payments lockType Expense` | `payments.canView` | Покрыто routing/integration частично |
| `moneyBank` | `/?ppTab=moneyBank` | `MoneyOperations source=Bank` | `payments.canView` | Есть `MoneyOperations.integration.test.tsx` |
| `moneyCash` | `/?ppTab=moneyCash` | `MoneyOperations source=Cash` | `payments.canView` | Покрыто через MoneyOperations tests частично |
| `moneyCards` | `/?ppTab=moneyCards` | `MoneyOperations source=Card` | `payments.canView` | Покрыто через MoneyOperations tests частично |
| `moneyEMoney` | `/?ppTab=moneyEMoney` | `MoneyOperations source=EMoney` | `payments.canView` | Есть navigation routing coverage |
| `moneyTotal` | `/?ppTab=moneyTotal` | `MoneyOperations source=Total` | `payments.canView` | Покрыто через MoneyOperations tests частично |
| `contracts` | `/contracts` | `Contracts` | `contracts.canView` | Есть `ContractDetailPage.integration.test.tsx`; create form без direct test |
| `contractCreate` | `/?ppTab=contractCreate` | `ContractCreatePage` | `contracts.canCreate` | Нет прямого test |
| `contractDetail` | `/contracts/{id}` | `ContractDetailPage` | `contracts.canView` | Есть integration test; без id показывает ограничение/fallback |
| `companyProfile` | `/?ppTab=companyProfile` | `CompanyProfile` | admin only | Есть utils tests, прямого page test не видно |
| `profile` | `/?ppTab=profile` | `ProfilePage` | авторизованный пользователь | Есть `ProfilePage.test.tsx` |
| `users` | `/?ppTab=users` | `Users` | admin only | Backend admin tests есть, page direct test не видно |
| `userDetail` | `/?ppTab=userDetail` | `UserDetailPage` | admin only | Нет прямого test |
| `roles` | `/?ppTab=roles` | `Roles` | admin или `roles.canView` | Есть audit panel tests, modal без direct test |
| `audit` | `/?ppTab=audit` | `ObjectAuditPage` | admin или `audit.canView` | Backend/object audit tests есть, page direct test не видно |
| `userActivity` | `/?ppTab=userActivity` | `UserActivity` | admin only | Есть settings/user online tests |
| `yandexDisk` | `/?ppTab=yandexDisk` | `AppSettings initialSection` | admin или `yandexDisk.canView` | Есть `YandexDisk.test.tsx` |
| `s3Storage` | `/?ppTab=s3Storage` | `AppSettings initialSection` | admin или `s3Storage.canView` | Есть `S3Storage.test.tsx` |
| `dictionaries` | `/?ppTab=dictionaries` | `Dictionaries` | `dictionaries.canView` | Есть `Dictionaries.test.tsx` |
| `settings` | `/?ppTab=settings` | `AppSettings` | audit/notifications/storage/admin settings access | Есть набор settings tests |

## 8. Адаптивность

Проверенная матрица:

- mobile: 390x844;
- tablet: 768x1024;
- desktop: 1440x900.

Результат:

- desktop: критичного горизонтального overflow не обнаружено;
- tablet: критичного горизонтального overflow не обнаружено;
- mobile: найден overflow 59px в трех task-разделах.

### Mobile overflow в задачах

| Раздел | URL | Viewport | Overflow |
|---|---|---:|---:|
| Журнал задач | `/tasks` | 390x844 | 59px |
| Согласования задач | `/tasks/approvals` | 390x844 | 59px |
| Календарь задач | `/tasks/calendar` | 390x844 | 59px |

Рекомендация:

- проверить верхние панели `TaskJournal`: фильтры, переключатели вида, bulk actions, календарные controls;
- добавить responsive test/e2e smoke для `TaskJournal` на 390px;
- для фиксированных toolbar/table controls использовать `min-w-0`, wrapping, horizontal scroll inside local container, а не на документе.

## 9. API и backend

Статистика:

- backend source `.cs` без test project: 880;
- backend test files: 67;
- контроллеров: 54;
- найдено endpoints: 663;
- контроллеров с найденными тестовыми связями: 36;
- контроллеров без найденных controller-tests: 18;
- контроллеров с `[Authorize...]`: 51;
- контроллеры только с `[AllowAnonymous...]`: `ExternalAuthController`, `FeatureFlagsController`, `HealthController`;
- контроллеров без `[Authorize...]` и без `[AllowAnonymous...]`: 0.

Вывод по авторизации: на уровне атрибутов базовая защита выглядит последовательно. Высокорисковых контроллеров без явного auth/anonymous marker не найдено.

### Контроллеры без найденных тестов

| Контроллер | Endpoints | Авторизация | Риск |
|---|---:|---|---|
| `NotificationsController.cs` | 22 | `[Authorize]` + 1 anonymous endpoint | Высокий: уведомления, realtime, пользовательские события |
| `DictionariesReadController.cs` | 18 | `[Authorize]` | Средний: справочники используются многими формами |
| `UsersController.cs` | 10 | `[Authorize(Policy="Admin")]` | Высокий: администрирование пользователей |
| `AdminUsersController.cs` | 8 | `[Authorize(Policy="Admin")]` | Высокий: администрирование пользователей |
| `AuthProvidersAdminController.cs` | 8 | `[Authorize(Policy="Admin")]` | Высокий: внешняя авторизация |
| `ExternalAuthController.cs` | 8 | `[AllowAnonymous]` | Высокий: OAuth/callback/login flows |
| `ContactsController.cs` | 7 | `[Authorize]` | Средний: контакты |
| `RolesController.cs` | 7 | `[Authorize]` | Высокий: роли и права |
| `V1/CasesV1Controller.cs` | 5 | `[Authorize]` | Средний: legacy API |
| `V1/ClientsV1Controller.cs` | 5 | `[Authorize]` | Средний: legacy API |
| `V1/PaymentsV1Controller.cs` | 5 | `[Authorize]` | Средний: legacy API |
| `UserSettingsController.cs` | 4 | `[Authorize]` | Средний: пользовательские настройки |
| `ClientReferenceDataController.cs` | 3 | `[Authorize]` | Низкий/средний |
| `FormRequirementsController.cs` | 3 | `[Authorize]` | Средний: обязательность полей |
| `ClientPassportController.cs` | 2 | `[Authorize]` | Средний: персональные данные |
| `V2/StatsV2Controller.cs` | 2 | `[Authorize]` | Низкий/средний |
| `HealthController.cs` | 1 | `[AllowAnonymous]` | Низкий |
| `InstallmentsController.cs` | 1 | `[Authorize]` | Средний |

Рекомендация: сначала добавить тесты для `NotificationsController`, `RolesController`, `ExternalAuthController`, `AuthProvidersAdminController`, `UsersController/AdminUsersController`, потому что там выше цена ошибки.

### Самые большие API-поверхности

| Контроллер | Endpoints | Тесты |
|---|---:|---|
| `V2/CasesV2Controller.cs` | 89 | Есть |
| `DictionariesAdminController.cs` | 74 | Есть сервисные/статусные tests |
| `ReportsController.cs` | 36 | Есть |
| `YandexDiskController.cs` | 31 | Есть |
| `InvoicesController.cs` | 29 | Хорошее покрытие |
| `TasksController.cs` | 26 | Есть |
| `MoneyOperationsController.cs` | 25 | Есть |
| `ContractsController.cs` | 23 | Есть |
| `NotificationsController.cs` | 22 | Не найдено прямых tests |
| `CashOrdersController.cs` | 19 | Есть |

## 10. Права доступа

Frontend:

- `App.tsx` содержит централизованный `canViewTab` и route-level guards;
- `Navigation.tsx` строит меню на основе `useRolePermissions`;
- detail/create tabs дополнительно проверяют `canCreate`, `canEdit`, `canDelete`;
- настройки, аудит, пользователи и активность пользователей разделены на admin/permission-based branches.

Backend:

- `Program.cs` использует `UseAuthentication()` и `UseAuthorization()`;
- контроллеры явно маркированы `[Authorize...]` или `[AllowAnonymous...]`;
- глобальной fallback policy не найдено, поэтому важно не добавлять новые контроллеры без явного auth/anonymous атрибута;
- текущий скан не нашел контроллеров без явной auth/anonymous маркировки.

Рекомендация:

- добавить тест-guard, который падает, если новый controller не содержит `[Authorize...]` или `[AllowAnonymous...]`;
- добавить tests на отрицательные сценарии permissions для ролей: view/create/edit/delete/export;
- отдельно проверить `NotificationsController` и `RolesController` на отсутствие доступа у обычной роли.

## 11. Realtime/SignalR

Во время browser-smoke проверок в консоли многократно повторялась ошибка:

`Failed to start the connection: The connection was stopped during negotiation`

Вероятная зона проблемы:

- frontend realtime client;
- URL/API base для SignalR в dev-режиме;
- CORS/credentials/websocket negotiation;
- lifecycle подключения при быстрых переходах между маршрутами.

Рекомендация:

1. Воспроизвести отдельно на одной странице без быстрой навигации.
2. Проверить фактический URL negotiate-запроса и HTTP status.
3. Добавить integration/smoke test для `RealtimeNotificationToasts`.
4. Проверить, не создаются ли повторные подключения при каждом route transition.

## 12. Производительность и bundle

Production build проходит.

Предупреждение Vite:

- `AppSettings` chunk: около 524.92 kB minified, 105.92 kB gzip.

Рекомендация:

- разделить `AppSettings` на lazy sub-sections;
- особенно вынести тяжелые настройки: database maintenance, storage, notification settings, document templates, imports;
- оставить общий shell настроек легким, а секции грузить по требованию.

## 13. Data/time audit

`npm run audit:dates` успешно выполнился:

- просканировано 1311 файлов;
- найдено 7313 date/time matches;
- сгенерирован/обновлен `docs/date-time-inventory.md`.

Рекомендация: использовать этот inventory как отдельную карту рисков по часовым поясам. Особенно связать с lint warnings в `InvoiceModal`, `ActModal`, `PaymentModal`, `Payments`, `TaskJournal`.

## 14. Файлы и рабочая копия

Во время аудита были обнаружены/созданы изменения:

- `src/components/Tasks/TaskJournal.test.tsx` - ранее удалены неиспользуемые test helpers, чтобы `npm run lint` проходил без errors;
- `docs/date-time-inventory.md` - обновлен штатной командой `npm run audit:dates`;
- `backend/appsettings.Development.json` - был изменен до начала моих правок, я его не редактировал;
- `docs/full-project-audit-2026-06-10.md` - новый файл с этим отчетом.

В отчет не включены секреты из локальной конфигурации.

## 15. Рекомендации по приоритету

### P0/P1: исправить видимые и runtime-риски

1. Исправить mobile overflow 59px в `TaskJournal` на 390px.
2. Разобрать повторяющуюся SignalR negotiation error.
3. Добавить controller guard test: каждый controller обязан иметь `[Authorize...]` или `[AllowAnonymous...]`.

### P1: усилить тесты прав и критичных настроек

1. `RolePermissionsModal.tsx`: добавить tests на изменение прав, сохранение, disabled/read-only states.
2. `RolesController.cs`: добавить backend tests на доступ, CRUD ролей, audit trail.
3. `UsersController.cs` и `AdminUsersController.cs`: добавить tests на admin-only доступ и protected user flows.
4. `AuthProviderSettings.tsx`, `AuthProvidersAdminController.cs`, `ExternalAuthController.cs`: покрыть OAuth/admin settings flows.

### P1/P2: усилить тесты данных и импорта

1. `SpreadsheetImportSettings.tsx` и `SpreadsheetImportPreviewModal.tsx`: добавить UI tests на preview/apply/cancel/error states.
2. `NotificationSettings.tsx` и `NotificationsController.cs`: добавить tests на preferences, templates, delivery, inbox/realtime.
3. `LegalEntityFinanceSection.tsx`: покрыть расчеты, empty/error/loading states, переходы к счетам/платежам.

### P2: техническое качество

1. Разобрать 18 lint warnings.
2. Разбить `AppSettings` chunk.
3. Добавить responsive smoke в CI минимум для:
   - `/tasks`;
   - `/tasks/approvals`;
   - `/tasks/calendar`;
   - `/invoices`;
   - `/payments`;
   - `/cases`;
   - `/settings`.

## 16. Что соответствует требованиям

- Сборка frontend проходит.
- Unit tests frontend проходят.
- Integration tests frontend проходят.
- Backend tests проходят.
- Backend подключается к локальной БД.
- `/api/health` работает.
- Все 47 маршрутов открываются в авторизованной локальной сессии.
- На desktop/tablet не найден document-level horizontal overflow.
- Базовая схема frontend permissions централизована и читаема.
- Backend controllers имеют явную auth/anonymous маркировку.
- Нет найденных `TODO/FIXME/HACK/debugger/console.log` в `src`/`backend`.

## 17. Что не соответствует или требует доработки

- Mobile overflow в task-разделах.
- Realtime SignalR negotiation error в браузере.
- Недостаток direct tests у части крупных frontend-компонентов.
- Недостаток direct controller tests у части API.
- Lint warnings по hook dependencies.
- Крупный chunk настроек.
- Detail routes без ID показывают fallback/ограничение; это допустимо, но лучше иметь явную UX-страницу "объект не выбран" или redirect в список.

## 18. Краткий вывод

Проект находится в рабочем состоянии: тесты и сборка проходят, локальный backend и frontend стартуют, основные разделы открываются. Самые важные реальные дефекты после аудита: mobile overflow в задачах и повторяющаяся SignalR negotiation error. Самые важные зоны для усиления качества: permissions/roles/users, уведомления/realtime, импорт, внешняя авторизация и крупные настройки.

## 19. Дополнительный проход: что было добрано

Повторный проход добавил то, что в первой версии было слишком сжато:

- полный список frontend-компонентов без найденной тестовой связи;
- полную таблицу backend-контроллеров с количеством endpoints, HTTP-методами, auth-маркерами и тестами;
- распределение всех найденных API endpoints по HTTP-методам;
- unauth-smoke проверку API без токена;
- оценку покрытия `src/services/api.ts` тестами `src/services/api.test.ts`.

Новые уточненные метрики:

| Область | Значение |
|---|---:|
| Frontend component/support files в `src/components` | 127 |
| С найденной тестовой связью | 94 |
| Без найденной тестовой связи | 33 |
| Frontend test files | 84 |
| Backend controllers | 54 |
| Controllers с найденной тестовой связью | 36 |
| Controllers без найденной тестовой связи | 18 |
| API endpoints по атрибутам `[Http...]` | 663 |
| Controllers без auth/anonymous marker | 0 |
| `apiService` methods, извлеченные эвристически | 482 |
| `apiService` methods с прямым упоминанием в `api.test.ts` | 85 |
| `apiService` methods без прямого упоминания в `api.test.ts` | 397 |

Распределение endpoints по HTTP-методам:

| Метод | Количество |
|---|---:|
| GET | 276 |
| POST | 221 |
| PUT | 91 |
| DELETE | 64 |
| PATCH | 11 |

## 20. Дополнительный unauth-smoke API

Цель: проверить не только наличие `[Authorize...]`/`[AllowAnonymous...]` в коде, но и фактическое поведение локального backend без токена.

Результат: публичные endpoints отвечают `200`, защищенные endpoints отвечают `401`. Это соответствует ожидаемой модели доступа.

| Endpoint | Ожидание | Факт | Вывод |
|---|---:|---:|---|
| `GET /api/health` | 200 | 200 | OK, публичный health |
| `GET /api/features` | 200 | 200 | OK, публичные feature flags |
| `GET /api/features/legal-integrations/diagnostics` | 200 | 200 | OK, публичная диагностика legal flags |
| `GET /api/auth/providers` | 200 | 200 | OK, публичные настройки внешнего входа |
| `GET /api/users` | 401/403 | 401 | OK, защищено |
| `GET /api/auth/admin/users` | 401/403 | 401 | OK, защищено |
| `GET /api/roles` | 401/403 | 401 | OK, защищено |
| `GET /api/clients` | 401/403 | 401 | OK, защищено |
| `GET /api/notifications/settings` | 401/403 | 401 | OK, защищено |
| `GET /api/database/status` | 401/403 | 401 | OK, защищено |
| `GET /api/tasks` | 401/403 | 401 | OK, защищено |
| `GET /api/dictionaries/deal-types` | 401/403 | 401 | OK, защищено |

Важное уточнение: публичность `GET /api/features/legal-integrations/diagnostics` и `GET /api/auth/providers` выглядит намеренной по `[AllowAnonymous]`. Но так как эти endpoints раскрывают конфигурационное состояние, их стоит держать в тестах и продуктовой модели безопасности явно, а не как случайный побочный эффект.

## 21. Полный список frontend-компонентов без найденной тестовой связи

Эта таблица показывает файлы, для которых автоматический поиск не нашел прямого соседнего test-файла или явного упоминания компонента в существующих тестах. Это не доказывает, что поведение совсем не покрыто: часть может проверяться через родительские интеграционные сценарии. Но именно прямой проверяемости здесь не видно.

| Файл | Строк | Приоритет |
|---|---:|---|
| `src/components/Settings/NotificationSettings.tsx` | 2148 | P1 |
| `src/components/LegalEntities/LegalEntityFinanceSection.tsx` | 1386 | P1 |
| `src/components/Settings/AuthProviderSettings.tsx` | 1147 | P1 |
| `src/components/Roles/RolePermissionsModal.tsx` | 1002 | P1 |
| `src/components/Settings/InvoiceDocumentTemplateSettings.tsx` | 960 | P1/P2 |
| `src/components/Settings/SpreadsheetImportSettings.tsx` | 906 | P1 |
| `src/components/Settings/SpreadsheetImportPreviewModal.tsx` | 785 | P1 |
| `src/components/Users/UserDetailPage.tsx` | 675 | P1 |
| `src/components/Auth/ExternalAuthPanel.tsx` | 605 | P1/P2 |
| `src/components/ObjectAudit/ObjectAuditPage.tsx` | 569 | P2 |
| `src/components/Contracts/ContractCreatePage.tsx` | 503 | P1 |
| `src/components/Contracts/ContractCreateForm.tsx` | 433 | P1 |
| `src/components/Settings/UserMergeSettings.tsx` | 411 | P1 |
| `src/components/Contacts/ContactEditorModal.tsx` | 407 | P2 |
| `src/components/Settings/AppVersionSettings.tsx` | 403 | P2 |
| `src/components/ui/ObjectAuditHistoryCard.tsx` | 328 | P2 |
| `src/components/AppVersion/AppVersionModal.tsx` | 295 | P2 |
| `src/components/Statistics/TypeStatsBlock.tsx` | 289 | P3 |
| `src/components/Accounts/InvoiceLineItemLookupDialog.tsx` | 282 | P2 |
| `src/components/Contacts/YealinkImportPreviewModal.tsx` | 247 | P2 |
| `src/components/LegalEntities/LegalEntityInlineEditor.tsx` | 233 | P2 |
| `src/components/Roles/RoleModal.tsx` | 191 | P1/P2 |
| `src/components/Calculator/Calculator.tsx` | 150 | P3 |
| `src/components/Statistics/StatCardItem.tsx` | 144 | P3 |
| `src/components/ui/AuditInfoCard.tsx` | 119 | P3 |
| `src/components/Acts/ActCreatePage.tsx` | 114 | P1/P2 |
| `src/components/ui/TextPromptModal.tsx` | 114 | P3 |
| `src/components/ui/RouteErrorBoundary.tsx` | 111 | P2 |
| `src/components/Calendar/PaymentCard.tsx` | 92 | P3 |
| `src/components/Auth/AwaitingApproval.tsx` | 71 | P3 |
| `src/components/Calendar/SummaryCards.tsx` | 70 | P3 |
| `src/components/ui/ConfirmDialog.tsx` | 59 | P2 |
| `src/components/Clients/clientLookupOptions.ts` | 49 | P3 |

Практичный порядок закрытия:

1. `RolePermissionsModal`, `RoleModal`, `Users/UserDetailPage` - права и пользователи.
2. `NotificationSettings`, `ExternalAuthPanel`, `AuthProviderSettings` - интеграции и уведомления.
3. `SpreadsheetImportSettings`, `SpreadsheetImportPreviewModal`, `UserMergeSettings` - операции с высоким риском изменения данных.
4. `LegalEntityFinanceSection`, `ContractCreatePage`, `ContractCreateForm`, `ActCreatePage` - финансовые и юридические документы.
5. UI primitives и небольшие карточки - после критичных workflow.

## 22. Полная таблица backend-контроллеров

Колонки:

- `Auth` - количество `[Authorize...]`;
- `Anon` - количество `[AllowAnonymous...]`;
- `Perm` - эвристика упоминаний permission checks, `CanView/CanCreate/...`, `Forbidden`;
- `Tests` - найденные прямые или косвенные backend tests.

| Контроллер | Route prefix | Endpoints | Methods | Auth | Anon | Perm | Tests |
|---|---|---:|---|---:|---:|---:|---|
| `V2/CasesV2Controller.cs` | `api/v2/cases` | 89 | GET 38, POST 30, PATCH 6, PUT 8, DELETE 7 | 1 | 0 | 149 | yes |
| `DictionariesAdminController.cs` | `api/dictionaries` | 74 | POST 37, PUT 18, DELETE 18, GET 1 | 1 | 0 | 0 | yes |
| `ReportsController.cs` | `api/reports` | 36 | GET 36 | 1 | 0 | 18 | yes |
| `YandexDiskController.cs` | `api/yandex-disk` | 31 | GET 11, PUT 5, DELETE 3, POST 12 | 1 | 0 | 76 | yes |
| `InvoicesController.cs` | `api/[controller]` | 29 | GET 8, POST 16, PUT 4, DELETE 1 | 1 | 0 | 6 | yes |
| `TasksController.cs` | `api/tasks` | 26 | GET 12, PUT 2, POST 8, DELETE 2, PATCH 2 | 1 | 0 | 32 | yes |
| `MoneyOperationsController.cs` | `api/money-operations` | 25 | GET 14, POST 9, PUT 1, DELETE 1 | 1 | 0 | 11 | yes |
| `ContractsController.cs` | `api/[controller]` | 23 | GET 9, POST 9, PUT 4, DELETE 1 | 1 | 0 | 6 | yes |
| `NotificationsController.cs` | `api/notifications` | 22 | GET 6, PUT 6, POST 8, DELETE 2 | 1 | 1 | 25 | no |
| `CashOrdersController.cs` | `api/cash-orders` | 19 | GET 10, POST 7, PUT 1, DELETE 1 | 1 | 0 | 0 | yes |
| `DictionariesReadController.cs` | `api/dictionaries` | 18 | GET 18 | 1 | 0 | 0 | no |
| `LegalEntitiesController.cs` | `api/legal-entities` | 17 | POST 7, DELETE 1, GET 6, PUT 3 | 5 | 0 | 0 | yes |
| `ActsController.cs` | `api/[controller]` | 15 | GET 5, POST 8, PUT 1, DELETE 1 | 1 | 0 | 6 | yes |
| `DatabaseAdminController.cs` | `api/database` | 15 | GET 5, PUT 1, POST 7, DELETE 2 | 1 | 0 | 0 | yes |
| `PaymentsController.cs` | `api/[controller]` | 13 | GET 3, POST 5, PUT 4, DELETE 1 | 1 | 0 | 0 | yes |
| `S3StorageController.cs` | `api/s3-storages` | 13 | GET 4, POST 6, DELETE 2, PUT 1 | 1 | 0 | 31 | yes |
| `ClientsController.cs` | `api/[controller]` | 12 | GET 5, POST 5, PUT 1, DELETE 1 | 1 | 0 | 0 | yes |
| `NotificationInboxController.cs` | `api/notifications/inbox` | 10 | GET 3, POST 3, PATCH 3, DELETE 1 | 1 | 0 | 0 | yes |
| `UserActivityController.cs` | `api/user-activity` | 10 | GET 4, POST 4, PUT 1, DELETE 1 | 9 | 0 | 0 | yes |
| `UsersController.cs` | `api/users` | 10 | POST 4, DELETE 1, GET 4, PUT 1 | 1 | 0 | 0 | no |
| `AdminUsersController.cs` | `api/auth/admin` | 8 | GET 3, POST 3, PUT 1, DELETE 1 | 1 | 0 | 0 | no |
| `AuthProvidersAdminController.cs` | `api/auth/admin/providers` | 8 | GET 1, PUT 6, POST 1 | 1 | 0 | 0 | no |
| `ExternalAuthController.cs` | `api/auth` | 8 | GET 4, POST 4 | 0 | 8 | 0 | no |
| `GoogleCalendarController.cs` | `api/google-calendar` | 8 | GET 3, POST 3, PUT 1, DELETE 1 | 6 | 2 | 0 | yes |
| `AppVersionController.cs` | `api/app-version` | 7 | GET 3, POST 2, PUT 1, DELETE 1 | 5 | 0 | 0 | yes |
| `ContactsController.cs` | `api/contacts` | 7 | GET 2, PUT 2, POST 2, DELETE 1 | 1 | 0 | 0 | no |
| `RolesController.cs` | `api/roles` | 7 | GET 3, POST 2, PUT 1, DELETE 1 | 1 | 0 | 32 | no |
| `V2/PaymentsV2Controller.cs` | `api/v2/payments` | 7 | GET 4, POST 1, PUT 1, DELETE 1 | 1 | 0 | 4 | yes |
| `YandexCalendarController.cs` | `api/yandex-calendar` | 7 | GET 3, POST 2, PUT 1, DELETE 1 | 6 | 1 | 0 | yes |
| `ProfileController.cs` | `api/profile` | 6 | GET 2, PUT 1, POST 2, DELETE 1 | 1 | 0 | 0 | yes |
| `BanksController.cs` | `api/banks` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 4 | 0 | 0 | yes |
| `CalendarSyncController.cs` | `api/calendar-sync` | 5 | GET 4, PUT 1 | 3 | 0 | 0 | yes |
| `CasesController.cs` | `api/[controller]` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 1 | 0 | 0 | yes |
| `MoneyAccountsController.cs` | `api/money-accounts` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 4 | 0 | 0 | yes |
| `RolePermissionsController.cs` | `api/role-permissions` | 5 | GET 3, PUT 1, DELETE 1 | 1 | 0 | 106 | yes |
| `V1/CasesV1Controller.cs` | `api/v1/cases` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 1 | 0 | 0 | no |
| `V1/ClientsV1Controller.cs` | `api/v1/clients` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 1 | 0 | 0 | no |
| `V1/PaymentsV1Controller.cs` | `api/v1/payments` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 1 | 0 | 0 | no |
| `V2/ClientsV2Controller.cs` | `api/v2/clients` | 5 | GET 2, POST 1, PUT 1, DELETE 1 | 1 | 0 | 0 | yes |
| `ObjectAuditController.cs` | `api/object-audit` | 4 | GET 3, POST 1 | 1 | 0 | 8 | yes |
| `PowerOfAttorneyController.cs` | `api/clients/{clientId:int}/powers-of-attorney` | 4 | GET 1, POST 1, PUT 1, DELETE 1 | 1 | 0 | 0 | yes |
| `UserSettingsController.cs` | `api/user-settings` | 4 | GET 2, PUT 2 | 3 | 0 | 0 | no |
| `AccountsController.cs` | `api/[controller]` | 3 | GET 3 | 1 | 0 | 0 | yes |
| `AuthController.cs` | `api/[controller]` | 3 | POST 2, GET 1 | 1 | 2 | 0 | yes |
| `ClientBalancesController.cs` | `api/clients/{clientId:int}/balance` | 3 | GET 2, POST 1 | 1 | 0 | 0 | yes |
| `ClientReferenceDataController.cs` | `api/client-reference-data` | 3 | GET 3 | 1 | 0 | 0 | no |
| `FormRequirementsController.cs` | `api/form-requirements` | 3 | GET 2, PUT 1 | 1 | 0 | 0 | no |
| `ClientPassportController.cs` | `api/clients/{clientId:int}/passport` | 2 | GET 1, PUT 1 | 1 | 0 | 0 | no |
| `FeatureFlagsController.cs` | `api/features` | 2 | GET 2 | 0 | 2 | 0 | yes |
| `SystemJobsController.cs` | `api/system/jobs` | 2 | GET 1, POST 1 | 1 | 0 | 0 | yes |
| `V2/StatsV2Controller.cs` | `api/v2/stats` | 2 | GET 2 | 1 | 0 | 0 | no |
| `HealthController.cs` | `api/[controller]` | 1 | GET 1 | 0 | 1 | 0 | no |
| `InstallmentsController.cs` | `api/[controller]` | 1 | POST 1 | 1 | 0 | 0 | no |
| `StatsController.cs` | `api/[controller]` | 1 | GET 1 | 1 | 0 | 0 | yes |

Контроллеры с `Tests = no`, которые стоит закрыть первыми:

1. `NotificationsController.cs` - много endpoints, realtime/settings/templates/delivery.
2. `RolesController.cs` - управление ролями, высокая цена ошибки доступа.
3. `UsersController.cs` и `AdminUsersController.cs` - пользователи и admin-only действия.
4. `AuthProvidersAdminController.cs` и `ExternalAuthController.cs` - внешний вход и callbacks.
5. `ContactsController.cs` - рабочие контакты и импорт/экспорт.
6. `V1/*Controller.cs` - legacy API, чтобы не сломать обратную совместимость.

## 23. Frontend `apiService` coverage

`src/services/api.ts` - большой API-client. Эвристический проход нашел 482 метода. В `src/services/api.test.ts` явно обнаружено 85 методов. Для 397 методов прямого совпадения в этом test-файле не найдено.

Это не значит, что 397 методов совсем не проверяются: часть вызывается из component/integration tests через mocked `apiService`, часть покрывается backend tests, часть может проверяться косвенно. Но отдельный контракт frontend API-client покрыт неравномерно.

Зоны `apiService`, которые требуют приоритетного контрактного покрытия:

- destructive методы: `delete*`, `rollback*`, `cancel*`, `terminate*`, `archive*`;
- финансовые операции: `createPayment`, `updatePayment`, `createInvoice`, `updateInvoice`, `createMoneyOperation`, `transferPaymentRemainder`, `adjustPaymentRemainderTransfer`;
- импорт и миграции: `previewSpreadsheetImport`, `applySpreadsheetImport`, `rollbackSpreadsheetImportBatch`, `importSqliteDatabase`, `importBankStatement`;
- документы и файлы: `download*`, `upload*`, `generate*Document*`, `send*ByEmail`, `send*ToSigning`;
- роли и пользователи: `updateRolePermissions`, `resetRolePermissions`, user settings/admin user flows;
- внешние интеграции: Google/Yandex calendar, Yandex Disk, S3, Telegram, OAuth providers.

Рекомендация: не пытаться покрыть все 397 методов отдельными тестами сразу. Более полезный план:

1. Добавить контрактные tests для всех destructive методов: корректный HTTP method, URL, body, error handling.
2. Добавить matrix tests для download/upload методов: blob handling, file name, content type, authorization error.
3. Для grouped resources (`cases`, `tasks`, `money-operations`, `notifications`, `external auth`) сделать table-driven tests по endpoint map.
4. После этого добавить lightweight coverage guard: новый метод в `apiService` должен иметь хотя бы один test или явный комментарий-исключение.

## 24. Дополнительный проход по frontend support-слоям

Повторно проверены не только страницы и компоненты, но и вспомогательные frontend-слои: hooks, utils, services, contexts, config и types. Это важно, потому что значительная часть логики находится не в JSX-компонентах, а в вычислениях, форматтерах, API-адаптерах, permission helpers и shared state.

| Слой | Файлов | С прямой test-связью | Без прямой test-связи |
|---|---:|---:|---:|
| `src/hooks` | 59 | 24 | 35 |
| `src/utils` | 31 | 23 | 8 |
| `src/services` | 5 | 4 | 1 |
| `src/contexts` | 6 | 3 | 3 |
| `src/config` | 1 | 1 | 0 |
| `src/types` | 5 | 4 | 1 |

Файлы hooks без прямой test-связи:

| Файл | Строк |
|---|---:|
| `src/hooks/usePeriodStats.ts` | 131 |
| `src/hooks/useCaseFinanceReport.ts` | 74 |
| `src/hooks/useCaseSuccessReport.ts` | 74 |
| `src/hooks/useCurrentCasesReport.ts` | 74 |
| `src/hooks/useLegalWorkReport.ts` | 74 |
| `src/hooks/useTaskReport.ts` | 74 |
| `src/hooks/useCaseActivityReport.ts` | 73 |
| `src/hooks/useCashFlowReport.ts` | 73 |
| `src/hooks/useLawyerWorkloadReport.ts` | 73 |
| `src/hooks/useOverdueProceduralDeadlinesReport.ts` | 73 |
| `src/hooks/useAdvanceReport.ts` | 66 |
| `src/hooks/useCashReport.ts` | 66 |
| `src/hooks/useCounterpartyReport.ts` | 66 |
| `src/hooks/useCurrencyReport.ts` | 66 |
| `src/hooks/useDirectionReport.ts` | 66 |
| `src/hooks/useJournalStatusReport.ts` | 66 |
| `src/hooks/useMatchStatusReport.ts` | 66 |
| `src/hooks/useMoneyCategoryReport.ts` | 66 |
| `src/hooks/usePaidAccountReport.ts` | 66 |
| `src/hooks/usePaidInvoiceReport.ts` | 66 |
| `src/hooks/usePaymentOrderReport.ts` | 66 |
| `src/hooks/usePaymentPriorityReport.ts` | 66 |
| `src/hooks/usePaymentPurposeReport.ts` | 66 |
| `src/hooks/usePlanFactReport.ts` | 66 |
| `src/hooks/useProjectReport.ts` | 66 |
| `src/hooks/useSalesPointReport.ts` | 66 |
| `src/hooks/useVatReport.ts` | 66 |
| `src/hooks/useActStatusReport.ts` | 63 |
| `src/hooks/useBankStatementImportReport.ts` | 63 |
| `src/hooks/useCashOrderReport.ts` | 63 |
| `src/hooks/useContractStatusReport.ts` | 63 |
| `src/hooks/useInvoiceStatusReport.ts` | 63 |
| `src/hooks/useSpreadsheetImportReport.ts` | 63 |
| `src/hooks/useSummaryStats.ts` | 29 |
| `src/hooks/useClientStats.ts` | 17 |

Файлы utils/services/contexts/types без прямой test-связи:

| Файл | Строк | Комментарий |
|---|---:|---|
| `src/services/permissionsService.ts` | 160 | Высокий приоритет: frontend-логика прав и видимости действий. |
| `src/contexts/ToastContext.tsx` | 190 | Проверить очереди toast, auto-hide, dismiss и повторные сообщения. |
| `src/types/userActivity.ts` | 84 | Низкий риск, но типы связаны с журналом активности. |
| `src/utils/taskNotificationDefaults.ts` | 71 | Средний риск: дефолты уведомлений для задач. |
| `src/utils/formatters.ts` | 40 | Средний риск: пользовательское отображение чисел/дат/денег. |
| `src/utils/installmentBridge.ts` | 36 | Средний риск: рассрочки и связь с платежами. |
| `src/utils/statsAdapters.ts` | 35 | Средний риск: адаптация данных отчетов. |
| `src/utils/timeZoneOptions.ts` | 32 | Низкий/средний риск: настройки часового пояса. |
| `src/utils/sessionViewState.ts` | 27 | Средний риск: сохранение состояния экранов. |
| `src/contexts/dialogContextCore.ts` | 26 | Низкий риск, core для dialog context. |
| `src/contexts/toastContextCore.ts` | 23 | Низкий риск, core для toast context. |
| `src/utils/formOptions.ts` | 20 | Низкий риск, справочные option helpers. |
| `src/utils/userNotifications.ts` | 10 | Низкий риск, но относится к пользовательским уведомлениям. |

Вывод: основные gaps во frontend support-слое - это report hooks и `permissionsService`. Для hooks лучше не писать 35 почти одинаковых тестов вручную, а сделать table-driven suite, где проверяется общий контракт: загрузка, параметры периода, обработка ошибок, отмена при unmount и корректное обновление состояния.

## 25. Дополнительный проход по backend services/data/models/migrations

Проверены backend-слои за пределами controllers: services, data, models, extensions, helpers, filters, hubs и migrations. Это отдельная зона риска, потому что controller tests не всегда доказывают корректность фоновых процессов, импорта, notification pipeline, OAuth/linking flows и низкоуровневых преобразований.

| Слой | Файлов | С прямой test-связью | Без прямой test-связи |
|---|---:|---:|---:|
| `backend/Services` | 115 | 64 | 51 |
| `backend/Data` | 6 | 2 | 4 |
| `backend/Models` | 292 | 207 | 85 |
| `backend/Extensions` | 8 | 2 | 6 |
| `backend/Helpers` | 1 | 0 | 1 |
| `backend/Filters` | 1 | 0 | 1 |
| `backend/Hubs` | 1 | 0 | 1 |

Крупнейшие service-файлы без прямой test-связи:

| Файл | Строк | Риск |
|---|---:|---|
| `backend/Services/AuthService.Accounts.cs` | 1067 | Высокий: учетные записи, вход, правила auth. |
| `backend/Services/SpreadsheetImportService.Utility.cs` | 1064 | Высокий: импорт таблиц и преобразование данных. |
| `backend/Services/SpreadsheetImportService.Preview.cs` | 926 | Высокий: preview импорта до применения. |
| `backend/Services/SpreadsheetImportService.Xml.cs` | 774 | Высокий: XML parsing/import. |
| `backend/Services/LegalEntities/DadataLegalEntityEnrichmentService.cs` | 719 | Средний/высокий: внешнее обогащение юрлиц. |
| `backend/Services/ExternalAuthService.LinkingFlows.cs` | 656 | Высокий: привязка внешних аккаунтов. |
| `backend/Services/SpreadsheetImportService.State.cs` | 630 | Высокий: состояние batch/import flows. |
| `backend/Services/ExternalAuthService.Helpers.cs` | 583 | Высокий: auth helper logic. |
| `backend/Services/Notifications/NotificationContracts.cs` | 511 | Средний/высокий: контракты уведомлений. |
| `backend/Services/DatabaseImportService.CopyHelpers.cs` | 506 | Высокий: импорт/копирование БД. |
| `backend/Services/SpreadsheetImportService.Parsing.cs` | 487 | Высокий: parsing пользовательских данных. |
| `backend/Services/SpreadsheetImportService.Apply.cs` | 467 | Высокий: применение импорта в данные. |
| `backend/Services/ExternalAuthService.OAuth.cs` | 422 | Высокий: OAuth flow. |
| `backend/Services/ClientCaseDeadlineTaskSyncService.cs` | 405 | Высокий: синхронизация сроков и задач. |
| `backend/Services/SpreadsheetImportService.Rollback.cs` | 386 | Высокий: rollback import batch. |
| `backend/Services/SpreadsheetImportService.ApplyShared.cs` | 294 | Высокий: shared apply logic. |
| `backend/Services/SpreadsheetImportService.Core.cs` | 285 | Высокий: ядро импорта. |
| `backend/Services/Notifications/NotificationRetrySchedulerService.cs` | 260 | Средний/высокий: повторная доставка уведомлений. |
| `backend/Services/Notifications/NotificationTemplateCatalog.cs` | 237 | Средний: шаблоны уведомлений. |
| `backend/Services/InstallmentService.cs` | 210 | Высокий: рассрочки и финансы. |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 133 | Средний/высокий: фоновая синхронизация календаря. |
| `backend/Services/YandexCalendarBackgroundSyncService.cs` | 133 | Средний/высокий: фоновая синхронизация календаря. |
| `backend/Services/UserActivity/UserActivityRetentionService.cs` | 125 | Средний: очистка журнала активности. |

Backend data/helpers/extensions без прямой test-связи:

| Файл | Риск |
|---|---|
| `backend/Data/ApplicationDbContextFactory.cs` | Низкий: design-time/factory, но важно для migrations/tooling. |
| `backend/Data/DesignTimeDbContextFactory.cs` | Низкий: design-time migrations. |
| `backend/Data/SqliteDateTimeConvention.cs` | Средний: правила хранения дат для SQLite. |
| `backend/Data/TaskNotificationDefaultsCache.cs` | Средний: дефолты уведомлений задач. |
| `backend/Extensions/DictionaryEndpointExtensions.cs` | Средний: shared endpoint behavior. |
| `backend/Extensions/UserAccessExtensions.cs` | Высокий: права доступа. |
| `backend/Helpers/SqliteRetryHelper.cs` | Средний: retry для SQLite. |
| `backend/Filters/UserActivityLoggingFilter.cs` | Средний/высокий: аудит пользовательских действий. |
| `backend/Hubs/NotificationsHub.cs` | Средний/высокий: realtime уведомления и SignalR. |

Migrations inventory:

| Группа | Количество |
|---|---:|
| SQLite/Main migrations | 234 |
| PostgreSQL migrations | 167 |
| Всего migration-файлов | 401 |

Последние найденные PostgreSQL migration-файлы включают `20260602053100_AddCalendarSyncFromDatePostgres.cs` и актуальный snapshot. Отдельной проверки применения всех migrations на чистой PostgreSQL базе в этом проходе не было: запускался backend и тесты, но не поднималась независимая PostgreSQL-инстанция для полного migration rehearsal.

Приоритет рекомендаций для backend:

1. Закрыть тестами `UserAccessExtensions` и `permissions`-связанные сценарии, потому что это напрямую влияет на доступ.
2. Закрыть critical paths импорта: preview, apply, rollback, XML parsing, duplicate handling, partial failure.
3. Добавить tests для `ExternalAuthService.*`: linking, callback state, отказ провайдера, уже привязанный аккаунт.
4. Добавить focused tests для notification pipeline: template rendering, retry scheduler, SignalR publisher, inbox updates.
5. Проверить migration rehearsal отдельно для SQLite и PostgreSQL: чистая база, upgrade до последней версии, seed/sync startup.

## 26. Зависимости и уязвимости

Дополнительно проверены зависимости frontend и backend.

| Проверка | Результат |
|---|---|
| `npm audit --audit-level=moderate` | Пройдено, найдено 0 vulnerabilities. |
| `dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive` | Пройдено, vulnerable packages не найдены для `PayPlanner.Api` и `PayPlanner.Api.Tests`. |

Вывод: на момент проверки известных уязвимостей уровня moderate+ в npm-зависимостях не найдено, и NuGet vulnerability scan тоже не нашел уязвимых пакетов. Это не заменяет runtime security review, но dependency hygiene сейчас выглядит нормально.

## 27. Строгий TypeScript и форматирование .NET

Дополнительно запущены проверки, которые не входили в основной happy-path `npm run build`.

| Проверка | Результат |
|---|---|
| `npx tsc -p tsconfig.app.json --noEmit` | Не пройдено. Найдено много TypeScript errors. |
| `dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal` | Не пройдено. Найдены whitespace formatting violations. |

Важно: ранее `npm run build` проходил успешно. Это означает, что текущая production build-команда не ловит часть TypeScript-проблем, которые показывает строгий `tsc --noEmit` по `tsconfig.app.json`. Поэтому статус frontend такой: bundle собирается, но typecheck как отдельный quality gate сейчас красный.

Первые и наиболее показательные TypeScript-проблемы из вывода:

| Файл | Пример проблемы |
|---|---|
| `src/App.tsx:1303` | Сравнение несовместимых literal states: `"contactsExternal"` и `"contactsClients"`. |
| `src/components/Accounts/Accounts.tsx:2553` | Несовпадение типа handler. |
| `src/components/Accounts/Accounts.tsx:3116` | `Invoice | null | undefined` передается туда, где ожидается `Invoice | null`. |
| `src/components/Accounts/Installments.tsx:424` | Возможное обращение к `sortKey`, которого нет в части union-типа. |
| `src/components/Accounts/InvoiceModal.tsx:458` | `InvoiceActLink` не содержит ожидаемые поля legal entity. |
| `src/components/Accounts/InvoiceModal.tsx:1772` | В объекте не хватает `workflowStatus`. |
| `src/components/Acts/ActModal.tsx:574` | Type predicate несовместим с типом параметра. |
| `src/components/Calculator/Calculator.tsx:68` | Используется shorthand `legalEntityId`, но переменная не найдена. |
| `src/components/Calendar/PaymentModal.tsx:481` | Draft invoice без обязательного `isNotForAccounting`. |
| `src/components/Calendar/PaymentModal.tsx:2471` | Строковый статус не совпадает с `PaymentStatus`. |
| `src/components/Cases/CaseDetailPage.tsx` | Несколько ошибок, включая отсутствующий `loadDocuments` и несовпадения типов ответственных/актов. |
| `src/components/Tasks/TaskJournal.tsx:1499` | `replaceAll` недоступен при текущем target/lib. |
| `src/components/Tasks/TaskJournal.tsx:1937` | `(string | null | undefined)[]` передается как `string[]`. |
| `src/utils/acts.ts:104` | Небезопасное приведение к `Act`. |
| `src/utils/clientInvoiceModalApi.test.ts:110` | Test вызывает функцию с аргументом, где тип ожидает 0 аргументов. |

Также множество test-файлов используют `.at(...)`, а текущая TypeScript lib-конфигурация не дает этот метод для массивов. Примеры: `Accounts.integration.test.tsx`, `TaskJournal.test.tsx` и другие. Это может быть либо проблемой `lib` в tsconfig, либо сигналом, что test-файлы не должны входить в `tsconfig.app.json`.

`dotnet format --verify-no-changes` показал whitespace violations. В явном выводе были нарушения в:

- `backend/Controllers/ReportsController.cs`;
- `backend/Services/SpreadsheetImportService.Xml.cs`.

Рекомендация по quality gates:

1. Развести app/test TypeScript configs: production app typecheck отдельно, test typecheck отдельно.
2. Добавить `npm run typecheck` в CI и локальный стандарт проверки, чтобы `npm run build` не был единственным сигналом.
3. Исправить app-level TypeScript errors до включения gate в обязательный режим.
4. Запустить `dotnet format` отдельной задачей и коммитить formatting-only изменения отдельно от бизнес-правок.
5. После исправлений повторить полный цикл: lint, build, unit, integration, backend tests, visual smoke на ключевых маршрутах.

## 28. Что было дописано в этом повторном проходе

После дополнительного добора в отчет добавлены ранее неразложенные зоны:

- frontend support-слои: hooks, utils, services, contexts, config, types;
- backend support-слои: services, data, models, extensions, helpers, filters, hubs;
- migration inventory по SQLite/Main и PostgreSQL;
- dependency vulnerability checks для npm и NuGet;
- строгий TypeScript no-emit check;
- .NET formatting verification.

Главные новые выводы по сравнению с предыдущей версией отчета:

1. В проекте нет найденных npm/NuGet уязвимостей по выполненным vulnerability checks.
2. `npm run build` проходит, но `npx tsc -p tsconfig.app.json --noEmit` сейчас не проходит, поэтому type-safety gate нужно привести в порядок.
3. `.NET` тесты проходят, но `dotnet format --verify-no-changes` не проходит из-за whitespace.
4. Наибольшие непокрытые зоны теперь яснее: report hooks, frontend permissions service, backend import pipeline, external auth, notifications/realtime, access extensions и migrations rehearsal.

## 29. Дополнительный проход по CI, scripts и release gates

Проверены `package.json`, `.github/workflows/deploy.yml`, Vite/Vitest/TypeScript/ESLint configs и backend `.csproj`.

Frontend scripts:

| Script | Назначение | Статус в этом аудите |
|---|---|---|
| `npm run lint` | ESLint по проекту | Ранее пройден, 18 warnings. |
| `npm run build` | Vite production build | Ранее пройден. |
| `npm run test:unit` | Unit tests без `*.integration.test.*` | Ранее пройден: 71 files, 497 tests. |
| `npm run test:integration` | Integration tests по отдельному runner | Ранее пройден: 13 files, 224 tests. |
| `npm run test:guard` | Routing/navigation guard tests | Дополнительно пройден: 2 files, 30 tests. |
| `npm run test:ci` | `test:guard` + `test:unit` | Дополнительно пройден, локально занял около 261 секунд. |
| `npm run audit:dates` | Date/time inventory | Ранее пройден, обновил `docs/date-time-inventory.md`. |
| `npm run test:legal-e2e:public` | Public legal smoke: frontend shell + features | Дополнительно пройден. |
| `npm run test:legal-readiness:frontend` | Frontend-only readiness smoke | Дополнительно пройден, создал artifact в `.codex-build/legal-readiness/...`. |
| `node scripts/run-legal-load-check.mjs --scenarios=features --iterations=3 --warmups=1 --concurrency=1` | Public load check для `/features` | Дополнительно пройден: 3 requests, 0 errors, p95 6 ms. |

GitHub Actions workflow `.github/workflows/deploy.yml`:

| Этап | Есть в CI |
|---|---|
| Backend restore | yes |
| Backend tests | yes |
| Backend publish | yes |
| Frontend install via `npm ci` | yes |
| `npm run test:ci` | yes |
| `npm run build` | yes |
| Deploy to VPS + health checks | yes |
| `npm run lint` | no |
| `npx tsc -p tsconfig.app.json --noEmit` | no |
| `npm run test:integration` | no |
| `npm audit` | no |
| `dotnet list package --vulnerable` | no |
| `dotnet format --verify-no-changes` | no |
| `npm run audit:dates` | no |
| Visual smoke на ключевых маршрутах | no |

Вывод: deploy pipeline закрывает backend tests, frontend guard/unit и build, но не закрывает lint, strict typecheck, frontend integration tests, dependency audit, formatting и визуальный smoke. Это объясняет, почему `npm run build` может быть зеленым при красном `tsc --noEmit`.

Рекомендация: добавить отдельный pre-deploy quality job до публикации артефактов:

1. `npm run lint`.
2. `npx tsc -p tsconfig.app.json --noEmit` после исправления текущих TypeScript errors.
3. `npm run test:integration`.
4. `npm audit --audit-level=moderate`.
5. `dotnet list backend/PayPlanner.Api.sln package --vulnerable --include-transitive`.
6. `dotnet format backend/PayPlanner.Api.sln --verify-no-changes --no-restore`.

## 30. Дополнительный public legal smoke

На время проверки были временно подняты локальные backend и frontend, после проверки процессы остановлены.

| Проверка | Результат |
|---|---|
| `npm run test:legal-e2e:public` | passed |
| `frontend-shell` scenario | ok, примерно 157 ms |
| `features` scenario | ok, примерно 22 ms |
| `npm run test:legal-readiness:frontend` | passed |
| `node scripts/run-legal-load-check.mjs --scenarios=features --iterations=3 --warmups=1 --concurrency=1` | passed |
| Public `/features` load sample | 3 requests, 0 errors, `200:3`, avg 4.3 ms, p95 6 ms |

Важно: это public-only проверка. Authenticated legal/readiness/load scenarios не запускались, потому что для них нужен тестовый пользователь/token и безопасная договоренность, какие данные можно трогать. В проекте такие сценарии есть: role audit, cases registry, legacy clients/invoices/payments/tasks, calendar reports, legal reports и automation candidates.

Рекомендация: завести отдельного тестового пользователя/роль для authenticated readiness и прогонять:

- `test:legal-readiness:backup`;
- `test:legal-release-gate`;
- `test:legal-roles:strict`;
- `test:legal-load` с ограниченным набором authenticated scenarios.

## 31. Маршруты, navigation guards и permission wiring

Проверен `src/appRouting.ts`, routing tests и permission wiring в `src/App.tsx`.

`src/appRouting.ts` содержит 48 tab values:

- `calendar`, `notifications`;
- `tasks`, `tasksApprovals`, `tasksCalendar`;
- `reports`, `calculator`;
- `legalEntities`, `cases`, `caseDetail`;
- `clients`, `clientDetail`;
- `contacts`, `contactsClients`, `contactsLegal`, `contactsExternal`;
- `accounts`, `accountsIncome`, `accountsExpense`, `installments`;
- `acts`, `actsIncome`, `actsExpense`, `actCreate`, `actDetail`;
- `payments`, `paymentsIncome`, `paymentsExpense`;
- `moneyBank`, `moneyCash`, `moneyCards`, `moneyEMoney`, `moneyTotal`;
- `contracts`, `contractCreate`, `contractDetail`;
- `companyProfile`, `profile`, `users`, `userDetail`, `roles`, `audit`, `userActivity`;
- `yandexDisk`, `s3Storage`, `dictionaries`, `settings`.

Deep-link helpers покрывают:

- `/payments`, `/payments/{id}`;
- `/tasks`, `/tasks/{id}`, `/tasks/approvals`, `/tasks/calendar`;
- `/clients`, `/clients/{id}`;
- `/cases`, `/cases/{id}`;
- `/contracts`, `/contracts/{id}`;
- `/acts`, `/acts/new`, `/acts/{id}`;
- `/notifications`;
- `/invoices`, `/invoices/{id}`.

Guard tests:

| Test file | Что проверяет | Статус |
|---|---|---|
| `src/App.routing.test.ts` | URL parsing, initial tab, return tabs, href generation | passed |
| `src/components/Navigation/Navigation.test.tsx` | real links, SPA click handling, submenu links | passed |

Permission wiring в `App.tsx`:

| Раздел | Guard |
|---|---|
| Calendar | `permissions.calendar.canView` |
| Tasks/approvals/calendar | `permissions.tasks.canView` |
| Reports | `permissions.reports.canView` |
| Calculator | `permissions.calculator.canView` |
| Legal entities | `permissions.legalEntities.canView` |
| Cases/detail | `permissions.cases.canView`, create/edit/delete передаются отдельно |
| Clients/detail | `permissions.clients.canView` |
| Contacts all | `permissions.clients.canView || permissions.legalEntities.canView` |
| Contacts clients/external | `permissions.clients.canView` |
| Contacts legal | `permissions.legalEntities.canView` |
| Accounts/installments | `permissions.accounts.canView` |
| Acts/detail | `permissions.acts.canView` |
| Act create | `permissions.acts.canCreate` |
| Payments/money operations | `permissions.payments.canView` |
| Contracts/detail | `permissions.contracts.canView` |
| Contract create | `permissions.contracts.canCreate` |
| Company profile/users | admin role |
| Roles | admin role or `permissions.roles.canView` |
| Audit/user activity | admin role or audit/settings guards |
| Yandex Disk/S3/settings | соответствующие settings/integration guards |
| Dictionaries | `permissions.dictionaries.canView` |

Отдельная находка: строка `src/App.tsx:1303` уже попала в TypeScript errors. В ветке `case 'contactsExternal'` сравнивается `activeTab === 'contactsClients'`, хотя в этой ветке `activeTab` уже сужен до `'contactsExternal'`. Runtime-эффект, вероятно, небольшой, но это показатель, что часть route state логики требует typecheck cleanup.

## 32. Backend auth attribute map

Повторно проверены auth-атрибуты на 54 backend controllers. После корректного пересчета, учитывающего порядок `[Authorize]` и `[Http...]`, endpoints без явного class/action auth или `[AllowAnonymous]` не найдено.

| Метрика | Значение |
|---|---:|
| Controllers | 54 |
| HTTP endpoints, найденных статическим парсером | 657 |
| Controllers без class-level `[Authorize]` | 6 |
| `[AllowAnonymous]` endpoints | 17 |
| Endpoints без очевидного auth/anonymous атрибута | 0 |

Controllers без class-level `[Authorize]`, но с action-level правилами:

| Controller | Публичные endpoints | Комментарий |
|---|---:|---|
| `ExternalAuthController.cs` | 8 | Ожидаемо публичные OAuth/external auth endpoints. |
| `AuthController.cs` | 2 | Login/register public, `me` защищен action-level `[Authorize]`. |
| `FeatureFlagsController.cs` | 2 | Public feature flags/diagnostics. |
| `GoogleCalendarController.cs` | 2 | Callback/webhook public, остальные action-level `[Authorize]`. |
| `YandexCalendarController.cs` | 1 | Callback public, остальные action-level `[Authorize]`. |
| `HealthController.cs` | 1 | Public health endpoint. |

Дополнительный риск не в auth attributes, а в business permissions внутри endpoints: action-level `[Authorize]` доказывает только наличие пользователя, но не всегда доказывает право на конкретный объект/раздел. Это особенно важно для files/downloads, object audit, import/export, role/admin endpoints и integrations.

## 33. Config/security hygiene

Проверены `.env.*`, `backend/appsettings*.json`, `.github/workflows/deploy.yml` и `backend/Program.cs`. Значения секретов в отчет намеренно не включены.

Найдены tracked config-файлы с чувствительными значениями или placeholders:

| Файл | Что найдено | Риск |
|---|---|---|
| `backend/appsettings.json` | JWT secret, DaData API key/secret, SMTP password placeholder, connection string | Высокий, если значения реальные или переиспользуются в окружениях. |
| `backend/appsettings.Development.json` | PostgreSQL connection string с паролем | Высокий для локального/общего репозитория. |
| `backend/appsettings.Production.json` | Production-like connection string path/settings без секретов в явном виде | Средний: нужно убедиться, что production secrets приходят из environment/secret store. |
| `.github/workflows/deploy.yml` | Использует GitHub Secrets для VPS host/user/key/port | Нормально: сами секреты не hardcoded. |
| `.env.development`, `.env.production` | Только `VITE_API_URL` | Низкий риск; эти файлы не tracked по `git ls-files`. |

`Program.cs`:

| Настройка | Наблюдение |
|---|---|
| JWT validation | `ValidateIssuerSigningKey`, `ValidateIssuer`, `ValidateAudience`, `ValidateLifetime` включены, `ClockSkew` 2 минуты. |
| Swagger | Включается только в Development. |
| CORS | `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()` для default policy. |
| Static files | `UseDefaultFiles()` и `UseStaticFiles()` включены. |
| SignalR token | Для notifications hub access token читается из query string только на пути hub. |

Главный security-риск этого прохода: секреты/пароли в tracked appsettings. Даже если часть значений тестовая, безопаснее считать их скомпрометированными.

Рекомендации:

1. Ротировать реальные JWT/DaData/DB secrets, если они когда-либо использовались вне локальной машины.
2. Заменить tracked secrets на placeholders.
3. Перенести реальные значения в user secrets, environment variables, VPS secret files или secret manager.
4. Добавить secret scanning в CI или pre-commit.
5. Сузить CORS для production до конкретных origins (`crm.elitpravo.com`, нужные localhost только для dev).

## 34. Static assets и frontend shell

Проверены `public`, `index.html` и ссылки на logo/favicon.

| Asset | Размер | Проверка |
|---|---:|---|
| `public/logo.png` | 392865 bytes | PNG signature ok, 640x640 |
| `public/logo.ico` | 4286 bytes | ICO header ok, 1 image |

`index.html`:

- `lang="ru"` задан;
- favicon подключен как `/logo.ico`;
- apple touch icon подключен как `/logo.png`;
- viewport задан;
- title: `crm.elitpravo.com`.

Использование логотипа найдено в auth screens, navigation и shell. Отдельных `manifest.json`, `robots.txt`, `sitemap.xml` в `public` не найдено. Для CRM это не критично, но если приложение должно вести себя как PWA/устанавливаться на мобильных устройствах, manifest отсутствует.

## 35. Bundle artifacts и крупные chunks

По текущему `dist` после успешной production build проверены крупнейшие файлы:

| Artifact | Размер |
|---|---:|
| `dist/assets/AppSettings-*.js` | 524923 bytes |
| `dist/logo.png` | 392865 bytes |
| `dist/assets/vendor-recharts-*.js` | 261548 bytes |
| `dist/assets/Reports-*.js` | 242620 bytes |
| `dist/assets/ClientDetail-*.js` | 233183 bytes |
| `dist/assets/index-*.js` | 222922 bytes |
| `dist/assets/CaseDetailPage-*.js` | 189829 bytes |
| `dist/assets/InvoiceModal-*.js` | 179241 bytes |
| `dist/assets/TaskJournal-*.js` | 164893 bytes |
| `dist/assets/LegalEntityModal-*.js` | 155906 bytes |
| `dist/assets/MoneyOperations-*.js` | 151424 bytes |

Chunk splitting уже есть в `vite.config.ts`: React, Recharts, D3, Framer Motion, icons и SignalR выносятся в vendor chunks. Основные heavy areas ожидаемые для CRM: settings, reports, client/case detail, invoice/task/money modules.

Рекомендации:

1. Проверить lazy-loading `AppSettings`: он самый крупный app chunk.
2. Для reports держать Recharts/D3 строго в lazy route, чтобы они не попадали в начальный shell.
3. Рассмотреть image optimization для `logo.png`, если 640x640 не нужен в таком размере для auth/navigation.
4. Добавить bundle-size budget хотя бы advisory-level: предупреждать, если отдельный app chunk превышает выбранный лимит.

## 36. Technical markers

Быстрый проход по техническим маркерам:

| Маркер | Количество | Комментарий |
|---|---:|---|
| `TODO` | 0 | Явных TODO не найдено. |
| `FIXME` | 0 | Явных FIXME не найдено. |
| `HACK` | 0 | Явных HACK не найдено. |
| `debugger` | 0 | Debugger statements не найдены. |
| `@ts-ignore` | 0 | TypeScript ignores не найдены. |
| `@ts-expect-error` | 0 | TypeScript expect-error не найдены. |
| `eslint-disable` | 8 | Есть точечные отключения правил. |
| `console.log` | 37 | Найдены в CLI scripts, для scripts это ожидаемо. |

`eslint-disable` находится в:

- context files для `react-refresh/only-export-components`;
- `YandexDisk.tsx`, `S3Storage.tsx`, `CaseModal.tsx` для `react-hooks/exhaustive-deps`;
- `LegalEntityModal.tsx` для `react-refresh/only-export-components`.

Рекомендация: отдельно ревьюнуть `react-hooks/exhaustive-deps` disables в `YandexDisk`, `S3Storage` и `CaseModal`, потому что такие отключения иногда маскируют stale closure bugs.

## 37. Что добрал этот проход

В этом повторном доборе добавлено то, чего не хватало в предыдущем отчете:

1. CI/deploy workflow и реальный состав gates.
2. Локальный прогон `test:guard`, `test:ci`, public legal e2e/readiness/load.
3. Route/navigation/permission map по frontend shell.
4. Backend auth attribute map по controllers/endpoints.
5. Config/security hygiene без публикации секретных значений.
6. Static assets, frontend shell metadata и текущие bundle artifacts.
7. Technical markers: TODO/FIXME/HACK/debugger/ts-ignore/eslint-disable/console.log.

Главные новые выводы:

- CI сейчас не ловит strict TypeScript, lint, frontend integration tests, dependency audit и formatting.
- В tracked appsettings есть чувствительные значения; это стоит исправить как security priority.
- Backend auth attributes выглядят системно: endpoints без явного auth/anonymous атрибута статически не найдено.
- Public legal smoke/readiness/load проходят локально.
- Permission wiring на frontend в целом явный, но TypeScript уже подсвечивает проблемное сужение `contactsExternal`.

## 38. Release notes integrity

Проверен `backend/AppReleases/releases.json`, потому что AGENTS.md требует добавлять туда user-facing release notes для видимых изменений.

| Метрика | Значение |
|---|---:|
| Release entries | 270 |
| Active entries | 270 |
| Inactive entries | 0 |
| Items total | 695 |
| `new` items | 123 |
| `improved` items | 279 |
| `fixed` items | 203 |
| `important` items | 90 |
| Duplicate `releaseId` | 0 |
| Duplicate `version` | 0 |
| Invalid/missing required fields | 0 |
| Invalid item types | 0 |
| Sort order by `releasedAt` desc | yes |
| Date range | `2026-05-21T14:34:44+07:00` - `2026-06-09T15:15:00+07:00` |

Последние entries:

| Version | Release id | Title |
|---|---|---|
| `1.13.270` | `2026-06-09-invoice-offer-document` | Появился счет-оферта |
| `1.13.269` | `2026-06-09-external-calendar-empty-user-calendars` | Календари сотрудников создаются заранее |
| `1.13.268` | `2026-06-09-settings-compact-layout` | Настройки стали компактнее |
| `1.13.267` | `2026-06-09-settings-menu-independent-scroll` | Меню настроек прокручивается отдельно |
| `1.13.266` | `2026-06-09-dictionaries-compact-scroll-layout` | Справочники стали компактнее |

Вывод: структура release notes выглядит валидной и согласованной. Риск не в JSON-целостности, а в процессе: при каждой видимой правке нужно помнить добавлять новую запись или явно фиксировать, почему она не нужна.

## 39. README и docs drift

Проверены `README.md` и каталог `docs`.

| Область | Наблюдение |
|---|---|
| `docs` files | 20 markdown/sql files, включая этот audit report. |
| Самый крупный docs artifact | `docs/date-time-inventory.md`, около 313 KB. |
| README quick start | Использует путь `/workspace/PayPlanner`, не текущий Windows path. Это нормально как пример, но может путать локально. |
| README backend config | Пример использует `DefaultConnection`, а текущие appsettings используют `ConnectionStrings:Default`. |
| README database note | Говорит “PostgreSQL по умолчанию”, но `backend/appsettings.json` сейчас содержит provider `sqlite`, а `appsettings.Development.json` - `postgresql`. |
| README audit section | Старый раздел “Результаты технического аудита (2026-02-15)” противоречит свежему состоянию. |
| README typecheck claim | Утверждает, что `tsc --noEmit` проходит, но текущий `npx tsc -p tsconfig.app.json --noEmit` падает. |
| README CI claim | Утверждает, что CI не настроен, но `.github/workflows/deploy.yml` уже есть. |
| README dotnet note | Утверждает, что `dotnet` отсутствовал в окружении старого аудита; сейчас `dotnet build/publish/test` доступны и проходят. |

Рекомендация: обновить README после исправления TypeScript/quality gates. Сейчас README может ввести разработчика в заблуждение по БД, CI и статусу typecheck.

## 40. Localization inventory

Проверен `src/locales/ru.ts`.

| Метрика | Значение |
|---|---:|
| Keys | 541 |
| Duplicate keys | 0 |

Структурно файл валиден как TS export, дубликатов ключей не найдено. Отдельной проверки “все используемые ключи существуют” и “нет неиспользуемых ключей” в проекте нет. Судя по текущей архитектуре, значительная часть UI-текста находится прямо в компонентах, а не в `ru.ts`, поэтому это не полноценная i18n-система, а частичный словарь.

Рекомендации:

1. Если планируется поддерживать только русский интерфейс, оставить как есть и не усложнять.
2. Если планируется полноценная локализация, добавить typed key guard и тест на соответствие ключей.
3. Для новых shared UI-компонентов выбрать одно правило: либо строки рядом с компонентом, либо через словарь, чтобы не было смешанного подхода без причины.

## 41. Frontend/backend API route contract pass

Сделан эвристический проход по `src/services/api.ts` и backend controllers.

| Метрика | Значение |
|---|---:|
| Backend HTTP endpoints, найденных статическим парсером | 663 |
| Backend unique normalized routes | 509 |
| Frontend `this.request(...)` path literals | 362 |

Крупнейшие frontend API path groups:

| Segment | Paths |
|---|---:|
| `v2` | 56 |
| `reports` | 31 |
| `notifications` | 27 |
| `money-operations` | 19 |
| `invoices` | 17 |
| `tasks` | 17 |
| `cash-orders` | 16 |
| `contracts` | 14 |
| `clients` | 13 |
| `database` | 13 |
| `acts` | 10 |
| `legal-entities` | 10 |
| `yandex-disk` | 10 |

Первичный mismatch-проход подсветил `case-calendar`, `case-documents`, `document-templates`, `document-template-tokens`. Ручная проверка показала, что это не реальные отсутствующие API:

| Frontend segment | Backend location |
|---|---|
| `case-calendar` | `backend/Controllers/V2/CasesV2Controller.cs`, absolute routes `/api/case-calendar/...` |
| `case-documents` | `backend/Controllers/V2/CasesV2Controller.cs`, absolute routes `/api/case-documents/...` |
| `document-templates` | `backend/Controllers/V2/CasesV2Controller.cs`, duplicate routes `/api/document-templates/...` and `/api/v2/cases/document-templates...` |
| `document-template-tokens` | `backend/Controllers/V2/CasesV2Controller.cs`, duplicate routes `/api/document-template-tokens` and `/api/v2/cases/document-template-tokens` |

Вывод: явного отсутствующего backend segment по этим маршрутам не подтверждено. Но route contract сейчас проверяется только эвристически и частично через `api.test.ts`; автоматического строгого соответствия frontend client <-> backend routes нет.

Рекомендации:

1. Для `apiService` добавить generated/declared endpoint map и table-driven contract tests.
2. Включить в contract tests абсолютные route-атрибуты вроде `/api/case-calendar/...`, потому что простые parser-скрипты легко их пропускают.
3. Для duplicated routes (`/document-templates` и `/v2/cases/document-templates`) явно зафиксировать, какие из них публичный стабильный контракт, а какие compatibility alias.

## 42. EF Core, migrations и release publish

Дополнительно проверены EF CLI, DbContext discovery, Release build и publish.

| Проверка | Результат |
|---|---|
| `dotnet ef --version` | EF CLI доступен, версия `9.0.9`. |
| `dotnet ef dbcontext list` | passed, найдены `PaymentContext` и `PostgreSqlPaymentContext`. |
| `dotnet build backend\PayPlanner.Api.sln --no-restore -c Release ...` | passed, 0 warnings, 0 errors. |
| `dotnet publish backend\PayPlanner.Api.csproj --no-restore -c Release ...` | passed. |
| `npm run audit:dates:test` | passed, self-test date/time audit script. |

EF migrations observations:

| Команда | Наблюдение |
|---|---|
| `dotnet ef migrations list --context PaymentContext --no-build` with `ASPNETCORE_ENVIRONMENT=Production` | Список SQLite migrations получен, все показаны как `Pending` для production `payplanner.db`. Это ожидаемо, если локальная production SQLite база отсутствует или пустая. |
| `dotnet ef migrations list --context PostgreSqlPaymentContext --no-build` with `ASPNETCORE_ENVIRONMENT=Production` | Список PostgreSQL migrations получен, но EF не смог подключиться к `127.0.0.1:5432`, поэтому applied/pending status определить нельзя. |

Важно: это не полноценный migration rehearsal. Я не применял миграции к новой базе и не трогал реальные данные. Для полной уверенности нужны отдельные temporary SQLite/PostgreSQL databases:

1. Создать пустую SQLite DB и применить все migrations.
2. Создать пустую PostgreSQL DB и применить все PostgreSQL migrations.
3. Запустить backend startup seed/sync на обеих.
4. Прогнать smoke API после migration apply.
5. Удалить temporary DB.

## 43. Repository hygiene: lockfile, env и ignored artifacts

Проверены `.gitignore`, tracked files и ignored artifacts.

| Элемент | Статус |
|---|---|
| `package-lock.json` | Есть локально, но ignored и не tracked. |
| `.env.development`, `.env.production` | Есть локально, ignored и не tracked. |
| `backend/appsettings.json` | tracked. |
| `backend/appsettings.Development.json` | tracked. |
| `backend/appsettings.Production.json` | tracked. |
| `.codex-build/` | ignored. |
| `dist/` | ignored. |
| `node_modules/` | ignored. |

Локальные ignored artifacts на момент проверки:

| Artifact group | Наблюдение |
|---|---|
| root `.codex*.log` | Есть несколько логов от проверок; крупнейшие около 44.7 MB и 26.9 MB. |
| `.codex-build/` | 260 files, суммарно около 163 MB после readiness/build/publish checks. |
| `dist/` | ignored production build output. |

Риск по `package-lock.json`: workflow умеет работать и с lockfile, и без него, но если lockfile не tracked, CI на чистом checkout пойдет по fallback `npm install`, а не `npm ci`. Это снижает воспроизводимость frontend dependency tree.

Рекомендации:

1. Решить стратегию lockfile: либо tracked `package-lock.json` + `npm ci`, либо сознательно без lockfile, но тогда зафиксировать это в README/CI.
2. Для production CRM лучше tracked lockfile.
3. Не коммитить `.env.*`, но добавить `.env.example` с безопасными placeholder values.
4. Почистить локальные `.codex*.log` и `.codex-build/`, если они больше не нужны для разбора аудита.

## 44. Что добрал этот проход

В этом доборе добавлены зоны, которых не было в предыдущих секциях:

- валидность `backend/AppReleases/releases.json`;
- drift между README и реальным состоянием проекта;
- инвентаризация `src/locales/ru.ts`;
- эвристическая сверка frontend API paths с backend routes;
- EF CLI/DbContext discovery, Release build и publish;
- migration list observations без применения миграций;
- repository hygiene: lockfile, env, ignored artifacts.

Главные новые выводы:

1. Release notes JSON валиден: 270 entries, дубликатов и плохих item types нет.
2. README заметно устарел и противоречит текущему состоянию по CI, DB config и TypeScript.
3. `package-lock.json` локально есть, но ignored и не tracked; это снижает воспроизводимость CI.
4. Release build и publish backend проходят без warnings/errors.
5. Полный migration rehearsal все еще не выполнен: нужны отдельные временные SQLite/PostgreSQL базы.

## 45. Frontend import graph и циклы

Проверен import graph по `src` с разрешением относительных импортов.

| Метрика | Значение |
|---|---:|
| `src` TS/TSX/JS/JSX files | 324 |
| Test files | 84 |
| Reachable files from `src/main.tsx` plus tests | 317 |
| Unresolved relative imports | 2 |
| Runtime circular components | 0 |
| Type-level circular components | 1 |

Unresolved relative imports:

| File | Import | Комментарий |
|---|---|---|
| `src/main.tsx` | `./App.tsx` | False positive парсера: import с явным `.tsx`, файл существует. |
| `src/main.tsx` | `./index.css` | False positive для CSS import. |

Type-level cycle:

| Files | Комментарий |
|---|---|
| `src/components/Roles/Roles.tsx` -> `RoleModal.tsx` -> `Roles.tsx`; `Roles.tsx` -> `RolePermissionsModal.tsx` -> `Roles.tsx` | Runtime-цикла нет, потому что модалки импортируют `type Role` из `Roles.tsx`. Но тип роли лучше вынести в отдельный файл, например `roleTypes.ts`, чтобы модальные компоненты не зависели от родительского screen component. |

Вывод: runtime import graph выглядит здорово, явных circular runtime dependencies не найдено. Есть небольшой архитектурный запах в ролях из-за shared type, объявленного в screen component.

## 46. Кандидаты на неиспользуемые frontend-файлы

Import graph подсветил production-файлы, которые не достижимы из `main.tsx` и tests в текущей эвристике.

| File | Наблюдение |
|---|---|
| `src/components/Calendar/SummaryCards.tsx` | Экспортирует `SummaryCards`, прямых импортов не найдено. |
| `src/components/Contracts/ContractCreateForm.tsx` | Экспортирует `ContractCreateForm`, прямых импортов не найдено. |
| `src/components/LegalEntities/LegalEntityInlineEditor.tsx` | Экспортирует `LegalEntityInlineEditor`, прямых импортов не найдено. |
| `src/components/ui/Hint.tsx` | Экспортирует `Hint`, прямых импортов не найдено. |
| `src/hooks/useClientStats.ts` | Дублирует имя hook, при этом `useClients.ts` тоже экспортирует `useClientStats`. Прямых импортов отдельного файла не найдено. |

Технические false positives:

| File | Почему не считать проблемой |
|---|---|
| `src/test/setup.ts` | Подключается через `vitest.config.ts`, а не import graph. |
| `src/vite-env.d.ts` | TypeScript ambient declarations, не импортируется явно. |

Рекомендация: перед удалением этих файлов выполнить ручную проверку истории и `git grep`. Если это остатки старой реализации, удалить отдельным cleanup PR. Особое внимание `useClientStats.ts`: наличие одноименного hook в `useClients.ts` может путать разработчиков.

## 47. Accessibility/static UX scan

Проведен статический scan JSX. Это не заменяет `axe`/screen reader тесты, но дает карту очевидных зон.

| Проверка | Результат |
|---|---:|
| `<img>` tags | 18 |
| `<img>` без `alt` | 0 |
| `<table>` tags | 78 |
| `<form>` tags | 17 |
| Positive `tabIndex` | 0 |
| `dangerouslySetInnerHTML` | 0 |
| Rough interactive `div/span` candidates | 62 |
| Rough large density classes (`p-6`, `gap-6`, `space-y-6`, `p-8` и т.д.) | 578 |

Ручные accessibility-кандидаты из scan:

| File | Наблюдение |
|---|---|
| `src/components/Dictionaries/Dictionaries.tsx` | Icon-only edit/delete buttons выглядят без явного `aria-label`/`title`. |
| `src/components/Dictionaries/DictionariesModal.tsx` | Icon-only close buttons выглядят без явного `aria-label`/`title`. |
| `src/components/Settings/MoneyAccountSettings.tsx` | Icon-only close button выглядит без явного `aria-label`/`title`. |
| `src/components/ui/TableContextMenu.tsx` | Есть custom `role="menu"` surface; стоит отдельно проверить keyboard navigation. |
| `src/components/Roles/Roles.tsx`, `src/components/UserActivity/UserActivity.tsx` | Column resize handles через `role="separator"`; стоит проверить keyboard resizing или fallback. |

Density scan:

| Зона | Наблюдение |
|---|---|
| Auth screens/modals | `p-8`, `space-y-6` ожидаемы и не всегда проблема. |
| Operational CRM screens | Много `p-6`, `px-6`, `py-6`, `gap-6`, особенно в `ClientDetail`, `InvoiceModal`, `PaymentModal`, `CaseDetailPage`, `Accounts`, `Calendar`. |
| AGENTS.md UI density | Для внутренних рабочих разделов предпочтительнее `p-3 lg:p-4`, `gap-3`, `space-y-3`. |

Вывод: по alt/tabIndex грубых проблем не найдено. Основные accessibility/UX зоны для ручного добора - icon-only buttons, keyboard navigation в custom menus/resizers и плотность рабочих экранов.

## 48. Browser storage inventory

Собраны localStorage/sessionStorage keys и key prefixes.

| Метрика | Значение |
|---|---:|
| Storage keys/prefixes found | 37 |

Ключевые группы:

| Key/prefix | Назначение |
|---|---|
| `pp_token`, `auth_token`, `pp.jwt` | Auth token compatibility keys. |
| `pp.last_active_tab` | Последняя вкладка приложения. |
| `pp.sidebar_collapsed`, `pp.sidebar_expanded` | Состояние навигации. |
| `pp.calendar_view`, `pp.calendar_section_mode` | Вид календаря. |
| `pp.clients_view`, `pp.clients_table_settings`, `pp.clients_table_filters` | Состояние списка клиентов. |
| `pp.task_journal.filters.v1` | Фильтры журнала задач. |
| `pp.task_journal.items.v1` | Local task journal store. |
| `pp.cases_registry_views.v1` | Сохраненные представления реестра дел. |
| `pp.case-detail.${caseId}.view.v1` | Состояние карточки дела в sessionStorage. |
| `pp.invoice-editor-draft`, `pp.installment-draft` | Временные draft bridges. |
| `pp.companyProfileSelectedCompanyId`, `pp.companyProfileMode` | Состояние профиля компании. |
| `pp.chunk-load-retry-url` | Защита от повторной перезагрузки при chunk load error. |

Security observation: JWT/access tokens хранятся в `localStorage` compatibility keys. Это удобно для SPA, но повышает риск при XSS: если в приложении появится XSS, токен можно прочитать из JavaScript.

Рекомендации:

1. Для текущей архитектуры усилить XSS hygiene: не вводить `dangerouslySetInnerHTML`, проверять HTML preview/print flows, держать CSP на production.
2. Рассмотреть переход на HttpOnly cookie auth или short-lived access token + refresh strategy, если security требования вырастут.
3. Добавить storage registry doc, чтобы новые ключи не появлялись хаотично.
4. Для draft keys добавить TTL/version cleanup, если пользовательские черновики могут устаревать.

## 49. Dangerous DOM/download/print patterns

Проверены опасные DOM-паттерны и download/print helpers.

| Pattern | Count | Комментарий |
|---|---:|---|
| `dangerouslySetInnerHTML` | 0 | Хорошо: прямого React HTML injection не найдено. |
| `eval(` | 0 | Не найдено. |
| `new Function` | 0 | Не найдено. |
| `innerHTML` | 2 | `ExternalAuthPanel.tsx` очищает контейнер Telegram widget через `container.innerHTML = ''`. |
| `document.write` | 7 | Используется для print/preview windows: invoice, template settings, reconciliation act, payment print. |
| `window.open` | 27 | OAuth popups, blob previews/downloads, external docs. |
| `target="_blank"` | 9 | Большинство ссылок с `rel="noreferrer"`, но стоит проверить все. |
| `URL.createObjectURL` | 35 direct matches | Экспорты/downloads/previews. |
| `URL.revokeObjectURL` | 49 matches | Cleanup в целом присутствует чаще, чем create, что хороший знак. |

Риски:

1. `document.write` безопасен только если HTML полностью контролируется приложением или шаблонизатором с экранированием. Для редактируемых шаблонов документов нужен отдельный XSS review.
2. `window.open` для external URLs должен всегда использовать `noopener,noreferrer`.
3. Blob URLs должны освобождаться через `URL.revokeObjectURL`; большинство мест это делают, но стоит завести shared helper, чтобы не проверять вручную каждый экспорт.

## 50. Feature flags и env matrix

Backend legal feature flags из `backend/appsettings.json`:

| Flag | Default |
|---|---|
| `FeatureFlags:Legal:ExternalCourtSync` | `false` |
| `FeatureFlags:Legal:ExternalCalendarSync` | `false` |
| `FeatureFlags:Legal:CourtEvents` | `false` |
| `FeatureFlags:Legal:DocumentTemplateLibrary` | `false` |

Frontend/Vite env usage:

| Key | Usage |
|---|---|
| `VITE_API_URL` | `src/services/apiBaseUrl.ts`; задан в `.env.development` и `.env.production`. |
| `VITE_ENABLE_CALENDAR_SYNC_READINESS_PANEL` | `CalendarSyncProviderSettings.tsx`; inline readiness panel. |
| `import.meta.env.DEV` | Logging/dev behavior in auth/activity/realtime. |
| `import.meta.env.PROD` | API base URL resolution. |

Наблюдение: feature flags читаются с backend, но часть юридических UI/API routes доступна как кодовая поверхность даже при disabled flags. Это нормально, если backend endpoints сами проверяют flags там, где это критично. Для release readiness уже есть strict flag checks, но они не включены в GitHub deploy workflow.

Рекомендация: добавить в CI/release gate сценарий `test:legal-flags:strict` или `test:legal-release-gate`, когда юридические экспериментальные возможности не должны случайно попасть включенными в production.

## 51. Complexity hotspots

Собраны самые крупные файлы и длинные функции/компоненты. Это не баг само по себе, но такие файлы дороже менять и сложнее покрывать тестами.

Крупнейшие frontend files:

| File | Lines |
|---|---:|
| `src/components/Clients/ClientDetail.tsx` | 10993 |
| `src/components/Tasks/TaskJournal.tsx` | 6767 |
| `src/components/Reports/Reports.tsx` | 6552 |
| `src/services/api.ts` | 6342 |
| `src/components/Cases/CaseDetailPage.tsx` | 6202 |
| `src/components/Accounts/InvoiceModal.tsx` | 5774 |
| `src/components/Money/MoneyOperations.tsx` | 5630 |
| `src/components/LegalEntities/LegalEntityModal.tsx` | 5168 |
| `src/components/Calendar/PaymentModal.tsx` | 3936 |
| `src/components/Accounts/Accounts.tsx` | 3753 |

Крупнейшие backend files без migrations:

| File | Lines |
|---|---:|
| `backend/Controllers/V2/CasesV2Controller.cs` | 11407 |
| `backend/Controllers/InvoicesController.cs` | 6446 |
| `backend/PayPlanner.Api.Tests/CasesV2ControllerTests.cs` | 6379 |
| `backend/Controllers/ReportsController.cs` | 6139 |
| `backend/PayPlanner.Api.Tests/MoneyOperationsControllerTests.cs` | 5372 |
| `backend/Controllers/MoneyOperationsController.cs` | 4824 |
| `backend/Controllers/TasksController.cs` | 4566 |
| `backend/Data/PaymentContext.cs` | 3408 |
| `backend/Controllers/PaymentsController.cs` | 3346 |

Самые длинные frontend components/functions:

| Function/component | File | Lines |
|---|---|---:|
| `ClientDetail` | `src/components/Clients/ClientDetail.tsx` | 9660 |
| `InvoiceModal` | `src/components/Accounts/InvoiceModal.tsx` | 4895 |
| `LegalEntityModal` | `src/components/LegalEntities/LegalEntityModal.tsx` | 4447 |
| `PaymentModal` | `src/components/Calendar/PaymentModal.tsx` | 3677 |
| `Accounts` | `src/components/Accounts/Accounts.tsx` | 3223 |
| `CaseDetailPage` | `src/components/Cases/CaseDetailPage.tsx` | 2458 |

Самые длинные backend methods outside tests:

| Method | File | Lines |
|---|---|---:|
| `GetRegistry` | `backend/Controllers/AccountsController.cs` | 461 |
| `Get` | `backend/Controllers/V2/ClientsV2Controller.cs` | 430 |
| `SeedClientsCasesPaymentsAsync` | `backend/Services/SeedDataService.cs` | 402 |
| `TryReadInstallmentToPay` | `backend/Controllers/AccountsController.cs` | 395 |
| `GetPaged` | `backend/Controllers/LegalEntitiesController.cs` | 361 |
| `RenderHtml` | `backend/Services/Acts/ActDocumentTemplateEngine.cs` | 351 |
| `BuildMoneyOperationsQuery` | `backend/Controllers/MoneyOperationsController.cs` | 335 |

Рекомендации:

1. Не делать “рефакторинг ради рефакторинга” по всему проекту. Начинать только с файлов, которые реально часто меняются или уже падают в typecheck.
2. Для frontend сначала выносить pure helpers и small subcomponents из `ClientDetail`, `InvoiceModal`, `PaymentModal`, `TaskJournal`.
3. Для backend выносить query builders/projections из controllers в services/query objects там, где уже есть tests.
4. Перед каждым разрезанием крупного файла фиксировать regression tests вокруг текущего поведения.

## 52. Что добрал этот проход

В этом проходе добавлены:

- frontend import graph и runtime/type-level cycles;
- кандидаты на неиспользуемые frontend-файлы;
- static accessibility/UX scan;
- storage key inventory;
- dangerous DOM/download/print pattern scan;
- feature flags/env matrix;
- complexity hotspots по frontend/backend.

Главные новые выводы:

1. Runtime import cycles не найдены.
2. Есть type-level cycle в ролях из-за импорта `type Role` из screen component.
3. Есть несколько кандидатов на неиспользуемые frontend-файлы, включая `SummaryCards.tsx`, `ContractCreateForm.tsx`, `LegalEntityInlineEditor.tsx`, `Hint.tsx`, `useClientStats.ts`.
4. JWT хранится в localStorage compatibility keys, что стоит учитывать в XSS threat model.
5. Крупные компоненты и контроллеры остаются главным maintainability-risk.

## 53. Backend file upload/download surface

Дополнительно проверены серверные точки загрузки и скачивания файлов: `IFormFile`, `[FromForm]`, `File(...)`, `PhysicalFile(...)`, `RequestFormLimits`, локальные хранилища документов, задачи, профильные фото, резервные копии, S3 и Яндекс.Диск.

Итоговая карта:

| Surface | Files/controllers | Findings |
|---|---:|---|
| Controllers with file upload/download logic | 16 | Есть явная файловая поверхность, требующая отдельного regression/security набора. |
| `IFormFile` usages in controllers | 15 | Загрузки есть в `Contacts`, `DatabaseAdmin`, `MoneyOperations`, `Profile`, `S3Storage`, `Tasks`, `CasesV2`, `YandexDisk`. |
| `[FromForm]` usages in controllers | 23 | Основная форма-загрузка сконцентрирована вокруг импорта, S3/Я.Диск и документов дел/задач. |
| `RequestFormLimits` usages | 3 | Лимиты явно стоят только в `DatabaseAdminController`; для остальных upload endpoint лимиты либо стандартные, либо реализованы кодом. |
| `File(...)` responses in controllers | 32 | PDF/DOCX/CSV/XLS/ICS/XML/download flows. |
| `PhysicalFile(...)` responses | 4 | Локальные файлы резервных копий, документов дел/задач. |

Положительные проверки:

- `CaseDocumentStorage.ResolvePath` нормализует root и итоговый путь через `Path.GetFullPath` и проверяет, что путь остается внутри root.
- `TasksController.ResolveTaskFilePath` и `YandexDiskController.ResolveTaskFilePath` используют такую же защиту от выхода из директории.
- `CasesV2Controller` и `TasksController` очищают имя файла через `Path.GetFileName`, invalid chars и ограничение длины.
- `ProfileController` проверяет расширение фото, нормализует путь удаления предыдущего фото и не удаляет файл вне `webroot`.
- `YandexDiskService` проверяет сегменты пути на `.`/`..`, нормализует `disk:` paths, маскирует token в отображении.
- `DatabaseAdminController` имеет явный максимум импорта `1 GB` и `RequestSizeLimit`.

Риски/пробелы:

1. Не все upload endpoints имеют явные `[RequestSizeLimit]`/`[RequestFormLimits]`. Нужно решить, где достаточно ASP.NET defaults, а где нужны продуктовые лимиты: фото профиля, task files, case documents, S3 upload, Yandex Disk upload.
2. Для файловых endpoint нужен отдельный тестовый набор: path traversal (`../`, encoded slashes), пустой файл, слишком большой файл, дубликаты имен, удаление, скачивание чужого файла, скачивание после удаления.
3. `DatabaseAdminController` сознательно допускает очень большой импорт. Это административный сценарий, но стоит проверить timeout, память и отказоустойчивость на поврежденном файле.
4. File download access надо покрыть не только 401, но и 403/404 на уровне ownership/role: чужая задача, чужое дело, несуществующий storage path.

Рекомендации:

1. Ввести единую таблицу лимитов файлов по типам: profile photo, task attachment, case document, database import, S3 upload, Yandex upload.
2. Добавить backend integration tests на файловые endpoint с fake storage/root.
3. Добавить shared helper для upload validation, чтобы ограничения размера/расширения/имени не расходились между контроллерами.
4. Для всех download endpoint явно фиксировать поведение: 401 без токена, 403 без прав, 404 если объект не найден или файл отсутствует.

## 54. External integrations and network clients

Повторно собрана карта внешних интеграций и `HttpClient` registration в `Program.cs`.

Найденные интеграции:

| Integration | Registration/surface | Status |
|---|---|---|
| DaData | `AddHttpClient<ILegalEntityEnrichmentService, DadataLegalEntityEnrichmentService>` | Есть typed client и options. |
| Судебные интеграции/Sudrf | `AddHttpClient<ISudrfCourtSyncService, SudrfCourtSyncService>` | Есть typed client, feature flags и scheduler. |
| Яндекс.Диск | `AddHttpClient<IYandexDiskService, YandexDiskService>` | Есть typed client, user/admin flows, CRM sync scheduler. |
| Google Calendar | services + background sync | Есть OAuth/calendar sync/manual sync/webhook paths. |
| Yandex Calendar | services + background sync | Есть OAuth/calendar sync/manual sync/callback paths. |
| Telegram | bot auth, widget login, webhook/polling, notifications | Есть public callbacks и background polling. |
| S3 | storage client factory/service/controller | Есть backup/storage flows. |
| SMTP Email | `IEmailService` | Есть service, settings and send flows. |
| PDF/browser | document generation services | Требует runtime dependencies для PDF generation. |

Положительные проверки:

- Основные HTTP-интеграции зарегистрированы через `AddHttpClient`, а не создаются вручную повсеместно.
- Legal/calendar flags позволяют отключать части внешних интеграций.
- Для Telegram/Google/Yandex public callback endpoints есть явное `[AllowAnonymous]`, остальные контроллеры не оставлены случайно публичными.

Риски/пробелы:

1. В CI нет отдельного smoke набора по отключенным/включенным feature flags для внешних интеграций.
2. В `appsettings*.json` есть чувствительные настройки/секретные поля; ранее уже отмечено, что tracked config нельзя считать безопасным местом для реальных ключей.
3. Для внешних API нужно отдельно проверять timeout/retry/circuit-breaker ожидания. Сейчас карта показывает клиентов, но не доказывает единый resilience policy.
4. Public callback endpoints требуют отдельного security checklist: подпись/секрет/webhook token/replay protection/логирование отказов.

Рекомендации:

1. Добавить integration tests с mocked `HttpMessageHandler` для DaData, Sudrf, Я.Диск, Google/Yandex Calendar.
2. В CI добавить strict legal/calendar flag checks для сценариев `disabled by default` и `enabled with missing settings`.
3. Вынести реальные secrets из tracked config в user-secrets/env/secret store; в репозитории оставить только пустые placeholders.

## 55. Hosted/background services

В `Program.cs` найдено 15 `AddHostedService` registrations:

| Service | Named test detected |
|---|---:|
| `DatabaseBackupSchedulerService` | no |
| `PaymentStatusUpdater` | no |
| `InvoicePaymentStateReconciliationService` | no |
| `TaskJournalReminderNotificationService` | no |
| `NotificationRetrySchedulerService` | no |
| `NotificationDeliveryRetentionService` | no |
| `CalendarSyncSchedulerService` | no |
| `GoogleCalendarBackgroundSyncService` | no |
| `YandexCalendarBackgroundSyncService` | no |
| `TaskJournalAutomationSchedulerService` | no |
| `TelegramBotPollingService` | no |
| `MoneyBalanceReconciliationSchedulerService` | no |
| `ClientCaseCourtSyncSchedulerService` | no |
| `UserActivityRetentionService` | no |
| `YandexDiskCrmSyncSchedulerService` | no |

Важно: `Named test detected = no` означает, что прямой поиск по имени сервиса в backend test project не нашел теста. Часть логики может покрываться через сервисы/контроллеры косвенно, но сам scheduler lifecycle, cancellation и interval behavior явно не зафиксированы.

Дополнительные observations:

- Большинство фоновых служб используют `PeriodicTimer`/`Task.Delay` и `ExecuteAsync`.
- Scheduler services закрывают `TaskCanceledException`/`OperationCanceledException`, но coverage этого поведения нужно подтверждать отдельными тестами.
- Фоновые задачи затрагивают критичные области: платежные статусы, счета, напоминания, уведомления, календарь, Telegram, остатки денег, судебные синхронизации, ретеншн активности, Яндекс.Диск.

Риски/пробелы:

1. Отсутствие прямых tests для scheduler lifecycle повышает риск бесконечных циклов, неправильного interval, silent failure и некорректной отмены.
2. Фоновые jobs меняют данные, но не все имеют очевидный dry-run или ручной health/status endpoint.
3. Scheduler failures должны быть видны администратору, иначе часть системы может тихо перестать синхронизироваться.

Рекомендации:

1. Для каждого scheduler добавить минимальный unit/integration test: старт, один tick, cancellation, обработка exception без падения host.
2. Для критичных jobs добавить admin-visible last run/last success/last error.
3. В CI запускать отдельный быстрый набор scheduler tests без реальных внешних API.

## 56. Request validation and error handling

Проверена серверная валидация DTO в `backend/Models/Requests` и глобальный middleware pipeline.

Request DTO scan:

| Metric | Value |
|---|---:|
| Request files | 67 |
| Request classes/records | 102 |
| Request properties | 708 |
| `[Required]` | 29 |
| `[StringLength]` | 17 |
| `[MaxLength]` | 106 |
| `[Range]` | 28 |
| `[EmailAddress]` | 2 |
| Request files without validation attributes | 50 |

Error/status scan:

| Metric | Value |
|---|---:|
| Controller `BadRequest(...)` | 436 |
| Controller `Unauthorized(...)` | 220 |
| Controller `Forbid(...)` | 122 |
| Controller `NotFound(...)` | 335 |
| Controller `Problem(...)` | 72 |
| `ValidationProblem(...)` | 21 |
| `ModelState` references | 60 |
| `catch (...)` outside migrations | 322 |
| `throw new ...` outside migrations | 350 |
| `LogError(...)` outside migrations | 29 |
| `LogWarning(...)` outside migrations | 39 |

Middleware pipeline observations:

- `UseAuthentication`, `UseAuthorization`, `UseCors`, `UseStaticFiles`, `MapHub` есть.
- `UseExceptionHandler`, `UseDeveloperExceptionPage`, `UseStatusCodePages`, `AddProblemDetails`, `UseHsts`, `UseHttpsRedirection` в `Program.cs` не найдены.

Вывод:

Валидация и error mapping реализованы в основном локально в контроллерах/сервисах. Это работает, но создает неоднородность: одинаковые ошибки могут возвращаться разными форматами, а непойманные исключения зависят от default ASP.NET behavior.

Риски/пробелы:

1. 50 request files без validation attributes не обязательно ошибочны, но требуют ручной проверки: часть DTO может валидироваться сервисом, часть может принимать partial update, часть может быть недовалидирована.
2. Нет явного global exception/problem details слоя. Для API лучше иметь единый формат ошибок и predictable mapping для `ArgumentException`, `InvalidOperationException`, `UnauthorizedAccessException`, validation errors.
3. Большое количество локальных `catch`/`Problem` в контроллерах усложняет единое поведение API.

Рекомендации:

1. Составить whitelist DTO без annotations: какие валидируются вручную, какие являются partial request, какие нужно усилить.
2. Ввести/документировать единый error contract для API.
3. Добавить tests на формат ошибок для common failures: validation, unauthorized, forbidden, not found, conflict, external provider unavailable.

## 57. EF model constraints, indexes and audit hooks

Проверен `PaymentContext` как центральная карта модели.

| EF configuration metric | Count |
|---|---:|
| `.HasIndex(...)` | 362 |
| `.IsUnique(...)` | 37 |
| `.HasMaxLength(...)` | 522 |
| `.HasPrecision(...)` | 2 |
| `.IsRequired(...)` | 207 |
| `DeleteBehavior.*` | 146 |
| `.ToTable(...)` | 12 |

Положительные проверки:

- Много индексов на фильтры/поиск/связи: платежи, счета, акты, договоры, справочники, денежные операции, аудит, календарные/уведомительные таблицы.
- Есть soft-delete query filter: `HasQueryFilter`.
- Есть audit metadata на `CreatedByUserId/Name`, `UpdatedByUserId/Name`, `DeletedByUserId/Name`.
- `SaveChanges`/`SaveChangesAsync` переопределены и вызывают `ApplyAuditMetadata`, затем сохраняют `ObjectAuditEvent`.
- Для `ObjectAuditEvents` есть индексы по объекту и `CreatedAt`.

Риски/пробелы:

1. `HasPrecision` найден только 2 раза. Для финансовой системы нужно отдельно проверить, что все денежные поля имеют корректный тип/precision в SQLite/PostgreSQL и миграциях.
2. 37 unique indexes нужно сверить с бизнес-правилами: где uniqueness глобальная, а где должна учитывать soft-delete, компанию, год, тип документа или legal entity.
3. `DeleteBehavior.Cascade/SetNull/Restrict` смешаны. Это нормально, но требует отдельной матрицы “что удаляется, что сохраняется” для клиент/дело/платеж/счет/акт/договор.
4. Аудит через `SaveChanges` не покрывает операции, которые могут идти raw SQL/bulk update, если такие появятся.

Рекомендации:

1. Добавить отдельную таблицу финансовых decimal properties и проверить precision в обеих БД.
2. Написать tests на soft-delete + unique constraints в ключевых сущностях.
3. Документировать delete behavior для основных сущностей и зафиксировать integration tests.

## 58. Authorization coverage, audit logging and public endpoints

Повторно проверены backend controllers на явную авторизацию.

| Metric | Value |
|---|---:|
| Controllers | 54 |
| Controllers without explicit `[Authorize]` or `[AllowAnonymous]` | 0 |
| Controllers mixing protected and public actions | 4 |
| Controllers with admin policy or `User.IsInRole("admin")` checks | 22 |

Controllers with public/protected mix:

- `AuthController`
- `GoogleCalendarController`
- `NotificationsController`
- `YandexCalendarController`

Положительные проверки:

- Нет контроллеров без явного auth intent.
- Admin-only paths есть в настройках пользователей, банков, релизов, database admin, system jobs, user activity, permissions-related sections.
- Object audit events создаются централизованно в `PaymentContext`, а также вручную в отдельных сценариях экспорта/синхронизаций.

Риски/пробелы:

1. `User.IsInRole("admin")` используется рядом с policy-based authorization. Это может быть нормально, но лучше постепенно привести к единым policies/permission helpers.
2. Public actions в calendar/notifications/auth должны иметь отдельные тесты на webhook secret/state/token и replay/invalid payload.
3. Не все права доступа можно доказать статикой: нужны scenario tests для ролей “admin”, обычный пользователь, ответственный, пользователь без доступа к компании/делу.

Рекомендации:

1. Добавить permission matrix tests по ключевым ресурсам: payments, invoices, acts, contracts, cases, tasks, money operations, settings.
2. Для public callback endpoints добавить negative tests: отсутствует secret/state, неверный secret/state, повторный callback, чужой user binding.
3. Унифицировать admin checks через policy/permission service там, где сейчас ручной `User.IsInRole`.

## 59. Что добрал этот проход

В этот проход добавлены:

- backend file upload/download inventory;
- path traversal and filename sanitization observations;
- external integrations and `HttpClient` registration map;
- hosted/background services list and direct test coverage gap;
- request DTO validation inventory;
- error handling/middleware inventory;
- EF constraints/index/delete behavior/audit hooks summary;
- explicit controller authorization coverage.

Главные новые выводы:

1. У всех 54 backend controllers есть явный auth intent: `[Authorize]` или `[AllowAnonymous]`.
2. Файловые endpoint имеют хорошие локальные path guards в ключевых местах, но лимиты размера и тесты на traversal/ownership покрыты неравномерно.
3. 15 hosted services не имеют прямых тестов по именам; для scheduler lifecycle это заметный пробел.
4. DTO validation неоднородна: 50 request files без attributes требуют whitelist/manual review.
5. В `Program.cs` не найден единый global exception/problem-details слой; API errors сейчас в основном мапятся локально.
6. EF-модель богата индексами и audit hooks, но финансовые precision/unique constraints/delete behavior нужно проверять отдельными бизнес-тестами.

## 60. Frontend routing, tab registry and permission gates

Дополнительно проверена фактическая схема routing. В проекте нет классической таблицы React Router `<Route path=...>`. Навигация построена на tab-based routing: `Tab` union, `appRouting.ts`, `ppTab` query parameter, отдельные path parsers для detail/list страниц и `Navigation.tsx`.

Собранная карта:

| Area | Result |
|---|---:|
| `Tab` union values | 47 |
| `ALL_TABS` in `appRouting.ts` | 47 |
| Tabs missing from `ALL_TABS` | 0 |
| Extra tabs in `ALL_TABS` | 0 |
| Tabs present in sidebar navigation | 35 |
| Hidden/detail tabs not shown directly in sidebar | 12 |

Hidden/detail tabs:

- `caseDetail`
- `contactsExternal`
- `actCreate`
- `actDetail`
- `contractCreate`
- `contractDetail`
- `clientDetail`
- `companyProfile`
- `profile`
- `userDetail`
- `yandexDisk`
- `s3Storage`

Положительные проверки:

- `Tab` union и `ALL_TABS` синхронизированы полностью.
- `App.tsx` содержит `canViewTab`, `accessibleMenuTabs`, fallback на доступную вкладку и guards внутри `renderContent`.
- Detail/open handlers проверяют соответствующие permission groups перед переходом: clients, legal entities, accounts, payments, cases, tasks, acts, contracts.
- `App.routing.test.ts` покрывает path parsing и `getTabHref` для payments/tasks/contracts/clients/cases/acts/notifications/query tabs.
- `Navigation.tsx` фильтрует sidebar пункты через `useRolePermissions` и `isAdmin()`.

Риски/пробелы:

1. `contactsExternal` есть в `Tab`/`ALL_TABS`, но не выводится как отдельный sidebar child. Это может быть ожидаемо, если внешний контакт выбирается переключателем внутри контактов, но стоит подтвердить продуктово.
2. `yandexDisk` и `s3Storage` не в обычном sidebar list, хотя `Navigation.tsx` считает `canViewYandexDisk`/`canViewS3Storage`. Нужно проверить, доступны ли они из settings/admin UI, и нет ли “мертвых” вкладок.
3. `profile`, `companyProfile`, `userDetail` и detail/create tabs не должны быть menu tabs; при этом их deep-link/fallback поведение стоит держать в guard tests.
4. Доступность вкладки на frontend не заменяет backend permissions. Нужны role scenario tests на API для каждого скрытого detail route.

Рекомендации:

1. Добавить snapshot/table test: `Tab` union, `ALL_TABS`, `getTabHref`, `Navigation` visible tabs и `canViewTab` должны расходиться только по явно разрешенному whitelist.
2. Отдельно зафиксировать ожидаемый путь входа в `yandexDisk`/`s3Storage`/`contactsExternal`.
3. Добавить negative frontend tests: пользователь без permission открывает deep link `/cases/1`, `/acts/new`, `/contracts/1`, `/invoices/1`, `/tasks/1` и получает fallback/нет доступа.

## 61. Frontend build/test/config surface

Проверены `package.json`, `vite.config.ts`, `vitest.config.ts`, `tsconfig*.json`, `.env.*`, ESLint/Tailwind/PostCSS config.

| Area | Result |
|---|---|
| package scripts | 25 scripts |
| runtime dependencies | 6 packages |
| dev dependencies | 18 packages |
| Vite server | `host: true`, `port: 5173`, `strictPort: true` |
| Vite proxy | none |
| Vite sourcemap setting | not enabled explicitly |
| Vite manual chunks | yes |
| Vitest environment | `jsdom` |
| Vitest setup | `./src/test/setup.ts` |
| Vitest coverage config | not configured |
| TS app target/lib | `target ES2020`, `lib ES2020, DOM, DOM.Iterable` |
| TS strict | enabled |
| `.env.development` / `.env.production` | only `VITE_API_URL` |

Положительные проверки:

- `strict`, `noUnusedLocals`, `noUnusedParameters`, `noFallthroughCasesInSwitch` включены.
- Vite chunks разделяют SignalR/Recharts/D3/Motion/Lucide/React.
- Vitest исключает `.codex-build/**` и `dist/**`, что предотвращает случайный прогон build artifacts.
- Frontend env не содержит секретов, виден только `VITE_API_URL`.

Риски/пробелы:

1. `tsconfig.app.json` использует `lib: ES2020`, но код уже использует современные методы вроде `.at()`/`replaceAll()`; ранее `tsc --noEmit` падал. Нужно либо поднять lib target до фактически используемого уровня, либо убрать новые API.
2. `vite.config.ts` с `server.host: true` удобен для LAN, но dev server слушает внешние интерфейсы. Для локальной разработки это допустимо, но не стоит запускать на недоверенной сети.
3. Нет coverage thresholds/config. Сейчас тесты показывают факт прохождения, но не задают минимальную планку покрытия.
4. `package-lock.json` локально есть, но ранее отмечено, что он не tracked/ignored. CI поэтому допускает `npm install` fallback и менее воспроизводимую сборку.
5. `package.json` name все еще `vite-react-typescript-starter`, не соответствует продукту.

Рекомендации:

1. Исправить TypeScript errors, затем добавить `tsc --noEmit` в CI.
2. Согласовать `lib` с используемым JS API, минимум ES2022 если остаются `.at()` и `replaceAll()`.
3. Закоммитить lockfile или осознанно перейти на другой lock strategy.
4. Добавить coverage report хотя бы без жесткого порога, затем постепенно вводить thresholds по utils/services/core components.

## 62. Backend startup, deploy and runtime configuration

Дополнительно проверены `Program.cs`, `.github/workflows/deploy.yml`, backend csproj и deploy behavior.

Startup pipeline:

| Item | Status |
|---|---|
| Swagger | only in Development |
| CORS | `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()` |
| Response compression | enabled |
| Authentication/Authorization | enabled |
| Static files/default files | enabled |
| DB migration on startup | enabled |
| Seed on startup | enabled |
| Release sync on startup | enabled |
| Backfills on startup | enabled |
| Global exception handler | not found |
| HTTPS redirection/HSTS | not found |

Startup data operations:

- `Database.MigrateAsync()`
- `SeedDataService.SeedAsync(...)`
- `AppReleaseSeedService.SyncAsync(...)`
- `PaymentRemainderTransferBackfillService.EnsureLegacyLinksAsync(...)`
- `TaskContractorPaymentBackfillService.EnsureGeneratedPaymentDetailsAsync(...)`

Deploy workflow:

- Runs backend restore/test/publish.
- Runs frontend `test:ci`.
- Builds frontend with `VITE_API_URL=https://api.elitpravo.com/api`.
- Packages backend/frontend into tarball.
- Deploys to VPS via SSH.
- Backs up current release.
- Stops service, rsyncs backend/frontend, restarts systemd service.
- Checks private backend health and public API/frontend URLs.
- Keeps old frontend hashed chunks in target dirs to reduce lazy-load breakage after deploy.

Положительные проверки:

- Deploy has concurrency lock and health checks.
- Deployment creates pre-release backup.
- Frontend and backend are packaged together, reducing version skew.
- Swagger is not exposed outside Development by this code path.

Риски/пробелы:

1. `AllowAnyOrigin/Method/Header` is broad. It may be acceptable because JWT protects API, but it is still wider than needed for production.
2. `UseHttpsRedirection`/`UseHsts` are absent. If TLS is fully terminated by nginx, this may be intentional; it should be documented in deploy/runbook.
3. Startup migrations/seed/backfills can make the app fail to start or start slowly if a migration/backfill is heavy. This needs observability and rollback procedure.
4. CI deploy still does not run lint, `tsc`, frontend integration tests, `npm audit`, dotnet vulnerable package scan, `dotnet format`, `audit:dates:test`.
5. Workflow hardcodes public frontend/API URLs in YAML. This is fine for one environment, but not flexible for staging.

Рекомендации:

1. Restrict production CORS to known origins if browser clients are fixed.
2. Document TLS responsibility: nginx/systemd/app. If app must enforce HTTPS, add HSTS/HTTPS redirection.
3. Add migration/backfill logging and explicit startup timeout expectations.
4. Split deploy gate into required quality checks and deployment steps; do not deploy if typecheck/format/security checks fail.

## 63. Seed data, release sync and permission catalog

Проверены seed-related files and test references.

| File/service | Lines | Notes |
|---|---:|---|
| `backend/Services/SeedDataService.cs` | 1466 | Seeds dictionaries, roles, role permissions, admin user, optional demo clients/cases/payments. |
| `backend/Services/AppReleaseSeedService.cs` | 234 | Reads and validates `backend/AppReleases/releases.json`, syncs release entries. |
| `backend/Models/RolePermissionCatalog.cs` | 203 | Central catalog for permission names/groups. |

Test references:

| Topic | Direct refs in backend tests |
|---|---:|
| `SeedDataService` | 1 |
| `AppReleaseSeedService` | 4 |
| `RolePermissionCatalog` | 84 |
| `DataVisibilityService` | 1 |
| `Program` | 0 |
| `Cors` | 0 |
| `Swagger` | 0 |
| `ExceptionHandler` | 0 |

Положительные проверки:

- Release seed service validates release JSON before syncing.
- Permission catalog is referenced heavily in tests.
- Seed is run for both SQLite and PostgreSQL contexts through the same high-level path.

Риски/пробелы:

1. `SeedDataService` is large and business-critical, but direct tests are minimal by name search.
2. `SeedDataService` performs many `SaveChanges` calls and no explicit transaction was detected. This may be fine for idempotent seeding, but partial startup seed failure should be considered.
3. Startup calls seed/backfill every time. Idempotency tests matter more here than for one-off scripts.
4. Optional demo data controlled by `Seed:ClientsAndPayments` must remain disabled in production configs.

Рекомендации:

1. Add seed idempotency tests: run seed twice against empty DB and verify no duplicates.
2. Add production config assertion: demo seed flag must be false.
3. Add test for AppRelease seed validation against malformed release entry.
4. Consider transaction boundaries or clear recovery behavior for multi-step seed/backfill.

## 64. Raw SQL / EF batch operations

Проверены production usages of raw-ish database operations outside migrations.

| File | Operation |
|---|---|
| `InvoicesController.cs` | `SqlQuery<int>($"SELECT 1 FROM \"Payments\" WHERE \"InvoiceId\" = {invoiceEntityId} FOR UPDATE")` |
| `AuthService.cs` | `ExecuteUpdateAsync` |
| `UserActivityController.cs` | `ExecuteUpdateAsync`, `ExecuteDeleteAsync` |
| `LegalEntitiesController.cs` | `ExecuteDeleteAsync` |
| `NotificationDeliveryRetentionService.cs` | `ExecuteDeleteAsync` |
| `UserActivityRetentionService.cs` | `ExecuteDeleteAsync` |

Положительные проверки:

- `FromSqlRaw`/`ExecuteSqlRaw` in production code were not detected by this scan.
- The found SQL query uses EF interpolated SQL form, not string concatenation.
- Batch update/delete operations are EF APIs, not ad hoc SQL strings.

Риски/пробелы:

1. `FOR UPDATE` is database-specific. Need confirm behavior for SQLite vs PostgreSQL paths; SQLite may not support this syntax the same way.
2. `ExecuteUpdate/Delete` bypasses normal entity tracking and can bypass audit hooks implemented in `SaveChanges`.
3. Retention/delete jobs should have explicit tests for “deletes only eligible rows”.

Рекомендации:

1. Add cross-provider tests around the invoice locking path or guard it by provider.
2. Document which batch operations intentionally bypass object audit.
3. Add retention boundary tests with old/new records.

## 65. Frontend test setup and mocking risks

Проверен `src/test/setup.ts` and frontend test patterns.

Setup behavior:

- Cleans React Testing Library after each test.
- Globally mocks `window.open`.
- Prevents navigation/download behavior for anchors with `download` or `blob:` URLs.
- Overrides `HTMLAnchorElement.prototype.click` for download/blob links.
- Adds `matchMedia` mock when missing.
- Adds `URL.createObjectURL` / `URL.revokeObjectURL` mocks when missing.

Test inventory additions:

| Metric | Value |
|---|---:|
| Frontend test files | 84 |
| Unit-ish test files by filename/content heuristic | 71 |
| Integration test files | 13 |
| Component render tests | 49 |
| Hook tests detected by `renderHook` heuristic | 0 |
| Accessibility/a11y text heuristic | 6 |
| Visual/screenshot/playwright heuristic | 1 |

Положительные проверки:

- The setup prevents jsdom from accidentally navigating away or failing on browser-only APIs.
- There are many component render tests, not only pure util tests.
- Integration tests exist for heavy UI areas: accounts, invoices, acts, calendar, clients, contracts, payments, money operations, legal entities.

Риски/пробелы:

1. Global `window.open` and anchor click mocks can hide real browser download/print/new-tab issues.
2. No MSW/setupServer pattern was detected. Many tests mock fetch/api ad hoc, which can drift from actual API behavior.
3. `userEvent` is unevenly used; some integration tests render complex pages but do not simulate realistic user interactions.
4. Hook tests are not detected as a separate layer, even though hooks were previously found to have coverage gaps.

Рекомендации:

1. Add a small Playwright/browser smoke for download/print/new-tab flows that Vitest intentionally mocks.
2. Consider MSW or a shared API mock layer for frontend tests.
3. Add hook-specific tests for hooks without coverage from the earlier inventory.
4. Add accessibility assertions with `axe` or explicit aria role/name checks for navigation, modals and toolbar-heavy screens.

## 66. Что добрал этот проход

В этот проход добавлены:

- frontend tab/routing/permission gate consistency;
- `appRouting.ts`, `Tab`, `Navigation` drift checks;
- Vite/Vitest/TypeScript/package/env config inventory;
- backend startup/deploy/runtime pipeline review;
- seed/release/permission catalog review;
- raw SQL and EF batch operation scan;
- frontend global test setup and mocking risk review.

Главные новые выводы:

1. `Tab` union and `ALL_TABS` are synchronized 47/47.
2. Frontend has route guards and permission fallbacks, but hidden/detail tabs need explicit negative deep-link tests.
3. `tsconfig.app.json` is stricter than the current code can satisfy: `tsc --noEmit` still remains a real blocker from the earlier pass.
4. Production startup runs migrations, seed, release sync and backfills automatically; this needs observability and idempotency tests.
5. CORS is broad, HTTPS/HSTS/global exception middleware are not present in app code.
6. Frontend tests globally mock browser download/new-window behavior; those flows need real-browser smoke coverage.
