# Журнал реализации PAY-33…PAY-36 (ветка `claude/admiring-newton-nyddyv`)

Назначение: чтобы агент, проверяющий ветку локально, видел по шагам, **что изменено, зачем и как проверить**.
Один шаг = один коммит. Основная ветка не затрагивалась. Планы-источники: [PAY-33](company-profile-yougile-roadmap-2026-10-09.md),
[PAY-34](accounting-documents-yougile-roadmap-2026-10-09.md), [PAY-36](bank-integrations-yougile-roadmap-2026-10-09.md),
[PAY-35](kudir-yougile-roadmap-2026-10-09.md), [PAY-31](tz-crm-applications-automation-roadmap-2026-10-07.md).

## Как проверять локально

- Бэкенд: `dotnet build backend/PayPlanner.Api.csproj`; тесты — `dotnet test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj --filter "FullyQualifiedName~<Имя>"`.
  В облачном окружении, где писался код, стоял только .NET SDK 10: `global.json` там был локально отключён (`git update-index --skip-worktree global.json`) и тесты шли с `DOTNET_ROLL_FORWARD=Major`. Сам файл в ветке не менялся.
- Фронтенд: `npm ci`, затем `npx vitest run <путь>`, `npx tsc --noEmit -p tsconfig.app.json`, `npx eslint <путь>`.
- Локальный запуск бэкенда — только с `BackgroundServices__Enabled=false` и `Email__Enabled=false` (правило AGENTS.md).

## Шаг 1. Матрица требований PAY-33

- Что: добавлен [company-profile-gap-matrix-2026-10-09.md](company-profile-gap-matrix-2026-10-09.md); пункт 2 плана PAY-33 закрыт.
- Зачем: перед кодом зафиксировать, что в проекте уже есть и чего не хватает. Главная находка — счета и акты всегда берут реквизиты «главной компании», выбора компании в документе нет.
- Код не менялся.

## Шаг 2. Проверка реквизитов и порядок банковских счетов (PAY-33, пп. 5 и 7)

Что изменено:

| Файл | Что и зачем |
| --- | --- |
| `backend/Services/Companies/RussianRequisitesValidator.cs` | Новый. Проверка ИНН (10/12 цифр, контрольные разряды), КПП, ОГРН/ОГРНИП, БИК, формата и контрольного разряда расчётного и корреспондентского счетов. Чистые функции, без БД. |
| `backend/Services/Companies/CompanyBankAccountList.cs` | Новый. Нормализация `LegalEntity.BankAccountsJson`: каждому счёту присваивается стабильный `id`; архивный счёт не может быть основным; среди активных основной ровно один (старое значение типа «Основной» учитывается, если флагов нет); дубликат активного счёта с тем же БИК — ошибка; неизвестные поля сохраняются. Формат/контрольные разряды дают только предупреждения — в базе много импортированных и тестовых счетов. |
| `backend/Controllers/LegalEntitiesController.cs` | Создание и изменение юрлица пропускают `BankAccountsJson` через нормализацию (ошибка → 400 `{message}`). Новый admin-метод `GET api/legal-entities/{id}/requisites-check` возвращает замечания по ИНН/КПП/ОГРН/счетам, ничего не блокируя. |
| `backend/Models/Responses/RequisitesCheckResponse.cs` | Новый DTO ответа проверки. |
| `backend/Services/Invoices/InvoiceDocumentTemplateEngine.cs`, `backend/Services/Acts/ActDocumentTemplateEngine.cs` | При выборе счёта продавца архивные счета пропускаются, основным считается счёт с `isPrimary`, затем по старому признаку «Основной». Поведение для старых данных не меняется. |
| `src/types/index.ts` | В `LegalEntityBankAccount` добавлены `id`, `isPrimary`, `isArchived`. |
| `src/components/LegalEntities/legalEntityBankAccountUtils.ts` | Новый. Чистые функции: один основной счёт, сохранение `id` при редактировании, архивация. |
| `src/components/LegalEntities/LegalEntityFinanceSection.tsx` | Сохранение счёта идёт через `upsertBankAccount` (раньше при правке терялись бы `id`/архив); кнопка «В архив / Из архива», метка «Архив» в таблице. |

Проверки: `RussianRequisitesValidatorTests` (22), `CompanyBankAccountListTests`, четыре новых теста в `LegalEntitiesControllerTests` (нормализация, дубликат, check-requisites), плюс прогон `InvoiceDocumentTemplateTests`/`ActDocument*` — зелёные; `legalEntityBankAccountUtils.test.ts` (5) и все тесты `src/components/LegalEntities` (34) зелёные; `tsc` и `eslint` чисты.

Не сделано на этом шаге: миграции не нужны (формат лежит в существующем JSON-поле); выбор счёта в документе — шаг 9.

## Шаг 3. Проверка формата фирменных изображений (PAY-33, п. 8, часть 1)

Что изменено:

| Файл | Что и зачем |
| --- | --- |
| `backend/Services/Companies/InlineImageDataUrlValidator.cs` | Новый. Логотип, печать и подпись хранятся как data-URL; теперь допускаются только PNG, JPEG, WebP в корректном base64. SVG, GIF, HTML и т.п. отклоняются (риск скриптов и плохой рендер в печатных формах). |
| `backend/Controllers/LegalEntitiesController.cs` (`ValidateInlineAssetPayload`) | Формат проверяется **после** проверок размера, чтобы не менять прежние сообщения о лимитах. Ошибка → 400 с понятным текстом («Логотип компании: …»). |
| `backend/PayPlanner.Api.Tests/CompanyRequisitesTests.cs` | Тесты валидатора. |

Совместимость: интерфейс карточки юрлица всегда перекодирует картинку в PNG/JPEG через canvas (`convertImageToFittingDataUrl`), поэтому штатная загрузка не затронута; отклоняются только запросы в обход интерфейса.
Проверки: `InlineImageDataUrlValidatorTests` и весь `LegalEntitiesController*` — 74 теста зелёные.
Остаётся по п. 8: права на файлы, подписанты и снимок реквизитов — шаги 4–6.

## Шаг 4. Статус профиля компании и филиалы (PAY-33, пп. 5 и 6)

Решения по «допущениям» из [матрицы](company-profile-gap-matrix-2026-10-09.md): филиал не имеет своего ИНН и названия юрлица; у него могут быть только собственные КПП, адрес, банковский счёт и подписант, всё остальное наследуется. Пустое поле = «как у компании».

**База данных (парные миграции):**

| Файл | Что |
| --- | --- |
| `backend/Models/CompanyBranch.cs` | Новая сущность `CompanyBranch` (вид: Branch/Office/Point/Direction; статус Active/Archived; КПП, адрес, `BankAccountId`, `SignerJson`, примечание) и справочники значений `CompanyProfileStatuses`, `CompanyBranchKinds`. |
| `backend/Models/LegalEntity.cs` | Новое поле `ProfileStatus` (Draft/Active/Archived), по умолчанию `Active` — у всех существующих компаний значение `Active`. |
| `backend/Data/PaymentContext.cs` | Таблица `CompanyBranches`, уникальный индекс `(LegalEntityId, Name)`, FK `Restrict`, фильтр запросов как у родителя (иначе EF предупреждает о фильтре и тест `EfWarningDiagnosticsTests` падает). |
| `backend/Migrations/20261009103644_AddCompanyBranches*` | Миграция SQLite (создана `dotnet ef migrations add`). |
| `backend/Migrations/PostgreSql/20261009104024_AddCompanyBranchesPostgres*` | Миграция PostgreSQL. Обе добавляют `LegalEntities.ProfileStatus NOT NULL DEFAULT 'Active'` и таблицу `CompanyBranches`; откат удаляет их. |

**Серверная логика:**

| Файл | Что |
| --- | --- |
| `backend/Controllers/CompanyBranchesController.cs` | `GET/POST api/legal-entities/{companyId}/branches`, `PUT .../{id}`, `GET .../{id}/effective-requisites`. Только администратор. Проверки: название обязательно и уникально внутри компании (в т.ч. при гонке — уникальный индекс → 409), вид и статус из списка, КПП 9 знаков, счёт обязан быть **активным счётом этой же компании** (чужой/архивный → 400), подписант — JSON-объект. В архивную компанию подразделения не добавляются. Удаления нет: подразделение архивируется. |
| `backend/Services/Companies/CompanyRequisitesResolver.cs` | Чистая функция сборки реквизитов документа и тип `CompanyRequisitesSnapshot` (снимок на момент документа, п. 8–9). Заполненное поле филиала побеждает; архивный/чужой счёт даёт возврат к основному счёту компании; для каждого поля сохраняется источник (`company`/`branch`). |
| `backend/Controllers/LegalEntitiesController.cs` | `ProfileStatus` принимается в создании/изменении (по умолчанию `Active`, неизвестное → 400, в ответе приходит). Компанию из списка собственных нельзя перевести в черновик/архив (409). В список собственных компаний (`main-companies/settings`) попадают только активные (409 иначе). |
| `docs/batch-operations-audit-coverage-2026-06-10.md` | Обновлён номер строки `LegalEntitiesController.cs` (1000 → 1010): тест `BatchOperationAuditCoverageTests` сверяет номера строк. **Если снова править этот контроллер выше строки с `ExecuteDeleteAsync`, номер надо обновить.** |

**Интерфейс:**

| Файл | Что |
| --- | --- |
| `src/types/index.ts`, `src/services/api.ts` | Типы `CompanyBranch*`, `CompanyProfileStatus`, методы `getCompanyBranches`, `createCompanyBranch`, `updateCompanyBranch`. |
| `src/components/LegalEntities/CompanyBranchesSection.tsx` | Новая компактная секция «Филиалы, офисы и точки» с таблицей, редактором, архивом. |
| `src/components/LegalEntities/LegalEntityModal.tsx` | Для собственных компаний: выбор состояния профиля и секция подразделений. |

**Тесты:** `CompanyRequisitesResolverTests`, `CompanyBranchesControllerTests` (17), три теста статуса в `LegalEntitiesControllerTests`; фронтенд `CompanyBranchesSection.test.tsx` (6). 

## Шаг 5. Выбор компании и подразделения-продавца в счёте и акте (PAY-33, п. 9)

Что и зачем: раньше счёт и акт всегда брали реквизиты единственной «главной» компании (`LoadMainCompanyAsync` → `BuildSeller`). Теперь у документа можно выбрать собственную компанию и подразделение, а реквизиты при выдаче файла **фиксируются** в документе — последующая правка профиля уже выданный документ не меняет.

**База данных (парные миграции, только новые nullable-колонки, без внешних ключей — на SQLite они пересобирали бы крупные таблицы):**

| Файл | Что |
| --- | --- |
| `backend/Models/Payment.cs`, `backend/Models/Act.cs` | Поля `SellerCompanyId`, `SellerBranchId`, `SellerSnapshotJson`; оба класса реализуют `IDocumentSeller`. |
| `backend/Data/PaymentContext.cs` | Длина снимка 16 000, индексы по `SellerCompanyId`/`SellerBranchId`. |
| `backend/Migrations/20261009120639_AddDocumentSellerSelection*`, `backend/Migrations/PostgreSql/20261009121033_AddDocumentSellerSelectionPostgres*` | Добавляют три колонки и два индекса в `Payments` и `Acts`. |
| `backend/PayPlanner.Api.Tests/CancelledInvoiceBillingMigrationTests.cs` | Тест апгрейда сеял платежи моделью EF на старой схеме. Теперь он временно добавляет новые колонки перед вставкой и удаляет их, чтобы реальная миграция создала их сама. |

**Серверная логика:**

| Файл | Что |
| --- | --- |
| `backend/Services/Companies/DocumentSellerService.cs` | `SellerLegalEntityComposer` собирает «виртуальное юрлицо» (компания + переопределения подразделения: КПП, адрес, подписант, выбранный счёт), поэтому генераторы документов не переписывались; снимок хранит только текстовые реквизиты (печать и подпись берутся у компании). `DocumentSellerService`: проверка выбора (компания собственная, активная; подразделение её и активное), выбор продавца для печати, фиксация снимка. Зарегистрирован в `Program.cs`. |
| `backend/Controllers/InvoicesController.cs`, `ActsController.cs` | Новые методы `PUT api/invoices/{id}/seller`, `PUT api/acts/{id}/seller` (право «изменение»; 400 при неверном выборе, 409 если реквизиты уже зафиксированы; для счёта применяется ко всем платежам этого счёта; расходный счёт — отказ, продавец там контрагент). Печать/выдача файла и отправка (Word/PDF, Яндекс.Диск, e-mail) фиксируют реквизиты, **просмотр HTML — нет**. В DTO добавлены `sellerCompanyId`, `sellerBranchId`, `sellerFrozen`. |
| `backend/Controllers/OwnCompaniesController.cs` | `GET api/own-companies/options` — активные собственные компании с активными подразделениями (только названия и id) для выпадающих списков; доступен любому авторизованному. |

**Интерфейс:**

| Файл | Что |
| --- | --- |
| `src/components/Documents/DocumentSellerPicker.tsx` | Компактный выбор компании и подразделения; не показывается, если выбирать не из чего; после фиксации — только текст. |
| `src/components/Acts/ActDetailPage.tsx`, `src/components/Accounts/InvoiceModal.tsx` | Подключён выбор продавца (в счёте — только для доходных, в режиме редактирования). |
| `src/services/api.ts`, `src/types/index.ts` | Методы и типы. |

**Важно для проверяющего:**
- Документы без явного выбора по-прежнему берут компанию по умолчанию; их реквизиты фиксируются при ближайшей выдаче файла.
- Договоры (`ContractsController`) пока используют прежнюю «главную компанию» — отдельный шаг при необходимости.
- Редактирование `InvoicesController.cs` ведётся с сохранением смешанных окончаний строк (файл частично CRLF): не пересохранять его утилитами, которые меняют окончания.

**Тесты:** `SellerLegalEntityComposerTests`, `DocumentSellerServiceTests`, `ActSellerEndpointTests`, `OwnCompaniesControllerTests`, два теста в `InvoiceDocumentTemplateTests` (выбор продавца, фиксация, чужая компания); фронтенд `DocumentSellerPicker.test.tsx` (5). Запускались также все тесты счетов, актов и документов (424 зелёных) и миграционные/моделей на реальном PostgreSQL (`PAYPLANNER_TEST_POSTGRES`).

## Шаг 6. Выбор компании и подразделения в договоре (PAY-33, п. 9, продолжение)

То же, что в шаге 5, для договоров: файл договора и соглашение о расторжении раньше всегда брали «главную» компанию.

| Файл | Что |
| --- | --- |
| `backend/Models/Contract.cs`, `backend/Data/PaymentContext.cs`, `backend/Models/Responses/ContractDto.cs` | Поля `SellerCompanyId`, `SellerBranchId`, `SellerSnapshotJson`, индексы, поля `sellerCompanyId/sellerBranchId/sellerFrozen` в ответе. |
| `backend/Migrations/20261009125201_AddContractSellerSelection*`, `backend/Migrations/PostgreSql/20261009125621_AddContractSellerSelectionPostgres*` | Парные миграции (три nullable-колонки и два индекса). |
| `backend/Controllers/ContractsController.cs` | `PUT api/contracts/{id}/seller` (право «изменение»); четыре места выдачи (документ, соглашение о расторжении, Яндекс.Диск, e-mail) используют `ResolveContractSellerAsync`; просмотр HTML реквизиты не фиксирует. |
| `src/components/Contracts/ContractDetailPage.tsx`, `src/components/Documents/DocumentSellerPicker.tsx` | Выбор продавца в карточке договора (тот же компонент, вид `contract`); тестовые моки дополнены `getOwnCompanyOptions` и правами `contracts`. |
| `backend/AppReleases/releases.json` | Запись 1.14.58 дополнена договором. |

Тесты: `ContractsControllerTests.SetSeller_SavesChoice_FreezesRequisitesOnExport_AndRejectsLaterChanges`; фронтенд — существующие тесты договоров (12) с обновлёнными моками.
