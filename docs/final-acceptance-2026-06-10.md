# Финальная приемка roadmap - 2026-06-10

## Выполнено

- Release gate после последних изменений проходит: `npm ci`, `npm run lint`, `npx tsc -p tsconfig.app.json --noEmit`, `npm run test:unit`, `npm run test:integration`, `npm run audit:dates:test`, `npm run build`, `dotnet test backend\PayPlanner.Api.sln --no-restore -c Debug`, `dotnet format backend\PayPlanner.Api.sln --verify-no-changes --no-restore --verbosity minimal`, `dotnet list backend\PayPlanner.Api.sln package --vulnerable --include-transitive`, `npm audit --audit-level=moderate`.
- Локальная SQLite DB проверена на свежем старте: миграции применяются, backend стартует, `/api/health` возвращает `200`.
- API auth smoke проверен: public health `200`, bad login `401`, protected endpoint without token `401`, forbidden role `403`, missing object `404`.
- Targeted checks после исправлений проходят: `SqliteMigrationSmokeTests`, `authService.test.ts`, `InvoiceModal.integration.test.tsx`, `run-visual-smoke.test.mjs`.
- Лишних процессов `vite`, `npm run dev` и `dotnet run` для workspace после проверки не осталось.

## Исправления

- SQLite migrations теперь используют проектный generator, который пропускает неподдерживаемые SQLite foreign key операции; ручная миграция `AddTaskContractorPayments` видима EF Core.
- Получение текущего пользователя во frontend переведено на профильный endpoint, чтобы shell не падал на несовпадении auth routes.
- Редактор счета больше не запускает бесконечные обновления состояния при открытии страницы счетов и проверке связанных платежей.
- Visual smoke стал диагностировать failures подробнее, закрывает стартовую модалку релизов и игнорирует ожидаемый SignalR negotiation stop во время переходов.

## Осталось

- Full-app visual smoke пока не закрыт полностью:
  - desktop `/` имеет horizontal overflow 55px;
  - tablet `/clients` имеет horizontal overflow 295px;
  - optional user-settings возвращают 404 для `money.bankStatement.autoImport`, `money.bankStatement.categoryRules`, `notifications.typePreferences.v1`.
- Файл `docs/date-time-inventory.md` был изменен вне этой работы и оставлен нетронутым.

## Release Notes

- Добавлена запись `2026-06-11-profile-and-invoice-editor-stability` в `backend/AppReleases/releases.json`.
