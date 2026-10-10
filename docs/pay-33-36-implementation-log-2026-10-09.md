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
| `backend/AppReleases/releases.json` | Запись 1.14.59 дополнена договором (номер 1.14.58 занят выпуском main). |

Тесты: `ContractsControllerTests.SetSeller_SavesChoice_FreezesRequisitesOnExport_AndRejectsLaterChanges`; фронтенд — существующие тесты договоров (12) с обновлёнными моками.

## Шаг 7. Общая основа банковских подключений (PAY-36, пп. 2, 4, 5–9 частично)

Реальных адаптеров Точки и Сбера нет: нет доступа к документации/ключам/sandbox. Подробно — [bank-integrations-api-verification-2026-10-09.md](bank-integrations-api-verification-2026-10-09.md).

| Файл | Что |
| --- | --- |
| `backend/Models/BankConnection.cs` | `BankConnection`, `BankConnectionAccount`, `BankStatementOperation` и справочники статусов. |
| `backend/Services/Banking/IBankProvider.cs` | Контракт адаптера банка (счета, страница выписки), исключение `BankAuthorizationException`. |
| `backend/Services/Banking/BankSyncService.cs` | Общая логика: защищённое хранение токенов, идемпотентная загрузка счетов, выписок без дублей и потерь, изоляция ошибок. Зарегистрирован в `Program.cs`. |
| `backend/Data/PaymentContext.cs`, миграции `20261009132119_AddBankConnections*`, `.../PostgreSql/20261009132451_AddBankConnectionsPostgres*` | Таблицы и уникальные индексы. |
| `backend/PayPlanner.Api.Tests/BankSyncServiceTests.cs` | 9 тестов на поддельном банке: токены не в открытом виде, идемпотентность, чужой ИНН, отказ авторизации, все страницы, повтор и перекрытие, незавершённые операции, сбой посреди загрузки, изоляция счетов. |

Пока нет: HTTP-методов и интерфейса подключения, фонового расписания, разнесения операций в платежи.

## Итоговая проверка ветки и остаток работ

**Фронтенд (полный гейт):** `npm run lint`, `npx tsc --noEmit`, `npm run audit:encoding` (нужен `global.json` в корне — локально он был временно возвращён), `npm run test:unit` — 237 файлов / 2480 тестов зелёные, `npm run build` успешен.

**Бэкенд:** полный прогон в облачном окружении не показателен: десятки тестов хранилища/браузера/отдельных процессов (`StorageStageCapacityServiceTests`, `InternalChatControllerTests`, `ManagedCaseDocument*`, `CaseDocumentRetirementBrowserTests` и др.) падают там по окружению (нет нужных процессов/браузера/путей Windows) и не связаны с изменениями; сравнения с базовой веткой для них не делалось. Зелёными подтверждены целевые наборы: все тесты счетов, актов, договоров, документов, юрлиц (424+), аудит/права/маршруты (601), миграции и модель EF на реальном PostgreSQL 16 (115, чистая база и обновление), банковская основа (9). **Перед выпуском на нормальной машине нужен полный прогон backend и полный CI.**

**Что осталось (не сделано, причины):**
- PAY-33: п. 3–4 — решения владельца; п. 5 — поведение DaData при недоступности; п. 8 — права на файлы; п. 10 — приёмка.
- PAY-34, PAY-35, PAY-31: код не начат. Word-ТЗ лежат на `D:/` у владельца и в репозитории отсутствуют; ключевые правила (налоги НДС/КУДиР, нумерация, доступы юристов) отмечены в планах как `[decision]`. Придумывать их нельзя.
- PAY-36: реальные адаптеры Точки/Сбера, OAuth-интерфейс, расписание, разнесение — нужны доступы и сверка API.

## Шаг 8. Исправления по независимому код-ревью ветки

Найдено и исправлено (все с тестами):

| Проблема | Исправление |
| --- | --- |
| Снимок реквизитов мог превысить колонку 16 000 при длинном списке счетов → ошибка при выдаче документа | В снимок и виртуальное юрлицо попадает один счёт (выбранный или основной); бухгалтер не входит; слишком большой снимок не фиксируется, а не падает. |
| Фиксация снимка только у одного платежа счёта | Снимок и выбор копируются всем платежам того же счёта; сохраняется и подразделение. |
| Сохранение компании падало из-за старых дублей счетов | Дубль, уже бывший в сохранённом списке, — предупреждение; ошибка только для нового дубля. |
| Архивация счёта, выбранного у подразделения, молча меняла реквизиты | Сохранение компании отклоняется (409) с перечнем подразделений. |
| Устаревший выбор продавца (компанию убрали/архивировали) фиксировался в документе | При выдаче выбор проверяется; устаревший не применяется, берётся компания по умолчанию. |
| Банк: токены не обновлялись, строки банка превышали длину колонок, параллельный запуск падал, выписка целиком копилась в памяти, пустой счёт перечитывал всю историю | Контракт получил `RefreshTokensAsync`; токены обновляются до вызова и сразу сохраняются; идентификаторы длиннее колонок пропускаются; конфликт уникальности перехватывается; страницы сохраняются по одной; начало периода — после последней успешной загрузки/самой ранней незавершённой операции. |

Известное ограничение: если банк выдаёт незавершённой операции временный id, а после проведения — другой, прежняя запись останется незавершённой. Правило сопоставления зависит от документации конкретного банка и добавляется вместе с адаптером.

## Шаг 9. Проверки уровня CI

- `dotnet format --verify-no-changes` — чисто (в тесте миграции убраны предупреждения EF1002).
- `npm run audit:surface` была устаревшей из-за новых контроллеров — `docs/system-surface-inventory.generated.md` перегенерирован (`npm run audit:surface:update`), проверка проходит; `audit:dates:test`, `audit:encoding`, `npm audit --audit-level=moderate` (0 уязвимостей) проходят.
- `npm run test:integration`: 305 из 306; единственный отказ `vite.restored-browser.integration.test.ts` (родной браузер) воспроизводится на чистом `origin/main` — не связан с ветвью.
- На чистом `origin/main` те же классы хранилища/чата/подписи/управляемых документов дают 522 отказа из 1105 — подтверждено, что массовые отказы полного backend в облачном окружении существовали до ветви.

## Шаг 10. Ставка НДС по позициям счёта и акта (PAY-34, пп. 4–5, часть)

Решения владельца (9 октября): ставка задаётся у каждой позиции; акт без счёта по-прежнему допустим.

| Файл | Что |
| --- | --- |
| `backend/Services/Tax/VatCalculator.cs` | Коды ставок (`none`, 0, 5, 7, 10, 20, 22), расчёт НДС «в том числе»: `сумма × ставка / (100 + ставка)`, округление до копеек от нуля **построчно**, итог — сумма строк; «Без НДС» и ставка 0% различаются; атрибут проверки `VatRateCode`. |
| `backend/Models/InvoiceLineItem.cs`, `ActLineItem.cs`, `Data/PaymentContext.cs`, миграции `20261009182038_AddLineItemVatRate*` и `.../PostgreSql/20261009182414_AddLineItemVatRatePostgres*` | Колонка `VatRate` (nullable, 8 символов). Существующие строки остаются без ставки. |
| `backend/Models/Requests/*`, `Models/Responses/*`, `InvoicesController`, `ActsController`, `AccountsController`, `InvoiceLineItemStorage` | Ставка принимается (неизвестная → 400), нормализуется («22%» → «22»), сохраняется, возвращается; частичный/полный акт по счёту наследует ставку позиции счёта; корректирующий акт копирует её. |
| `InvoiceDocumentTemplateEngine`, `ActDocumentTemplateEngine` | Если хотя бы у одной позиции есть ставка — НДС считается построчно (токены `vat` позиции, итог «В том числе НДС» или «Без налога (НДС)»); если ставок нет — прежнее поведение по признаку продавца (без регрессии). |
| `src/components/Documents/VatRateSelect.tsx`, `InvoiceModal.tsx`, `ActModal.tsx`, типы | Столбец «НДС» в позициях счёта и ручного акта. |
| `backend/AppReleases/releases.json` | Запись 1.14.60. |

Не реализовано: режимы «сверху» и «без налога» (меняют сумму), даты действия ставок, авансы/возвраты, обязательные формы (СФ/УПД) — ждут бухгалтерской модели. Тесты: `VatCalculatorTests` (27), `LineItemVatRenderingTests` (8).

## Шаг 11. Банки по ТЗ v1.1: статусы счетов, настройки, журнал, выписки по всем счетам (PAY-36)

Источник: [ТЗ v1.1](yougile-sources/2026-10-10/attachments/PAY-36-1791504514347-Specifikaciya_Bankovskie_integracii_Tochka_Sber_v2.txt). Реальные адаптеры Точки и Сбера по-прежнему не написаны (нет доступа и sandbox).

| Файл | Что |
| --- | --- |
| `backend/Models/BankConnection.cs`, миграции `20261009202438_ExtendBankConnections*`, `.../PostgreSql/20261009202808_ExtendBankConnectionsPostgres*` | Статусы счёта `Pending`, `NotFoundInBank`; поля `IsDefault`, `StatusChangedAt`, `RefreshTokenExpiresAt`; таблица журнала `BankSyncLogs`; `BankConnectionStatuses.Describe` (за 14 дней до окончания токена — «Токен скоро истечёт»). |
| `backend/Services/Banking/BankSyncService.cs` | Счёт, пропавший из ответа банка, → «Не найден в банке» (не удаляется; пустой ответ — сбой, статусы не меняются); закрытие снимает «по умолчанию»; `UpdateAccountSettingsAsync` (один счёт по умолчанию на компанию — только открытый и не скрытый); загрузка «по всем счетам» только по Открытым/Ограниченным с включёнными выписками; записи журнала. |
| `backend/Services/Banking/TochkaAccountStatus.cs` | Сопоставление статусов Точки (Enabled/Disabled/Deleted/ProForma/Pending) и разбор `accountId` «номер/БИК». |
| `backend/Controllers/BankConnectionsController.cs` | `GET api/bank-connections`, `POST {id}/sync-accounts`, `POST {id}/sync-statements`, `POST accounts/{id}/sync-statement`, `PATCH accounts/{id}`, `GET accounts/{id}/operations`, `GET {id}/logs`. **Только администратор** — роли руководителя/бухгалтера ждут настройки прав PAY-18. Токены не возвращаются. Период ≤ 366 дней, страницы ≤ 200. |
| `src/components/LegalEntities/CompanyBankConnectionsSection.tsx`, `api.ts`, `types` | Блок «Банковские подключения» в карточке собственной компании (показывается, только если подключение есть): статусы, чекбоксы, «Обновить счета», «Загрузить выписки по всем счетам» с итогом по каждому счёту, журнал. |
| Тесты | `BankSyncServiceTests` (+6), `BankConnectionsControllerTests` (5), `TochkaAccountStatusTests`, фронтенд `CompanyBankConnectionsSection.test.tsx` (4). |

Пользовательской записи «Что нового» нет: подключение банка создать пока нельзя, блок на экране не появится.

## Шаг 12. Регистр книги доходов и расходов (PAY-35, основа)

Источник: [ТЗ КУДиР](yougile-sources/2026-10-10/attachments/) и план `kudir-yougile-roadmap-2026-10-09.md`. Налоговые правила (форма книги, признание расходов, налоговая база по режиму) не придуманы — их определяет бухгалтерия.

| Файл | Что |
| --- | --- |
| `backend/Models/TaxRegisterEntry.cs`, `Data/PaymentContext.cs`, миграции `20261009210141_AddTaxRegister*` и `.../PostgreSql/20261009210536_AddTaxRegisterPostgres*` | Таблицы `TaxRegisterEntries` (статусы Draft/Approved/Archived, типы Income/Expense/Correction, сумма положительная + знак, дата денег `OperationDate` отдельно от `PostingAt`), `TaxRegisterEvents` (история), `TaxRegisterYearCloses` (закрытие года + SHA-256). Уникальный индекс по `BankStatementOperationId`: одна операция выписки — одна запись. Колонки без внешних ключей. |
| `backend/Services/Tax/TaxRegisterService.cs` | Создание/правка черновика (для записи из выписки дата, сумма и направление берутся из выписки; непроведённая операция отклоняется), утверждение (нужна ссылка на первичный документ, год не закрыт), архив (только черновик), корректировка сторно (причина обязательна, один раз на запись, корректировку нельзя исправить), закрытие года (нет черновиков, повторно нельзя), книга за год (только утверждённые, итоги со знаком), CSV с защитой от формул Excel. |
| `backend/Controllers/TaxRegisterController.cs`, `Program.cs` | `api/tax-register/*`: `entries` (список с фильтрами и пагинацией), создание/правка, `approve`, `archive`, `correct`, `events`, `book`, `book/export.csv`, `close-year`. **Только администратор** до настройки прав PAY-18. |
| `src/components/LegalEntities/CompanyTaxRegisterSection.tsx`, `api.ts`, `types`, `LegalEntityModal.tsx` | Блок «Книга доходов и расходов» в карточке собственной компании (виден, только если сервер отдал данные, то есть администратору): итоги, форма черновика, утверждение/архив, «Исправить», «Закрыть год», «Выгрузить CSV». |
| `backend/AppReleases/releases.json` | Запись 1.14.62. |

Не реализовано: PDF/Excel/XML, нумерация по году, журналы по делу, связь с делом и статьёй, исторический dry-run, права бухгалтера, закрытие года с правами руководителя. Тесты: `TaxRegisterServiceTests` (7), фронтенд `CompanyTaxRegisterSection.test.tsx` (5).

### Шаг 12а. Защита корректировок и журнал регистра (PAY-35)

| Файл | Что |
| --- | --- |
| миграции `20261010031400_TaxRegisterUniqueCorrection*`, `.../PostgreSql/20261010031710_TaxRegisterUniqueCorrectionPostgres*` | Индекс `IX_TaxRegisterEntries_CorrectsEntryId` стал уникальным: две параллельные корректировки одной записи не пройдут на уровне БД; сервис превращает отказ в 409. |
| `TaxRegisterService.GetJournalAsync`, `GET api/tax-register/journal` | Журнал событий компании (создание, утверждение, корректировка, архив) с фильтрами по периоду и действию, пагинацией (≤ 200) и подписанной суммой. |
| Тесты | `TaxRegisterServiceTests` (+2: журнал и отказ БД на вторую корректировку). Миграции проверены на PostgreSQL. |

### Шаг 12б. Выгрузка книги в Excel (PAY-35)

`TaxRegisterXlsxWriter` собирает `.xlsx` без новых зависимостей (один лист, текст — inline-строки, управляющие символы удаляются), `GET api/tax-register/book/export.xlsx`, кнопка «Выгрузить Excel» в блоке книги. Это рабочая таблица, не официальная форма. Тесты: чтение файла через ExcelDataReader, фронтенд-тест кнопки.

## Шаг 13. Заявки воронки продаж — серверная основа (PAY-31)

Источник: [полное ТЗ](yougile-sources/2026-10-10/attachments/PAY-31-specification-full-text.txt). Решения владельца: «Оплачено» = фактический платёж в CRM; юристы заявок не видят.

| Файл | Что |
| --- | --- |
| `backend/Models/SalesRequest.cs`, миграции `20261010061819_AddSalesRequests*` и `.../PostgreSql/20261010062233_AddSalesRequestsPostgres*` | `SalesRequest`, `SalesRequestStatus` (финал успех/провал), `SalesRequestStatusTaskRule` («при входе в статус создать задачу»), `SalesRequestEvent`; `TaskJournalItem.SalesRequestId`. Клиент и дело — ссылки без внешних ключей. |
| `RolePermissionCatalog`, `RolePermissionsDto`, `RolePermissionsController` | Новый раздел прав `requests` с областью «свои/все»; у всех ролей, кроме администратора, по умолчанию закрыт — юристы заявок не видят. |
| `Extensions/DataVisibilityQueryExtensions.cs` | `ApplySalesRequestVisibility`, подключён к матрице прав. |
| `Services/Requests/SalesRequestService.cs` | Стандартные 10 статусов и правила задач из ТЗ (исполнитель — ответственный по заявке, пока админ не задаст другого), создание с поиском/созданием клиента и блокировкой дубля по телефону, привязка к делу, переходы с оптимистичной блокировкой, закрытие автозадач прежнего статуса, блок финала при открытых задачах и принудительное закрытие, успех только при платеже клиента с поступившей оплатой, ручные задачи, отчёт. |
| `Controllers/SalesRequestsController.cs` | `api/requests`: `statuses`, список, карточка, `clients/search`, создание, правка, `transition`, `tasks`, `report`. |
| Тесты | `SalesRequestTests` (9), матрица прав `requests`. |

Не реализовано: интерфейс (раздел заявок, права в редакторе ролей), автосоздание дела при оплате, генерация документов, SLA и конструктор триггеров, уведомления, кнопки в карточках задач, перенос старых обращений.

### Шаг 13а. Раздел «Заявки» в интерфейсе (PAY-31)

| Файл | Что |
| --- | --- |
| `src/components/Requests/SalesRequestsPage.tsx` | Список с фильтрами (статус, источник, поиск, закрытые), создание (поиск клиента, «+ Добавить нового», выбор дела или «Новое обращение»), карточка: переходы по статусам, номер платежа для «Оплачено», подтверждение принудительного закрытия задач, ручная постановка задачи, история, отчёт за месяц. |
| `src/types/permissions.ts`, `services/permissionsService.ts`, `RolePermissionsModal.tsx`, `appRouting.ts`, `types/tabs.ts`, `Navigation.tsx`, `App.tsx` | Раздел прав «Заявки» (в редакторе ролей, область «свои/все»), вкладка и пункт меню; у ролей без права раздел скрыт. |
| `backend/AppReleases/releases.json` | Запись 1.14.63. |

Тесты: `SalesRequestsPage.test.tsx` (5); полный `test:unit` — 2502 зелёных, lint, tsc и сборка чистые. Принудительное закрытие распознаётся по тексту ответа сервера («незакрытые задачи»).
