# Финальная приемка roadmap - 2026-06-10

## Выполнено

- Release gate после последних изменений проходит: `npm ci`, `npm run lint`, `npx tsc -p tsconfig.app.json --noEmit`, `npm run test:unit`, `npm run test:integration`, `npm run audit:dates:test`, `npm run build`, `dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug`, `dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal`, `dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive`, `npm audit --audit-level=moderate`.
- Локальная SQLite DB проверена на свежем старте: миграции применяются, backend стартует, `/api/health` возвращает `200`.
- API auth smoke проверен: public health `200`, bad login `401`, protected endpoint without token `401`, forbidden role `403`, missing object `404`.
- Targeted checks после исправлений проходят: `SqliteMigrationSmokeTests`, `authService.test.ts`, `InvoiceModal.integration.test.tsx`, `run-visual-smoke.test.mjs`.
- Full visual smoke прошел на desktop/tablet/mobile по маршрутам `/`, `/tasks`, `/tasks/approvals`, `/tasks/calendar`, `/clients`, `/invoices`, `/payments`, `/acts`, `/contracts`, `/?ppTab=moneyBank`, `/?ppTab=settings`, `/?ppTab=users`.
- Лишних процессов `vite`, `npm run dev` и `dotnet run` для workspace после проверки не осталось.

## Исправления

- SQLite migrations теперь используют проектный generator, который пропускает неподдерживаемые SQLite foreign key операции; ручная миграция `AddTaskContractorPayments` видима EF Core.
- Получение текущего пользователя во frontend переведено на профильный endpoint, чтобы shell не падал на несовпадении auth routes.
- Редактор счета больше не запускает бесконечные обновления состояния при открытии страницы счетов и проверке связанных платежей.
- Visual smoke стал диагностировать failures подробнее, закрывает стартовую модалку релизов и игнорирует ожидаемый SignalR negotiation stop во время переходов.
- Исправлен horizontal overflow на desktop `/` и tablet `/clients`.
- Отсутствующие optional user-settings теперь обрабатываются как пустые настройки и не создают 404 console errors.

## Осталось

- Открытых пунктов roadmap не осталось.
- Файл `docs/date-time-inventory.md` был изменен вне этой работы и оставлен нетронутым.

## Release Notes

- Добавлены записи `2026-06-11-profile-and-invoice-editor-stability` и `2026-06-11-visual-smoke-layout-and-settings` в `backend/AppReleases/releases.json`.
