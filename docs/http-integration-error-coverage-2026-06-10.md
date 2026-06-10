# Mocked HTTP integration error coverage

Дата: 2026-06-10.

## Scope

Roadmap item: Milestone 8, "Добавить mocked HTTP integration tests".

Покрытие добавлено или расширено без реальных внешних запросов. Все проверки используют mocked `HttpMessageHandler`, fake `IHttpClientFactory` или SDK-level fake/exception mapping для S3.

## Coverage matrix

| Integration | Test coverage | Contract |
| --- | --- | --- |
| DaData | `DadataLegalEntityEnrichmentServiceTests` | 401/403/404/429/500/503 возвращают пустой результат или `null`; transient 429/503 ретраятся; timeout/request failure не ломают пользовательский сценарий; caller cancellation сохраняется. |
| Sudrf | `SudrfCourtSyncServiceTests` | 401/403/404/429/500/503 мапятся в `SudrfCourtSyncException` с понятным статусом; внешний timeout мапится в `SudrfCourtSyncException`; caller cancellation сохраняется. |
| Yandex Disk | `YandexDiskServiceTests` | 401/403/404/429/500/503 и 507 мапятся в `YandexDiskOperationException` с ожидаемым статусом/сообщением; timeout мапится в 504. |
| Google Calendar | `GoogleCalendarSyncServiceTests` | 401/403/429/500/503 при cleanup сохраняют подключение и пишут error log; 404 считается идемпотентным успехом; timeout сохраняет подключение и пишет error log. |
| Yandex Calendar | `YandexCalendarSyncServiceTests` | 401/403/429/500/503 при cleanup сохраняют подключение и пишут error log; 404 считается идемпотентным успехом; timeout сохраняет подключение и пишет error log. |
| Telegram | `TelegramNotificationServiceHttpErrorTests` | 401/403/404/429/500/503 возвращают failed delivery с описанием Telegram; внешний timeout возвращает failed delivery; caller cancellation сохраняется. |
| S3 | `S3StorageServiceTests` | `AmazonS3Exception` 401/403/404/429/500/503 и network-style status 0 мапятся в `S3StorageOperationException` с ожидаемым статусом/сообщением. |

## Targeted test command

```powershell
dotnet test backend\PayPlanner.Api.Tests\PayPlanner.Api.Tests.csproj --filter "DadataLegalEntityEnrichmentServiceTests|SudrfCourtSyncServiceTests|TelegramNotificationServiceHttpErrorTests|YandexDiskServiceTests|S3StorageServiceTests|GoogleCalendarSyncServiceTests|YandexCalendarSyncServiceTests"
```

Result: 146 passed.
