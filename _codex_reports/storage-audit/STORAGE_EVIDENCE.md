# STORAGE EVIDENCE — PayPlanner

Версия: 1.0, 2026-09-22. Baseline commit: `09ae8b58e74787a20d7f7d2361331b83731ab8b4`, branch `main`.

Этот индекс отделяет факты текущего кода от рекомендаций и от исторических runtime-наблюдений. Runtime evidence `EVID-120..126` было собрано read-only в предыдущем аудите 22.09.2026 до начала текущего запроса. В текущем запуске production и реальные storage accounts не опрашивались.

## Репозиторий и wiring

| ID | Claim | Доказательство | Тип / baseline | Confidence и ограничения |
|---|---|---|---|---|
| EVID-001 | Baseline — `main` / `09ae8b58…`; до текущего запуска untracked был только прежний audit report | `git branch --show-current`; `git rev-parse HEAD`; `git status --short` | локальная команда, exit 0 | HIGH |
| EVID-002 | Один backend .NET 9 и React/Vite frontend; отдельного storage worker/service нет | `backend/PayPlanner.Api.csproj`; `package.json`; `backend/Program.cs` | manifest/code | HIGH; generated/vendor исключены |
| EVID-003 | AWSSDK.S3 4.0.23.5 — единственный object-storage SDK | `backend/PayPlanner.Api.csproj:16` | manifest | HIGH |
| EVID-004 | Реальные storage integrations: local FS, S3-compatible API, Yandex Disk REST API | `backend/Program.cs:211-234`; `S3StorageService`; `YandexDiskService`; repository-wide search | code/search | HIGH; внешние managed backup вне репозитория NOT VERIFIED |
| EVID-005 | Google Drive/OneDrive/Dropbox/Azure Blob/WebDAV/SFTP/rclone storage adapters отсутствуют | repository-wide search по SDK/API/endpoint/DI; SFTP найден только как будущая идея в `docs/contacts-directory-implementation-log.md:59,99` | code/search | MEDIUM-HIGH; отсутствие внешнего сервиса не доказано |
| EVID-006 | Background jobs — ASP.NET `BackgroundService`, включаются `BackgroundServices:Enabled`; broker/Hangfire/Quartz отсутствуют | `backend/Program.cs:270-300`; `backend/Services/DatabaseBackupSchedulerService.cs` | code/wiring | HIGH |
| EVID-007 | Публичный health не проверяет DB/storage и всегда возвращает `status=ok` | `backend/Controllers/HealthController.cs:8-15`, `HealthController.Get()` | code | HIGH |
| EVID-008 | CI full gate включает backend tests, lint, encoding, frontend unit/integration и build | `.github/workflows/deploy.yml:64-120`; `package.json:scripts` | CI/config | HIGH |

## Local persistent files и модели

| ID | Claim | Доказательство | Тип | Confidence / ограничения |
|---|---|---|---|---|
| EVID-010 | Общий root задаётся `FileStorage:RootPath`; fallback — `ContentRoot/App_Data` | `backend/Services/Cases/CaseDocumentStorage.cs:15-35`, `ApplicationFileStoragePaths.ResolveRoot()` | code | HIGH |
| EVID-011 | `ICaseDocumentStorage` имеет только local implementation | `backend/Services/Cases/CaseDocumentStorage.cs:5-102` | code/DI | HIGH |
| EVID-012 | Task files: 25 МБ, версии, local write; связанная с делом задача создаёт вторую физическую копию | `backend/Controllers/TasksController.cs:2810-2930`, `UploadFile()` | code | HIGH |
| EVID-013 | Task download — local `PhysicalFile`; delete сначала меняет DB, затем best-effort physical delete | `backend/Controllers/TasksController.cs:2944-3030` | code | HIGH |
| EVID-014 | Case documents: 25 МБ, версии, local storage abstraction | `backend/Controllers/V2/CasesV2Controller.cs:4680-4775`, `UploadDocumentCore()` | code | HIGH |
| EVID-015 | Case delete удаляет DB row до best-effort физического удаления | `backend/Controllers/V2/CasesV2Controller.cs:5032-5078`, `DeleteDocument()` | code | HIGH |
| EVID-016 | `TaskJournalFile` и `ClientCaseDocument` не содержат strong checksum/provider/replica/tombstone | `backend/Models/TaskJournalFile.cs:5-37`; `backend/Models/ClientCaseDocument.cs:5-55` | model | HIGH |
| EVID-017 | Internal chat: до 10 файлов/20 МБ, local storage; executable blacklist; Range download | `backend/Controllers/InternalChatController.cs:19-44,315-389,512-524` | code | HIGH |
| EVID-018 | Удаление chat message soft-delete-ит сообщение, но hard-delete-ит attachments и их metadata | `backend/Controllers/InternalChatController.cs:419-438`, `DeleteMessage()` | code | HIGH |
| EVID-019 | Messenger delivery делает immutable local snapshot с SHA-256 и одноразовым токеном | `backend/Services/MessengerDocumentDeliveryService.cs:185-270,460-525` | code | HIGH |
| EVID-020 | External work-order attachments используют case file и сверяют SHA-256 при выдаче | `backend/Services/ExternalWorkOrderApprovalService.cs:490-589` | code | HIGH |
| EVID-021 | E-signature по умолчанию пишет в `ContentRoot/storage/electronic-signatures`, хранит абсолютный path и SHA-256 | `backend/Services/ElectronicSignatureService.cs:156-244` | code | HIGH; production use отдельно NOT VERIFIED текущим запуском |
| EVID-022 | Profile photo пишет в `backend/wwwroot/user-photos`, не в shared `FileStorage` | `backend/Controllers/ProfileController.cs:162-243` | code | HIGH |
| EVID-023 | Legal entity logo хранится data URL в PostgreSQL | `backend/Models/LegalEntity.cs:159`; `backend/Data/PaymentContext.cs:621` | model | HIGH |
| EVID-024 | Spreadsheet import загружает весь IFormFile в `MemoryStream`/`byte[]` | `backend/Controllers/DatabaseAdminController.cs:452-466`; `backend/Services/SpreadsheetImportService.cs:20` | code | HIGH |
| EVID-025 | PDF/Word/CSV/Excel exports в основном transient и возвращаются из памяти | `backend/Controllers/ActsController.cs:1240-1248`; `ContractsController.cs:396-449`; `CashOrdersController.cs:967-1006` | code samples | MEDIUM-HIGH; полный перечень в Data Matrix |

## S3-compatible integration

| ID | Claim | Доказательство | Тип | Confidence / ограничения |
|---|---|---|---|---|
| EVID-040 | Можно сохранить несколько S3 connections, но каждая операция принимает один connection ID | `backend/Services/S3StorageService.cs:16-30,85-188,328-370` | code | HIGH |
| EVID-041 | Access/Secret Key сохраняются plaintext в JSON `SystemSettings` | `backend/Services/S3StorageService.cs:115-175,566-607` | code | HIGH; API response маскирует preview |
| EVID-042 | Client использует custom endpoint/region, `ForcePathStyle=true`, static basic credentials | `backend/Services/S3StorageService.cs:840-853` | code | HIGH |
| EVID-043 | Upload — одиночный `PutObject`; multipart/resume/checksum/version capture отсутствуют | `backend/Services/S3StorageService.cs:959-975` | code | HIGH |
| EVID-044 | Download целиком буферизуется в `MemoryStream`; presign/Range отсутствуют | `backend/Services/S3StorageService.cs:995-1015` | code | HIGH |
| EVID-045 | UI list запрашивает одну страницу до 1000 объектов; recursive delete list умеет pagination | `backend/Services/S3StorageService.cs:882-936` | code | HIGH |
| EVID-046 | Upload endpoint имеет request limit 50 ГБ и не вводит type allowlist | `backend/Controllers/S3StorageController.cs:136-167` | code | HIGH |
| EVID-047 | S3 controller требует auth/role permissions; audit не включает secrets | `backend/Controllers/S3StorageController.cs:14-54,276-295` | code | HIGH |
| EVID-048 | Endpoint validation проверяет только absolute HTTP/HTTPS; private/link-local blocking нет | `backend/Services/S3StorageService.cs:690-712` | code | HIGH |
| EVID-049 | Frontend сохраняет native S3 file-manager contract: connections/files/logs, CRUD/test/list/upload/download | `src/components/S3Storage/S3Storage.tsx:238-580,976-1255`; `src/services/api.ts` S3 methods | frontend/code | HIGH |
| EVID-050 | Документация заявляет protected keys и multipart, чего код не реализует | `docs/administration-storage-business-rules.md:38-45` vs EVID-041/043 | docs drift | HIGH |

## Yandex Disk integration

| ID | Claim | Доказательство | Тип | Confidence / ограничения |
|---|---|---|---|---|
| EVID-060 | Используется Yandex Disk REST API `cloud-api.yandex.net`, не Yandex Object Storage/S3 | `backend/Services/YandexDiskService.cs:102-125,1464-1505` | code/endpoint | HIGH |
| EVID-061 | Несколько connections; OAuth start/status/callback; root defaults `disk:/PayPlanner` | `backend/Services/YandexDiskService.cs:167-447` | code | HIGH |
| EVID-062 | OAuth token защищён ASP.NET Data Protection и legacy plaintext мигрируется при save | `backend/Services/YandexDiskService.cs:1037-1143,1296-1334` | code | HIGH |
| EVID-063 | Native operations: list/create/copy/move/trash-or-permanent delete/upload/download/text | `backend/Services/YandexDiskService.cs:591-859` | code | HIGH |
| EVID-064 | Upload получает native upload URL, затем PUT; download получает URL, но backend буферизует результат | `backend/Services/YandexDiskService.cs:746-817` | code | HIGH |
| EVID-065 | CRM sync отправляет case/task/contract/invoice/act docs и фиксирует `YandexDiskCrmSyncState` | `backend/Services/YandexDiskService.cs:862-967`; controllers `ActsController`, `ContractsController`, `InvoicesController`, `YandexDiskController` | code | HIGH |
| EVID-066 | Frontend поддерживает multi-connection, OAuth popup/polling, files, move/copy/delete, sync/logs | `src/components/YandexDisk/YandexDisk.tsx:245-604,801-1151` | frontend/code | HIGH |
| EVID-067 | Yandex UI удаляет connection settings, но не remote files | `src/components/YandexDisk/YandexDisk.tsx:505-523` | frontend contract | HIGH |
| EVID-068 | Yandex API capabilities подтверждаются только документацией REST и кодом; account quota/revisions/Range/immutability NOT VERIFIED | `https://yandex.com/dev/disk/rest/`, checked 2026-09-22 | official docs | MEDIUM; real account not queried |

## Database backup, deploy и restore — baseline до реализации

| ID | Claim | Доказательство | Тип | Confidence / ограничения |
|---|---|---|---|---|
| EVID-080 | PostgreSQL backup создаётся `pg_dump`, SQLite — `VACUUM INTO`; compression/format configurable | `backend/Services/DatabaseBackupService.cs:441-646` | code | HIGH |
| EVID-081 | Schedule/retention/S3/Yandex settings и последние результаты хранятся в `SystemSettings` | `backend/Services/DatabaseBackupService.cs:93-105,862-1034` | code | HIGH |
| EVID-082 | DB backup последовательно отправляется во все выбранные S3 connections, failure сохраняется отдельно | `backend/Services/DatabaseBackupService.cs:697-784` | code | HIGH |
| EVID-083 | DB backup отдельно отправляется в Yandex Disk | `backend/Services/DatabaseBackupService.cs:786-860` | code | HIGH |
| EVID-084 | Retry только ручной для failed result; durable general retry queue нет; history ограничена 100 | `backend/Services/DatabaseBackupService.cs:256-298,862-964` | code | HIGH |
| EVID-085 | Retention обходит backup root рекурсивно и не требует verified cloud copies | `backend/Services/DatabaseBackupService.cs:668-694` | code | HIGH |
| EVID-086 | Scheduler in-process, polling; iteration exception приводит к retry через 30 секунд | `backend/Services/DatabaseBackupSchedulerService.cs:21-69` | code | HIGH |
| EVID-087 | Admin API умеет list/settings/create/retry/delete/download; full restore endpoint отсутствует | `backend/Controllers/DatabaseAdminController.cs:67-212` | code | HIGH |
| EVID-088 | SQLite import endpoint — merge/import данных, не disaster restore | `backend/Controllers/DatabaseAdminController.cs:214-251`; `DatabaseImportService.ImportSqliteAsync()` | code | HIGH |
| EVID-089 | Deploy backup копирует release backend/frontend/revision, не shared files/DB | `.github/workflows/deploy.yml:324-340` | CI/deploy | HIGH |
| EVID-090 | Deploy настраивает persistent `/opt/payplanner/shared/files` и переносит legacy App_Data | `.github/workflows/deploy.yml:168-180,246-305` | CI/deploy | HIGH |
| EVID-091 | `rsync --delete` backend делает неперсистентные `wwwroot/user-photos` и default e-signature path уязвимыми | `.github/workflows/deploy.yml:334-340`; EVID-021/022 | code/deploy | HIGH |
| EVID-092 | Runbook требует backup перед deploy, но не содержит проверяемого полного DB+files restore pipeline | `docs/deploy-runbook.md:216-245` | docs | HIGH |

## Tests и официальные capability sources

| ID | Claim | Доказательство | Тип | Confidence / ограничения |
|---|---|---|---|---|
| EVID-100 | 96 targeted S3/Yandex/backup/upload tests passed | `dotnet test ... --filter FullyQualifiedName~S3StorageServiceTests|...`; exit 0; 96/96, 2026-09-22 | local isolated test | HIGH; mocks/in-memory, не real provider |
| EVID-101 | S3 tests покрывают CRUD/path/error mapping, но не multipart/presign/replica/failover | `backend/PayPlanner.Api.Tests/S3StorageServiceTests.cs`; `S3StorageControllerTests.cs` | tests review | HIGH |
| EVID-102 | Backup tests покрывают multiple destination result/failure/manual retry | `backend/PayPlanner.Api.Tests/DatabaseBackupServiceS3Tests.cs` | tests review | HIGH |
| EVID-103 | Yandex tests покрывают OAuth/settings/native file operations/CRM endpoints | `backend/PayPlanner.Api.Tests/YandexDiskServiceTests.cs`; `YandexDiskControllerTests.cs`; frontend tests | tests review | HIGH |
| EVID-104 | AWS S3 API поддерживает Range/versionId/checksum mode; conditional writes требуют explicit headers | AWS `API_GetObject`, `conditional-writes`, checked 2026-09-22 | official docs | HIGH для AWS S3; совместимость HOSTKEY отдельно NOT VERIFIED |
| EVID-105 | AWS multipart имеет отдельный lifecycle и incomplete upload cleanup; текущий adapter его не использует | AWS `mpuoverview`, checked 2026-09-22; EVID-043 | official docs + code | HIGH для AWS; S3-compatible provider may differ |
| EVID-106 | Yandex Disk — отдельный REST API с OAuth и upload URL workflow, не S3 semantics | `https://yandex.com/dev/disk/rest/`, checked 2026-09-22; EVID-060/064 | official docs + code | HIGH |
| EVID-107 | Git secret pattern scan не нашёл production secrets в tracked config; совпадения только в tests | local `rg` secret patterns + tracked `appsettings*.json`, 2026-09-22 | local static scan | MEDIUM; не замена secret scanner/IAM audit |

## Исторические runtime evidence предыдущего аудита

| ID | Claim | Источник/время | Confidence / ограничения |
|---|---|---|---|
| EVID-120 | На VPS revision `09ae8b58…`, service active; PostgreSQL ~306 МБ | read-only SSH/systemctl/SQL, 2026-09-22 02:50 UTC | OBSERVED RUN EVIDENCE; не перепроверялось в текущем запуске |
| EVID-121 | `/shared/files` ~145 МБ: task 43/43, case 38/38, missing/orphan 0 | read-only DB/filesystem reconciliation, 2026-09-22 | OBSERVED RUN EVIDENCE; point-in-time |
| EVID-122 | DB backups ~562 МБ/49 files; последний scheduled dump 00:00:22 UTC; manual pre-deploy backup присутствовал | read-only filesystem, 2026-09-22 | OBSERVED RUN EVIDENCE; restore не доказывает |
| EVID-123 | HOSTKEY S3 API отвечал `UserSuspended`; bucket controls/inventory нельзя было прочитать | read-only S3 API, 2026-09-22 | OBSERVED RUN EVIDENCE; current state now NOT VERIFIED |
| EVID-124 | Backup scheduler повторял `UnauthorizedAccessException` на manual backup directory | read-only `journalctl`, 2026-09-22 | OBSERVED RUN EVIDENCE; current state now NOT VERIFIED |
| EVID-125 | Yandex last-100 upload history была success; remote objects напрямую не проверялись | read-only settings/DB, 2026-09-22 | PARTIAL; object existence/checksum NOT VERIFIED |
| EVID-126 | 1 profile DB URL `/user-photos/...`, но 0 live files; e-sign/chat/messenger/work-order attachment rows = 0 | read-only SQL/filesystem, 2026-09-22 | OBSERVED RUN EVIDENCE; profile link broken at snapshot time |

## Локальная реализация после аудита

| ID | Claim | Доказательство | Тип | Confidence / ограничения |
|---|---|---|---|---|
| EVID-130 | Добавлены каталог объектов/поколений/реплик, durable operations и tombstones для SQLite и PostgreSQL | `backend/Models/StorageCatalogModels.cs`; migrations `20260922052603_AddStorageCatalog` и `20260922052737_AddStorageCatalogPostgres`; snapshots | code + migration tests | HIGH локально; production migrations не применялись |
| EVID-131 | Реализованы policy routing, immutable generations, same-generation fallback, идемпотентная запись, lease/retry/dead-letter и reconciliation/repair | `backend/Services/Storage/StorageObjectService.cs`; `StorageOperationProcessor.cs`; `StorageReconciliationService.cs`; `StoragePolicyService.cs` | code + isolated tests | HIGH локально; routing/worker выключены по умолчанию |
| EVID-132 | Реализованы tombstone, retention/legal hold, circuit breaker и административная наблюдаемость/управление | `backend/Services/Storage/StorageProviderHealthTracker.cs`; `backend/Controllers/StorageAdminController.cs`; storage tests | code + isolated tests | HIGH локально; real provider failure injection не выполнялся |
| EVID-133 | Исторические task/case/chat/messenger/e-sign/profile файлы можно регистрировать dry-run/apply порциями с resume и идемпотентностью без удаления источников | `backend/Services/Storage/LegacyStorageCatalogBackfillService.cs`; `backend/PayPlanner.Api.Tests/LegacyStorageBackfillTests.cs` | code + isolated tests | HIGH локально; production backfill не запускался |
| EVID-134 | Backup включает manifest/checksum и persistent file archive; SQLite restore drill реально восстанавливает одноразовую копию и проверяет integrity/FK/tables | `backend/Services/DatabaseBackupService.cs`; `backend/Services/DatabaseRestoreDrillService.cs`; backup tests | code + isolated tests | HIGH для SQLite; production PostgreSQL restore не выполнялся |
| EVID-135 | PostgreSQL restore защищён opt-in, allowlist/prefix, несовпадением с active DB, confirmation token и timeout | `backend/Services/DatabaseRestoreDrillService.cs`; `backend/appsettings.json` | code review + tests | HIGH для guardrails; реальный PostgreSQL restore NOT VERIFIED |
| EVID-136 | Profile/e-sign/Data Protection пути сделаны persistent, S3 secrets защищены, endpoint проверяется на TLS/credentials/private network/DNS rebinding | deploy workflow; `UserPhotoStoragePaths.cs`; `ElectronicSignatureService.cs`; `S3EndpointSecurityPolicy.cs`; security/path tests | code + isolated tests | HIGH локально; deploy не выполнялся |
| EVID-137 | Пользовательский экран обслуживания БД получил проверку резервной копии и restore drill; добавлена объединённая запись «Что нового» | `src/components/Settings/DatabaseMaintenanceSettings.tsx`; `backend/AppReleases/releases.json` | frontend tests + static checks | HIGH локально; production UI не публиковался |
| EVID-138 | Финальный полный backend gate после последней оптимизации прошёл без ошибок | `dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore`; 2827/2827, 2026-09-22 | local full test suite | HIGH; disposable/local dependencies, не production providers |
| EVID-139 | Полный frontend gate прошёл: 189 test files/1450 tests, lint, TypeScript, encoding audit и production build | `npm run test:unit`; `npm run lint`; `npx tsc --noEmit`; `npm run audit:encoding`; `npm run build`, 2026-09-22 | local full frontend/static/build gates | HIGH; browser production smoke не выполнялся |

## Доказательства комплектности текущего отчёта

| ID | Claim | Доказательство | Тип / baseline | Confidence и ограничения |
|---|---|---|---|---|
| EVID-127 | Созданы ровно шесть обязательных непустых отчётов; Roadmap содержит 26 task cards, ТЗ — 20 REQ/12 DEC/17 TEST/15 AC, risk register — 27 risks | локальная проверка имён/размеров и regex-count, 2026-09-22 | local report validation, exit 0 | HIGH; это проверка плана, не реализации |
| EVID-128 | Все явно указанные `REQ/DEC/TEST/AC/RISK/EVID/TASK` ID имеют определение; dependency review не выявил цикла | локальная ID cross-reference проверка + ручная сверка DAG, 2026-09-22 | local static validation | HIGH для явных ID; ranges проверены отдельно |
| EVID-129 | Финальный Git status содержит прежний untracked audit и только шесть новых audit reports; tracked diff пуст | `git status --short --untracked-files=all`; `git diff --name-only`; baseline EVID-001 | local Git comparison | HIGH; untracked прежний файл не имеет Git content baseline, но в текущем запуске не изменялся |

## Выполненные команды текущего запуска

| Команда | Среда | Результат | Side effects |
|---|---|---|---|
| `git branch --show-current; git rev-parse HEAD; git status --short` | local repo | exit 0; baseline captured | none |
| repository-wide `rg`/`rg --files` storage discovery | local repo | exit 0 | none |
| чтение manifests/code/docs/tests | local repo | exit 0 | none |
| official documentation open/search | public web | AWS/Yandex sources read | none in project/provider accounts |
| Targeted test run из EVID-100 | local repo, earlier same working tree | exit 0, 96/96 | ignored `bin/obj` only |
| `dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --no-restore` | local repo, final implementation tree | exit 0; 2827/2827, 0 failed/skipped | disposable test data and ignored `bin/obj` only |
| `npm run test:unit` | local repo, final frontend tree | exit 0; 189 files, 1450 tests | ignored test/build artifacts only |
| `npm run lint`; `npx tsc --noEmit`; `npm run audit:encoding`; `npm run build` | local repo, final frontend tree | all exit 0; encoding audit passed 2104 tracked text files; production build succeeded | ignored `dist`/cache only |
| report presence/count/ID cross-reference | six report files | exit 0; EVID-127/128 | none |
| `npm run audit:encoding` + strict UTF-8/mojibake scan reports | local repo/reports | exit 0; tracked audit passed 2104 files; six reports UTF-8 valid, no marker found | read-only |
| `git diff --check`; final `git status --short --untracked-files=all` | local repo | exit 0; no tracked diff; EVID-129 | none |

## Границы доказательств

- Production и реальные storage accounts в текущем запросе не опрашивались.
- EVID-120..126 — исторический point-in-time evidence, а не заявление о текущем здоровье.
- Real provider IAM, billing, quota, lifecycle, versioning, encryption, Object Lock и data residency — `NOT VERIFIED`.
- Disposable SQLite restore drill выполнен на синтетической локальной копии; production PostgreSQL restore и реальные provider accounts по-прежнему `NOT VERIFIED`.
- Vendor/generated folders (`node_modules`, `bin`, `obj`, `dist`, migration designer repetition) не читались бессистемно; manifests и runtime integration points проверены.
