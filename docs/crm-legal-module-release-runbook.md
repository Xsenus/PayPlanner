# Runbook запуска и отката юридического модуля CRM

Дата актуализации: 20.05.2026

Документ нужен для безопасной выкладки блока "Дела + Задачи + Календарь" на боевую среду.

## 1. Перед выкладкой

### Проверка кода

Обязательные команды:

```powershell
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj /p:UseSharedCompilation=false
npm test -- --run
npm run build
git diff --check
```

Дополнительно перед релизом:

```powershell
npm run test:integration
```

Автоматизированный e2e-smoke frontend/API:

```powershell
$env:PAYPLANNER_E2E_FRONTEND_URL = "http://localhost:5173"
$env:PAYPLANNER_E2E_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_E2E_TOKEN = "<jwt администратора или тестового пользователя>"
npm run test:legal-e2e
```

Сводный readiness-отчет для dry-run:

```powershell
$env:PAYPLANNER_READINESS_FRONTEND_URL = "http://localhost:5173"
$env:PAYPLANNER_READINESS_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_READINESS_TOKEN = "<jwt администратора или тестового пользователя>"
npm run test:legal-readiness
```

Отчет сохраняется в `.codex-build/legal-readiness/<timestamp>/readiness-report.md` и включает e2e, нагрузочный smoke, feature flags и аудит готовности ролей. В JSON есть `result.status`, `blockingReasons`, `warnings`, `failedSummaries` и `artifacts` для CI/ручной приемки; в консоли печатается короткая строка `Result: passed/failed`. Если summary-блоки readiness не смогли прочитать backend-диагностику, отчет считается упавшим. В markdown-отчете есть таблица ролей с критичными замечаниями, а CSV-аудит ролей сохраняется рядом с отчетом как артефакт для сверки с заказчиком. Если backend или токен еще не готовы, можно проверить только frontend-shell:

JWT и пароль не сохраняются в `frontendUrl`, `apiUrl`, `checks[].command`, `checks[].stdout`, `checks[].stderr` и текстовых stdout/stderr-артефактах: в отчете они заменяются на `<redacted>`.

Если в dry-run используются дополнительные временные секреты, их можно добавить в редактирование артефактов:

```powershell
$env:PAYPLANNER_READINESS_REDACT_VALUES = "<temporary-api-key>,<internal-secret>"
$env:PAYPLANNER_READINESS_REDACT_VALUES_FILE = ".secure/readiness-redactions.txt"
```

Файл поддерживает одну строку на значение или значения через запятую. Сами значения в JSON не сохраняются, только количество правил и путь к файлу.

```powershell
npm run test:legal-readiness:frontend
```

Финальный release gate перед выкладкой:

```powershell
$env:PAYPLANNER_READINESS_FRONTEND_URL = "http://localhost:5173"
$env:PAYPLANNER_READINESS_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_READINESS_TOKEN = "<jwt администратора>"
$env:PAYPLANNER_READINESS_EXPECTED_ROLES_FILE = "docs/customer-expected-roles.txt"
$env:PAYPLANNER_READINESS_ALLOWED_BROAD_SCOPE_ROLES = "admin"
npm run test:legal-release-gate
```

Команда запускает полный readiness с e2e, load-check, проверкой свежего backup, strict-проверкой внешних feature flags, широких ролей и назначений ожидаемых ролей. Для запуска обязательно нужен список ожидаемых ролей через `PAYPLANNER_READINESS_EXPECTED_ROLES` или `PAYPLANNER_READINESS_EXPECTED_ROLES_FILE`.

По текущему согласованию заказчика широкие права роли `admin` являются ожидаемым поведением. Strict gate передает это как явное исключение через `--allowed-broad-scope-roles=admin` или `PAYPLANNER_READINESS_ALLOWED_BROAD_SCOPE_ROLES=admin`: broad scopes у `admin` остаются в readiness-отчете предупреждением, а broad scopes у любых других ролей блокируют `test:legal-release-gate` и `test:legal-roles:strict`.

Strict-проверки нельзя запускать с отключенными summary-блоками: проверка feature flags требует API summary, проверки ролей требуют role audit, а проверка backup требует database summary. Если такой блок отключен, readiness завершится fail-fast до сетевых запросов.

Для блока "Database And Backups" нужен админский JWT, потому что используются `GET /api/database/status` и `GET /api/database/backups`. Если проверяется не администратор, а рабочая роль юриста/секретаря, database summary можно отключить:

```powershell
$env:PAYPLANNER_READINESS_SKIP_DATABASE_SUMMARY = "true"
npm run test:legal-readiness
```

Для финального dry-run перед выкладкой проверьте, что backup существует и он свежий:

```powershell
$env:PAYPLANNER_READINESS_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_READINESS_TOKEN = "<jwt администратора>"
npm run test:legal-readiness:backup
```

Команда читает `GET /api/database/backups`, требует хотя бы один backup и валит readiness, если последний backup старше 24 часов. Порог можно изменить через `PAYPLANNER_READINESS_MAX_BACKUP_AGE_HOURS` или `--max-backup-age-hours`.

Для быстрой сверки только ролевой матрицы и CSV-артефакта:

```powershell
$env:PAYPLANNER_READINESS_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_READINESS_TOKEN = "<jwt администратора или роли с доступом к ролям>"
npm run test:legal-roles
```

Для сверки фактических ролей с согласованной матрицей заказчика:

```powershell
$env:PAYPLANNER_READINESS_EXPECTED_ROLES = "Администратор,Руководитель,Юрист,Секретарь"
npm run test:legal-roles
```

Если список длинный, его можно хранить в файле: одна роль на строку или через запятую.

```powershell
$env:PAYPLANNER_READINESS_EXPECTED_ROLES_FILE = "docs/customer-expected-roles.txt"
npm run test:legal-roles
```

Если хотя бы одной ожидаемой роли нет в аудите ролей, readiness завершится с `Result: failed` и добавит отсутствующие роли в `result.missingExpectedRoles`.

Если нужно проверить не только наличие ролей, но и назначение пользователей на эти роли:

```powershell
$env:PAYPLANNER_READINESS_EXPECTED_ROLES = "Администратор,Руководитель,Юрист,Секретарь"
npm run test:legal-roles:assigned
```

В этом режиме роли без назначенных пользователей попадут в `result.unassignedExpectedRoles` и заблокируют readiness. Команда требует `PAYPLANNER_READINESS_EXPECTED_ROLES` или `PAYPLANNER_READINESS_EXPECTED_ROLES_FILE`, чтобы strict-проверка не проходила пустым списком.

Для ознакомительного аудита, который сохраняет отчет и CSV, но не валит запуск из-за критичных замечаний ролей:

```powershell
node scripts/run-legal-readiness-report.mjs --skip-e2e --skip-load --skip-database-summary --no-fail-on-role-issues
```

Для финального согласования ролей можно включить strict-режим: он считает роли с широкой видимостью данных блокирующим замечанием. Роль `admin` разрешена как согласованное исключение, остальные роли с broad scopes должны быть исправлены или отдельно согласованы до включения юридических feature flags.

```powershell
npm run test:legal-roles:strict
```

Для отдельной strict-проверки внешних feature flags:

```powershell
npm run test:legal-flags:strict
```

Эта команда читает публичный `GET /api/features` и валит readiness, если внешний юридический флаг уже включен без явного согласования релиза.

Для быстрой проверки старых данных после миграций:

```powershell
$env:PAYPLANNER_E2E_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_E2E_TOKEN = "<jwt тестового пользователя>"
npm run test:legal-legacy-data
```

Проверка открывает старые реестры клиентов, счетов, платежей и задач; если в реестре есть записи, дополнительно запрашивает первую карточку.

Если backend или токен еще не готовы, можно проверить только сборку frontend-shell:

```powershell
npm run test:legal-e2e:frontend
```

Нагрузочный smoke-check юридического контура на запущенном API:

```powershell
$env:PAYPLANNER_LOAD_API_URL = "http://localhost:5080/api"
$env:PAYPLANNER_LOAD_TOKEN = "<jwt администратора или тестового пользователя>"
npm run test:legal-load
```

Для dry-run на копии боевой базы рекомендуется увеличить число прогонов:

```powershell
$env:PAYPLANNER_LOAD_ITERATIONS = "30"
$env:PAYPLANNER_LOAD_CONCURRENCY = "4"
$env:PAYPLANNER_LOAD_P95_THRESHOLD_MS = "3000"
npm run test:legal-load
```

Если полный load-check на копии базы слишком долгий, можно ограничить сценарии readiness-отчета:

```powershell
$env:PAYPLANNER_READINESS_LOAD_SCENARIOS = "features,cases-registry,legacy-clients,legacy-invoices,legacy-payments,legacy-tasks"
$env:PAYPLANNER_READINESS_LOAD_ITERATIONS = "30"
$env:PAYPLANNER_READINESS_LOAD_CONCURRENCY = "4"
$env:PAYPLANNER_READINESS_LOAD_P95_THRESHOLD_MS = "3000"
npm run test:legal-readiness
```

Общий таймаут readiness применяется к API summary, e2e и вложенному load-check:

```powershell
$env:PAYPLANNER_READINESS_TIMEOUT_MS = "30000"
npm run test:legal-readiness
```

Проверка покрывает `GET /api/features`, `GET /api/role-permissions/audit`, `GET /api/role-permissions/audit.csv`, реестр дел v2, старые реестры клиентов/счетов/платежей/задач, фильтр неактивных дел, отчеты `legal-work`/`case-activity` и кандидатов автоконтроля дел.

### Проверка миграций

Проверить отдельно:

- SQLite на временной базе;
- PostgreSQL на временной базе или копии схемы;
- отсутствие ручных правок production-базы без миграции.

### Проверка данных

На копии базы проверить:

- пользователи имеют отделы;
- роли имеют корректные разделы и правила видимости;
- клиенты, физические лица, юридические лица и дела имеют ответственных;
- финансовые документы имеют ответственных там, где это требуется;
- старые задачи не теряют статусы;
- старые счета, акты, договоры и платежи не дублируются после сохранения.

## 2. Dry-run на копии боевой базы

Порядок:

1. Создать актуальный backup production-базы.
2. Развернуть копию в отдельной тестовой среде.
3. Применить все миграции.
4. Запустить backend и frontend на тестовом домене или локально.
5. Проверить вход под администратором, руководителем, юристом и секретарем.
6. Пройти smoke-тест из пользовательской инструкции.
7. Прогнать `npm run test:legal-readiness` с токеном роли, которая будет работать с юридическим контуром.
8. Проверить `GET /api/role-permissions/audit`, выгрузку `GET /api/role-permissions/audit.csv` и панель "Аудит готовности ролей" в разделе ролей; критичные замечания закрыть до включения флагов.
9. При необходимости прогнать отдельно `npm run test:legal-e2e` и `npm run test:legal-load`.
10. Проверить логи API и браузерную консоль.
11. Проверить, что CORS и авторизация работают на обоих доменах CRM/API.

## 3. Выкладка

Рекомендуемый порядок:

1. Сделать backup базы и файлов.
2. Остановить фоновые процессы, если они запускаются отдельно.
3. Обновить backend.
4. Применить миграции.
5. Запустить backend.
6. Обновить frontend.
7. Очистить кэш CDN/nginx, если используется.
8. Запустить фоновые процессы.
9. Проверить вход и основные разделы.

## 4. Feature flags

Внешние интеграции юридического контура должны включаться только после отдельной проверки на копии боевой базы.

Backend-флаги находятся в `FeatureFlags:Legal`:

- `ExternalCourtSync` - внешняя синхронизация с судебными источниками;
- `ExternalCalendarSync` - внешняя синхронизация с Google/Яндекс календарями;
- `CourtEvents` - отдельный слой судебных событий;
- `DocumentTemplateLibrary` - расширенная библиотека шаблонов документов.

По умолчанию все эти флаги выключены. Перед включением нужно пройти dry-run, проверить роли, журналы и сценарий отката. Текущее состояние флагов можно проверить через `GET /api/features`.

Для CI или ручной приемки перед включением используйте `npm run test:legal-flags:strict`; в обычном readiness-отчете включенные юридические флаги отображаются как предупреждение, а в strict-режиме становятся blocking reason.

## 5. Мониторинг после запуска

В первые часы проверить:

- ошибки API 4xx/5xx;
- CORS-ошибки в браузере;
- ошибки авторизации;
- работу выбора клиентов и юридических лиц;
- работу сохранения задач и согласований;
- создание уведомлений;
- диагностику автоконтроля дел;
- отсутствие дублей платежей и счетов при сохранении старых документов.

## 6. Откат

Откат нужен, если после выкладки есть критичные ошибки входа, потери данных, массовое нарушение прав доступа или невозможность работать с финансовыми документами.

Порядок отката:

1. Остановить backend и фоновые процессы.
2. Вернуть предыдущую версию backend.
3. Вернуть предыдущую версию frontend.
4. Если миграции изменили структуру данных необратимо, восстановить базу из backup.
5. Если миграции обратимы и backup не требуется, выполнить согласованный down-script.
6. Запустить backend.
7. Запустить frontend.
8. Проверить вход, клиентов, счета, платежи, задачи и уведомления.
9. Зафиксировать причину отката в журнале релиза.

## 7. Что нельзя делать при откате

- Нельзя вручную удалять таблицы или колонки на production без backup.
- Нельзя смешивать новую frontend-сборку со старым API, если были изменены DTO.
- Нельзя оставлять включенными фоновые процессы, пока база находится в промежуточном состоянии.
- Нельзя чистить пользовательские роли и настройки без отдельного согласования.

## 8. Критерии успешного запуска

- Пользователи входят без ошибок.
- Роли ограничивают видимость клиентов, дел, задач и финансовых документов.
- Задачи создаются, редактируются, согласуются и завершаются.
- Календарь и нагрузка открываются.
- Уведомления создаются и открывают связанные объекты.
- Автоконтроль дел показывает диагностику и не создает дубли.
- Отчеты открываются, ограничиваются правами и позволяют перейти в карточку дела.
- `npm run test:legal-readiness` проходит на тестовом пользователе юридического контура и сохраняет отчет dry-run.
- `npm run test:legal-release-gate` проходит на копии боевой базы с ожидаемыми ролями заказчика.
- `npm run test:legal-e2e` проходит на тестовом пользователе юридического контура.
- `npm run test:legal-load` проходит на копии боевой базы без 4xx/5xx и без превышения p95-порога.
- `git diff --check`, backend-тесты, frontend-тесты и frontend build проходят.
