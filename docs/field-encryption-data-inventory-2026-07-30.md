# Инвентаризация данных для прикладного шифрования

Дата аудита: 30 июля 2026.

Назначение: техническая карта мест хранения и распространения чувствительных данных перед выбором KMS и утверждением бизнес-классификации. Документ не является решением «шифровать всё перечисленное»: окончательный перечень, сроки хранения, маскирование и ограничения поиска требуют решения заказчика.

## 1. Критические семейства данных

### Физические лица и сотрудники

| Семейство | Поля-кандидаты | Почему требует отдельной защиты |
| --- | --- | --- |
| Клиент | `Client.Name`, `Client.Email`, `Client.Phone`, `Client.Address`, `Client.Notes` | ФИО/контакты, адрес и свободный текст |
| Паспорт | `ClientPassport.Series`, `ClientPassport.Number`, `ClientPassport.IssuedBy`, `ClientPassport.IssuedAt`, `ClientPassport.DepartmentCode`, `ClientPassport.BirthPlacePassport`, `ClientPassport.BirthPlaceFias` | Реквизиты документа и место рождения; высший приоритет |
| Адреса | `AddressRecord.Value`, `AddressRecord.UnrestrictedValue`, `AddressRecord.PostalCode`, `AddressRecord.House`, `AddressRecord.Flat`, `AddressRecord.GeoLat`, `AddressRecord.GeoLon` | Точный адрес и географические координаты |
| Внешние контакты | `ExternalContact.FullName`, `ExternalContact.Phone`, `ExternalContact.Email`, `ExternalContact.PhonesJson`, `ExternalContact.EmailsJson`, `ExternalContact.Notes` | Контакты представителей и свободный текст |
| Сотрудники | `User.Email`, `User.FullName`, `User.DateOfBirth`, `User.PhoneNumber`, `User.LastActivityIpAddress`, `User.LastActivityUserAgent` | Персональные и эксплуатационные данные |

`User.PasswordHash` хранит односторонний хэш и не должен переводиться в обратимое шифрование. Его защита требует современной password-hashing policy, а не decryptable ciphertext.

### Юридические лица и банковские реквизиты

| Семейство | Поля-кандидаты | Особенности |
| --- | --- | --- |
| Организация | `LegalEntity.Inn`, `LegalEntity.Kpp`, `LegalEntity.Ogrn`, `LegalEntity.Address`, `LegalEntity.Phone`, `LegalEntity.Email`, `LegalEntity.Director` | Часть реквизитов публична, но совокупность и контакты требуют утверждённой классификации |
| Банковские данные | `LegalEntity.BankAccountsJson`, `LegalEntity.CardsJson`, `LegalEntity.TreasuryAccountNumber`, `LegalEntity.TreasuryCorrAccountNumber`, `MoneyAccount.BankAccountNumber` | Точный поиск возможен через blind index; `contains` после шифрования запрещается |
| Подписи и изображения | `LegalEntity.SealDataUrl`, `LegalEntity.DirectorSignatureDataUrl`, `LegalEntity.DocumentDirectorJson`, `LegalEntity.ChiefAccountantJson` | Изображения подписи и сведения подписантов; высший приоритет |

### Дела, задачи, документы и финансы

| Семейство | Поля-кандидаты | Особенности |
| --- | --- | --- |
| Судебное дело | `ClientCase.CourtCaseNumber`, `ClientCase.Claimant`, `ClientCase.Defendant`, `ClientCase.ThirdParties`, `ClientCase.Representatives`, `ClientCase.CourtSecretaryContacts`, `ClientCase.DocumentsSummary`, `ClientCase.EventHistory`, `ClientCase.Strategy` | Участники, контакты и юридическая стратегия |
| Комментарии и заметки | `TaskJournalComment.Text`, `TaskJournalComment.MetadataJson`, `StickyNote.Text` | Неструктурированный текст может содержать любые специальные категории |
| Файлы | `ClientCaseDocument.OriginalFileName`, `ClientCaseDocument.StoragePath`, `TaskJournalFile.OriginalFileName`, `TaskJournalFile.StoragePath` и сами объекты хранилища | Шифрование метаданных БД не защищает содержимое файлов |
| Финансы | `BankStatementImportRow.AccountName`, `BankStatementImportRow.CounterpartyName`, `BankStatementImportRow.Purpose`, `BankStatementImportRow.PaymentOrderNumber`, `Payment.Description`, `Payment.Notes`, `Payment.SystemNotes` | Назначения платежей и импортированные выписки часто содержат ФИО/номера дел |

### Производные копии, интеграции и аудит

| Семейство | Поля-кандидаты | Риск |
| --- | --- | --- |
| Доставки | `NotificationDelivery.RecipientAddress`, `NotificationDelivery.Subject`, `NotificationDelivery.BodyPreview`, `UserNotification.BodyPreview`, `UserNotification.MetadataJson` | Вторичная копия контакта и фрагмента сообщения |
| Активность | `UserActivityLog.UserEmail`, `UserActivityLog.IpAddress`, `UserActivityLog.UserAgent`, `UserActivityLog.QueryString`, `UserActivityLog.MetadataJson` | Query string и metadata могут случайно сохранить исходные значения |
| Аудит объектов | `ObjectAuditEvent.Description`, `ObjectAuditEvent.MetadataJson` | Before/after и произвольные описания не должны обходить шифрование |
| Импорт | `SpreadsheetImportBatchOperation.BeforeJson`, `SpreadsheetImportBatchOperation.AfterJson`, `SpreadsheetImportBatchOperation.MetadataJson` | Полные снимки строк создают скрытую plaintext-копию |
| Календари | `GoogleCalendarConnection.GoogleEmail`, `GoogleCalendarConnection.AccessTokenProtected`, `GoogleCalendarConnection.RefreshTokenProtected`, `YandexCalendarConnection.YandexEmail`, `YandexCalendarConnection.AccessTokenProtected`, `YandexCalendarConnection.RefreshTokenProtected` | Идентификаторы аккаунтов; OAuth access/refresh token уже хранится в protected-полях |
| Клиентский кабинет | `ClientPortalAccount.DisplayName`, `ClientPortalAccount.Email`, `ClientPortalLoginChallenge.TokenHash`, `ClientPortalSession.TokenHash`, `ClientPortalSession.UserAgent`, `ClientPortalAuditEvent.UserAgent` | Контакты клиента, одноразовые ссылки, сессии и эксплуатационные данные; исходные токены не сохраняются |
| Внешнее согласование | `ExternalWorkOrderApprovalRequest.AccessTokenHash`, `ExternalWorkOrderApprovalRequest.RecipientEmail`, `ExternalWorkOrderApprovalRequest.RecipientName`, `ExternalWorkOrderApprovalRequest.DecisionUserAgent` | Доступ к публичному согласованию и доказательства решения; исходный access token не сохраняется |
| Электронная подпись | `ElectronicSignatureSigner.DisplayName`, `ElectronicSignatureSigner.MaskedContact`, `ElectronicSignatureSigner.ProtectedContact` | Контакт подписанта хранится в protected-виде, а наружу возвращается только маска |
| MAX и мессенджеры | `MaxInboundCorrespondenceMessage.ProtectedText`, `MaxOutboxMessage.ProtectedText`, `MaxRecipientLink.LinkTokenHash`, `MessengerDocumentDelivery.LinkTokenHash`, `MessengerDocumentDelivery.ProtectedLinkToken`, `MessengerRecipientBinding.AddressHash`, `MessengerRecipientBinding.DisplayName`, `MessengerRecipientBinding.MaskedAddress`, `MessengerRecipientBinding.ProtectedAddress` | Тексты, адреса получателей и одноразовые ссылки; исходные значения защищаются Data Protection, для поиска используются хэши и маски |

## 2. Пути чтения и распространения

- API/DTO: карточки клиентов и организаций, профиль сотрудников, карточка дела, журнал задач, финансы, уведомления и административный аудит. Field-level encryption нельзя включать только на записи: проекции, сортировки и DTO должны получать расшифрованное значение лишь после проверки права.
- Поиск: клиентские ФИО, email и телефон; ИНН/КПП/ОГРН; номер дела и стороны; текст задачи/комментариев; назначение платежа. Точный поиск допускает нормализованный keyed blind index. Частичный `contains`, сортировка и уникальность по ciphertext не работают и требуют изменения продукта либо отдельного безопасного индекса.
- Экспорты: CSV, Word, PDF, HTML/печать, ICS, email и шаблоны документов. Каждый экспорт является самостоятельной точкой авторизации и маскирования; расшифровка «потому что запись доступна» недостаточна.
- Уведомления и realtime: email, Telegram, Web Push, SMS, SignalR и внутренний центр уведомлений создают производные payload. В них должен попадать минимальный разрешённый текст, а не полное расшифрованное поле.
- Файлы и внешние хранилища: локальное файловое хранилище, S3-совместимое хранилище и Яндекс.Диск требуют отдельного шифрования объектов/ключей и не защищаются EF-конвертером.
- Резервные копии: SQLite-файлы, PostgreSQL dumps и копии хранилища содержат те же plaintext-колонки и файлы. Старые backup останутся раскрывающими данные после миграции, пока не истечёт утверждённый retention.
- Логи и аудит: application/systemd-журналы, `UserActivityLog`, `ObjectAuditEvent`, журналы импорта и delivery preview должны проходить redaction до форматирования строки.

## 3. Индексы и ограничения поиска

1. До миграции снять фактический список индексов SQLite и PostgreSQL на выбранных колонках.
2. Уникальный или точный поиск переносить на нормализованный blind index с отдельным ключом и контекстом `entity + field`.
3. Не использовать один blind-index key для разных полей и организаций без утверждённой tenant-модели.
4. Не поддерживать `contains`-поиск через детерминированное шифрование, n-gram хэши без threat review или расшифровку полной таблицы.
5. Сначала фильтровать по незашифрованным разрешённым индексам и правам, затем расшифровывать только ограниченную проекцию.

## 4. Threat model

| Угроза | Что раскрывается сейчас | Требуемая граница защиты |
| --- | --- | --- |
| Кража файла SQLite/PostgreSQL dump | Все обычные колонки и audit/import JSON | AEAD field encryption для утверждённых полей; ключи отдельно от backup |
| Кража backup файлового хранилища | Документы и изображения независимо от БД | Шифрование объектов либо storage-side encryption с отдельно управляемым ключом |
| Чтение application/systemd logs | Query string, внешние ошибки, случайные structured values | Централизованный redaction и запрет plaintext на info/error |
| Компрометация обычной учётной записи | Поля в пределах слишком широких ролей/экспортов | Field/document visibility, маскирование и аудит экспорта |
| Компрометация администратора CRM | Почти все расшифрованные данные через приложение | Разделение полномочий, step-up для экспорта и неизменяемый аудит; шифрование БД само по себе не спасает |
| Компрометация VPS вместе с service identity | Приложение и ключ доступны одновременно | KMS policy, короткоживущая выдача data key, network/identity boundary и alert |
| Утечка master key | Возможна массовая расшифровка | Envelope encryption, rotation/revocation, dual control и отдельный DR |
| Потеря master key | Данные необратимо недоступны | Проверенный recovery runbook и восстановление ключа отдельно от БД |
| Подмена ciphertext/перенос между строками | Незаметная порча или подстановка | AEAD и authenticated context с entity id, field id, key version |
| Plaintext в памяти процесса | Доступен при runtime-компрометации/dump | Минимальная проекция, короткая жизнь объектов, отсутствие глобального кэша plaintext |

## 5. Что защищает инфраструктура, а что требует field-level encryption

- TLS защищает передачу, но не dump, backup и чтение диска.
- Disk/LUKS и cloud volume encryption защищают выключенный/снятый диск, но не SQL-доступ работающего сервера и не выгруженный dump.
- PostgreSQL TDE, если будет выбран и поддержан окружением, защищает страницы/backup на уровне СУБД, но не DTO, экспорт, лог и привилегированный SQL в работающей системе.
- Field-level encryption требуется для утверждённых highly sensitive полей, когда нужно снизить ущерб от утечки БД/backup. Оно не заменяет права, маскирование и защиту файлов.

## 6. Открытые решения перед реализацией

- Утвердить фактическую классификацию каждой группы и исключения для публичных реквизитов организаций.
- Выбрать KMS/HSM/Vault и модель service identity для production, CI и восстановления.
- Утвердить срок хранения и порядок удаления старых backup/plaintext.
- Утвердить, какие поиски можно заменить точным поиском, а какие должны остаться без field-level encryption.
- После выбора провести dry run на копии production и измерить размер, latency, backfill и восстановление.

## Воспроизводимая проверка

Команда `npm run audit:encryption-inventory` проверяет наличие перечисленных критических моделей и полей в исходном коде, их маркеров в этом документе и актуальность автоматически найденных кандидатов. Команда `npm run audit:encryption-inventory:update` обновляет `docs/field-encryption-candidates.generated.md`; это не позволяет считать каталог разовой статичной таблицей.
