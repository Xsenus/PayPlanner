# Roadmap 6. Приемка, стабильность и выпуск

Дата создания: 8 июля 2026.
Статус: активный roadmap.

## Сводка прогресса

Прогресс: 8 из 9 рабочих пунктов закрыто, 89%.

- Всего рабочих пунктов: 9.
- Закрыто и проверено: 8.
- В работе: 0.
- Заблокировано или отмечено как риск: 0.
- Требует решения заказчика: 0.
- Требует ручной приемки: 1.

## Цель

Не просто реализовать пробелы ТЗ, а довести их до проверяемого production-состояния: тесты, локальная БД, "Что нового", GitHub Actions, deploy и проверка VPS.

## Рабочие пункты

- `[x]` Для каждого завершенного функционального этапа добавить записи в "Что нового" человеческим языком. Проверены записи `1.13.589`-`1.13.609`: судебная синхронизация, SMS/MAX, переписка дела, финансовые сценарии, этапы дел и внесудебный режим описаны в `backend/AppReleases/releases.json`.
- `[x]` Проверить кодировку измененных файлов и русские тексты в UI/API. Проверено 62 текстовых файла из серии roadmap-коммитов после `ebf01336`: UTF-8 валиден, BOM и символы замены не найдены, типичные mojibake-маркеры в русских UI/API-текстах отсутствуют.
- `[x]` Прогнать релевантные frontend unit/integration тесты по измененным экранам. Пройдены точечные Vitest-наборы для карточки дела, мастера счета, формирования задач по делу и предпочтений уведомлений: 114 тестов в 4 файлах; `npm run build` завершился успешно.
- `[x]` Прогнать релевантные backend-тесты по измененным контроллерам, сервисам и моделям. Пройден фильтр по 11 измененным тестовым классам контроллеров, сервисов, уведомлений, судебной синхронизации, финансовых цепочек, ролей и задач: 609 тестов; сборка `PayPlanner.Api` завершилась без предупреждений и ошибок.
- `[x]` Проверить сценарии на локальной БД, включая миграции и существующие данные. Миграции применены к временной копии существующей `payplanner.db`, pending-миграций не осталось; дополнительно пройдены SQLite smoke-тест миграций на новой базе и файловый сценарий создания акта.
- `[x]` Проверить права доступа: пользователь без прав не видит и не вызывает новые действия прямым API. Пройдены прямые API-тесты прав по ролям, актам, судебной синхронизации, переписке, документам дела, заказ-нарядам, финансам дела и задачам.
- `[x]` После push дождаться GitHub Actions, исправить ошибки при необходимости и проверить деплой. Первый прогон `28935342216` упал на frontend lint; ошибка исправлена отдельным коммитом `c333ecb3`. Повторный прогон `28936107151` завершился успешно: backend checks, frontend lint/typecheck/tests/build, загрузка пакета на VPS, deploy и production readiness прошли.
- `[x]` Проверить production health, frontend entry point, service worker при изменении уведомлений и статус VPS-сервиса. Проверены `https://api.elitpravo.com/api/health`, `https://crm.elitpravo.com/`, `https://crm.elitpravo.com/payplanner-push-worker.js`, `https://crm.elitpravo.com/deploy-revision.txt`; revision совпал с `c333ecb32f6c65905fe35ea17a8bfe1c72810089`. В логах deploy на VPS `payplanner.service` перезапущен, внутренний health `http://127.0.0.1:5080/api/health` стал `ok` на 5-й попытке.
- `[acceptance]` Получить ручное подтверждение заказчика по SMS/MAX, судебной синхронизации и клиентскому согласованию.

## Definition of Done

- Все новые сценарии покрыты тестами и проверены на локальной БД.
- GitHub Actions и deploy завершились успешно.
- В "Что нового" описаны видимые изменения.
- Roadmap обновлен и перенесен в архив после финальной production-проверки.

## Проверки

- 2026-07-08: сверены активные roadmap 1-5 и история индекса с `backend/AppReleases/releases.json`; для закрытых пользовательских изменений найдены release notes от `1.13.589` до `1.13.609`.
- 2026-07-08: новая запись в "Что нового" не добавлялась, потому что текущая задача фиксирует аудит документации выпуска и не меняет пользовательский функционал.
- 2026-07-08: Node-проверкой строго декодированы измененные текстовые файлы; `git diff --check` и JSON-парсинг `backend/AppReleases/releases.json` прошли без ошибок.
- 2026-07-08: `npx vitest run src/components/Cases/CaseDetailPage.test.tsx src/utils/caseTaskPayload.test.ts src/utils/userNotificationPreferences.test.ts --no-file-parallelism` прошел: 60 тестов.
- 2026-07-08: `npx vitest run src/components/Accounts/InvoiceModal.integration.test.tsx --no-file-parallelism` прошел: 54 теста.
- 2026-07-08: `npm run build` прошел; остались только известные предупреждения сборщика по `@microsoft/signalr` pure-аннотациям и размеру чанков.
- 2026-07-08: `dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter ... --no-restore` по измененным backend-тестовым классам прошел: 609 тестов.
- 2026-07-08: `dotnet build backend\PayPlanner.Api.csproj --no-restore` прошел без предупреждений и ошибок.
- 2026-07-08: `dotnet ef database update` на копии `payplanner.db` применил недостающие SQLite-миграции до `20260617145243_AddStickyNoteAssignments`; `dotnet ef migrations list` не показал pending-миграций.
- 2026-07-08: `dotnet ef dbcontext info` подтвердил SQLite `PaymentContext` на временной копии локальной базы; временная база удалена после проверки.
- 2026-07-08: `dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter "FullyQualifiedName~SqliteMigrationSmokeTests|FullyQualifiedName~Create_FullAct_WithLongComment_PersistsCommentAndInvoiceLineItems_InLocalSqliteFile" --no-restore` прошел: 2 теста.
- 2026-07-08: `dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter ... --no-restore` по прямым API-проверкам прав прошел: 28 тестов.
- 2026-07-08: GitHub Actions и deploy не запускались, потому что пользователь явно запретил push до отдельного разрешения.
- 2026-07-08: пользователь разрешил push; deploy-пункт переведен в работу перед отправкой изменений в `origin/main`.
- 2026-07-08: `git push origin main` отправил roadmap-статус в `683dddd4`; GitHub Actions `28935342216` упал на frontend lint из-за неиспользуемого alias в `src/utils/caseTaskPayload.ts`.
- 2026-07-08: исправлен frontend lint, локально пройдены `npm run lint`, `npx vitest run src/utils/caseTaskPayload.test.ts --no-file-parallelism`, `git diff --check` и `npm run build`.
- 2026-07-08: `git push origin main` отправил `c333ecb3`; GitHub Actions `28936107151` завершился успешно, deploy на VPS выполнен.
- 2026-07-08: production-проверка подтвердила API health `{"status":"ok"}`, frontend 200, service worker 200, deploy revision `c333ecb32f6c65905fe35ea17a8bfe1c72810089`; readiness также подтвердил websocket auth challenge для `/hubs/notifications` и `/hubs/notes`.

## История

- 2026-07-08: закрыта проверка полноты "Что нового" по уже завершенным функциональным этапам.
- 2026-07-08: закрыта проверка кодировки измененных файлов и русских текстов в UI/API.
- 2026-07-08: закрыта проверка релевантных frontend unit/integration тестов и production-сборки.
- 2026-07-08: закрыта проверка релевантных backend-тестов и сборки API.
- 2026-07-08: закрыта проверка локальной SQLite-БД, миграций и сценариев на локальных данных.
- 2026-07-08: закрыта проверка прав доступа прямыми API-вызовами.
- 2026-07-08: пункт GitHub Actions/deploy отмечен заблокированным до разрешения на push.
- 2026-07-08: пункт GitHub Actions/deploy переведен в работу после разрешения на push.
- 2026-07-08: пункт GitHub Actions/deploy закрыт после успешного прогона `28936107151` и production-проверки ревизии `c333ecb32f6c65905fe35ea17a8bfe1c72810089`.
- 2026-07-08: закрыта техническая проверка production health, frontend entry point, service worker и статуса VPS-деплоя; ручная приемка заказчика осталась открытой.
