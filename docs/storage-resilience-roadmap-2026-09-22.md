# Отказоустойчивое хранение PayPlanner — активный Roadmap

Дата старта реализации: 22 сентября 2026 года. Baseline: `main@09ae8b58e74787a20d7f7d2361331b83731ab8b4`.

Подробные архитектурные решения, требования, риски и карточки задач сохранены в [`_codex_reports/storage-audit/STORAGE_IMPLEMENTATION_ROADMAP.md`](../_codex_reports/storage-audit/STORAGE_IMPLEMENTATION_ROADMAP.md). Этот файл является активным индексом выполнения по правилам репозитория; ID и scope подробного плана не переопределяются.

## Прогресс

- Всего: 26 задач.
- Завершено и проверено: 9.
- В работе: 12.
- Заблокировано внешними условиями: 0; production-проверки пока не разрешены и не запускаются.
- Требует будущей приёмки на реальной среде: TASK-021, TASK-025.
- Общий прогресс: **35%**.

## Этапы и задачи

- STG-00 — baseline: `[~]` TASK-001.
- STG-01 — backup/restore foundation: `[~]` TASK-002, TASK-003, TASK-004.
- STG-02 — persistent paths/secrets: `[~]` TASK-005, TASK-006.
- STG-03 — catalog/provider abstraction: `[x]` TASK-007, TASK-008, TASK-009, TASK-010.
- STG-04 — routing/replication/failover: `[x]` TASK-011, TASK-012, TASK-013, TASK-017; `[~]` TASK-014, TASK-015, TASK-016.
- STG-05 — full backup set: `[~]` TASK-018, TASK-019.
- STG-06 — migration/backfill: `[x]` TASK-020; `[acceptance]` TASK-021.
- STG-07 — streaming/API/UI: `[~]` TASK-022.
- STG-08 — observability/qualification/rollout: `[~]` TASK-023, TASK-024; `[acceptance]` TASK-025.
- STG-09 — documentation/release: `[~]` TASK-026.

## Текущий рабочий блок

### TASK-001 — characterization baseline

- [~] Зафиксировать существующее backup/S3/Yandex/local поведение тестами.
- [ ] Зафиксировать owner decisions по RPO/RTO, required copies, retention и разрешённым runtime providers; до решения используются безопасные defaults и feature flags.
- [x] Выполнить targeted baseline и отметить фактический результат: итоговый backend gate 2827/2827, frontend gate 1450/1450, lint/type/encoding/build прошли.

### TASK-002 — защита scheduler/retention

- [~] Изолировать ошибки недоступных подкаталогов, чтобы retention не останавливал scheduler.
- [x] Никогда не удалять последнюю доступную локальную резервную копию.
- [x] Перед очисткой находить целую копию по manifest и контрольной сумме: при повреждённом новейшем файле защищать последнюю проверенную, а при отсутствии проверенной копии не удалять ничего.
- [x] В пределах одного процесса проверять слот расписания под блокировкой; незавершённая или повреждённая копия не закрывает слот, повторный конкурентный запуск не создаёт дубль.
- [x] Проверка копии и restore drill отвергают manifest со статусом «не завершено», даже если файл и контрольная сумма совпадают.
- [~] Границы retention и повреждённые копии покрыты тестами; отдельно проверить отказ доступа к вложенному каталогу на подходящей файловой системе.
- [ ] Отдельно, только после разрешения владельца, проверить актуальность исторических production-инцидентов S3/permissions.

### Реализованный фундамент STG-01..04

- `[~]` TASK-003/004: для новых DB-копий создаётся sidecar manifest с SHA-256 и файловыми частями; доступны проверка и изолированный SQLite restore drill. PostgreSQL execution остаётся закрытым безопасными настройками. Для явного запуска добавлена проверка пустоты отдельной БД до восстановления и убрана очистка существующих объектов; при отмене внешний процесс останавливается. После восстановления пробной PostgreSQL-копии запрет исходящей почты записывается даже при отсутствии прежней настройки; SMTP-данные в копии очищаются, ожидающие и ошибочные email-отправки не будут повторены. На одноразовой локальной PostgreSQL-базе подтверждены отказ для заполненной БД и новая защита почты; полный restore drill с реальным дампом ещё нужно проверить в отдельной тестовой среде, не в production.
- `[~]` TASK-005: фото профиля и электронные подписи вынесены из release tree в persistent roots, сохранено dual-read legacy-путей и добавлена deploy-миграция без удаления источника.
- `[~]` TASK-006: S3 Secret Key защищается Data Protection, plaintext автоматически мигрирует, потеря key ring приводит к явному отказу; endpoint проходит HTTPS/credentials/private-network/DNS policy. Внешнее хранение key ring требует решения владельца.
- `[x]` TASK-007: additive catalog schema и отдельные SQLite/PostgreSQL migrations проверены на clean/upgrade paths и уникальность idempotency key.
- `[x]` TASK-008: capability-based provider contracts и typed outcomes реализованы.
- `[x]` TASK-009: безопасные local и read-only legacy-local providers реализованы и проверены; legacy source остаётся доступным при выключенной маршрутизации.
- `[x]` TASK-010: deterministic policy evaluator и admin explain endpoint реализованы в dry-run; defaults local-only, routing выключен.
- `[x]` TASK-011: durable worker реализует lease, retry/dead-letter, UNKNOWN reconciliation и tombstone suppression; admin API позволяет видеть и повторять операции.
- `[x]` TASK-012/013: существующие S3 и Яндекс.Диск обёрнуты streaming provider adapters без потери native API; реальные аккаунты не использовались.
- `[~]` TASK-014/015/016: доступны opt-in write routing, проверяемый same-generation read failover, circuit breaker и reconciliation. Подключение всех продуктовых call sites и глобальный failback намеренно оставлены за feature flags/production gate.
- `[x]` TASK-017: логическое удаление, tombstone, retention/legal hold и безопасный reconcile исключают воскрешение удалённых объектов.
- `[~]` TASK-018/019: полный локальный BackupSet включает БД и persistent file roots; manifest загружается последним в S3/Яндекс.Диск. Durable copy catalog и две подтверждённые независимые production-копии ещё не приняты.
- `[x]` TASK-020: admin backfill API работает dry-run-first, пакетно и возобновляемо для задач, дел, чата, messenger snapshots, ЭП и фото; исходники не удаляются.
- `[~]` TASK-022/023: native downloads переведены на streaming, в UI появились verify/restore drill, admin health показывает backlog, replica debt и circuit state. Alert escalation остаётся внешним решением.

## Защитные ограничения

- Никакого production-доступа, migration/cutover, удаления пользовательских файлов или смены primary без отдельного разрешения и выполненного restore gate.
- Локальный backend запускается только с отключёнными email и background services; для копии production БД дополнительно отключается DB-настройка email.
- Legacy local/S3/Yandex контракты сохраняются; новая маршрутизация включается только feature flags после regression gates.

## Журнал

- 2026-09-22: реализация начата; TASK-001 и локальная часть TASK-002 переведены в работу. Код ещё не изменён, production не затрагивался.
- 2026-09-22: реализованы и локально проверены backup manifest/retention, persistent paths, защита S3-секрета, catalog migrations, provider contracts/local provider, policy dry-run и основа durable worker. Production и реальные provider accounts не затрагивались.
- 2026-09-22: добавлены full fileset backup, изолированный restore drill, S3 endpoint policy, streaming S3/Яндекс.Диск, routing/read failover, tombstone/retention, reconciliation, circuit breaker, resumable legacy backfill, admin API/UI и эксплуатационный runbook. Production, реальные storage accounts и historical data не затрагивались; TASK-021/025 остаются приёмочными.
- 2026-09-27: локальная очистка больше не принимает самый новый файл за пригодную копию без проверки. Перед удалением проверяются manifest и контрольные суммы; при отсутствии целой копии очистка останавливается, а при повреждённом новом файле сохраняется последняя целая копия. Проверка слота расписания перенесена под блокировку и требует целой копии; повреждённый файл приводит к новой попытке. В частом опросе успешная проверка переиспользуется не более часа при неизменных метаданных файла, manifest и обязательных частей; перед удалением и восстановлением контрольные суммы всегда проверяются заново. Manifest со статусом «не завершено» не проходит проверку и restore drill. Адресные сценарии и полный серверный набор (3085/3085), аудит кодировки прошли. Прогресс 9/26 (35%) не меняется, поскольку TASK-002 в целом ещё не закрыта: отдельно нужны проверка отказа доступа к каталогу, межпроцессная координация и приёмка на реальной среде.
- 2026-09-27: в PostgreSQL restore drill устранён пробел безопасности: отсутствие настройки email больше не оставляет восстановленную копию с возможной отправкой писем. В одной транзакции записывается запрет почты, очищаются SMTP-реквизиты копии и пропускаются ожидающие и ошибочные email-доставки; состояние исходной базы не меняется. Адресный сценарий прошёл на созданной для теста и затем удалённой локальной PostgreSQL-базе (порт 5433), итоговый backend-набор — 3085/3085, аудит кодировки — без замечаний. Полный restore drill с реальным дампом остаётся открытым; прогресс 9/26 (35%) без изменений.
