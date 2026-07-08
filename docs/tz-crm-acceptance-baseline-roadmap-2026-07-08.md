# Roadmap 0. Приемка уже реализованных блоков CRM

Дата создания: 8 июля 2026.
Статус: активный roadmap.

## Сводка прогресса

Прогресс: 2 из 3 рабочих пунктов закрыто, 67%.

- Всего рабочих пунктов: 3.
- Закрыто и проверено: 2.
- В работе: 0.
- Заблокировано или отмечено как риск: 0.
- Требует решения заказчика: 0.
- Требует ручной приемки: 1.

## Цель

Подтвердить на реальных данных, что уже реализованные модули действительно закрывают базовую часть ТЗ: дела, задачи, календарь, документы, финансы, права, уведомления и отчеты.

## Рабочие пункты

- `[x]` Выполнена первичная сверка ТЗ с проектом: найдены реализованные модули, частичные зоны и неподтвержденные интеграции. Проверены выборочные frontend/backend тесты по ключевым разделам.
- `[x]` Проведена техническая часть приемки основных закрытых блоков на production: доступность CRM, API health, frontend, service worker, VPS-сервис и доступные smoke-проверки. Ручная проверка пользовательских сценариев на реальных данных остается в acceptance до подтверждения заказчиком.
- `[acceptance]` Проверить на реальных ролях заказчика ограничения видимости дел, финансов, задач, судебных событий и документов.

## Definition of Done

- Есть подтверждение, что реализованные блоки работают на реальных данных заказчика.
- Все найденные расхождения вынесены в отдельные активные roadmap, а не остаются заметками в чате.
- После приемки этот roadmap переносится в `docs/archive/roadmaps/`, а индекс обновляется.

## Проверки

- 2026-07-08: `node scripts/run-production-deploy-readiness.mjs --api-url=https://api.elitpravo.com --frontend-url=https://crm.elitpravo.com --require-revision=true --allow-websocket-auth-challenge=true --timeout-ms=20000` — успешно. Проверены frontend 200, revision marker `ebf01336c6366f5b52e1f442be009806850161f1`, API health 200, service worker 200, WebSocket auth challenge 401 для `/hubs/notifications` и `/hubs/notes`.
- 2026-07-08: VPS `payplanner.service` active/running, enabled; backend/app/frontend revision совпадает с публичной ревизией `ebf01336c6366f5b52e1f442be009806850161f1`; локальный `http://127.0.0.1:5080/api/health` вернул `{"status":"ok"}`; предупреждений и ошибок в журнале сервиса за последние 2 часа нет; диск `/` занят на 20%.
- 2026-07-08: `npm run test:unit -- src\components\Cases\CasesRegistry.test.tsx src\components\Cases\CaseDetailPage.test.tsx src\components\Tasks\TaskJournal.test.tsx src\components\Reports\Reports.test.ts` — 190 тестов пройдено.
- 2026-07-08: `npx vitest run src\components\Calendar\Calendar.integration.test.tsx src\components\Money\MoneyOperations.integration.test.tsx --no-file-parallelism` — 62 теста пройдено.
- 2026-07-08: `dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --no-restore --filter "FullyQualifiedName~CasesV2ControllerTests|FullyQualifiedName~TasksControllerTests|FullyQualifiedName~ProceduralDeadlineCalculatorTests|FullyQualifiedName~GoogleCalendarSyncServiceTests|FullyQualifiedName~YandexCalendarSyncServiceTests|FullyQualifiedName~SudrfCourtSyncServiceTests|FullyQualifiedName~InvoicesControllerCrudTests|FullyQualifiedName~ActsControllerTests|FullyQualifiedName~ContractsControllerTests|FullyQualifiedName~MoneyOperationsControllerTests|FullyQualifiedName~CashOrdersControllerTests|FullyQualifiedName~ReportsControllerTests"` — 708 тестов пройдено.

## Осталось для ручной приемки

- Проверить production-сценарии на реальных пользователях и ролях заказчика: администратор, руководитель, юрист, бухгалтер, секретарь или фактические роли проекта.
- Подтвердить, что ограничения видимости дел, финансов, задач, судебных событий и документов совпадают с ожиданиями заказчика на реальных данных.
