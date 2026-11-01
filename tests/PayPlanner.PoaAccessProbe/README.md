# Адресные проверки доступа к доверенностям

Проект подключает исходный файл `PowerOfAttorneyControllerTests.cs` основного backend-проекта без копии тестов и без изменения утверждений. Через настоящий MVC pipeline проверяются права, видимость клиентов, ответы и сохранённые записи. Используется существующая SQLite-in-memory фикстура этого файла; SMTP и фоновые службы приложения не запускаются, отдельный сервер БД не создаётся.

Это быстрый локальный запуск конкретного набора проверок. Он не проверяет миграции или совместимость PostgreSQL, не заменяет полный `PayPlanner.Api.Tests` и обязательные семь jobs финального CI. Тот же файл остаётся в основном проекте и выполняется там перед выпуском.

В ссылке на API используется существующий `PayPlannerSkipMigrationsBuild=true`: эти тесты вызывают `EnsureCreated` тестовой фикстуры, не применяют миграции и не требуют их сборки. Фикстура явно задаёт сборку тестов как `MigrationsAssembly`, используя поддержанный контекстом и уже проверяемый в `MigrationsAssemblyStartupTests` вариант. Это отменяет только неиспользуемую загрузку исторических миграций: модель, создание схемы в памяти, MVC, запросы и утверждения остаются теми же. Сам API собирается обычным способом. Основной проект тестов и сценарий выпуска не меняются.

Пример адресного запуска из корня репозитория (каталог артефактов должен принадлежать текущей рабочей копии):

```powershell
$env:BackgroundServices__Enabled='false'
$env:Email__Enabled='false'
$env:DOTNET_PROCESSOR_COUNT='1'
$env:DOTNET_GCConserveMemory='9'
$env:DOTNET_gcServer='0'
dotnet test tests/PayPlanner.PoaAccessProbe/PayPlanner.PoaAccessProbe.csproj --artifacts-path D:/PayPlannerRelease/poa-access/artifacts -m:1 -nr:false -p:UseSharedCompilation=false --filter FullyQualifiedName~PowerOfAttorneyControllerTests
```

Сохраняйте фактический состав тестов, TRX, хеши исходников и DLL. Не называйте результат этого проекта полным backend-прогоном. Аудит зависимостей, проверка миграций и остальные обязательные барьеры выпуска сохраняются в финальном CI.
