# Локальная среда Claude Code — 12 октября 2026

Это инструкция подготовки, а не реализация пакетов. Ветка `claude/practical-bardeen-pglcf3`, исходная ревизия `ee6eef4a`. Код продукта не менялся. Данные создаются заново; боевую БД и макеты как источник клиентских записей не использовать. Итоги команд и ограничения — в [отчёте](codex-preparation-report-2026-10-12.md).

## Версии и предварительные условия

Проверено на Windows / PowerShell 7, Node **22.14.0**, npm **10.9.2**, .NET SDK **9.0.100** (версия из `global.json`, rollForward `latestFeature`). Установленный дополнительно системный SDK 9.0.318 не подменяет требуемую версию. Локальная PostgreSQL **17.2** нужна части backend-тестов; приложение в этой инструкции использует SQLite. `npm ci` устанавливает зависимости строго из lock-файла, в проверенном запуске Vitest 4.1.11 и Vite 8.0.16.

```powershell
git switch claude/practical-bardeen-pglcf3
git fetch origin claude/practical-bardeen-pglcf3
git pull --ff-only
node --version
npm --version
```

Выполнять в корне checkout. Все пути ниже вычисляются для текущего checkout; разные checkout имеют разные базы и пароли. Не запускать второй backend на тот же порт.

## Точная версия SDK с нуля

Официальный [dotnet-install](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script) устанавливает SDK в локальный каталог, не меняя глобальную установку.

```powershell
$localSdkRoot = Join-Path (Get-Location).Path '.codex-build/dotnet-9.0.100'
New-Item -ItemType Directory -Path $localSdkRoot -Force | Out-Null
$sdkInstaller = Join-Path $localSdkRoot 'dotnet-install.ps1'
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $sdkInstaller
& $sdkInstaller -Version 9.0.100 -InstallDir $localSdkRoot -NoPath
$dotnetTool = Join-Path $localSdkRoot 'dotnet.exe'
$env:DOTNET_ROOT = $localSdkRoot
$env:Path = $localSdkRoot + ';' + $env:Path
& $dotnetTool --version
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnetTool build backend/PayPlanner.Api.csproj -c Release -m:1 /nodeReuse:false
& $dotnetTool build scripts/claude-preparation/Seed/Seed.csproj -c Release -m:1 /nodeReuse:false
& $dotnetTool scripts/claude-preparation/Seed/bin/Release/net9.0/Seed.dll .
```

В текущем выполнении SDK установлен вне Git: `D:/PayPlannerPreparation-20261012/dotnet/dotnet.exe`; команды выше показывают переносимый вариант. Сборки запускают имеющийся проект SQLite-миграций. Seed использует **существующий** `UsePayPlannerSqliteMigrations()`, как основной backend, а не стандартный генератор SQLite и не `EnsureCreated` вместо миграций.

В новых терминалах также задавать DOTNET_ROOT/PATH на локальный SDK: отдельные тесты запускают дочерний `dotnet`. На проверенной машине системный dotnet уже доступен; на чистой машине одного абсолютного пути для родительской команды недостаточно.

Seed отказывает, если `synthetic.db` или `runtime.json` уже существует; он не очищает и не переиспользует базы. Для чистого повторения взять отдельный checkout. При неудаче сохранить частичные артефакты вне Git и отдельно разобраться с причиной; автоматического удаления нет. Junction/symlink в пути среды запрещены.

## Состав синтетических данных

| Объект | Количество / назначение |
| --- | --- |
| Физлица / юрлица | 3 / 2, названия «Синтетический…», только адреса `.local`, без телефонов клиентов |
| Дела / инстанции | 3 / 12: first, appeal, cassation, supreme — допустимые значения текущего API |
| Задачи | 5: new, inProgress, onApproval, done, cancelled; связаны с синтетическим делом |
| Счета / платежи | 1 счёт на 1000 и 1 условное поступление 200; в таблице Payments две записи типов Invoice/Payment, в Invoices одна. Реальные переводы не выполняются |
| Пользователи | admin, accountant, secretary, executor, observer — по одному |

Admin имеет действующие административные права. Остальные роли получают тестовый просмотр основных разделов; secretary/executor — создание/изменение задач, accountant — счета/акты/платежи, observer — просмотр. Удаление/экспорт/аналитика этим четырём ролям не выдаются. Это **fixture для разработки**, а не согласование будущей модели PAY-18. Начисления, налоговый учёт, финансовые отчёты и все состояния согласования этим набором не квалифицированы.

Локальные файлы только в игнорируемом `.codex-build/claude-synthetic/`:

- `synthetic.db` — новая база; `seed-summary.json` — количества.
- `runtime.json` — параметры конкретной среды и instance ID.
- `credentials.local.json` — случайные пароли для пяти `.local` логинов; открывать только локально, не вставлять в чат/Git.
- `keys/` и `storage-settings.json` — отдельные ключи и инвентаризация Data Protection для проверки изоляции. Не брать ключи сервера.

## Защита перед каждым запуском

Dot-source [Use-SyntheticEnvironment.ps1](../scripts/claude-preparation/Use-SyntheticEnvironment.ps1) в **каждом новом терминале** перед backend/frontend. Скрипт проверяет завершённость сидирования, фиксированный путь своей SQLite, loopback и безопасные флаги; задаёт переменные только текущему процессу.

| Переменная | Значение / смысл |
| --- | --- |
| ASPNETCORE_ENVIRONMENT | ClaudeSynthetic, без Development user-secrets и production-конфигурации |
| ASPNETCORE_URLS | http://127.0.0.1:5300 |
| Cors__AllowedOrigins__0 | http://127.0.0.1:5301; имя ClaudeSynthetic не включает development CORS автоматически |
| Database__Provider / ConnectionStrings__Default | sqlite / путь собственной synthetic.db, формируется Seed |
| BackgroundServices__Enabled / Email__Enabled | false / false, обязательно при каждом backend-запуске |
| RestoredRuntimeIsolation__Enabled | true: существующая защита блокирует внешние HTTP-запросы и опасные интеграционные операции |
| RestoredRuntimeIsolation__InstanceId | собственный GUID среды, совпадает с отметкой в базе |
| RestoredRuntimeIsolation__KeysPath / StorageSettingsPath | собственные локальные ключи/метаданные |
| DataProtection__ApplicationName | PayPlanner.Claude.Synthetic |
| MessengerDocumentDelivery__Enabled / Providers__Max__Enabled | false / false |
| ElectronicSignature__Enabled | false |
| FileStorage__RootPath / ProfilePhotos__StorageRoot | собственные `claude-synthetic/files` / `claude-synthetic/photos` |
| ElectronicSignature__StorageRoot / StorageOrchestration__LocalRoot | собственные `claude-synthetic/electronic-signatures` / `claude-synthetic/storage-objects` |
| FieldEncryption__Enabled / ClientPassportDualWriteEnabled | false / false для новых синтетических записей |
| PAYPLANNER_SKIP_CLIENT_SEED | 1, стандартный набор клиентов не нужен |
| VITE_API_URL | http://127.0.0.1:5300/api |
| VITE_RESTORED_RUNTIME_ISOLATION / INSTANCE_ID | true / тот же GUID; существующая browser policy и отдельное пространство сессий |

Seed явно устанавливает в БД `notifications.email.enabled=false`, `notifications.max.enabled=false`, `notifications.telegram.enabled=false`. Даже при отсутствии SMTP-реквизитов нельзя полагаться только на название окружения. Базу production не восстанавливать в этот каталог. Изолированный backend отказывает запросам без заголовка `X-PayPlanner-Restored-Instance`; frontend добавляет его через существующий механизм. Публичен только handshake идентичности; обычный health без заголовка возвращает **409**, с правильным — **200**.

После перезапуска backend вход выполняется заново: restored-runtime создаёт временный секрет сессии на каждый запуск. Для каждой роли используйте отдельный чистый профиль браузера. Не очищайте целиком localStorage уже работающего профиля: там хранится отметка изолированной среды, и guard справедливо откажет открывать профиль с оставшимися данными без этой отметки. Для смены пользователя используйте обычный выход или отдельный новый профиль. Отдельные проверки паспортного шифрования запускаются на одноразовых тестовых БД; синтетическая UI-среда специально использует отключённое шифрование, как указано выше.

## Backend и frontend вместе

Терминал 1, корень checkout (переменную `$dotnetTool` определить заново, если терминал новый):

```powershell
. ./scripts/claude-preparation/Use-SyntheticEnvironment.ps1
$dotnetTool = Join-Path (Get-Location).Path '.codex-build/dotnet-9.0.100/dotnet.exe'
& $dotnetTool backend/bin/Release/net9.0/PayPlanner.Api.dll --contentRoot (Join-Path (Get-Location).Path 'backend')
```

Терминал 2:

```powershell
npm ci
. ./scripts/claude-preparation/Use-SyntheticEnvironment.ps1
node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5301 --strictPort
```

Открыть **http://127.0.0.1:5301/**, войти одним из локальных пользователей. Прямая команда Vite проверена: в этом PowerShell передача аргументов через `npm run dev -- --port 5301` была некорректной (`vite 5301`), поэтому её здесь не использовать. Не открывать production CRM в качестве frontend этой среды. Внешние почтовые/банковские/MAX/ЭДО действия в изоляции должны отказывать; не выключать guard ради их проверки.

Терминал 3, проверка уже запущенной пары (PowerShell 7):

```powershell
./scripts/claude-preparation/Test-SyntheticEnvironment.ps1
```

Проверяет instance ID, health, отказ без заголовка, вход пяти ролей и frontend HTTP 200; пароли/токены не выводит. Это smoke, не визуальная приёмка макетов, не полная проверка разрешений. Остановить оба сервера `Ctrl+C` в их терминалах.

## Backend-тесты: отдельная пустая PostgreSQL

Использовать установленный PostgreSQL 17, добавить его `bin` в PATH. Скрипт [Start-SyntheticPostgres.ps1](../scripts/claude-preparation/Start-SyntheticPostgres.ps1) создаёт **новый** кластер, отказывает занятому порту/существующему каталогу; никогда не подключает рабочую базу автоматически. Loopback 127.0.0.1:55432, локальная trust-авторизация, никаких production-реквизитов. Не предоставлять сетевой доступ к этому кластеру.

pg_ctl запускается скрытым отдельным процессом с выводом в start.out.log/start.err.log, иначе фоновый postgres может удерживать перенаправленный поток PowerShell и не возвращать управление. Этот вариант проверен с нуля вместе с отдельным тестом ниже.

```powershell
$env:Path = 'C:/Program Files/PostgreSQL/17/bin;' + $env:Path
. ./scripts/claude-preparation/Start-SyntheticPostgres.ps1
$dotnetTool = Join-Path (Get-Location).Path '.codex-build/dotnet-9.0.100/dotnet.exe'
& $dotnetTool test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj -c Release -m:1 /nodeReuse:false --filter 'FullyQualifiedName~InternalChat|FullyQualifiedName~TaskBillingConcurrencyTests|FullyQualifiedName~ClientBirthdayTaskSchedulerServiceTests'
# Один тест / одно семейство:
& $dotnetTool test backend/PayPlanner.Api.Tests/PayPlanner.Api.Tests.csproj -c Release --no-build --filter 'FullyQualifiedName~TaskBillingConcurrencyTests.ConcurrentInvoiceCreationAndLinkingCannotBillTheSameTaskTwice'
# После тестов:
pg_ctl.exe -D .codex-build/claude-test-postgres -m fast -w stop
```

В проверенном выполнении пустой кластер расположен вне checkout: `D:/PayPlannerPreparation-20261012/postgres`, тот же loopback/порт. PostgreSQL-фикстуры создают свои базы с непредсказуемыми именами; миграционная template-фикстура клонирует собственную пустую схему. Не передавать `PAYPLANNER_TEST_POSTGRES` от сервера/чужой локальной базы. Без переменной часть тестов падает, а старые проверки могут завершиться без выполнения тела — это не PostgreSQL-проверка.

В disposable-кластере выключены fsync/synchronous_commit/full_page_writes, чтобы Windows-диск не задерживал DDL/удаление тестовых баз; `Command Timeout=180` относится к тестовому соединению. Это не режим приложения/production и не проверка восстановления после сбоя. Порог ожидания в самих concurrency-тестах не меняется. Результаты первоначального прогона с обычной дисковой синхронизацией и итоговые результаты указаны отдельно в отчёте.

## Frontend-проверки и опись действий

```powershell
npm run lint
npx tsc -p tsconfig.app.json --noEmit
npx vitest run --maxWorkers=3
npm run build
npm run audit:encoding
node scripts/claude-preparation/extract-action-inventory.mjs
git diff --check
```

Vitest запускает весь набор; ограничение workers снижает нагрузку, не исключает тесты. Повторять уже успешные полные проверки только после релевантных изменений. Генератор описи читает TSX/интерфейсы, не запускает приложение и не изменяет исходники продукта; обновляет автоматически помеченную часть трёх карт и JSON. Семантические таблицы над этой частью сохраняются. При изменении продукта обновить границы функций/выборку в генераторе и `PREPARATION_SOURCE_REVISION`, затем вручную сверить динамические команды и права.

## Ограничения следующего этапа

Макеты и action inventory доступны локально, но дизайн ещё не реализован и не принят. Бизнес-решения — [отдельный файл](owner-decisions-2026-10-12.md). Среда не заменяет реальную приёмку Анны/MAX/банка и не подключается к VPS. Новые user-facing изменения следующего этапа должны иметь release notes по AGENTS; эта подготовка выпуска продукта не создаёт.
