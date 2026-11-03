# Объединение контактов: фактическая карта ссылок

Срез 4 ноября 2026. Получен из обеих работающих EF-моделей, без чтения реальных записей. SQLite и PostgreSQL дают одинаковые **40 связей: Client17, LegalEntity21, ExternalContact2**. Использован тот же отбор principal/Id/single FK/table, что и `ContactsController.GetMergeReferences`; это карта существующего алгоритма, не разрешение переноса всех строк. Две проверки модели не открывают соединение с PostgreSQL и не являются native DB-тестами. 1304 исходника Models/Data/Migrations неизменны относительно76ffc66a.

**Дополнение — история подписи и доставки:** [48проверок исходного профиля+4portable filesystem](company-reference-ownership-checkpoint-2026-11-01.md#объединение--сохранность-подписи-и-доставки). Все scalar поля6моделей, кроме canonical ClientId, ciphertext decrypt и source-файлы/manifest сохранены; explicit messenger renewal остаётся отдельным действием. Это не48на конечнойtestDLL, не real certificate/provider verification и не downstream permission policy. Нет application bug/fix в данной границе.


**Дополнение 4 ноября — права основной собственной компании:** [адресный пакет85/85](company-reference-ownership-checkpoint-2026-11-01.md#объединение--права-основной-собственной-компании) закрывает обход CompanyProfileAccess через merge: CanView/CanEdit и employee assignment для primary из OwnCompanyRegistry; registry pinned для обоих действий. Это не новое правило финансовых переносов и не доказательство печати выданных документов после архива. Фактическая карта40связей ниже неизменна.


В текущем пакете сохранность банковской и собственной компании идентичности проверена отказом автоматического архивирования/замены полной организации: оба направления legal+legal/external+legal, preview/execute и обе БД. Снятое подтверждение, отключённое подключение, архивный филиал, неактивный счёт, отменённый ордер и прежняя сверка/откаченный импорт сохраняют владельца. История на неизменяемой основной карточке остаётся без переназначения и не блокирует операцию. Проверка добавляет один ограниченный PK/EXISTS-запрос, не загружает реквизиты/токены и не блокирует все таблицы. Она дополняет прежний запрет для текущей собственной компании, указанной в настройках/правах.

Вне FK-карты находятся BankConnection.LegalEntityId и CompanyBrandImageVersion.LegalEntityId. Первый учитывается новым защитным условием. Второй сознательно не переносится: версия изображения должна пережить архивирование профиля; печать и допуск к ранее выпущенному документу после объединения ещё требуют отдельной квалификации. Не делать вывод, что все неизменяемые документы уже проверены.

Для ClientPassport реальные AEAD ciphertext всех7 полей и2blind indexes остались точными после двух направлений переноса владельца на SQLite/PostgreSQL. Decrypt с прежним passport ID прошёл. Синтетические ключи были только в памяти, реальные хранилища/ключи не использовались. Это доказательство одной модели AAD, а не всех шифрованных получателей/подписей.

| Principal | Модель и поле | Таблица и столбец | Область | Текущий статус |
| --- | --- | --- | --- | --- |
| Client | `Act.ClientId` | `Acts.ClientId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| Client | `AddressRecord.ClientId` | `AddressRecords.ClientId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| Client | `CashOrder.ClientId` | `CashOrders.ClientId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| Client | `ClientBalanceTransaction.ClientId` | `ClientBalanceTransactions.ClientId` | Балансовый журнал | Открыты баланс, журнал и финансовые инварианты |
| Client | `ClientCaseRelatedParty.ClientId` | `ClientCaseRelatedParties.ClientId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| Client | `ClientCase.ClientId` | `ClientCases.ClientId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| Client | `ClientContract.ClientId` | `ClientContracts.ClientId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| Client | `ClientPassport.ClientId` | `ClientPassports.ClientId` | Паспорт: неизменный ID и AAD | 4 проверки ciphertext/index/decrypt прошли; общие права ещё открыты |
| Client | `ClientPortalAccount.ClientId` | `ClientPortalAccounts.ClientId` | Доступ в клиентский портал | Прежний отказ для дубля сохранён |
| Client | `ElectronicSignatureEnvelope.ClientId` | `ElectronicSignatureEnvelopes.ClientId` | Подпись и доставка | Scalar/file/decrypt/replay/rollback проверены; права/встречные писатели открыты |
| Client | `ExternalContact.LinkedClientId` | `ExternalContacts.LinkedClientId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| Client | `MessengerDocumentDelivery.ClientId` | `MessengerDocumentDeliveries.ClientId` | Подпись и доставка | Scalar/file/decrypt/replay/rollback проверены; права/встречные писатели открыты |
| Client | `MessengerRecipientBinding.ClientId` | `MessengerRecipientBindings.ClientId` | Подпись и доставка | Scalar/file/decrypt/replay/rollback проверены; права/встречные писатели открыты |
| Client | `Payment.ClientId` | `Payments.ClientId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| Client | `Payment.ExpenseClientId` | `Payments.ExpenseClientId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| Client | `PowerOfAttorney.ClientId` | `PowersOfAttorney.ClientId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| Client | `TaskJournalItem.ClientId` | `TaskJournalItems.ClientId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| ExternalContact | `ClientCaseRelatedParty.ExternalContactId` | `ClientCaseRelatedParties.ExternalContactId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| ExternalContact | `ExternalContact.ParentContactId` | `ExternalContacts.ParentContactId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| LegalEntity | `Act.LegalEntityId` | `Acts.LegalEntityId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| LegalEntity | `AddressRecord.LegalEntityId` | `AddressRecords.LegalEntityId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| LegalEntity | `BankStatementImportBatch.CompanyId` | `BankStatementImportBatches.CompanyId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `CashInventory.CompanyId` | `CashInventories.CompanyId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `CashOrder.CompanyId` | `CashOrders.CompanyId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `ClientBalanceTransaction.LegalEntityId` | `ClientBalanceTransactions.LegalEntityId` | Балансовый журнал | Открыты баланс, журнал и финансовые инварианты |
| LegalEntity | `ClientCaseRelatedParty.LegalEntityId` | `ClientCaseRelatedParties.LegalEntityId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| LegalEntity | `ClientCase.LegalEntityId` | `ClientCases.LegalEntityId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| LegalEntity | `ClientPortalAccount.LegalEntityId` | `ClientPortalAccounts.LegalEntityId` | Доступ в клиентский портал | Прежний отказ для дубля сохранён |
| LegalEntity | `Client.LegalEntityId` | `Clients.LegalEntityId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| LegalEntity | `CompanyBankAccountBinding.LegalEntityId` | `CompanyBankAccountBindings.LegalEntityId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `CompanyBranch.LegalEntityId` | `CompanyBranches.LegalEntityId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `ExternalContact.LegalEntityId` | `ExternalContacts.LegalEntityId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| LegalEntity | `ExternalContact.LinkedLegalEntityId` | `ExternalContacts.LinkedLegalEntityId` | Профили и зависимые записи | Общие источники защищены; исторические/дочерние записи ещё открыты |
| LegalEntity | `LegalEntityContract.LegalEntityId` | `LegalEntityContracts.LegalEntityId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| LegalEntity | `MoneyAccount.CompanyId` | `MoneyAccounts.CompanyId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `MoneyBalanceReconciliationResult.CompanyId` | `MoneyBalanceReconciliationResults.CompanyId` | Идентичность собственной компании | Отказ переноса владельца в этом пакете |
| LegalEntity | `Payment.ExpenseLegalEntityId` | `Payments.ExpenseLegalEntityId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| LegalEntity | `Payment.LegalEntityId` | `Payments.LegalEntityId` | Финансовые связи контрагента | Решение прав и финансовая конкуренция открыты |
| LegalEntity | `TaskJournalItem.LegalEntityId` | `TaskJournalItems.LegalEntityId` | Дела и задачи | Права дочерних записей и встречные писатели открыты |
| LegalEntity | `UserCompanyAccess.LegalEntityId` | `UserCompanyAccesses.LegalEntityId` | Права собственной компании | Прежний отказ объединения сохранён |

Перед общим выпуском остаются: решение пользователя о правах переноса обычных счетов/платежей/дел; ledger/балансы и повтор операций; подписанные/отправленные документы и получатели; исторические aliases/дочерние scopes; встречные создатели ссылок, registry/import/restore. Отдельно сверить права изменения реквизитов основной собственной компании: текущие положительные тесты доказывают только неизменность её владельцев и истории. Существующего типа FK недостаточно для выбора разрешённого действия.

Сверка writers: прямой production `BankConnections.Add` в controllers/services не найден; имеющиеся импорт/восстановление и будущий OAuth-создатель остаются предметом отдельного аудита, это не доказательство полного отсутствия писателей. Bind/Revoke используют company lock в существующем сервисе; произвольные SQL-вставки не считаются проверенным прикладным сценарием.

Доказательства: `D:/PayPlannerDevelopment/20261104-merge-bank-history/evidence/`. **107/107 составно:**105 на конечном API/test DLL и2 сохранённых model-check; **51 native PostgreSQL**, не53. Старый контроль82=2model passed/80 real safety failures;40 разрешённых preview и40 фактических архивирований. Это не full gate/CI, не production-приёмка и не разрешение менять реальные истории. Полный обязательный CI один раз перед общим выпуском, включая ordinary migration rebuild после прежнего passport JSON change.

Общий роадмап **9/167 (5.4%)**, CMP **0/6**, SEC **0/4**; эта карта не закрывает объединение целиком.
