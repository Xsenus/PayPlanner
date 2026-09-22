# Техническое задание: отказоустойчивое хранение PayPlanner

Версия 1.1, 2026-09-22. Реализация начата после отдельного поручения владельца; фактический статус задач и внешние gates ведутся в `STORAGE_IMPLEMENTATION_ROADMAP.md`.

## 1. Scope и требования

| ID | Обязательное требование | Проверяемый результат |
|---|---|---|
| REQ-001 | Сохранить все существующие local, S3 и Yandex Disk контракты | Старые API/settings/IDs/OAuth/native operations/UI работают при выключенной новой маршрутизации |
| REQ-002 | Разделить providers, destinations, pools, policies и tiers | Runtime, DB backup, file backup и archive не получают права/маршруты друг друга автоматически |
| REQ-003 | Ввести logical object + immutable generation + per-replica state | Для каждого файла известны committed generation и фактические пригодные copies |
| REQ-004 | Автоматический write failover до записи | При недоступном preferred writer выбирается только разрешённый writer; ручная смена ENV не нужна |
| REQ-005 | Автоматический version-aware read failover | Читается подтверждённая текущая generation; stale/missing/corrupt replica не выдаётся |
| REQ-006 | Автоматический backup destination fallback | Годный backup доставляется в доступные разрешённые destinations независимо от отказа одного |
| REQ-007 | Durable replication/retry/reconciliation/repair | Replica debt переживает restart, повторы идемпотентны, repair не распространяет порчу |
| REQ-008 | Полный backup невосстанавливаемых файлов | DB, file bytes, metadata, manifests и key dependencies имеют независимые копии |
| REQ-009 | Доказуемый restore | Изолированный DB+files restore и business smoke проходят регулярно |
| REQ-010 | Безопасная resumable migration | inventory/dry-run/copy/verify/delta/resume/cutover/rollback без удаления source |
| REQ-011 | Убрать plaintext storage secrets | DB/frontend/logs/reports не содержат постоянные credentials |
| REQ-012 | Bounded streaming для больших файлов | Нет whole-file RAM buffering; Range/multipart/resume применяются по capability |
| REQ-013 | Tombstone и управляемое удаление | Late retry не воскрешает deleted/old generation; backup retention независим |
| REQ-014 | Operation-scoped observability | Видны failover, debt/lag, protection, backup age, restore-test age, degraded/all-failed |
| REQ-015 | Backward-compatible rollout | Single-local, single-S3 admin, Yandex-only, mixed и routing-off конфигурации проходят gates |
| REQ-016 | Честная ack/UNKNOWN semantics | required copies не понижаются; unknown write не считается failed/success до resolution |
| REQ-017 | Safe recovery/catch-up/failback | B-only writes сохраняются; бывший primary возвращается только после проверки/cooldown |
| REQ-018 | Внешний backup catalog | Restore source/manifest/locators доступны при потере production DB/VPS |
| REQ-019 | Capability-aware eligibility | Destination выбирается по operation, tenant, rights, quota, tier, generation, capability |
| REQ-020 | Измеримые SLO | PROPOSED: runtime failover ≤15 с до первого byte/ack; mirror lag ≤15 мин; DB RPO ≤1 ч; full RTO ≤4 ч |

## 2. Non-goals и deferred

- Не добавлять Google Drive, Dropbox, OneDrive, WebDAV, SFTP, Kafka, Kubernetes или новый broker: исполняемых интеграций/необходимости нет.
- Не заменять Yandex Disk S3-адаптером и не расширять OAuth scopes ради общей маршрутизации.
- Не объявлять runtime replica backup-ом и не использовать archive как hot read fallback.
- Global multi-region HA приложения/БД — `DEFERRED`: сначала устранить фактические single-VPS/restore риски.
- CDN, client-side encryption и antivirus engine — `DEFERRED`, пока владелец не подтвердит workload/compliance; hooks и policy points предусмотреть.
- Cold/archive tier — `DEFERRED/BLOCKED_EXTERNAL` до выбора provider и подтверждения retrieval/RTO/cost.

## 3. Архитектурные решения

| ID | Решение | Причина / альтернатива / пересмотр |
|---|---|---|
| DEC-001 | Additive storage orchestration поверх существующих сервисов | Минимизирует регрессии; big-bang rewrite отклонён |
| DEC-002 | PostgreSQL tables + существующий `BackgroundService` для outbox/jobs | Достаточно масштабу; новый broker не нужен до измеренного bottleneck |
| DEC-003 | Immutable physical key per generation; DB публикует committed generation | Защищает от stale retries/update/delete races |
| DEC-004 | `PRIMARY_SYNC_MIRRORS_ASYNC` с policy-authorized writer fallback | UI не ждёт все mirrors; `MIRROR_ALL_SYNC` повышает latency и связывает failures |
| DEC-005 | В первой фазе local destination остаётся preferred runtime writer | Сохраняет ключи и рабочую схему; S3 primary возможен после verified backfill |
| DEC-006 | Runtime, DBBackup, FileBackup, Archive — отдельные pools | Не допускает ошибочного runtime write в backup-only account |
| DEC-007 | Yandex Disk сохраняется как native integration/backup destination | Не притворяется S3 replica; copy/move/trash/OAuth/sync сохраняются |
| DEC-008 | Strong SHA-256 — каноническая integrity value; ETag/version ID — provider metadata | ETag не универсален для multipart/providers |
| DEC-009 | BackupSet manifest хранится вне production DB минимум в двух копиях | Restore должен пережить потерю основной DB |
| DEC-010 | All-failed: controlled error; optional bounded `PENDING` staging только для явно разрешённой policy | Очередь без байтов не является сохранением; false 200 запрещён |
| DEC-011 | Автоматический read/write failover, operator-approved global failback | Снижает flapping/риск массового stale cutback; per-object repair автоматический |
| DEC-012 | Proxy-stream default; short-lived presign только для S3 large downloads | Yandex native API не обязан иметь S3 presign semantics |

## 4. Pools, policies и tiers

| Data class | Runtime pool | Write/ack | Read | Protection | Backup/tier |
|---|---|---|---|---|---|
| Task/case/chat/profile | `UserFilesPool` | preferred eligible writer + DB/object/outbox commit; `AVAILABLE_PROTECTION_DEGRADED` до desired mirrors | current generation local/S3 verified replica | required runtime copy=1, desired=2 independent | daily file backup, hot 30d PROPOSED |
| Messenger snapshots | `DeliverySnapshotsPool` | local preferred; async mirror before external send when policy requires | authorized one-time proxy | required=1; desired=2 for active links | retain per audit/legal policy |
| E-signature evidence | `LegalEvidencePool` | requiredCopies=2; no ordinary success with one unless explicit degraded approval | version-locked | checksum+immutable/retention | hot + independent immutable copy |
| S3/Yandex admin files | existing native pool | existing single-target contract by explicit connection | selected connection | no automatic cross-copy by default | optional policy only after owner opt-in |
| DB backup | `DatabaseBackupsPool` | valid local staging + delivery jobs; protected only after policy copies | restore selector only | required independent verified copies=2 PROPOSED | newest hot; older tier only after RTO gate |
| File backup | `FileBackupsPool` | inventory+content+manifest; per-copy state | restore tool only | required independent copies=2 PROPOSED | hot first; warm/cold deferred |
| Generated exports | none/transient | HTTP response | regenerate | no replica | no backup unless saved as document |
| Temp/migration spool | `StagingPool` | encrypted, bounded, TTL, not final success | internal only | no HA claim | cleanup after verified finalize |

## 5. Exact code changes

### 5.1 Новые proposed modules

- `backend/Services/Storage/IStorageProvider.cs` — capability-aware `Put/OpenRead/Head/Delete/Presign`, native locator opaque.
- `backend/Services/Storage/StorageRoutingService.cs` — policy/tenant/operation eligibility and deadlines.
- `backend/Services/Storage/StorageObjectService.cs` — generation, checksum, finalize, ack result.
- `backend/Services/Storage/StorageReplicationWorker.cs` — durable leases/retry/debt.
- `backend/Services/Storage/StorageReconciliationService.cs` — sampled/full scan and repair.
- `backend/Services/Storage/Providers/LocalStorageProvider.cs` — reuse `ApplicationFileStoragePaths`/safe path rules.
- `backend/Services/Storage/Providers/S3ObjectStorageProvider.cs` — reuse client factory, add Head/Range/multipart/presign/conditionals.
- `backend/Services/Storage/Providers/YandexDiskDestinationAdapter.cs` — optional backup/copy role only; wraps `IYandexDiskService`, never removes native controller/service.
- `backend/Services/Storage/BackupCatalogService.cs` и `StorageMigrationService.cs`.
- `backend/Controllers/StorageOperationsController.cs` — status only if `PENDING` upload is enabled.
- `backend/Controllers/StorageAdminController.cs` — pools/policies/health/debt; existing S3/Yandex routes stay stable.

### 5.2 Existing touchpoints

- `backend/Program.cs`: DI/hosted-service registration behind feature flags.
- `TasksController.UploadFile/DownloadFile/DeleteFile` и обе legacy/new task card APIs: route bytes through orchestration, preserve responses/permissions.
- `CasesV2Controller` and `CaseDocumentsController`: same for case documents/portal/external approvals.
- `InternalChatController`: preserve member authorization and native message semantics.
- `MessengerDocumentDeliveryService`, `ElectronicSignatureService`, `ProfileController`: persistent object references/checksums; no release-dir storage.
- `S3StorageService`: preserve admin browser API while extracting reusable adapter; secure credentials and add capabilities.
- `YandexDiskService`, `YandexDiskController`, document controllers, `YandexDiskCrmSyncSchedulerService`: retain native flows/paths/OAuth/sync state.
- `DatabaseBackupService`/scheduler/controller: split creation from delivery; durable BackupSet/Copy states; no recursive failure on unrelated path.
- Frontend `src/services/api.ts`, S3/Yandex settings and task/case cards: maintain old contracts; add status/degraded/retry UX only where required.
- `.github/workflows/deploy.yml`, `docs/deploy-runbook.md`: persistent roots, restore gate, no implicit cleanup.

## 6. DB/schema/backfill

`NEW / PROPOSED`:

```text
StorageDestination(Id, ConnectionType, ConnectionRef, NativeRoot, RolesJson,
  TenantScope, ReadPriority, WritePriority, CapabilitySnapshotJson,
  SecretReference, State, CreatedAt, UpdatedAt)
StoragePolicy(Id, DataClass, RuntimePool, BackupPool, RequiredCopies,
  DesiredCopies, AckMode, DegradedMode, RetentionJson, Revision, Enabled)
StorageObject(Id, LogicalKey, DataClass, OwnerType, OwnerId, CommittedGeneration,
  SizeBytes, ContentType, Sha256, State, PolicyId, PolicyRevision,
  CreatedAt, DeletedAt, RowVersion)
StorageObjectReplica(Id, ObjectId, DestinationId, Generation, NativeLocator,
  NativeVersionId, ETag, SizeBytes, Sha256, State, LastVerifiedAt,
  LastErrorCategory, RetryCount, NextAttemptAt, RowVersion)
StorageOperation(Id, OperationId, ObjectId, Generation, DestinationId,
  Kind, State, IdempotencyKey, LeaseOwner, LeaseUntil, AttemptCount,
  NextAttemptAt, LastError, CreatedAt, UpdatedAt)
BackupSet(Id, BackupId, RecoveryPointAt, SourceType, Format, ManifestSha256,
  State, ProtectionState, RestoreTestState, CreatedAt)
BackupCopy(Id, BackupSetId, DestinationId, NativeLocator, Tier, SizeBytes,
  Sha256, State, VerifiedAt, LastError, Retry fields)
```

Constraints/indexes: unique logical key+generation; unique replica per object/destination/generation; unique operation idempotency; indexes on pending due jobs, object owner, replica state, backup recovery point. Secret values запрещены в этих таблицах.

Совместимость:

1. Добавить nullable `StorageObjectId` в `TaskJournalFile`, `ClientCaseDocument`, `InternalChatAttachment`, messenger/e-sign/profile metadata.
2. Legacy `StoragePath`, `StoredFileName`, Yandex remote path/connection ID остаются источником чтения при feature flag off.
3. Backfill регистрирует существующий path как logical key без перемещения и вычисляет checksum throttled batches.
4. Not-null/legacy cleanup — только после verified coverage, observation и отдельного irreversible approval.

## 7. Configuration / ENV

Предлагаемые keys, defaults безопасно выключены:

```text
StorageRouting__Enabled=false
StorageRouting__WriteFailoverEnabled=false
StorageRouting__ReadFailoverEnabled=false
StorageRouting__ReplicationEnabled=false
StorageRouting__MaxConcurrentTransfers=2
StorageRouting__AttemptTimeoutSeconds=15       # PROPOSED, measure
StorageRouting__OperationDeadlineSeconds=30    # PROPOSED, measure
StorageRouting__FailbackCooldownMinutes=30     # PROPOSED
StorageStaging__RootPath=/opt/payplanner/shared/storage-staging
StorageStaging__MaxBytes=...                    # owner/capacity gate
StorageStaging__TtlHours=...
ElectronicSignature__StorageRoot=/opt/payplanner/shared/files/electronic-signatures
StorageSecrets__S3_<id>=...                     # secret reference/value only in protected env/store
```

Existing `FileStorage__RootPath`, S3/Yandex setting IDs and API DTOs remain supported. Credentials migration uses replace-only UI: preview visible, stored secret never returned.

## 8. Workers и state transitions

- `StorageOperation`: `Pending -> Leased -> InProgress -> VerificationPending -> Succeeded`; retryable `Failed -> Pending`; terminal `Conflict/Cancelled/DeadLetter`.
- Replica: `Pending -> Uploading -> Available`; or `Missing/Corrupted/Failed`; delete via `Deleting -> Deleted`.
- Backup: `Created -> ContentVerified -> DeliveryPending -> ProtectionDegraded/Protected`; restore state independent `NotTested/Testing/Passed/Failed`.
- Lease/fencing uses DB row version and expiration; a stale worker cannot publish newer generation.
- Retry budget is shared between router/SDK/worker; exponential backoff+jitter, `Retry-After`, per-destination breaker.
- Recovery pauses on suspected mass corruption/compromise and requires operator approval.

## 9. API, CLI и frontend contracts

### Upload result

```json
{
  "objectId": "...",
  "generation": 1,
  "availability": "available|pending|failed",
  "protection": "protected|degraded|pending",
  "operationId": "...",
  "actualDestinationId": "..."
}
```

Existing endpoints may keep current body/HTTP status while feature off. When strict `requiredCopies` is unmet: explicit `202 pending` only if durable bytes+status endpoint exist; otherwise 503/409, never ordinary success. `UNKNOWN` after lost response is resolved by `operationId`+Head/checksum before another destination attempt.

### Download

Stable logical endpoint authorizes first, selects committed verified replica, then streams or returns short-lived S3 presign. A failed direct URL requires client retry to logical endpoint; already-started streams are not spliced across providers.

### Migration CLI (`NEW / PROPOSED`)

`dotnet PayPlanner.Api.dll storage migrate inventory|plan|dry-run|copy|verify|diff|delta-sync|resume|repair|status|report|cutover-check|rollback-check`.

CLI requires explicit environment, policy/source/target, checkpoint path/DB rows, bounded concurrency; production actions operator-gated. It never deletes source.

## 10. Migration requirements

- Capture boundary and committed generations before bulk copy.
- Enable object registration/outbox before inventory or use maintenance snapshot.
- Preserve logical keys, Yandex native IDs/paths, content metadata and ownership.
- Copy immutable generations; verify size+SHA-256; checkpoint every batch.
- Delta includes creates, updates and tombstones.
- Cutover requires 100% required-object coverage, debt=0 for required copies, regression PASS and rollback-check PASS.
- Failback must first copy B-only generations; feature flag alone is not rollback.

## 11. Backup requirements

- Separate dump creation from delivery; invalid dump cannot become successful via fallback.
- `BackupSet` manifest: recovery point, DB engine/version, schema revision, file inventory generation, sizes/checksums, encryption/key reference (not secret), copies and retention.
- Fresh DB/file backups remain hot until RTO-proven alternatives exist.
- Delivery to healthy destinations continues if one fails; required vs desired protection is visible.
- Do not delete staging/last usable point before required copies verified.
- External catalog+manifest exists outside production DB in at least two independent destinations.
- File backup includes shared files, profile/e-signature after migration and Data Protection key recovery procedure.

## 12. Restore requirements

- Restore only to disposable/new DB/storage during validation.
- Verify checksum/decryption/decompression; `pg_restore --list`; `pg_restore --exit-on-error`.
- Restore DB + object generation set + Data Protection keys; run reconciliation and business smoke.
- Source selector prefers latest verified hot complete set; fallback never mixes incompatible backup chains silently.
- Monthly automated drill PROPOSED, plus operator-approved full DR exercise.

## 13. Security

- Secrets via environment/secret manager/Data Protection reference, rotation and least privilege.
- Separate runtime writer, backup writer (no delete), restore reader and lifecycle authority.
- Endpoint SSRF protection; TLS never disabled; redirects cannot forward Authorization cross-host.
- Destination eligibility includes tenant/account/region/encryption role.
- Presign TTL 5–10 minutes PROPOSED; URL never logged/stored as locator.
- Existing business authorization executes before fallback selection.
- Upload policy keeps current limits/contracts, adds quota and optional malware hook without broadening types silently.

## 14. Observability

Metrics/events: operation latency/outcome, destination/category, failover duration, UNKNOWN count, breaker state, required/available copies, oldest debt/lag, staging usage, backup creation/delivery/protection/restore-test age, reconciliation/corruption, recovery/failback. Liveness remains process-only; workload readiness reflects whether required operation has eligible destinations. Optional archive failure must not make unrelated APIs unready.

## 15. Tests

| ID | Обязательный сценарий |
|---|---|
| TEST-001 | Existing 96 S3/Yandex/backup/upload tests remain green |
| TEST-101 | Legacy local-only/routing-off upload/download/delete byte-identical |
| TEST-102 | Existing Yandex OAuth/list/copy/move/trash/sync/UI contract regression |
| TEST-103 | Existing S3 admin CRUD/list/upload/download/UI regression |
| TEST-104 | Preferred writer down before upload -> B selected within PROPOSED deadline, actual locator B |
| TEST-105 | A wrote but response lost -> UNKNOWN resolved, one logical object, no duplicate business action |
| TEST-106 | A/B partial success survives restart; only missing B retried |
| TEST-107 | Read A unavailable -> same generation/hash from B; stale/corrupt B rejected |
| TEST-108 | B-only object remains readable after A recovery and is repaired before failback |
| TEST-109 | All writers unavailable -> explicit pending/error, staging bounded, no false success |
| TEST-110 | Backup A fails -> B progresses; protection state/debt correct; chain complete |
| TEST-111 | Isolated DB+files+keys restore and business smoke |
| TEST-112 | Migration crash/resume/delta/tombstone/cutover/rollback preserves generations |
| TEST-113 | Delete vs stale replicate cannot resurrect object |
| TEST-114 | Large file keeps bounded RAM, Range/multipart/cancel/abort correct |
| TEST-115 | SSRF/tenant/permission/secret/presign expiry regression |
| TEST-116 | Multiple workers/leasing/duplicate delivery/policy change concurrency |

Real-provider tests require separately authorized non-production accounts. Mocks/MinIO are not proof of HOSTKEY/Yandex behavior.

## 16. Deployment compatibility

Order: additive schema → code with all flags off → local registration → replication shadow mode → verified backfill → read fallback canary → write fallback canary → backup fallback → wider rollout. Both task cards must remain behaviorally aligned per `AGENTS.md`. No deploy may change existing primary/keys by default.

## 17. Rollback / roll-forward

- Before cutover: flags off, legacy paths intact.
- After B-only writes: disabling routing is prohibited until those generations are copied to legacy-readable location or legacy reader consults catalog.
- DB migrations additive; destructive column removal deferred.
- Remote copied objects preserved; cleanup is separate irreversible approval.
- UNKNOWN/conflict/corruption stops automatic overwrite and uses quarantine/manual resolution.

## 18. Acceptance criteria

| ID | Criterion |
|---|---|
| AC-001 | Legacy single/mixed configs and S3/Yandex native tests pass with routing off |
| AC-002 | Every protected object has committed generation, SHA-256 and required replica states |
| AC-003 | TEST-104..109 prove read/write failover, honest degraded/all-failed and restart durability |
| AC-004 | TEST-108 proves catch-up/failback preserves B-only data and tombstones |
| AC-005 | TEST-110 proves independent backup progress and correct protection state |
| AC-006 | TEST-111 restores DB+files+keys and passes business smoke |
| AC-007 | TEST-112 proves resumable migration and rollback without source deletion |
| AC-008 | Large transfers satisfy measured bounded memory and Range/multipart contracts |
| AC-009 | Secrets absent from DB/frontend/logs/reports and rotation succeeds |
| AC-010 | Historical coverage report shows required copies for all in-scope files before failover claim |
| AC-011 | Alerts fire/recover for RPO, debt, all-failed, staging and restore-test age |
| AC-012 | Provider capability/tenant/role gates block forbidden fallback |
| AC-013 | Production operator approves independent destinations, RPO/RTO/retention and restore drill |
| AC-014 | Full repository CI gates pass; no code path silently lowers required copies |
| AC-015 | Runbooks/release notes accurately describe verified behavior and boundaries |

## 19. Definition of Done

Done только когда AC-001..015 имеют evidence, full CI зелёный, test-provider checks выполнены, production rollout прошёл canary/observation, исторические objects покрыты, restore drill успешен и владелец принял внешние параметры. Локальный фундамент реализован; production rollout, реальные provider checks и historical backfill остаются за отдельными приёмочными gates.

## 20. Routing/error/ack contract

- Eligibility проверяется до attempt; health — hint, не гарантия.
- Retryable transport/5xx/429 может fallback в пределах общего deadline; authorization/validation/conflict не обходятся другой копией.
- 403 vs 404 mapping provider-specific; object missing не отключает весь destination.
- Ack `available` = committed generation реально читаема с фактического locator; `protected` = verified required copies; это разные состояния.
- `UNKNOWN` хранится durable и разрешается Head/version/checksum/idempotency, затем repair/manual conflict.
- Recovery автоматически делает object-level catch-up; global priority failback — operator-approved после cooldown/debt=0/corruption=0.

## 21. Existing integration preservation gates

- S3: все текущие connection IDs, endpoint/bucket/prefix keys, permissions, audit и UI остаются; adapter extraction не меняет public routes.
- Yandex: OAuth callback/state/token protection, multiple connections, `disk:/` paths, copy/move/trash/permanent delete, CRM sync states/templates, backup upload и UI остаются native.
- Local: существующие relative `StoragePath` и filenames остаются читаемыми; registration не перемещает bytes.
- Profile/e-signature: compatibility read ищет legacy path до подтверждённого переноса.
- Backup: существующие manual create/download/retry endpoints продолжают работать, но получают durable catalog under the hood.

Полная последовательность реализации и карточки задач — в `STORAGE_IMPLEMENTATION_ROADMAP.md`.
