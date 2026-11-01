# Адресные HTTP-проверки доступа к DaData

Этот локальный проект подключает исходный `backend/PayPlanner.Api.Tests/ClientAddressEnrichmentAccessTests.cs` и два существующих файла ClientsControllerCoverageTests / LegalEntitiesControllerCoverageTests без копии или сокращения тестов. Общий помощник MainCompanySettingsVersionTestAccess вынесен в отдельный исходный файл без изменения тела и подключён также здесь. Основной backend-проект тестов продолжает включать те же файлы; обязательный финальный CI не изменён.

Проверяется настоящий MVC pipeline с существующим `CurrentUserClaimsTransformation`, авторизацией и обоими контроллерами. Проверяются все семь публичных методов адресов, контактов и организаций; существующие тесты сохраняют проверки валидации и формы ответа, используя действующего одобренного администратора. Только начальная аутентификация является фикстурой; внешний DaData заменён счётчиком вызовов и контролируемыми ответами. Отказы должны происходить до внешнего обращения, включая резервный шаг после отзыва прав или сессии. Тесты не обращаются к реальному DaData, SMTP, производственной БД или фоновым службам.

Используется обычная для проекта SQLite-in-memory фикстура и `EnsureCreated`. Отдельный сервер БД не создаётся. Поддержанный флаг `PayPlannerSkipMigrationsBuild=true` и явная сборка миграций тестовой фикстуры исключают неиспользуемую сборку исторических миграций из этого адресного запуска. Это не проверка миграций, PostgreSQL, JWT-подписи или полный backend gate.

Пример запуска из корня рабочей копии:

```powershell
$env:BackgroundServices__Enabled='false'
$env:Email__Enabled='false'
$env:DOTNET_PROCESSOR_COUNT='1'
$env:DOTNET_gcServer='0'
$env:DOTNET_GCConserveMemory='9'
dotnet test tests/PayPlanner.DadataAccessProbe/PayPlanner.DadataAccessProbe.csproj --artifacts-path D:/PayPlannerRelease/dadata-access/artifacts -m:1 -nr:false -p:UseSharedCompilation=false --filter FullyQualifiedName~ClientAddressEnrichmentAccessTests
```

Сохраняйте TRX, точные команды, окружение, каталог тестов и хеши исходников/DLL. Если для локального restore используются только уже установленные пакеты и `NuGetAudit=false`, такой restore не является успешным аудитом зависимостей: обязательные проверки зависимостей, миграций и остальные барьеры остаются в полном CI перед выпуском.
