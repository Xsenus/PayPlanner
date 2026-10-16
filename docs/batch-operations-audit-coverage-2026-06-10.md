## Актуальная инвентаризация — 17 октября 2026

Всего **129** мест ExecuteUpdateAsync/ExecuteDeleteAsync по неизменному предикату BatchOperationAuditCoverageTests. Предыдущие описания ниже сохранены как история решений по аудиту; после форматирования их номера строк могут отличаться. Текущие номера приведены в полном перечне здесь. Перечень обновлён по фактическим исходникам после адресных исправлений и форматирования; подготовленные overlays не используются.

Два дополнительных технических UPDATE:

- backend/Services/Storage/StorageStageCapacityService.cs:205 — запись наблюдённой нативной идентичности маркера writer. CAS проверяет точный ключ, физический ресурс, объект, группу, binding, размер, locator, состояние Pending, завершённый admission и неизменную идентичность корня. Выполняется в той же транзакции активации writer; результат строго одна строка, несовпадение запрещает активацию. Это техническое свидетельство владения, а не изменение документа: построчное объектное событие не создаётся, tracked edits не сохраняются, долг не снимается.
- backend/Services/Storage/StorageStageFileCheckpoints.cs:98 — запись точного предшествующего и следующего StageFileReceiptJson вместе с UpdatedAt. Serializable-транзакция повторно подтверждает владение checkpoint и нативным дескриптором, точную длину и полный binding; CAS включает корень/маркер, старый receipt, Pending, завершённый admission и отсутствие ProductContext. После UPDATE проверяются ровно одна строка и прежний дескриптор. Неясный исход commit подтверждается новым чтением того же полного состояния, а не даёт новое право записи. Это технический receipt стадии; продуктовые документы, финансовые записи и их аудит не меняются.


Пять добавленных UPDATE в SalesRequestService (3), TaxRegisterService (1) и BankSyncService (1) присваивают полю его прежнее значение для блокировки существующей строки внутри транзакции. Это не самостоятельное изменение бизнес-данных: сохранение заявки/задач/банковских счетов выполняется через ChangeTracker, а запись регистра — вместе с явным журналом в той же транзакции. SQL-блокировка не должна создавать фиктивное объектное событие. Конкурентные сценарии перехода заявки, корректировки/закрытия года и выбора основного счёта покрыты регрессионными тестами.

Четыре UPDATE долговечной очереди уведомлений заявок в SalesRequestService:

- Захват: условное обновление NextAttemptAt по ID, отсутствию финального результата и точному прежнему NextAttemptAt. Пять минут аренды отсчитываются отдельно от захвата каждой записи, а не от начала всего пакета из максимум 50 записей.
- Прекращение для закрытой/отсутствующей задачи: сохраняются AbandonedAt и ограниченная причина только при собственной прежней аренде и отсутствии финального результата.
- Подтверждение доставки: DeliveredAt и LastError меняются только по той же собственной аренде и незавершённому состоянию; счётчик результата растёт только при обновлении одной строки. Время передаётся параметром, совместимым с существующим UTC-преобразованием PostgreSQL.
- Повтор/прекращение после ошибки: AttemptCount, NextAttemptAt, AbandonedAt и LastError изменяются только по собственной аренде. Устаревший обработчик не отменяет результат другой попытки и не сокращает её аренду. Ошибка ограничена 500 символами; отмена сохраняет аренду до истечения.

Это технические состояния доставки. Заявка, задача и их предметная история сохраняются обычным ChangeTracker в исходной транзакции, в которую также добавляется запись очереди. Сама доставка проходит существующий сервис уведомлений с проверкой доступности задачи и стабильным ключом события. Внешняя отправка выполняется вне транзакции; неизвестный исход может потребовать повтора и не объявляется гарантией единственной внешней отправки. Дополнительный построчный объектный аудит технической очереди не создаётся. Проверяются успех, повтор, закрытая/удалённая задача, замена аренды, уже доставленный/прекращённый результат и оба провайдера БД.

### Дополнение банковского блока: 18 вызовов

Предикат инвентаризации сохранён: учитывается каждая строка исходника с ExecuteUpdateAsync или ExecuteDeleteAsync, без исключений для новых банковских файлов. Число выросло со 111 до 129; полный перечень ниже заново сверяет также перемещённые прежние вызовы.

**15 обновлений без изменения значения удерживают строки внутри транзакции.** Они не создают самостоятельное объектное событие: присваивается прежнее значение, финансовая сумма и пользовательские поля этим SQL не меняются. Предметные записи сохраняются отдельно через SaveChanges в той же транзакции. Это не исключение операций из инвентаризации.

- MoneyAccountsController (3): собственная компания → банковский источник → денежный счёт. Перед сохранением повторно проверяются права, принадлежность и реквизиты; обновление счёта использует прежний снимок. Блокировки защищают создание, изменение, отключение и выбор основного счёта от параллельной смены владельца/реквизитов.
- MoneyOperationsController.CompanyAccess (4): та же последовательность трёх блокировок плюс удержание существующей денежной операции по прежним счёту, UpdatedAt и банковскому происхождению. Финансовая операция и кассовый документ сохраняются обычным ChangeTracker; ошибка откатывает общую транзакцию или собственный savepoint, сохраняя внешнюю работу.
- BankStatementImportService (3) и BankStatementImportService.Native (1): удержание компании, банковского и денежного счетов перед повторной проверкой доступа и записью пакета/строк/денежных фактов; нативная загрузка дополнительно удерживает банковский источник при выборе денежного счёта. Блокировка не записывает оплату и не заменяет доказательство исходной строки. Предметная история хранится в пакете и его строках; денежные факты сохраняются через ChangeTracker.
- MoneyBalanceReconciliationService (1): блокировка каждой разрешённой компании перед чтением фактов и записью дневных результатов, без отдельного UPDATE каждого счёта. Используется Serializable-транзакция, повторная проверка полномочий и ограниченный повтор конфликта. Денежные факты не меняются; результат сверки содержит время и действительного инициатора и сохраняется через SaveChanges.
- SpreadsheetImportService.Rollback (3): удержание компании, исходной операции импорта и затронутых денежных счетов при откате контрагента. Изменение контрагента и результат отката сохраняются через SaveChanges в общей транзакции; техническое присваивание прежнего значения не выдаётся за отдельную пользовательскую правку.

**Три обновления MoneyAccountsController меняют производные флаги основного счёта.** EnsureSingleDefaultAsync снимает IsDefault у других счетов той же компании и источника. EnsureDefaultExistsAsync снимает устаревший флаг и назначает выбранный доступный счёт. Обновляются только подходящие строки и UpdatedAt; отслеживаемые экземпляры синхронизируются с SQL. Создание, изменение и отключение самого счёта проходят SaveChanges, а эти вторичные автоматические переключения не формируют отдельного построчного объектного события. Это явная граница покрытия аудита; суммы, даты и происхождение денежных операций не меняются. Изоляция компании/источника, недоступный банк, гонки и сохранность истории проверяются сценариями счетов на SQLite/PostgreSQL.

### Полный текущий перечень

| Место | Операция |
| --- | --- |
| backend/Controllers/InternalChatController.cs:566 | ExecuteUpdateAsync |
| backend/Controllers/InternalChatController.cs:755 | ExecuteUpdateAsync |
| backend/Controllers/LegalEntitiesController.cs:1010 | ExecuteDeleteAsync |
| backend/Controllers/MessengerDocumentDeliveriesController.cs:485 | ExecuteUpdateAsync |
| backend/Controllers/MessengerDocumentDeliveriesController.cs:496 | ExecuteUpdateAsync |
| backend/Controllers/MessengerDocumentDeliveriesController.cs:513 | ExecuteUpdateAsync |
| backend/Controllers/MoneyAccountsController.cs:238 | ExecuteUpdateAsync |
| backend/Controllers/MoneyAccountsController.cs:246 | ExecuteUpdateAsync |
| backend/Controllers/MoneyAccountsController.cs:253 | ExecuteUpdateAsync |
| backend/Controllers/MoneyAccountsController.cs:279 | ExecuteUpdateAsync |
| backend/Controllers/MoneyAccountsController.cs:290 | ExecuteUpdateAsync |
| backend/Controllers/MoneyAccountsController.cs:295 | ExecuteUpdateAsync |
| backend/Controllers/MoneyOperationsController.CompanyAccess.cs:50 | ExecuteUpdateAsync |
| backend/Controllers/MoneyOperationsController.CompanyAccess.cs:56 | ExecuteUpdateAsync |
| backend/Controllers/MoneyOperationsController.CompanyAccess.cs:63 | ExecuteUpdateAsync |
| backend/Controllers/MoneyOperationsController.CompanyAccess.cs:94 | ExecuteUpdateAsync |
| backend/Controllers/StorageAdminController.cs:145 | ExecuteUpdateAsync |
| backend/Controllers/TasksController.cs:3226 | ExecuteUpdateAsync |
| backend/Controllers/TasksController.cs:4538 | ExecuteUpdateAsync |
| backend/Controllers/TasksController.cs:4784 | ExecuteUpdateAsync |
| backend/Controllers/UserActivityController.cs:418 | ExecuteUpdateAsync |
| backend/Controllers/UserActivityController.cs:540 | ExecuteDeleteAsync |
| backend/Controllers/V2/CasesV2Controller.cs:10871 | ExecuteUpdateAsync |
| backend/Controllers/V2/CasesV2Controller.cs:5737 | ExecuteUpdateAsync |
| backend/Controllers/V2/CasesV2Controller.cs:6074 | ExecuteUpdateAsync |
| backend/Services/AuthService.cs:261 | ExecuteUpdateAsync |
| backend/Services/AuthService.cs:743 | ExecuteUpdateAsync |
| backend/Services/Banking/BankStatementImportService.Native.cs:74 | ExecuteUpdateAsync |
| backend/Services/Banking/BankStatementImportService.cs:456 | ExecuteUpdateAsync |
| backend/Services/Banking/BankStatementImportService.cs:475 | ExecuteUpdateAsync |
| backend/Services/Banking/BankStatementImportService.cs:481 | ExecuteUpdateAsync |
| backend/Services/Banking/BankSyncService.cs:471 | ExecuteUpdateAsync |
| backend/Services/CalendarSyncLogRetentionService.cs:198 | ExecuteDeleteAsync |
| backend/Services/CalendarSyncLogRetentionService.cs:209 | ExecuteDeleteAsync |
| backend/Services/DatabaseBackupService.cs:1110 | ExecuteUpdateAsync |
| backend/Services/DatabaseRestoreManagedReplicas.cs:29 | ExecuteUpdateAsync |
| backend/Services/DatabaseRestoreManagedReplicas.cs:92 | ExecuteUpdateAsync |
| backend/Services/ElectronicSignatureService.cs:484 | ExecuteUpdateAsync |
| backend/Services/ElectronicSignatureService.cs:681 | ExecuteUpdateAsync |
| backend/Services/ExternalAuthService.cs:937 | ExecuteUpdateAsync |
| backend/Services/ExternalAuthService.cs:960 | ExecuteUpdateAsync |
| backend/Services/GoogleCalendarSyncService.cs:192 | ExecuteUpdateAsync |
| backend/Services/MessengerDocumentDeliveryService.cs:673 | ExecuteUpdateAsync |
| backend/Services/MessengerDocumentDeliveryService.cs:699 | ExecuteUpdateAsync |
| backend/Services/MessengerDocumentDeliveryService.cs:771 | ExecuteUpdateAsync |
| backend/Services/MoneyBalanceReconciliationService.cs:35 | ExecuteUpdateAsync |
| backend/Services/Notifications/CaseDocumentNotificationDispatchProcessor.cs:42 | ExecuteUpdateAsync |
| backend/Services/Notifications/CaseDocumentNotificationDispatchProcessor.cs:68 | ExecuteUpdateAsync |
| backend/Services/Notifications/InternalChatNotificationRecoveryService.cs:56 | ExecuteUpdateAsync |
| backend/Services/Notifications/MaxWebhookService.cs:268 | ExecuteDeleteAsync |
| backend/Services/Notifications/NotificationDeliveryRetentionService.cs:150 | ExecuteDeleteAsync |
| backend/Services/Requests/SalesRequestService.cs:1002 | ExecuteUpdateAsync |
| backend/Services/Requests/SalesRequestService.cs:325 | ExecuteUpdateAsync |
| backend/Services/Requests/SalesRequestService.cs:488 | ExecuteUpdateAsync |
| backend/Services/Requests/SalesRequestService.cs:749 | ExecuteUpdateAsync |
| backend/Services/Requests/SalesRequestService.cs:969 | ExecuteUpdateAsync |
| backend/Services/Requests/SalesRequestService.cs:981 | ExecuteUpdateAsync |
| backend/Services/Requests/SalesRequestService.cs:991 | ExecuteUpdateAsync |
| backend/Services/SpreadsheetImportService.Rollback.cs:32 | ExecuteUpdateAsync |
| backend/Services/SpreadsheetImportService.Rollback.cs:34 | ExecuteUpdateAsync |
| backend/Services/SpreadsheetImportService.Rollback.cs:42 | ExecuteUpdateAsync |
| backend/Services/Storage/CaseDocumentPublicationProcessor.cs:172 | ExecuteUpdateAsync |
| backend/Services/Storage/CaseDocumentPublicationProcessor.cs:187 | ExecuteUpdateAsync |
| backend/Services/Storage/CaseDocumentUploadRetirementService.cs:25 | ExecuteUpdateAsync |
| backend/Services/Storage/CourtDocumentSourceRegistry.cs:413 | ExecuteUpdateAsync |
| backend/Services/Storage/CourtDocumentSourceRegistry.cs:50 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedCaseReservationRecovery.cs:37 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedCaseReservationRecovery.cs:51 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedCaseReservationRecovery.cs:98 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedFinancialAttemptRecovery.cs:41 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedFinancialAttemptRecovery.cs:54 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedFinancialClaims.cs:23 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedFinancialClaims.cs:69 | ExecuteDeleteAsync |
| backend/Services/Storage/GeneratedFinancialDocumentPublication.cs:77 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedFinancialDocumentRegistry.cs:165 | ExecuteUpdateAsync |
| backend/Services/Storage/GeneratedFinancialDocumentRegistry.cs:167 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatOriginalRecovery.cs:105 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatOriginalRecovery.cs:112 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatOriginalWriter.cs:154 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatOriginalWriter.cs:167 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatOriginalWriter.cs:173 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendAttemptRegistry.cs:122 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendContinuation.cs:34 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendPublication.cs:111 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendPublication.cs:119 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendPublication.cs:123 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendPublication.cs:131 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendPublication.cs:60 | ExecuteUpdateAsync |
| backend/Services/Storage/InternalChatSendRecoveryBatch.cs:70 | ExecuteUpdateAsync |
| backend/Services/Storage/LegacyStorageReplicaMigrationService.cs:119 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedCaseDocumentUploadService.cs:542 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedCaseDocumentUploadService.cs:585 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedCaseDocumentUploadService.cs:592 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedProfilePhotoUploadService.cs:125 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedTaskFileUploadService.cs:279 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedTaskFileUploadService.cs:377 | ExecuteUpdateAsync |
| backend/Services/Storage/ManagedTaskFileUploadService.cs:426 | ExecuteUpdateAsync |
| backend/Services/Storage/ProductStorageBindingService.cs:102 | ExecuteUpdateAsync |
| backend/Services/Storage/ProductStorageBindingService.cs:143 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageCapacityAdmission.cs:156 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageCapacityAdmission.cs:163 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageObjectService.cs:649 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageObjectService.cs:684 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageObjectService.cs:720 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationLeaseScope.cs:45 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:216 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:270 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:305 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:406 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:410 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:417 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:422 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:423 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:821 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageOperationProcessor.cs:86 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageReconciliationService.cs:243 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageReconciliationService.cs:257 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageStageCapacityService.cs:185 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageStageCapacityService.cs:205 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageStageCapacityService.cs:340 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageStageCapacityService.cs:647 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageStageFileCheckpoints.cs:98 | ExecuteUpdateAsync |
| backend/Services/Storage/StorageStagePreparationRecovery.cs:62 | ExecuteUpdateAsync |
| backend/Services/Storage/TaskFilePublicationProcessor.cs:147 | ExecuteUpdateAsync |
| backend/Services/Storage/TaskFilePublicationProcessor.cs:200 | ExecuteUpdateAsync |
| backend/Services/Storage/TaskFilePublicationProcessor.cs:266 | ExecuteUpdateAsync |
| backend/Services/Tax/TaxRegisterService.cs:87 | ExecuteUpdateAsync |
| backend/Services/UserActivity/UserActivityRetentionService.cs:109 | ExecuteDeleteAsync |
| backend/Services/YandexCalendarSyncService.cs:195 | ExecuteUpdateAsync |

# Пакетные операции и покрытие аудитом

Дата первоначальной инвентаризации: 10 июня 2026 года. Актуализировано 30 сентября 2026 года.

EF-операции `ExecuteUpdateAsync` и `ExecuteDeleteAsync` обходят ChangeTracker. Поэтому объектный аудит `PaymentContext` не видит изменения каждой сущности, выполненные такими операциями. В документе перечислены все текущие места использования и объяснено, чем обеспечивается аудит или почему его отсутствие допустимо.

| Расположение | Операция | Затрагиваемые данные | Поведение аудита | Обоснование |
| --- | --- | --- | --- | --- |
| `backend/Services/DatabaseRestoreManagedReplicas.cs:29` | `ExecuteUpdateAsync` | Исходные pending/running/unknown задания хранилища в изолированной восстановленной БД | Режим включается отдельно после проверки снимка и файлов; задания получают DeadLetter и явную причину изоляции внутри транзакции подготовки копии. Рабочая БД не меняется. | Пробная копия не должна повторять исходные облачные записи или удаления; обновление не загружает всю очередь в память. |
| `backend/Services/DatabaseRestoreManagedReplicas.cs:92` | `ExecuteUpdateAsync` | Прежние невыбранные реплики максимум 250 восстановленных поколений за пакет | Причина исключения старого адреса сохраняется в самой реплике; выбранная локальная реплика сохраняется в той же транзакции после проверки байтов и фактов снимка. | Изолированное чтение не обращается к прежним провайдерам; обновление ограничено ID текущего пакета, поколения и tombstone не изменяются. |
| `backend/Services/DatabaseBackupService.cs:1095` | `ExecuteUpdateAsync` | Незавершённые попытки резервирования того же каталога при следующем запуске | Построчный объектный аудит не создаётся: запись технической попытки сохраняет статус `interrupted`, прежний этап, время окончания и безопасную причину. Исходные комплекты и другие каталоги не меняются. | После получения общего lease каталог не используется другим согласованным запуском резервирования. Условный переход только из `running` фиксирует прерывание без загрузки всей истории и не превращает старую попытку в подтверждённую копию. |
| `backend/Services/AuthService.cs:261` | `ExecuteUpdateAsync` | Сводные поля входа и активности текущего пользователя | Объектное событие не создаётся; сам вход отражается в журнале авторизации и активности. | Небольшой набор технических полей присутствия обновляется атомарно без загрузки устаревшей отслеживаемой сущности. |
| `backend/Services/AuthService.cs:743` | `ExecuteUpdateAsync` | Версия JWT и сводные поля выхода текущего пользователя | Объектное событие не создаётся; выход отражается временем и действием `logout` в самой записи пользователя. | Условное атомарное увеличение версии делает отзыв всех JWT мгновенным и не позволяет двум параллельным logout повторно изменить состояние. |
| `backend/Controllers/UserActivityController.cs:418` | `ExecuteUpdateAsync` | Метаданные последней активности текущего пользователя | Объектное событие не создаётся; отдельная строка журнала активности является следом аудита. | Лёгкие поля присутствия обновляются без загрузки полного агрегата пользователя на каждый запрос. |
| `backend/Controllers/UserActivityController.cs:540` | `ExecuteDeleteAsync` | Очистка журнала активности по запросу администратора | Построчный объектный аудит не создаётся; контроллер журналирует количество удалённых строк и возвращает результат обслуживания. | Операция намеренно пакетно очищает технический журнал активности. |
| `backend/Controllers/LegalEntitiesController.cs:1010` | `ExecuteDeleteAsync` | Адреса юридического лица перед заменой коллекции | Построчный аудит адресов не создаётся; сохранение юридического лица фиксирует видимое изменение основной сущности. | Адреса являются заменяемыми дочерними строками формы юридического лица. |
| `backend/Services/UserActivity/UserActivityRetentionService.cs:109` | `ExecuteDeleteAsync` | Просроченные строки журнала активности | Построчный аудит не создаётся; сервис журналирует количество удалённых строк и границу хранения. | Регламентная очистка пакетно удаляет технические записи с истёкшим сроком хранения. |
| `backend/Services/Notifications/NotificationDeliveryRetentionService.cs:150` | `ExecuteDeleteAsync` | Просроченные технические строки доставки уведомлений | Построчный аудит не создаётся; сервис сохраняет состояние системного задания и журналирует количество удалённых строк. | Регламентная очистка пакетно удаляет устаревшую диагностику доставки. |
| `backend/Services/CalendarSyncLogRetentionService.cs:198` | `ExecuteDeleteAsync` | Просроченные строки синхронизации Google Календаря | Построчный аудит не создаётся; системное задание хранит число удалений по провайдеру и общий итог. | Диагностика синхронизации удаляется ограниченными порциями. |
| `backend/Services/CalendarSyncLogRetentionService.cs:209` | `ExecuteDeleteAsync` | Просроченные строки синхронизации Яндекс.Календаря | Построчный аудит не создаётся; системное задание хранит число удалений по провайдеру и общий итог. | Диагностика синхронизации удаляется ограниченными порциями. |
| `backend/Services/Notifications/MaxWebhookService.cs:268` | `ExecuteDeleteAsync` | Просроченные отпечатки webhook MAX и связанная зашифрованная переписка | Построчный аудит не создаётся; сохранённые webhook имеют статус обработки, переписка зашифрована, исходное тело запроса не хранится. | Технические события и зависимая переписка удаляются ограниченными порциями по настроенному сроку от 1 до 90 дней. |
| `backend/Services/ElectronicSignatureService.cs:566` | `ExecuteUpdateAsync` | Одна подготовленная версия электронной подписи при отмене | Объектный аудит ChangeTracker не используется; в той же транзакции создаётся отдельное неизменяемое событие отмены с пользователем и причиной. | Условное атомарное обновление статуса обеспечивает идемпотентность и не позволяет отменить уже отправленную версию. |
| `backend/Services/ExternalAuthService.cs:937` | `ExecuteUpdateAsync` | Один завершённый OAuth-запрос внешнего входа или привязки | Персональный объектный аудит не создаётся: строка является короткоживущим техническим state, а успешный вход фиксируется журналом авторизации и активности пользователя. | Условный переход `completed` → `consumed` гарантирует, что при параллельном polling токен или результат привязки получает только один запрос. |
| `backend/Services/ExternalAuthService.cs:960` | `ExecuteUpdateAsync` | Один завершённый запрос входа или привязки через Telegram | Персональный объектный аудит не создаётся: строка является короткоживущим техническим state, а вход и привязка отражаются в журнале авторизации и профиле пользователя. | Условный переход `completed` → `consumed` исключает повторную выдачу результата при одновременных запросах браузера. |
| `backend/Services/GoogleCalendarSyncService.cs:190` | `ExecuteUpdateAsync` | Один ожидающий OAuth-запрос подключения Google Calendar | Персональный объектный аудит не создаётся: техническая строка хранит собственный статус, время завершения и ограниченную причину ошибки. | Условный переход `pending` → `processing` разрешает обмен authorization code только одному из параллельных callback. |
| `backend/Services/YandexCalendarSyncService.cs:195` | `ExecuteUpdateAsync` | Один ожидающий OAuth-запрос подключения Яндекс.Календаря | Персональный объектный аудит не создаётся: техническая строка хранит собственный статус, время завершения и ограниченную причину ошибки. | Условный переход `pending` → `processing` предотвращает повторный внешний обмен одного authorization code. |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs:485` | `ExecuteUpdateAsync` | Статус одной доставки по callback `delivered` | Объектное событие не создаётся; технический статус, время доставки и обновления сохраняются в записи доставки. | Условие `ReadAt == null` не позволяет запоздалому подтверждению доставки понизить уже зафиксированное прочтение. |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs:496` | `ExecuteUpdateAsync` | Статус одной доставки по callback `read` | Объектное событие не создаётся; запись доставки хранит первое время доставки, первое чтение и момент последнего обновления. | Атомарное обновление объединяет связанные отметки времени и остаётся идемпотентным при повторном callback. |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs:513` | `ExecuteUpdateAsync` | Статус одной доставки по callback `failed` | Объектное событие не создаётся; безопасно ограниченная причина, время отказа и статус сохраняются в самой доставке. | Условие `ReadAt == null` не позволяет поздней или параллельной ошибке провайдера понизить подтверждённый статус чтения. |
| `backend/Services/MessengerDocumentDeliveryService.cs:494` | `ExecuteUpdateAsync` | Одна публичная доставка с отсутствующим снимком документа | Объектное событие не создаётся; технический статус доставки, время сбоя и безопасный код `SnapshotMissing` сохраняются в самой записи. | Условное обновление не перезаписывает доставку, если одноразовая ссылка уже была использована конкурентным запросом. |
| `backend/Services/MessengerDocumentDeliveryService.cs:513` | `ExecuteUpdateAsync` | Одна подтверждённая одноразовая загрузка документа | Объектное событие не создаётся; запись доставки хранит первое и последнее открытие, статус чтения и счётчик загрузок. | Повторная проверка срока, хеша токена и нулевого счётчика в атомарном `UPDATE` гарантирует выдачу файла только одному из параллельных запросов. |
| `backend/Services/Storage/StorageOperationProcessor.cs:86` | `ExecuteUpdateAsync` | Захват одной durable storage-операции worker-ом | Состояние lease, worker ID и время обновления сохраняются в самой операции. | Условный атомарный переход предотвращает одновременную обработку одной операции несколькими worker-ами. |
| `backend/Services/Storage/StorageObjectService.cs:649` | `ExecuteUpdateAsync` | Логическая пометка storage object как удалённого | Tombstone создаётся в той же транзакции, а версия объекта увеличивается. | Условный атомарный переход исключает две конкурентные процедуры удаления и воскрешение объекта. |
| `backend/Services/Storage/StorageObjectService.cs:684` | `ExecuteUpdateAsync` | Перевод физической реплики в `deletePending` | Durable delete operation и tombstone сохраняются в той же транзакции. | Реплика скрывается от чтения до асинхронного физического удаления с учётом retention/legal hold. |
| `backend/Services/Storage/StorageObjectService.cs:720` | `ExecuteUpdateAsync` | Пометка реплики как подозрительной после неуспешной проверки | Последняя безопасно ограниченная ошибка и время обновления записываются в реплику. | Fallback чтения не должен снова выбирать заведомо повреждённую или недоступную копию. |
| `backend/Services/Storage/StorageReconciliationService.cs:93` | `ExecuteUpdateAsync` | Подтверждение доступной реплики при reconciliation | Статус, время проверки и очистка ошибки сохраняются в каталоге реплик. | Пакетная проверка обновляет техническое состояние каталога без изменения пользовательского объекта. |
| `backend/Services/Storage/StorageReconciliationService.cs:109` | `ExecuteUpdateAsync` | Пометка реплики как missing/corrupt | Статус и диагностическая ошибка сохраняются в каталоге; при наличии источника создаётся durable repair operation. | Техническая сверка должна атомарно исключить плохую копию из чтения до repair. |

## Контрольные правила

### Дополнение к инвентаризации 30 сентября

Операции ниже появились или переместились после исходной таблицы. Указанные места используют пакетное обновление для технического состояния, а не для незаметного изменения пользовательских полей. Условие по статусу, версии или идентификатору является границей конкурентного перехода; объектный аудит `ChangeTracker` на них не распространяется.

- Чат и задача: `backend/Controllers/InternalChatController.cs:755`, `backend/Controllers/TasksController.cs:3220`, `backend/Controllers/TasksController.cs:4532`, `backend/Controllers/TasksController.cs:4778` — атомарные отметки активности, публикации и статуса с отдельной историей задачи/сообщения.
- Legacy-отправка чата: `backend/Controllers/InternalChatController.cs:566` — no-op блокировка одной строки чата в короткой transaction перед повторной проверкой membership и managed identity; синхронизирована с capture и исключает обход сохранённой попытки при смене flags. Самостоятельного пользовательского изменения или отдельного audit event нет; сообщение и notification pending создаются вместе после этих проверок.
- Судебное дело: `backend/Controllers/V2/CasesV2Controller.cs:6044`, `backend/Controllers/V2/CasesV2Controller.cs:10841` — условные переходы связанного технического состояния, отражаемые в истории дела.
- Прежняя загрузка документа дела: `backend/Controllers/V2/CasesV2Controller.cs:5707` — блокировка дела сериализует финальную проверку retirement и известного managed intent перед сохранением записи. Сам Title не меняется; документ/история/audit/уведомления сохраняются прежним writer только после проверки, при отказе общий atomic helper откатывает граф и очищает только созданный им файл.
- Административный повтор: `backend/Controllers/StorageAdminController.cs:145` — смена состояния операции допускается только при разрешённом статусе, отсутствии действующей аренды и durable retirement отметки для case publication; история попыток остаётся в самой операции.
- Резервное копирование, подпись и выдача документов: `backend/Services/DatabaseBackupService.cs:1109`, `backend/Services/ElectronicSignatureService.cs:566`, `backend/Services/MessengerDocumentDeliveryService.cs:605`, `backend/Services/MessengerDocumentDeliveryService.cs:631` — условные статусы, счётчики и отметки выдачи хранятся в собственных записях этих процессов.
- Повтор уведомления чата: `backend/Services/Notifications/InternalChatNotificationRecoveryService.cs:56` — атомарное закрепление попытки; время и результат остаются в записи сообщения.
- Перенос прежних файлов и привязки: `backend/Services/Storage/LegacyStorageReplicaMigrationService.cs:119`, `backend/Services/Storage/ManagedProfilePhotoUploadService.cs:125`, `backend/Services/Storage/ManagedTaskFileUploadService.cs:279`, `backend/Services/Storage/ManagedTaskFileUploadService.cs:377`, `backend/Services/Storage/ManagedTaskFileUploadService.cs:426`, `backend/Services/Storage/ProductStorageBindingService.cs:102`, `backend/Services/Storage/ProductStorageBindingService.cs:143` — условные проверки версии/владельца и публикация технических ссылок; каталог и журнал операций сохраняют результат.
- Самостоятельные документы дела: `backend/Services/Storage/ManagedCaseDocumentUploadService.cs:540`, `backend/Services/Storage/ManagedCaseDocumentUploadService.cs:583`, `backend/Services/Storage/ManagedCaseDocumentUploadService.cs:590` — блокировка дела без изменения полей, условный переход владельца и публикация результата intent; документ, история дела, отдельная запись аудита, уведомление и product binding сохраняются в общей транзакции контроллера. При отказе транзакция откатывается и tracking восстанавливается; неопределённый commit/rollback не разрешает очистку байтов.
- Прекращение попытки документа дела: `backend/Services/Storage/CaseDocumentUploadRetirementService.cs:25` — блокировка того же дела без изменения Title сериализует отказ пользователя и публикацию. Событие прекращения хранится отдельной неизменяемой отметкой case/actor/upload key/object/time внутри этой транзакции; один параметризованный INSERT не сохраняет чужие tracked edits. Документы, история и файлы не удаляются.
- Лимит свободного места: `backend/Services/Storage/StorageCapacityAdmission.cs:151`, `backend/Services/Storage/StorageCapacityAdmission.cs:158`, `backend/Services/Storage/StorageStageCapacityService.cs:581` — атомарное резервирование и освобождение ёмкости с записью резерва.
- Каталог и аренды нового хранилища: `backend/Services/Storage/StorageObjectService.cs:649`, `backend/Services/Storage/StorageObjectService.cs:684`, `backend/Services/Storage/StorageObjectService.cs:720`, `backend/Services/Storage/StorageOperationLeaseScope.cs:45`, `backend/Services/Storage/StorageOperationProcessor.cs:86`, `backend/Services/Storage/StorageOperationProcessor.cs:216`, `backend/Services/Storage/StorageOperationProcessor.cs:270`, `backend/Services/Storage/StorageOperationProcessor.cs:305`, `backend/Services/Storage/StorageOperationProcessor.cs:406`, `backend/Services/Storage/StorageOperationProcessor.cs:410`, `backend/Services/Storage/StorageOperationProcessor.cs:417`, `backend/Services/Storage/StorageOperationProcessor.cs:422`, `backend/Services/Storage/StorageOperationProcessor.cs:423`, `backend/Services/Storage/StorageOperationProcessor.cs:821` — переходы защищены статусом, поколением или арендой, а их результат записан в каталог и durable-операцию.
- Сверка копий: `backend/Services/Storage/StorageReconciliationService.cs:243`, `backend/Services/Storage/StorageReconciliationService.cs:257` — обновление только выбранного ограниченного набора реплик и текущего поколения; время, статус и диагностическая причина остаются в каталоге.
- Очередь публикации файлов: `backend/Services/Storage/TaskFilePublicationProcessor.cs:147`, `backend/Services/Storage/TaskFilePublicationProcessor.cs:200`, `backend/Services/Storage/TaskFilePublicationProcessor.cs:266` — статус, число попыток и владелец сохраняются в durable-операции с условием текущего состояния.

- `BatchOperationAuditCoverageTests` просматривает прикладной код backend и завершает проверку ошибкой, если новый вызов `ExecuteUpdateAsync` или `ExecuteDeleteAsync` не внесён в этот документ.
- Для каждой новой пакетной операции нужно явно указать, почему объектный аудит пропущен, каким журналом он заменён или какое компенсирующее событие требуется.

- Передача документа мессенджера: `backend/Services/MessengerDocumentDeliveryService.cs:702` — владелец подтверждённого storage object меняется с неизменного upload-intent на ID доставки атомарно с записью доставки и product binding. Условие проверяет класс, поколение, размер/SHA и прежнего владельца; при отказе транзакция откатывается. Технический переход фиксируется каталогом объекта, binding и durable commit-операцией; пользовательские поля доставки сохраняются обычным аудитируемым SaveChanges.

- Исходный PDF подписи: `backend/Services/ElectronicSignatureService.cs:377` — переход владельца storage object с content-keyed upload-intent на ID envelope проходит атомарно с envelope, подписантами, prepared-event и product binding. Класс, поколение, размер/SHA и прежний владелец проверяются условным UPDATE; каталог, durable commit receipt и событие подготовки сохраняют техническую и пользовательскую историю, а отказ публикации откатывает все связи.

- Очередь уведомлений документа: `backend/Services/Notifications/CaseDocumentNotificationDispatchProcessor.cs:42`, `backend/Services/Notifications/CaseDocumentNotificationDispatchProcessor.cs:68` — условные claim/settlement с текущей арендой; попытки, статус, срок и результат сохранены в durable dispatch row, без изменения текста/доменных данных.

- Очередь публикации документов дела: `backend/Services/Storage/CaseDocumentPublicationProcessor.cs:172`, `backend/Services/Storage/CaseDocumentPublicationProcessor.cs:187` — условное завершение и блокировка живой аренды внутри domain transaction; статус/попытки/причина сохраняются в storage operation, документы/история/audit/уведомления используют общий publisher. Запоздалый worker не завершает чужую аренду.

- Реестр судебных источников: `backend/Services/Storage/CourtDocumentSourceRegistry.cs:50` — no-op UPDATE заголовка берёт блокировку строки дела в короткой metadata transaction. Заголовок не меняется; accepted scope epoch, исходный автор и неизменяемая попытка сохраняются обычным SaveChanges. Блокировка сериализует одновременные capture одного дела, без файловых или сетевых операций внутри транзакции.
- Возврат судебного источника: `backend/Services/Storage/CourtDocumentSourceRegistry.cs:367` — возобновляет только прежнюю подтверждённую публикацию после forbidden при новой accepted membership. Conditional UPDATE исключает чужую аренду, изменённый статус, удалённый объект и устаревшее поколение; права исходного автора, source/content metadata и retirement проверены в той же транзакции. AttemptCount сохраняется; domain document/audit создаёт только обычный publisher.

- Финансовая попытка: `backend/Services/Storage/GeneratedFinancialDocumentRegistry.cs:153` — общий no-op UPDATE заголовка сериализует managed capture/publisher и допуск прежнего финансового маршрута одного дела. Заголовок не меняется. При capture исходные ID/снимок/HTML сохраняются в короткой транзакции без файловых/сетевых операций; managed финансовая запись и аудит возникают вместе с документом после копий. Legacy допуск повторно проверяет активные попытки/права/flags и завершает собственную финансовую транзакцию до файлового I/O; деловые изменения сохраняются обычным SaveChanges.
- Финансовый наряд: `backend/Services/Storage/GeneratedFinancialDocumentRegistry.cs:155` — общий no-op UPDATE заголовка удерживает исходный наряд после case lock для capture и legacy допуска. Точный replay не выделяет новые ID; другая pending Invoice попытка блокируется до allocation. Legacy не обходит принятый источник новым ключом или запросом без ключа. No-op не меняет пользовательские поля и не создаёт отдельный аудит; provider I/O вынесен за финансовую транзакцию.
- Область финансовых номеров: `backend/Services/Storage/GeneratedFinancialClaims.cs:19` — no-op UPDATE отдельной технической строки сериализует резервирование номера между разными делами. Version не меняется, техническое резервирование сохраняется обычным SaveChanges. Managed capture/publisher и legacy writer используют одну область и учитывают pending номера; пользовательский финансовый аудит сохраняется вместе с финансовыми строками обычным SaveChanges.
- Замена устаревшего резервирования номера: `backend/Services/Storage/GeneratedFinancialClaims.cs:61` — удаляет только точное совпадение kind/date/number после явного retirement marker либо публикации исходной попытки. Frozen снимок, HTML, исходные ID и финансовые записи не удаляются. Новое резервирование и этот переход находятся в одной транзакции; при ошибке удаление откатывается.
- Финансовая публикация наряда: `backend/Services/Storage/GeneratedFinancialDocumentPublication.cs:77` — no-op UPDATE заголовка удерживает исходный наряд после case lock перед повторной проверкой frozen proof, номера и услуг. Пользовательская смена статуса счёта и финансовые строки сохраняются обычным SaveChanges в той же транзакции с исходным автором; блокировка не вызывает отдельного аудита и не выполняет provider I/O.

- Восстановление принятого финансового источника: `backend/Services/Storage/GeneratedFinancialAttemptRecovery.cs:41` — условная аренда одной попытки, fresh token/expiry, счётчик и следующий допуск. Восстанавливаются только уже принятые исходные данные; операция не создаёт финансовую запись. Отдельный пользовательский аудит не нужен для технического lease; предметная публикация проходит существующий document/finance audit в общей транзакции.
- Результат восстановления финансового источника: `backend/Services/Storage/GeneratedFinancialAttemptRecovery.cs:54` — освобождение только собственного execution token и сохранение ограниченного статуса/следующего повтора. Устаревший обработчик не изменяет новую аренду. Прекращение и исходные данные не удаляются; финансовые изменения сохраняются обычным SaveChanges внутри публикации.
- Автоматическое восстановление generated reservations: `backend/Services/Storage/GeneratedCaseReservationRecovery.cs:37` — compare-and-set lease/backoff/counter только выбранного pending local резерва с неизменным metadata proof и без commit; ограниченный пакет максимум10, финансовые и обычные upload intents не включены. Это техническая отметка выполнения, не финансовая запись или готовый документ.
- Завершение reservation recovery: `backend/Services/Storage/GeneratedCaseReservationRecovery.cs:51` — очистка только собственного execution token и сохранение результата/следующего времени; отмена оставляет lease до истечения. Запоздалый исполнитель не меняет новый token или чужой metadata proof; capacity status и байты не освобождаются этой операцией.
- Fence reservation recovery: `backend/Services/Storage/GeneratedCaseReservationRecovery.cs:98` — no-op update живой lease внутри короткой domain transaction блокирует её замену до коммита; сохраняются actor/source checks. Generated document/history/object audit/notifications/binding остаются на существующем атомарном publisher; техническая lease не создаёт выдуманный StorageObject.
- Реестр отправки вложений чата: `backend/Services/Storage/InternalChatSendAttemptRegistry.cs:122` — no-op UPDATE заголовка чата удерживает общий короткий lock для capture и будущей publication. UpdatedAt и видимость сообщения не меняются; frozen header/files/mentions сохраняются обычным SaveChanges, без provider I/O и уведомлений. При отказе собственный граф отсоединяется, исходные tracked значения и исходное исключение сохраняются.
- Запись/восстановление оригиналов чата: `backend/Services/Storage/InternalChatOriginalRecovery.cs:105` — общий условный захват отдельной аренды попытки до provider I/O; техническая Version не меняет сообщение или права пользователя.
- Освобождение аренды оригиналов: `backend/Services/Storage/InternalChatOriginalRecovery.cs:112` — очищается только собственный token; запоздалый исполнитель не освобождает чужую аренду, отмена оставляет её до истечения.
- Принятие оригиналов: `backend/Services/Storage/InternalChatOriginalWriter.cs:154` — AcceptedAt появляется в общей короткой transaction после проверки всех frozen файлов, каталогов и проверенных локальных копий. Это техническая отметка долговечности, не публикация сообщения или уведомление.
- Fence оригиналов: `backend/Services/Storage/InternalChatOriginalWriter.cs:167` — продление только живой собственной аренды с неизменным frozen SHA; внутри transaction удерживается lock до commit, без provider I/O.
- Каталог оригиналов: `backend/Services/Storage/InternalChatOriginalWriter.cs:173` — no-op lock неудалённого объекта и текущего generation предотвращает гонку с tombstone/generation switch при принятии. Предметные сообщения, вложения и bindings этот writer не создаёт.

- Атомарная публикация вложений чата: `backend/Services/Storage/InternalChatSendPublication.cs:60` — общий transaction сообщения, всех вложений, owner handoff, bindings, thread activity и исторического маркера; свежие authority/copy guards и собственная живая lease, без provider I/O; rollback не удаляет стабильные байты.

- Атомарная публикация вложений чата: `backend/Services/Storage/InternalChatSendPublication.cs:102` — общий transaction сообщения, всех вложений, owner handoff, bindings, thread activity и исторического маркера; свежие authority/copy guards и собственная живая lease, без provider I/O; rollback не удаляет стабильные байты.

- Атомарная публикация вложений чата: `backend/Services/Storage/InternalChatSendPublication.cs:110` — общий transaction сообщения, всех вложений, owner handoff, bindings, thread activity и исторического маркера; свежие authority/copy guards и собственная живая lease, без provider I/O; rollback не удаляет стабильные байты.

- Атомарная публикация вложений чата: `backend/Services/Storage/InternalChatSendPublication.cs:114` — общий transaction сообщения, всех вложений, owner handoff, bindings, thread activity и исторического маркера; свежие authority/copy guards и собственная живая lease, без provider I/O; rollback не удаляет стабильные байты.

- Атомарная публикация вложений чата: `backend/Services/Storage/InternalChatSendPublication.cs:122` — общий transaction сообщения, всех вложений, owner handoff, bindings, thread activity и исторического маркера; свежие authority/copy guards и собственная живая lease, без provider I/O; rollback не удаляет стабильные байты.

- Продолжение отправки чата: `backend/Services/Storage/InternalChatSendContinuation.cs:34` — только собственный token/frozen SHA: записать публичный status, bounded retry time и saturating attempt count, очистить свою lease; exception/cancellation сохраняет её до expiry, чужая lease не освобождается. Никакие private text/bytes не копируются в outcome.

- Batch восстановления чата: `backend/Services/Storage/InternalChatSendRecoveryBatch.cs:70` — outcome/count/next time только собственного token/frozen SHA; нормальный результат освобождает lease, неизвестный provider/commit error сохраняет её до expiry, cancellation не завершает; successor metadata не меняется. Due-window bounded20, terminal statuses и needsInput без AcceptedAt исключены; после подтверждения всех originals точным повтором needsInput вновь доступен recovery. Originals/domain graph не удаляются.

- Подтверждение размера временного файла: `backend/Services/Storage/StorageStageCapacityService.cs:303` — условный UPDATE сохраняет фактический размер, неизменяемое подтверждение исходного верхнего предела и привязку резерва в одной транзакции с обновлением эпохи бюджета. Проверяются ключ, объект, путь, физическая группа и прежнее состояние. Это технический учёт ёмкости; документы и финансовые данные не меняются, чужие tracked edits не сохраняются.

- Подготовка writer: `backend/Services/Storage/StorageStageCapacityService.cs:171` — условный UPDATE Pending/подготовка на точном ключе, объекте, пути, binding, группе и размере блокирует строку до создания маркера и commit активации. Снятие долга не выполняется, несохранённые tracked изменения не сохраняются.
- Прекращение подготовки: `backend/Services/Storage/StorageStagePreparationRecovery.cs:61` — условный UPDATE той же точной старой подготовки под Serializable; отсутствие файла и физическая authority проверяются после захвата строки. Epoch обновляется в той же транзакции; живой/подменённый marker, существующие байты и изменённая строка сохраняются. Preview не выполняет UPDATE.
