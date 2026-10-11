# PayPlanner

Актуальные задания для Claude: [передача 37 карточек YouGile и подробные ТЗ — 10 октября 2026](docs/claude-yougile-development-handoff-2026-10-10.md).

## О проекте

**PayPlanner** — внутренняя система планирования и трекинга платежей с календарём, аналитикой и справочниками.  
Фронт на React, бэк на ASP.NET Core. Данные — через Web API. Для разработки предусмотрено локальное окружение.

## Оглавление

- [О проекте](#о-проекте)
- [Функциональность](#функциональность)
- [Технологии](#технологии)
  - [Frontend технологии](#frontend-технологии)
  - [Backend технологии](#backend-технологии)
- [Структура репозитория](#структура-репозитория)
- [Быстрый старт](#быстрый-старт)
  - [Frontend](#frontend)
  - [Backend](#backend)
- [Переменные окружения](#переменные-окружения)
- [Миграции БД и сидирование](#миграции-бд-и-сидирование)
- [Скрипты](#скрипты)
  - [Frontend скрипты](#frontend-скрипты)
  - [Backend скрипты](#backend-скрипты)
- [Карточка клиента: раздел «Счета»](#карточка-клиента-раздел-счета)
  - [Как работает сейчас](#как-работает-сейчас)
  - [Как считается сводка](#как-считается-сводка)
  - [Как считаются бакеты просрочки (aging)](#как-считаются-бакеты-просрочки-aging)
- [Качество кода](#качество-кода)
- [Актуальное состояние аудита и quality gates](#актуальное-состояние-аудита-и-quality-gates)
  - [Что проверено](#что-проверено)
  - [Критические проблемы](#критические-проблемы)
  - [Что доработать в первую очередь](#что-доработать-в-первую-очередь)
  - [Что можно реализовать дальше](#что-можно-реализовать-дальше)
- [Частые проблемы](#частые-проблемы)
- [Лицензия](#лицензия)

---

## Функциональность

- Календарь платежей (создание/редактирование/удаление).
- Статусы платежей: *Оплачено / Ожидается / Просрочено*.
- Клиенты и дела (кейсы), привязка платежей к делам.
- Диаграммы и KPI за периоды (месяц/диапазон месяцев).
- Справочники (источники платежей, типы доходов/расходов).
- Поиск, фильтрация, сортировка.
- Адаптивный UI (мобилка/десктоп).
- В карточке клиента в разделе **«Счета»** всегда показывается полная история счетов клиента за весь период.

## Технологии

### Frontend технологии

- React + TypeScript + Vite
- Recharts, Framer Motion, lucide-react
- CSS-утилиты (Tailwind/кастомные классы)

### Backend технологии

- ASP.NET Core (Minimal API/Controllers)
- Entity Framework Core
- БД: PostgreSQL (по умолчанию), поддержка SQLite для локальной разработки

## Структура репозитория

```bash
/ (root)
├─ /src               # Frontend (React + Vite)
│  ├─ /components
│  ├─ /hooks
│  ├─ /services       # api.ts и пр.
│  ├─ /types
│  └─ main.tsx / App.tsx
├─ /backend           # Backend (ASP.NET Core)
│  ├─ /Data           # DbContext (PaymentContext), миграции
│  ├─ /Models
│  ├─ /Services       # SeedDataService и т.д.
│  └─ Program.cs / appsettings.json
└─ README.md
```

## Быстрый старт

### Frontend

```bash
cd /workspace/PayPlanner
npm ci
npm run dev
```

- По умолчанию фронт ожидает API по адресу `<http://localhost:5080/api>` (настраивается через `VITE_API_URL`).

### Backend

Перед запуском выполните [подготовку локальной почты и копии БД](#локальная-почта-и-копия-бд). Для каждого локального запуска оба отключающих флага обязательны.

```bash
cd /workspace/PayPlanner/backend
dotnet restore PayPlanner.Api.sln
BackgroundServices__Enabled=false Email__Enabled=false dotnet run --project PayPlanner.Api.csproj
```

- API поднимается на `<http://localhost:5080>` (порт может отличаться согласно `launchSettings.json`).

## Переменные окружения

### Frontend (`/`)

Создайте файл `.env` (или `.env.local`) со значениями:

```bash
VITE_API_URL=http://localhost:5080/api
```

> Vite читает переменные, начинающиеся с `VITE_`.

### Backend (`/backend`)

Реальные секреты не хранятся в `appsettings*.json`. Для локальной разработки используйте `dotnet user-secrets`, для сервера — переменные окружения или secret store. Пример для PostgreSQL через user-secrets:

```bash
cd /workspace/PayPlanner/backend
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=payplanner;Username=postgres;Password=<password>"
dotnet user-secrets set "Jwt:Secret" "<at-least-32-random-characters>"
```

Пример серверных переменных окружения для production; отключающие флаги локальной разработки в этот пример не входят:

```bash
ConnectionStrings__Default=Host=localhost;Port=5432;Database=payplanner;Username=postgres;Password=<password>
Jwt__Secret=<at-least-32-random-characters>
DaData__ApiKey=<optional-dadata-api-key>
DaData__Secret=<optional-dadata-secret>
Email__SmtpPassword=<optional-smtp-password>
WebPush__Enabled=true
WebPush__PublicKey=<optional-vapid-public-key>
WebPush__PrivateKey=<optional-vapid-private-key>
WebPush__Subject=mailto:admin@example.com
FileStorage__RootPath=/opt/payplanner/shared/files
Cors__AllowedOrigins__0=https://crm.example.com
```

Обязательные параметры для запуска backend: `ConnectionStrings:Default` и `Jwt:Secret`. `Jwt:Secret` должен быть не короче 32 символов; приложение завершит старт с ошибкой, если секрет не задан. `DaData:*`, `Email:*` и `WebPush:*` нужны только для соответствующих интеграций.
В Development backend оставляет CORS открытым для локальной разработки. В Production внешние origins берутся только из `Cors:AllowedOrigins`; если список пустой, cross-origin запросы не разрешаются, а работа через тот же домен продолжает идти без CORS.

Production TLS/HSTS закреплены в [deploy runbook](docs/deploy-runbook.md): публичный HTTPS, редирект HTTP->HTTPS и заголовок `Strict-Transport-Security` должны обеспечиваться nginx, а backend слушает внутренний HTTP endpoint `127.0.0.1:5080`.
В production `FileStorage__RootPath` должен указывать на постоянный каталог вне директории текущего релиза. Стандартный deploy использует `/opt/payplanner/shared/files`, чтобы вложения задач и документы дел сохранялись между обновлениями приложения.

### Локальная почта и копия БД

Перед каждым локальным, тестовым или предварительным запуском проверьте фактические настройки SMTP из `appsettings`, переменных окружения, user-secrets и БД. Имя окружения `Development` и адреса получателей с доменом `.local` не запрещают отправку через рабочий SMTP. Всегда задавайте `BackgroundServices__Enabled=false` и `Email__Enabled=false`, как в командах запуска ниже и выше.

После каждого импорта или обновления копии production-БД, **до запуска backend**:

1. Подтвердите, что подключение указывает на отдельную локальную копию. Следующие изменения выполняются только в ней.
2. Установите в `SystemSettings` значение `notifications.email.enabled=false`: сохранённая настройка БД имеет приоритет над `Email__Enabled`. Удалите или замените рабочие SMTP host, username, password и адрес отправителя в настройках БД, конфигурации, окружении и секретах.
3. Отключите почтовых отправителей `NotificationSenders` и очистите их SMTP-конфигурацию. Уберите из повторной отправки локальные записи `NotificationDeliveries` канала `email` со статусами `pending`/`failed`; очистите или отключите другие очереди и фоновые задания, способные отправлять письма.
4. Убедитесь, что исходящая почта отключена либо направлена в локальный почтовый перехватчик без внешней доставки. Пока изоляция не подтверждена, не запускайте backend и сценарии, создающие уведомления, напоминания, согласования или платежи.

## Current developer setup

From the repository root:

```powershell
npm ci
dotnet restore backend\PayPlanner.Api.sln
$env:BackgroundServices__Enabled = "false"
$env:Email__Enabled = "false"
dotnet run --project backend\PayPlanner.Api.csproj
```

In another shell:

```powershell
$env:VITE_API_URL = "http://localhost:5080/api"
npm run dev
```

For local PostgreSQL development, provide `ConnectionStrings__Default` and `Jwt__Secret` through environment variables, `dotnet user-secrets`, or another local secret store. `Jwt__Secret` must contain at least 32 characters. Before launching, follow [local email and copied-database preparation](#локальная-почта-и-копия-бд); the two environment flags alone do not override email settings stored in a copied database. Startup applies migrations, seed data, release sync, and data maintenance steps automatically; do not commit real secrets to `appsettings*.json`.

Before a PR or deploy, use [quality gates](docs/quality-gates.md). For user-visible changes, follow [release note policy](docs/release-note-policy.md). Production operations, secrets, CORS, TLS/HSTS, startup migrations, backup/rollback, and health checks are documented in [deploy runbook](docs/deploy-runbook.md).

## Миграции БД и сидирование

Миграции находятся в отдельном проекте `backend/PayPlanner.Api.Migrations`, а API служит стартовым проектом. Выполняйте команды из корня репозитория и явно выбирайте контекст: `PaymentContext` для SQLite или `PostgreSqlPaymentContext` для PostgreSQL. Имя новой миграции должно описывать изменение; существующую начальную миграцию повторно создавать не нужно.

Пример генерации миграции SQLite:

```bash
dotnet ef migrations add AddExampleField --project backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --startup-project backend/PayPlanner.Api.csproj --context PaymentContext --output-dir Migrations
```

Для PostgreSQL используйте `--context PostgreSqlPaymentContext --output-dir Migrations/PostgreSql` и задайте `PAYPLANNER_PG_MIGRATIONS_CONNECTION` через локальное хранилище секретов. Для SQLite можно задать `PAYPLANNER_SQLITE_MIGRATIONS_CONNECTION`; оба контекста также используют `ConnectionStrings__Default`. Не применяйте команды обновления к рабочей production-БД для проверки.

Просмотр миграций без подключения к базе:

```bash
dotnet ef migrations list --no-connect --project backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --startup-project backend/PayPlanner.Api.csproj --context PaymentContext
dotnet ef migrations list --no-connect --project backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --startup-project backend/PayPlanner.Api.csproj --context PostgreSqlPaymentContext
```

Перед любым локальным запуском выполните [изоляцию почты](#локальная-почта-и-копия-бд). При старте API миграции выбранного провайдера применяются автоматически, затем выполняются первичное наполнение, синхронизация выпусков и обслуживание данных.

- При старте приложения вызывается `SeedDataService.SeedAsync(PaymentContext)` и, если БД пустая (нет клиентов), вносятся тестовые данные.

## Скрипты

### Frontend скрипты

- `npm run dev` — режим разработки
- `npm run build` — прод сборка
- `npm run preview` — предпросмотр прод-сборки
- `npm run lint` — линтинг
- `npm test` — полный набор frontend unit-тестов с ограничением до трёх worker-процессов
- `npm run test:watch` — интерактивный режим разработки с ограничением до двух worker-процессов
- `npm run test:unit:serial` — последовательный прогон для диагностики нестабильного теста
- `npm run test:integration` — последовательный набор интеграционных frontend-тестов
- `npm run test:integration -- src/components/Accounts/InstallmentPlanner.integration.test.tsx` — выбранный integration-файл для разработки; можно перечислить несколько точных путей
- `npm run test:integration -- --help` — допустимые параметры; неизвестный путь или флаг завершает команду ошибкой

Чистые проверки утилит и файловых контрактов выполняются в лёгком Node-окружении; компонентные и браузерные сценарии продолжают использовать `jsdom`.

Во время правок запускайте ближайший unit/component-файл, например `npx vitest run src/utils/reportPeriod.test.ts --maxWorkers=1`, или явно выбранные integration-файлы. Integration runner сохраняет один worker и полный набор без аргументов; поддерживает `--reporter`, но отвергает каталоги, glob-фильтры и `-t`/`--testNamePattern`, чтобы ошибочный выбор не превратился в успешный пропущенный прогон. Перед окончательной передачей или выпуском нужен полный применимый [quality gate](docs/quality-gates.md), а не результат выбранного файла. `npm ci` требуется при подготовке зависимостей, не перед каждым неизменным повтором теста.

### Backend скрипты

- `BackgroundServices__Enabled=false Email__Enabled=false dotnet run` — локальный старт API из каталога `backend` (Bash), после подготовки почты и копии БД
- `dotnet build backend/PayPlanner.Api.sln -c Release` — сборка из корня репозитория
- `dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj -c Release --filter "FullyQualifiedName~TaskBillingConcurrencyTests"` — пример адресной проверки после задания безопасной локальной среды ниже
- `dotnet ef ... --project backend/PayPlanner.Api.Migrations/PayPlanner.Api.Migrations.csproj --startup-project backend/PayPlanner.Api.csproj --context PaymentContext` — миграции SQLite из корня репозитория; для PostgreSQL выберите `PostgreSqlPaymentContext`

Перед локальными backend-проверками явно задайте оба флага (PowerShell); настройки копии БД также должны быть изолированы по разделу [локальной почты](#локальная-почта-и-копия-бд):

```powershell
$env:BackgroundServices__Enabled = "false"
$env:Email__Enabled = "false"
```

Для повторения тестов на неизменных исходниках после успешной сборки используйте ту же конфигурацию/output и `--no-build --no-restore`. При изменении C#, конфигурации или зависимостей сначала пересоберите затронутые проекты. Сам `--filter` не отменяет сборку. Полный выпускной backend проверяется в Release; необходимые PostgreSQL/browser-проверки и обязательный CI описаны в [quality gates](docs/quality-gates.md) и [workflow](.github/workflows/deploy.yml). Адресный прогон их не заменяет.

## Карточка клиента: раздел «Счета»

### Как работает сейчас

- Раздел `Счета` в карточке клиента работает **без локальных фильтров по дате/статусу/поиску**.
- Таблица загружается по `clientId` и типу `Income`, то есть показывает полную историю счетов клиента.
- Сохранены служебные действия интерфейса:
  - сортировка колонок,
  - пагинация,
  - кнопка ручного обновления.

### Как считается сводка

Верхний блок сводки в карточке клиента состоит из 3 карточек:

1. `Не оплачено всего` — bucket `pending`.
2. `Не оплачено в срок` — bucket `overdue`.
3. `Оплачено` — bucket `paid`.

Источник данных — API `GET /api/invoices/summary`.

### Как считаются бакеты просрочки (aging)

Дополнительно API возвращает структуру `OverdueAging` с разбиением просроченных сумм по диапазонам дней:

- `0–10`
- `11–30`
- `31–60`
- `61–90`
- `91+`

Логика:

- в расчет попадают только счета со статусом `Overdue`;
- число дней просрочки считается как разница между текущей датой (`UTC Date`) и датой оплаты счета (`due date` = `Payment.Date`);
- каждая сумма попадает в один соответствующий диапазон;
- `Total` в `OverdueAging` — это сумма всех aging-бакетов.

## Качество кода

- TypeScript strict, ESLint + Prettier.
- Чистый код: осмысленные имена, SRP, SOLID, минимальная область видимости.
- Обработка ошибок: валидация входных данных, аккуратные исключения, сообщения для пользователя.
- Тестирование: модульные (фронт/бэк), интеграционные для API (по мере готовности).

## Актуальное состояние аудита и quality gates

Состав проверок описан в [quality gates](docs/quality-gates.md), а точные команды и порядок выпуска — в [deploy workflow](.github/workflows/deploy.yml). Фактические результаты текущей интеграции и открытые барьеры собраны в [предпубликационном отчёте](docs/integration-deployment-2026-10-07.md). Наличие проверок в CI или успешный адресный тест сами по себе не подтверждают готовность всего выпуска.

### Уже действующие проверки

- Backend: обязательный аудит всех проектов при `dotnet restore --force -p:PayPlannerReleaseAudit=true`, проверка `dotnet format --verify-no-changes` и полный тестовый проект в конфигурации **Release**. CI предоставляет отдельный PostgreSQL для интеграционных и миграционных проверок.
- Frontend: ESLint, `tsc -p tsconfig.app.json --noEmit`, аудиты дат, кодировки и реестра экранов/API, `npm audit --audit-level=moderate`, guard/unit и integration tests, строгая проверка legal feature flags и production-сборка.
- CI также проверяет сохранение ключей интеграций, очистку копий развёртывания, перенос прежних файлов и восстановленную CRM через HTTP и браузер. Для восстановленной CRM используется та же Release-сборка с `--no-build`; публикация backend также повторно использует проверенную сборку.
- Все эти проверки предшествуют загрузке артефактов на VPS. После установки дополнительно проверяются ожидаемая ревизия, API, frontend и служба; итоговый статус фиксируется по результатам, а не по факту `git push`.

### Что изменилось со времени прежнего аудита

Ранее были исправлены типы переводов, callback-функций платёжных форм, ролей и nullable-полей. Типизация и тесты уже закреплены в CI; задача перед каждым выпуском — сохранить их успешное прохождение на итоговых исходниках.

Разделение frontend-кода уже реализовано: `src/App.tsx` загружает календарь, клиентов, платежи, отчёты и другие крупные страницы через `lazy`/`Suspense`. В `vite.config.ts` настроен `manualChunks` для React, SignalR, графиков, анимации и иконок. Размеры файлов и предупреждения нужно оценивать по текущему `npm run build`; прежняя оценка единого большого чанка не является актуальным измерением. Дальнейшую оптимизацию следует выбирать по замерам загрузки конкретных страниц, запросов и передаваемых данных.

### Локальные команды и ресурсы

Для обычной разработки используются команды из [быстрого старта](#быстрый-старт): `npm ci`, `npm run dev`, `dotnet restore` и запуск backend с явными `BackgroundServices__Enabled=false` и `Email__Enabled=false` после [изоляции локальной почты](#локальная-почта-и-копия-бд). Frontend собирается командой `npm run build`. Перед выпуском выполняется полный применимый набор проверок. В CI backend собирается один раз, все обнаруженные тесты распределяются между четырьмя параллельными заданиями, а проверка восстановленной CRM выполняется отдельно на той же сборке. Общая проверка подтверждает полный каталог без пропусков и повторов. Развёртывание начинается только после успешных проверок frontend, форматирования и backend; точные команды и порядок описаны в [quality gates](docs/quality-gates.md).

Полный локальный выпускной backend-прогон требует отдельного синтетического PostgreSQL и `PAYPLANNER_TEST_POSTGRES`, `PAYPLANNER_TEST_PG_DUMP`, `PAYPLANNER_TEST_PSQL`, `CI=true`, установленных браузеров и пинованного Playwright CLI с `PAYPLANNER_TEST_PLAYWRIGHT_CLI`/`PAYPLANNER_TEST_BROWSER`. Для HTTP/browser-проверок восстановленной CRM также задаётся `PAYPLANNER_TEST_RESTORED_BROWSER=true`. Точные зависимости и настройка — в [deploy workflow](.github/workflows/deploy.yml). Простой `dotnet test` без этой среды не подтверждает PostgreSQL и браузерные проверки.

Для тяжёлой backend-сборки на машине с ограниченной памятью, например 16 ГБ, можно дополнительно задать в текущем PowerShell-сеансе настройки, используемые в CI:

```powershell
$env:DOTNET_PROCESSOR_COUNT = "2"
$env:DOTNET_gcServer = "0"
$env:DOTNET_GCConserveMemory = "9"
```

Они ограничивают параллелизм и настраивают расход памяти GC; анализаторы и проверки остаются включёнными. Для сборки также нужно свободное место для временных файлов и файла подкачки. Эти настройки ресурсов необязательны для обычного запуска; обязательные флаги локальной почты и фоновых служб сохраняются.

### Дальнейшая работа

- Сохранять обязательные CI-проверки и добавлять адресные регрессии при изменении финансовых правил, прав доступа, интеграций и конкурентных операций.
- Проверять загрузку тяжёлых страниц и совместимость с поддерживаемыми браузерами по фактическим измерениям и пользовательским сценариям.
- Улучшать корреляцию серверных логов и диагностику frontend-ошибок по подтверждённым пробелам. Storybook, новые ADR и развитие staging остаются направлениями для отдельного согласования.
- Регулярные платежи, развитие шаблонов договоров и прогнозы DSO/кассового разрыва сохранены как предложения прежнего аудита для отдельной оценки с учётом уже имеющихся возможностей; это не перечень отсутствующих функций.
- Health-проверки, резервирование по расписанию и пробное восстановление уже существуют; их фактическую работу, сохранность файлов и ключей необходимо подтверждать при выпуске. Продуктовые идеи и ручная приёмка ведутся в [активном плане развития](docs/development-full-roadmap-2026-10-07.md), без автоматического закрытия по одному успешному тесту.

## Частые проблемы

- **CORS**: при старте фронта/бэка на разных портах настройте CORS в API.
- **ENV**: Vite видит только переменные, начинающиеся с `VITE_*`.
- **Миграции**: после изменения моделей добавляйте EF migration и проверяйте startup на локальной БД; приложение применяет миграции при старте.

## Лицензия

© 2025, PayPlanner. Все права защищены. Контакты: <ilel@list.ru>.
