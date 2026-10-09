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
