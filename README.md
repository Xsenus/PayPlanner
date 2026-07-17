# PayPlanner

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

```bash
cd /workspace/PayPlanner/backend
dotnet restore PayPlanner.Api.sln
dotnet run --project PayPlanner.Api.csproj
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

Эквивалентные переменные окружения:

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
Cors__AllowedOrigins__0=https://crm.example.com
```

Обязательные параметры для запуска backend: `ConnectionStrings:Default` и `Jwt:Secret`. `Jwt:Secret` должен быть не короче 32 символов; приложение завершит старт с ошибкой, если секрет не задан. `DaData:*`, `Email:*` и `WebPush:*` нужны только для соответствующих интеграций.
В Development backend оставляет CORS открытым для локальной разработки. В Production внешние origins берутся только из `Cors:AllowedOrigins`; если список пустой, cross-origin запросы не разрешаются, а работа через тот же домен продолжает идти без CORS.

Production TLS/HSTS закреплены в [deploy runbook](docs/deploy-runbook.md): публичный HTTPS, редирект HTTP->HTTPS и заголовок `Strict-Transport-Security` должны обеспечиваться nginx, а backend слушает внутренний HTTP endpoint `127.0.0.1:5080`.

## Current developer setup

From the repository root:

```powershell
npm ci
dotnet restore backend\PayPlanner.Api.sln
dotnet run --project backend\PayPlanner.Api.csproj
```

In another shell:

```powershell
$env:VITE_API_URL = "http://localhost:5080/api"
npm run dev
```

For local PostgreSQL development, provide `ConnectionStrings__Default` and `Jwt__Secret` through environment variables, `dotnet user-secrets`, or another local secret store. `Jwt__Secret` must contain at least 32 characters. Startup applies migrations, seed data, release sync, and data maintenance steps automatically; do not commit real secrets to `appsettings*.json`.

Before a PR or deploy, use [quality gates](docs/quality-gates.md). For user-visible changes, follow [release note policy](docs/release-note-policy.md). Production operations, secrets, CORS, TLS/HSTS, startup migrations, backup/rollback, and health checks are documented in [deploy runbook](docs/deploy-runbook.md).

## Миграции БД и сидирование

Генерация миграции:

```bash
cd /workspace/PayPlanner/backend
dotnet ef migrations add Init
```

Применение миграций и первичное наполнение (сидирование):

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

Чистые проверки утилит и файловых контрактов выполняются в лёгком Node-окружении; компонентные и браузерные сценарии продолжают использовать `jsdom`.

### Backend скрипты

- `dotnet run` — старт API
- `dotnet build` — сборка
- `dotnet test` — тесты (если настроены)
- `dotnet ef ...` — миграции EF Core

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

### Что проверено

- `npm run lint` — линтер проходит без ошибок.
- `npx tsc --noEmit -p tsconfig.app.json` — ошибок типизации не обнаружено (после исправлений).
- `npm run build` — production-сборка успешна, но есть предупреждение о большом чанке (`~1.2MB`).
- `dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug` — backend tests проходят; `dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal` закреплен как gate.

### Критические проблемы

1. **Часть критичных TypeScript-проблем устранена**.
   - Исправлены типы переводов с параметрами, сигнатуры callback'ов платёжных модалок, конфликты типов ролей и строгие проверки nullable-полей.
   - Текущее состояние: `tsc --noEmit` проходит успешно, но важно удерживать это как обязательный quality gate.

2. **Слабая “защита от регрессий”**.
   - В репозитории есть backend tests, frontend guard/unit/integration tests и отдельные smoke/readiness scripts.
   - Deploy workflow запускает backend format/package audit/tests, frontend lint/typecheck/audit/tests и legal feature flag gate до публикации артефактов.

3. **Проблема производительности frontend bundle**.
   - Vite предупреждает о крупном JS-чанке после минификации.
   - Нужны code splitting/ленивая загрузка тяжёлых страниц и/или ручное chunking-правило.

### Что доработать в первую очередь

1. **Закрепить достигнутую стабильность типов (P0)**
   - Зафиксировать в CI обязательный gate: `tsc --noEmit` + запрет merge при падении компиляции.
   - Добавить регрессионные проверки для зон, где были исправления (платежные модалки, роли, локализация).

2. **Поддерживать CI и quality gates**
   - Стандартный набор команд описан в [quality gates](docs/quality-gates.md).
   - Не ослаблять deploy gate без явного решения и причины.

3. **Ускорить загрузку фронтенда (P1)**
   - Вынести крупные разделы (Календарь/Клиенты/Платежи/Отчёты) в `React.lazy`.
   - Настроить `manualChunks` в `vite.config.ts` (vendor/UI/charts).

4. **Усилить наблюдаемость и диагностику (P1)**
   - Единый формат логирования на бэке (уровни, correlation id).
   - Централизованная обработка ошибок на фронте с кодами/контекстом.

### Что можно реализовать дальше

- **Функциональность продукта**:
  - уведомления о дедлайнах оплат (email/Telegram/внутренний inbox),
  - автоматические recurring-платежи и шаблоны договоров,
  - дашборд DSO/просрочка/кассовый разрыв с прогнозом.

- **Для команды**:
  - Storybook для UI-компонентов и визуальной регрессии,
  - интеграционные тесты API (контракты + happy-path + edge-cases),
  - ADR-документы для ключевых архитектурных решений.

- **Для эксплуатации**:
  - healthchecks + readiness/liveness,
  - резервное копирование БД по расписанию + проверка восстановления,
  - окружения staging/prod с одинаковыми миграционными процедурами.

## Частые проблемы

- **CORS**: при старте фронта/бэка на разных портах настройте CORS в API.
- **ENV**: Vite видит только переменные, начинающиеся с `VITE_*`.
- **Миграции**: после изменения моделей добавляйте EF migration и проверяйте startup на локальной БД; приложение применяет миграции при старте.

## Лицензия

© 2025, PayPlanner. Все права защищены. Контакты: <ilel@list.ru>.
