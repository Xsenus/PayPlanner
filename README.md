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
- [Качество кода](#качество-кода)
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
cd src
npm i
npm run dev
```

- По умолчанию фронт ожидает API по адресу `<http://localhost:5080/api>` (настраивается через `VITE_API_URL`).

### Backend

```bash
cd api
dotnet build
dotnet ef database update
dotnet run
```

- API поднимается на `<http://localhost:5080>` (порт может отличаться согласно `launchSettings.json`).

## Переменные окружения

### Frontend (`/src`)

Создайте файл `.env` (или `.env.local`) со значениями:

```bash
VITE_API_URL=http://localhost:5080/api
```

> Vite читает переменные, начинающиеся с `VITE_`.

### Backend (`/api`)

В `appsettings.json` укажите строку подключения к БД. Пример для PostgreSQL:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=payplanner;Username=postgres;Password=postgres"
  }
}
```

Локальные секреты/пароли храните в `appsettings.Development.json` или `User Secrets`.

## Миграции БД и сидирование

Генерация миграции:

```bash
cd api
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

### Backend скрипты

- `dotnet run` — старт API
- `dotnet build` — сборка
- `dotnet test` — тесты (если настроены)
- `dotnet ef ...` — миграции EF Core

## Качество кода

- TypeScript strict, ESLint + Prettier.
- Чистый код: осмысленные имена, SRP, SOLID, минимальная область видимости.
- Обработка ошибок: валидация входных данных, аккуратные исключения, сообщения для пользователя.
- Тестирование: модульные (фронт/бэк), интеграционные для API (по мере готовности).

## Частые проблемы

- **CORS**: при старте фронта/бэка на разных портах настройте CORS в API.
- **ENV**: Vite видит только переменные, начинающиеся с `VITE_*`.
- **Миграции**: после изменения моделей не забывайте `dotnet ef database update`.

## Лицензия

© 2025, PayPlanner. Все права защищены. Контакты: <ilel@list.ru>.
