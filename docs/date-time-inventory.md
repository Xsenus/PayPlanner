# Инвентаризация дат и времени

Сгенерировано: 2026-08-05T05:01:10.327Z

Этот отчет собирает места, где проект работает с датами, временем, периодами, экспортами и календарными интеграциями. Его задача - помочь переводить систему на единое правило: хранение в UTC плюс явный timezone ID пользователя или организации.

## Как обновить

```powershell
npm run audit:dates
```

## Сводка

- Проверено файлов: 1958
- Найдено совпадений: 10891

| Категория | Совпадений | Что проверить |
| --- | ---: | --- |
| Backend: поля и свойства дат | 2085 | Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID. |
| Backend: текущее время сервера | 2667 | Вызовы Now/UtcNow, которые надо привести к единому источнику времени. |
| Backend: преобразования timezone | 1533 | Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги. |
| Ручные смещения времени | 870 | Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов. |
| Frontend: работа с датами | 808 | JS/TS места, где браузер может применить локальную timezone пользователя автоматически. |
| Фильтры и периоды | 1108 | Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя. |
| Экспорты, импорты и файлы | 1495 | Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени. |
| Интеграции календарей | 325 | Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID. |

## Файлы с наибольшим количеством совпадений

| Файл | Совпадений |
| --- | ---: |
| `backend/AppReleases/releases.json` | 1143 |
| `backend/Controllers/ReportsController.cs` | 581 |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 524 |
| `backend/Controllers/V2/CasesV2Controller.cs` | 413 |
| `backend/PayPlanner.Api.Tests/TasksControllerTests.cs` | 315 |
| `backend/PayPlanner.Api.Tests/MoneyOperationsControllerTests.cs` | 301 |
| `backend/PayPlanner.Api.Tests/CasesV2ControllerTests.cs` | 226 |
| `backend/Services/GoogleCalendarSyncService.cs` | 190 |
| `backend/PayPlanner.Api.Tests/ReportsControllerTests.cs` | 186 |
| `backend/Controllers/TasksController.cs` | 180 |
| `backend/Services/Notifications/NotificationService.cs` | 147 |
| `backend/Controllers/MoneyOperationsController.cs` | 145 |
| `backend/PayPlanner.Api.Tests/PaymentsControllerTests.cs` | 142 |
| `src/components/Tasks/TaskJournal.tsx` | 128 |
| `backend/Services/YandexCalendarSyncService.cs` | 127 |
| `src/components/Reports/Reports.tsx` | 109 |
| `backend/Controllers/InvoicesController.cs` | 107 |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 81 |
| `src/components/Tasks/TaskJournal.test.tsx` | 77 |
| `backend/PayPlanner.Api.Tests/SystemJobsControllerTests.cs` | 74 |
| `src/types/index.ts` | 74 |
| `backend/Controllers/CashOrdersController.cs` | 71 |
| `backend/PayPlanner.Api.Tests/InvoicesControllerCoverageTests.cs` | 69 |
| `src/components/Money/MoneyOperations.tsx` | 67 |
| `backend/Services/DatabaseImportService.cs` | 65 |
| `backend/PayPlanner.Api.Tests/InvoicesControllerCrudTests.cs` | 60 |
| `backend/Controllers/PaymentsController.cs` | 59 |
| `backend/Controllers/SystemJobsController.cs` | 58 |
| `backend/Controllers/V2/PaymentsV2Controller.cs` | 57 |
| `backend/PayPlanner.Api.Tests/YandexCalendarSyncServiceTests.cs` | 55 |

## Детализация

### Backend: поля и свойства дат

Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 1651 | `DateTime? EffectivePlannedDate` | private static DateTime? EffectivePlannedDate(PaymentAggregateItem payment) |
| `backend/Controllers/AccountsController.cs` | 1721 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1722 | `DateTime? DueDate` | public DateTime? DueDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1723 | `DateTime? PlannedDate` | public DateTime? PlannedDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1729 | `DateTime? SentForSigningAt` | public DateTime? SentForSigningAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1730 | `DateTime? SignedAt` | public DateTime? SignedAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1731 | `DateTime? CancelledAt` | public DateTime? CancelledAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1735 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1766 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1775 | `DateTime? AccountDate` | public DateTime? AccountDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1776 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1777 | `DateTime PlannedDate` | public DateTime PlannedDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1778 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1802 | `DateTime DueDate` | DateTime DueDate, |
| `backend/Controllers/AccountsController.cs` | 1825 | `DateTime? DueDate` | public DateTime? DueDate { get; set; } |
| `backend/Controllers/AccountsController.cs` | 1826 | `DateTime? PaidDate` | public DateTime? PaidDate { get; set; } |
| `backend/Controllers/AccountsController.cs` | 34 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 35 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 497 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 498 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 735 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 736 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 1032 | `DateTime today)` | DateTime today) |
| `backend/Controllers/AccountsController.cs` | 1660 | `DateTime accountDate,` | DateTime accountDate, |
| `backend/Controllers/AccountsController.cs` | 1678 | `DateTime? accountDate,` | DateTime? accountDate, |
| `backend/Controllers/ActsController.cs` | 192 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 193 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 764 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 765 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 2053 | `DateTime now)` | private static string AppendCancellationComment(string? comment, string reason, DateTime now) |
| `backend/Controllers/AppVersionController.cs` | 328 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/AppVersionController.cs` | 337 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/AppVersionController.cs` | 343 | `DateTime CreatedAt` | DateTime CreatedAt, |
| `backend/Controllers/AppVersionController.cs` | 344 | `DateTime UpdatedAt` | DateTime UpdatedAt); |
| `backend/Controllers/AppVersionController.cs` | 353 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/CalendarSyncController.cs` | 409 | `DateTime NormalizeFilterDate` | private static DateTime NormalizeFilterDate(DateTime value) |
| `backend/Controllers/CalendarSyncController.cs` | 654 | `DateTime CreatedAt` | public DateTime CreatedAt { get; set; } |
| `backend/Controllers/CalendarSyncController.cs` | 288 | `DateTime? createdFrom,` | DateTime? createdFrom, |
| `backend/Controllers/CalendarSyncController.cs` | 289 | `DateTime? createdTo)` | DateTime? createdTo) |
| `backend/Controllers/CalendarSyncController.cs` | 409 | `DateTime value)` | private static DateTime NormalizeFilterDate(DateTime value) |
| `backend/Controllers/CashOrdersController.cs` | 42 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 43 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 78 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 79 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 116 | `DateTime? date,` | [FromQuery] DateTime? date, |
| `backend/Controllers/CashOrdersController.cs` | 137 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 138 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 183 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 184 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 253 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 254 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 318 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 319 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 995 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 996 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1182 | `DateTime date,` | private Task<decimal> CalculateCashBalanceAsync(DateTime date, string accountName, DataVisibilityAccess visibility, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 1192 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 1193 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1260 | `DateTime date,` | private async Task<string> GenerateNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 1278 | `DateTime date,` | private async Task<string> GenerateInventoryNumberAsync(DateTime date, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 1818 | `DateTime? from,` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/CashOrdersController.cs` | 1818 | `DateTime? to,` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/ClientPortalController.cs` | 268 | `DateTime ExpiresAt` | public DateTime ExpiresAt { get; set; } |
| `backend/Controllers/ClientPortalController.cs` | 215 | `DateTime expiresAt)` | private CookieOptions BuildCookieOptions(DateTime expiresAt) |
| `backend/Controllers/ClientsController.cs` | 1801 | `DateTime? PassportIssueDate` | public DateTime? PassportIssueDate { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1835 | `DateTime? IssueDate` | public DateTime? IssueDate { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1839 | `DateTime? ValidFrom` | public DateTime? ValidFrom { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1840 | `DateTime? ValidTo` | public DateTime? ValidTo { get; init; } |
| `backend/Controllers/ClientsController.cs` | 769 | `DateTime today)` | DateTime today) |
| `backend/Controllers/ClientsController.cs` | 812 | `DateTime today,` | DateTime today, |
| `backend/Controllers/ContractsController.cs` | 1732 | `DateTime? ValidUntil` | private async Task<(ActionResult? Error, string? Number, string? BillingMode, bool IsOpenEnded, decimal? Amount, DateTime? ValidUntil, List<(int ClientId, string? Comment)> Clients, List<(int LegalEntityId, string? Comme |
| `backend/Controllers/ContractsController.cs` | 233 | `DateTime? originalValidUntil,` | DateTime? originalValidUntil, |
| `backend/Controllers/ContractsController.cs` | 234 | `DateTime? currentValidUntil,` | DateTime? currentValidUntil, |
| `backend/Controllers/ContractsController.cs` | 259 | `DateTime? date)` | private static string FormatContractDate(DateTime? date) |
| `backend/Controllers/ContractsController.cs` | 308 | `DateTime? from,` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 308 | `DateTime? to,` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 1011 | `DateTime date,` | public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 1529 | `DateTime now,` | private void ApplyContractCancellationSideEffects(Contract contract, string reason, DateTime now, TimeZoneInfo contractTimeZone) |
| `backend/Controllers/ContractsController.cs` | 1854 | `DateTime date,` | private async Task<string> GenerateNextContractNumberAsync(DateTime date, int? excludeId, CancellationToken ct, ContractNumberingSettingsDto? settings = null) |
| `backend/Controllers/ContractsController.cs` | 1898 | `DateTime date,` | private static string FormatGeneratedContractNumber(int sequence, DateTime date, string template) |
| `backend/Controllers/ContractsController.cs` | 1915 | `DateTime date,` | private static bool TryParseGeneratedContractNumber(string? number, DateTime date, string template, out int sequence) |
| `backend/Controllers/ContractsController.cs` | 1965 | `DateTime nowUtc,` | private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 2009 | `DateTime date,` | private static string RenderContractTemplate(string template, DateTime date, string sequenceText) |
| `backend/Controllers/ContractsController.cs` | 2105 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/ContractsController.cs` | 2106 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/DadataIntegrationSettingsController.cs` | 195 | `DateTime? UpdatedAt` | public DateTime? UpdatedAt { get; init; } |
| `backend/Controllers/DadataIntegrationSettingsController.cs` | 147 | `DateTime now,` | private async Task UpsertSettingAsync(string key, string value, DateTime now, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 3689 | `DateTime EffectivePlannedDate` | private static DateTime EffectivePlannedDate(Payment payment) |
| `backend/Controllers/InvoicesController.cs` | 3794 | `DateTime InvoiceDate` | DateTime InvoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 5188 | `DateTime ResolveInvoiceDueDate` | private static DateTime ResolveInvoiceDueDate(UpsertInvoiceRequest request) |
| `backend/Controllers/InvoicesController.cs` | 5203 | `DateTime ResolveInvoiceDueDate` | private static DateTime ResolveInvoiceDueDate( |
| `backend/Controllers/InvoicesController.cs` | 6498 | `DateTime ResolveLegacyInstallmentDate` | private static DateTime ResolveLegacyInstallmentDate(Payment payment) |
| `backend/Controllers/InvoicesController.cs` | 6965 | `DateTime StartDate` | public DateTime StartDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6979 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6986 | `DateTime Date` | private sealed record InstallmentScheduleItem(int Sequence, DateTime Date, decimal Amount, int Id); |
| `backend/Controllers/InvoicesController.cs` | 6993 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6994 | `DateTime? DueDate` | public DateTime? DueDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7000 | `DateTime? SentForSigningAt` | public DateTime? SentForSigningAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7001 | `DateTime? SignedAt` | public DateTime? SignedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7002 | `DateTime? CancelledAt` | public DateTime? CancelledAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7006 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7042 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7045 | `DateTime? AuditUpdatedAt` | public DateTime? AuditUpdatedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7049 | `DateTime? AuditDeletedAt` | public DateTime? AuditDeletedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 241 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 242 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 368 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 369 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 1166 | `DateTime now,` | private async Task CancelLinkedActsAsync(Payment invoicePayment, string reason, DateTime now, int? actorId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 1190 | `DateTime now)` | private static string AppendActCancellationComment(string? comment, string invoiceNumber, string reason, DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 1213 | `DateTime utcNow,` | DateTime utcNow, |
| `backend/Controllers/InvoicesController.cs` | 1274 | `DateTime localNow)` | private static void AppendCaseEvent(ClientCase clientCase, string eventText, DateTime localNow) |
| `backend/Controllers/InvoicesController.cs` | 1750 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 1751 | `DateTime dueDate,` | DateTime dueDate, |
| `backend/Controllers/InvoicesController.cs` | 2030 | `DateTime? date,` | public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 3284 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 3285 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3539 | `DateTime? paidDate,` | private static void ApplyPaidFlags(Payment payment, DateTime? paidDate, DateTime today) |
| `backend/Controllers/InvoicesController.cs` | 3539 | `DateTime today)` | private static void ApplyPaidFlags(Payment payment, DateTime? paidDate, DateTime today) |
| `backend/Controllers/InvoicesController.cs` | 3632 | `DateTime accountDate,` | DateTime accountDate, |
| `backend/Controllers/InvoicesController.cs` | 4236 | `DateTime now,` | private void ApplyInvoiceWorkflowStatus(Payment payment, string workflowStatus, DateTime now, string? reason) |
| `backend/Controllers/InvoicesController.cs` | 4280 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 4304 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 4305 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 4957 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 5009 | `DateTime now)` | DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 5025 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 5059 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 5152 | `DateTime date,` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5206 | `DateTime nextInvoiceDate)` | DateTime nextInvoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 5912 | `DateTime targetDate,` | private async Task<int> GetNextInvoiceNumberAsync(DateTime targetDate, int startNumber, string allocationStrategy, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5947 | `DateTime accountDate,` | private async Task<string?> ResolveInvoiceNumberForSaveAsync(string number, DateTime accountDate, int? excludeId, bool allowDuplicateOnImport, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5983 | `DateTime accountDate,` | private async Task<HashSet<string>> LoadReservedInvoiceNumberLookupAsync(DateTime accountDate, int? excludeId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6005 | `DateTime accountDate,` | private async Task<ActionResult?> EnsureInvoiceNumberIsUniqueAsync(string number, DateTime accountDate, int? excludeId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6045 | `DateTime nowUtc,` | private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6084 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6131 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6146 | `DateTime invoiceDate)` | private static string BuildReservedInvoiceNumberKey(string number, DateTime invoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 6151 | `DateTime invoiceDate)` | private static void AssignLegacyAccountSource(Payment payment, string number, DateTime invoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 6170 | `DateTime localToday)` | DateTime localToday) |
| `backend/Controllers/InvoicesController.cs` | 6286 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6342 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6452 | `DateTime localToday)` | DateTime localToday) |
| `backend/Controllers/InvoicesController.cs` | 6503 | `DateTime nowUtc,` | private async Task UpsertUserSettingAsync(int userId, string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/LegalEntitiesController.cs` | 2505 | `DateTimeOffset? LiquidationDate` | public DateTimeOffset? LiquidationDate { get; init; } |
| `backend/Controllers/LegalEntitiesController.cs` | 2517 | `DateTimeOffset? RegistrationDate` | public DateTimeOffset? RegistrationDate { get; init; } |
| `backend/Controllers/LegalEntitiesController.cs` | 81 | `DateTime? invoiceDate,` | DateTime? invoiceDate, |
| `backend/Controllers/MoneyOperationsController.cs` | 4756 | `DateTime? LastCheckedAt` | public DateTime? LastCheckedAt { get; set; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4829 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4830 | `DateTime? RecognitionDate` | public DateTime? RecognitionDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4842 | `DateTime? LinkedInvoiceDate` | public DateTime? LinkedInvoiceDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4847 | `DateTime? LinkedActDate` | public DateTime? LinkedActDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4849 | `DateTime? MatchedAt` | public DateTime? MatchedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4853 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4854 | `DateTime? UpdatedAt` | public DateTime? UpdatedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4859 | `DateTime? CashOrderDate` | public DateTime? CashOrderDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4894 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4917 | `DateTime Date` | DateTime Date, |
| `backend/Controllers/MoneyOperationsController.cs` | 201 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 202 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 241 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 242 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 414 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 415 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 464 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 465 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 511 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 512 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 558 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 559 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 637 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 638 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 743 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 744 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 765 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 766 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 814 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 815 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 917 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 918 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1022 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1023 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1359 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1360 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1468 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1469 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3269 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 3270 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3448 | `DateTime date)` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date) |
| `backend/Controllers/MoneyOperationsController.cs` | 4182 | `DateTime date,` | DateTime date, |
| `backend/Controllers/MoneyOperationsController.cs` | 4781 | `DateTime date)` | private static bool TryParseStatementDate(string value, out DateTime date) |
| `backend/Controllers/ObjectAuditController.cs` | 42 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 3058 | `DateTime RemainderDate` | DateTime RemainderDate); |
| `backend/Controllers/PaymentsController.cs` | 3967 | `DateTime Date` | private static List<(string Number, DateTime Date)> BuildLookupCandidates(Payment payment) |
| `backend/Controllers/PaymentsController.cs` | 3969 | `DateTime Date` | var candidates = new List<(string Number, DateTime Date)>(); |
| `backend/Controllers/PaymentsController.cs` | 329 | `DateTime originalDate,` | DateTime originalDate, |
| `backend/Controllers/PaymentsController.cs` | 330 | `DateTime currentDate,` | DateTime currentDate, |
| `backend/Controllers/PaymentsController.cs` | 363 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 364 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 1507 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 1508 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 1596 | `DateTime today,` | DateTime today, |
| `backend/Controllers/PaymentsController.cs` | 2311 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 2353 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 2524 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 2597 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 2650 | `DateTime refundDate,` | DateTime refundDate, |
| `backend/Controllers/PaymentsController.cs` | 2654 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 2712 | `DateTime now,` | private async Task SyncPaymentCashOrderAsync(Payment payment, DateTime now, CancellationToken ct) |
| `backend/Controllers/PaymentsController.cs` | 2818 | `DateTime date,` | DateTime date, |
| `backend/Controllers/PaymentsController.cs` | 2976 | `DateTime date,` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/PaymentsController.cs` | 3012 | `DateTime now,` | private void AppendOverpaymentDecisionNote(Payment payment, string message, DateTime now, TimeZoneInfo paymentTimeZone) |
| `backend/Controllers/PaymentsController.cs` | 3023 | `DateTime suggestedDate)` | private static bool TryInferSuggestedRemainderDate(Payment payment, out DateTime suggestedDate) |
| `backend/Controllers/PaymentsController.cs` | 3157 | `DateTime remainderDate,` | DateTime remainderDate, |
| `backend/Controllers/PaymentsController.cs` | 3158 | `DateTime now)` | DateTime now) |
| `backend/Controllers/PaymentsController.cs` | 3314 | `DateTime remainderDate,` | DateTime remainderDate, |
| `backend/Controllers/PaymentsController.cs` | 3315 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 3333 | `DateTime remainderDate,` | DateTime remainderDate, |
| `backend/Controllers/PaymentsController.cs` | 3334 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 3709 | `DateTime lookupDate,` | private async Task<Payment?> ResolveInvoiceByPaymentHistoryAsync(Payment payment, string lookupNumber, DateTime lookupDate, CancellationToken ct) |
| `backend/Controllers/PaymentsController.cs` | 3971 | `DateTime? date)` | void Add(string? number, DateTime? date) |
| `backend/Controllers/PaymentsController.cs` | 4018 | `DateTime originalDueDate,` | DateTime originalDueDate, |
| `backend/Controllers/PaymentsController.cs` | 4019 | `DateTime originalPlannedDate,` | DateTime originalPlannedDate, |
| `backend/Controllers/PaymentsController.cs` | 4051 | `DateTime scheduleDate)` | private static string ReplaceInstallmentMetadataDate(string? notes, DateTime scheduleDate) |
| `backend/Controllers/ReportsController.cs` | 177 | `DateTime Utc` | private async Task<(DateTime Utc, DateTime Local, TimeZoneInfo TimeZone)> ResolveReportNowAsync(CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 177 | `DateTime Local` | private async Task<(DateTime Utc, DateTime Local, TimeZoneInfo TimeZone)> ResolveReportNowAsync(CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 184 | `DateTime Start` | private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 184 | `DateTime End` | private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 5568 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt(CurrentCaseQueryRow clientCase) |
| `backend/Controllers/ReportsController.cs` | 5574 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt( |
| `backend/Controllers/ReportsController.cs` | 5589 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt( |
| `backend/Controllers/ReportsController.cs` | 9491 | `DateTime DueAt` | DateTime DueAt, |
| `backend/Controllers/ReportsController.cs` | 9503 | `DateTime DueAt` | DateTime DueAt, |
| `backend/Controllers/ReportsController.cs` | 9508 | `DateTime DueAt` | DateTime DueAt, |
| `backend/Controllers/ReportsController.cs` | 9526 | `DateTime DueAt` | DateTime DueAt, |
| `backend/Controllers/ReportsController.cs` | 9532 | `DateTime CreatedAt` | DateTime CreatedAt, |
| `backend/Controllers/ReportsController.cs` | 9533 | `DateTime? UpdatedAt` | DateTime? UpdatedAt); |
| `backend/Controllers/ReportsController.cs` | 9554 | `DateTime CreatedAt` | DateTime CreatedAt, |
| `backend/Controllers/ReportsController.cs` | 9555 | `DateTime? StartDate` | DateTime? StartDate, |
| `backend/Controllers/ReportsController.cs` | 9556 | `DateTime? DueDate` | DateTime? DueDate, |
| `backend/Controllers/ReportsController.cs` | 9558 | `DateTime? NextHearingAt` | DateTime? NextHearingAt, |
| `backend/Controllers/ReportsController.cs` | 9559 | `DateTime? ClaimFiledAt` | DateTime? ClaimFiledAt, |
| `backend/Controllers/ReportsController.cs` | 9560 | `DateTime? AppealDeadlineAt` | DateTime? AppealDeadlineAt, |
| `backend/Controllers/ReportsController.cs` | 9561 | `DateTime? MotionDeadlineAt` | DateTime? MotionDeadlineAt, |
| `backend/Controllers/ReportsController.cs` | 9562 | `DateTime? ObjectionDeadlineAt` | DateTime? ObjectionDeadlineAt); |
| `backend/Controllers/ReportsController.cs` | 9589 | `DateTime CreatedAt` | DateTime CreatedAt, |
| `backend/Controllers/ReportsController.cs` | 9590 | `DateTime? StartDate` | DateTime? StartDate, |
| `backend/Controllers/ReportsController.cs` | 9591 | `DateTime? DueDate` | DateTime? DueDate, |
| `backend/Controllers/ReportsController.cs` | 9606 | `DateTime CreatedAt` | DateTime CreatedAt, |
| Еще 1835 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Backend: текущее время сервера

Вызовы Now/UtcNow, которые надо привести к единому источнику времени.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 26 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ActsController.cs` | 455 | `DateTime.UtcNow` | act.SentForSigningAt ??= DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 456 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 478 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 509 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 555 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 636 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 692 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 972 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 973 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1066 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 1275 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1799 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 2082 | `DateTime.UtcNow` | return $"{baseNumber}-К{DateTime.UtcNow:yyyyMMddHHmmss}"; |
| `backend/Controllers/AppVersionController.cs` | 115 | `DateTime.UtcNow` | SeenAt = DateTime.UtcNow, |
| `backend/Controllers/AppVersionController.cs` | 162 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/AppVersionController.cs` | 218 | `DateTime.UtcNow` | release.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/BanksController.cs` | 106 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/BanksController.cs` | 147 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CalendarSyncController.cs` | 111 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/CalendarSyncController.cs` | 113 | `DateTime.UtcNow` | normalized.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CalendarSyncController.cs` | 597 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 419 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 436 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 531 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 592 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 628 | `DateTime.UtcNow` | model.CancelledAt = model.Status == CashOrderStatus.Cancelled ? model.CancelledAt ?? DateTime.UtcNow : null; |
| `backend/Controllers/CashOrdersController.cs` | 630 | `DateTime.UtcNow` | model.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 686 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 763 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 797 | `DateTime.UtcNow` | order.CancelledAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 799 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 848 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 991 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientBalancesController.cs` | 137 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/ClientPassportController.cs` | 104 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ClientsController.cs` | 69 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientsController.cs` | 1188 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1189 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1280 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1281 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1341 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1355 | `DateTime.UtcNow` | current.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ClientsController.cs` | 164 | `DateTimeOffset.UtcNow` | var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); |
| `backend/Controllers/ContactsController.cs` | 245 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 333 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 465 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 844 | `DateTime.UtcNow` | contact.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 538 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 655 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 676 | `DateTime.UtcNow` | contract.SentForSigningAt ??= DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 677 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 711 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 743 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 798 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 911 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 955 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 986 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1003 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1064 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 1130 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DadataIntegrationSettingsController.cs` | 69 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 449 | `DateTime.UtcNow` | m.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 460 | `DateTime.UtcNow` | user.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 494 | `DateTime.UtcNow` | user.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 1267 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 1297 | `DateTime.UtcNow` | "countries" => new CountryDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1298 | `DateTime.UtcNow` | "base-currencies" => new BaseCurrencyDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1299 | `DateTime.UtcNow` | "service-languages" => new ServiceLanguageDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1300 | `DateTime.UtcNow` | "organization-forms" => new OrganizationFormDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1301 | `DateTime.UtcNow` | "timezones" => new TimezoneDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1302 | `DateTime.UtcNow` | "tax-systems" => new TaxSystemDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1303 | `DateTime.UtcNow` | "tax-system-modes" => new TaxSystemModeDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1304 | `DateTime.UtcNow` | "invoice-units" => new InvoiceUnitDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1305 | `DateTime.UtcNow` | "invoice-item-names" => new InvoiceItemNameDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/FormRequirementsController.cs` | 321 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/GoogleCalendarController.cs` | 142 | `DateTime.UtcNow` | Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((ex.AvailableAt - DateTime.UtcNow).TotalSeconds)).ToString(); |
| `backend/Controllers/InvoicesController.cs` | 101 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/InvoicesController.cs` | 597 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 712 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 742 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 771 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 835 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 942 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1402 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 1434 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 1437 | `DateTime.UtcNow` | ApplyInvoiceWorkflowStatus(payment, workflowStatus, DateTime.UtcNow, reason: null); |
| `backend/Controllers/InvoicesController.cs` | 1463 | `DateTime.UtcNow` | var linkedAt = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1635 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1918 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 2016 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2076 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2112 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2258 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 2777 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2830 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 3045 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 4462 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 4769 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 5285 | `DateTime.UtcNow` | PaymentDomainService.ApplyStatusRules(payment, DateTime.UtcNow, invoiceTimeZone); |
| `backend/Controllers/InvoicesController.cs` | 5801 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 5822 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 6255 | `DateTime.UtcNow` | InvoicePaymentStateService.CalculateRepresentativeState(representative, resultingInstallments, DateTime.UtcNow)); |
| `backend/Controllers/LegalEntitiesController.cs` | 211 | `DateTime.UtcNow` | var nowUtc = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 634 | `DateTime.UtcNow` | SetProperty(created, "CreatedAt", DateTime.UtcNow); |
| `backend/Controllers/LegalEntitiesController.cs` | 1150 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 1151 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 2091 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 2260 | `DateTime.UtcNow` | existingAddress.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 2308 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 2309 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntityBalancesController.cs` | 137 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 473 | `DateTime.UtcNow` | var receivedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 85 | `DateTime.UtcNow` | normalized.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 134 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 167 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 200 | `DateTime.UtcNow` | item.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 223 | `DateTime.UtcNow` | fallback.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 459 | `DateTime.UtcNow` | return File(bytes, "text/csv; charset=utf-8", $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"); |
| `backend/Controllers/MoneyOperationsController.cs` | 506 | `DateTime.UtcNow` | $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xls"); |
| `backend/Controllers/MoneyOperationsController.cs` | 600 | `DateTime.UtcNow` | return File(pdfBytes, "application/pdf", $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf"); |
| `backend/Controllers/MoneyOperationsController.cs` | 849 | `DateTime.UtcNow` | var checkedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1909 | `DateTime.UtcNow` | operation.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1948 | `DateTime.UtcNow` | operation.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1952 | `DateTime.UtcNow` | operation.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2014 | `DateTime.UtcNow` | model.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2022 | `DateTime.UtcNow` | model.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2076 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2160 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/MoneyOperationsController.cs` | 2304 | `DateTime.UtcNow` | var checkedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2526 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2536 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2545 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2555 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2563 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2568 | `DateTime.UtcNow` | batch.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2636 | `DateTime.UtcNow` | $"{batch.FileHash}:reprocess:{batch.Id}:{DateTime.UtcNow:O}", |
| `backend/Controllers/MoneyOperationsController.cs` | 2749 | `DateTime.UtcNow` | entity.MatchedAt = entity.MatchedAt ?? DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2767 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2809 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2832 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2846 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2865 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 2877 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3507 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 3656 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 3674 | `DateTime.UtcNow` | operation.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3762 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3914 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 4624 | `DateTime.UtcNow` | var note = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — зачтено {movementLabel} из раздела \"Деньги\" ({sourceLabel}) на сумму {appliedAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}."; |
| `backend/Controllers/MoneyOperationsController.cs` | 4648 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 4662 | `DateTime.UtcNow` | var overpaymentNote = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — переплата {overpaymentAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} зачислена на баланс клиента."; |
| `backend/Controllers/MoneyOperationsController.cs` | 4666 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, payment, DateTime.UtcNow, ct); |
| `backend/Controllers/NotificationInboxController.cs` | 51 | `DateTime.UtcNow` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 189 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 217 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 274 | `DateTime.UtcNow` | item.ArchivedAt = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 294 | `DateTime.UtcNow` | item.SnoozedUntil = DateTime.UtcNow.AddMinutes(minutes); |
| `backend/Controllers/NotificationInboxController.cs` | 310 | `DateTime.UtcNow` | item.DeletedAt ??= DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 440 | `DateTime.UtcNow` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 809 | `DateTime.UtcNow` | item.ReadAt = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 840 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/NotificationsController.cs` | 217 | `DateTime.UtcNow` | var nowUtc = DateTime.UtcNow; |
| `backend/Controllers/NotificationsController.cs` | 586 | `DateTime.UtcNow` | user.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 435 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 512 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 709 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 807 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 846 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 888 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 1173 | `DateTime.UtcNow` | await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(_db, payment, DateTime.UtcNow, _notificationService, ct); |
| `backend/Controllers/PaymentsController.cs` | 1347 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 1522 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 1719 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 1969 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 2031 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 4514 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, invoiceId.Value, DateTime.UtcNow, ct); |
| `backend/Controllers/PowerOfAttorneyController.cs` | 68 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ProfileController.cs` | 212 | `DateTime.UtcNow` | var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}"; |
| `backend/Controllers/PushSubscriptionsController.cs` | 154 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PushSubscriptionsController.cs` | 240 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PushSubscriptionsController.cs` | 273 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PushSubscriptionsController.cs` | 296 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ReportsController.cs` | 174 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 180 | `DateTime.UtcNow` | var nowUtc = NormalizeReportInstant(DateTime.UtcNow); |
| `backend/Controllers/ReportsController.cs` | 197 | `DateTime.UtcNow` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 557 | `DateTime.UtcNow` | var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date); |
| `backend/Controllers/ReportsController.cs` | 591 | `DateTime.UtcNow` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 627 | `DateTime.UtcNow` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 1303 | `DateTime.UtcNow` | var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 2326 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 2468 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 4447 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ReportsController.cs` | 4673 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 4848 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 5027 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 5220 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/RolePermissionsController.cs` | 58 | `DateTime.UtcNow` | GeneratedAtUtc = DateTime.UtcNow, |
| `backend/Controllers/RolePermissionsController.cs` | 90 | `DateTime.UtcNow` | var fileName = $"role-permissions-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"; |
| `backend/Controllers/RolePermissionsController.cs` | 537 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/RolePermissionsController.cs` | 766 | `DateTime.UtcNow` | role.PermissionsUpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/RolesController.cs` | 115 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/RolesController.cs` | 172 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/S3StorageController.cs` | 337 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/StickyNotesController.cs` | 837 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/SystemJobsController.cs` | 113 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 203 | `DateTime.UtcNow` | var startedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 334 | `DateTime.UtcNow` | var targetDate = MoneyBalanceReconciliationSchedulerService.ResolveReconciliationTargetDate(DateTime.UtcNow, timeZone); |
| `backend/Controllers/SystemJobsController.cs` | 364 | `DateTime.UtcNow` | var finishedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 589 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes); |
| `backend/Controllers/SystemJobsController.cs` | 658 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddDays(-retentionDays); |
| `backend/Controllers/SystemJobsController.cs` | 716 | `DateTime.UtcNow` | var criticalCutoff = DateTime.UtcNow.Date.AddDays(-criticalAgeDays); |
| `backend/Controllers/SystemJobsController.cs` | 808 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddDays(-retentionDays); |
| `backend/Controllers/SystemJobsController.cs` | 1226 | `DateTime.UtcNow` | SystemJobKeys.TaskReminders => await BuildTaskReminderJobAsync(DateTime.UtcNow, ct), |
| `backend/Controllers/SystemJobsController.cs` | 1233 | `DateTime.UtcNow` | SystemJobKeys.CaseInactivityControl => await BuildCaseControlJobAsync(settings, DateTime.UtcNow, ct), |
| `backend/Controllers/SystemJobsController.cs` | 1251 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes); |
| `backend/Controllers/TasksController.cs` | 221 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 288 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 386 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 481 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 626 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 862 | `DateTime.UtcNow` | var generatedAtUtc = NormalizeUtcInstant(DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 921 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1095 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1265 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1517 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1607 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1646 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1769 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1863 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 1868 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1988 | `DateTime.UtcNow` | var decidedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2082 | `DateTime.UtcNow` | var decidedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2317 | `DateTime.UtcNow` | UploadedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 2354 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2459 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2506 | `DateTime.UtcNow` | var today = DateTime.UtcNow.Date; |
| `backend/Controllers/TasksController.cs` | 2603 | `DateTime.UtcNow` | var createdAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2640 | `DateTime.UtcNow` | ControlMarkedAt = request.IsUnderControl ? DateTime.UtcNow : null, |
| `backend/Controllers/TasksController.cs` | 2791 | `DateTime.UtcNow` | var revisionDueAtValidation = ValidateRevisionDueAt(nextStatus, requestDueAt, DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 2803 | `DateTime.UtcNow` | var updatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2964 | `DateTime.UtcNow` | var revisionDueAtValidation = ValidateRevisionDueAt(nextStatus, requestedDueAt, DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 2974 | `DateTime.UtcNow` | entity.ApprovalDueAt = ResolveApprovalDueAt(nextStatus, null, settings.ApprovalDueDays, DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 2982 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3108 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3205 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3542 | `DateTime.UtcNow` | participant.RespondedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3543 | `DateTime.UtcNow` | participant.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3567 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 4904 | `DateTime.UtcNow` | var today = DateTime.UtcNow.Date; |
| Еще 2417 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Backend: преобразования timezone

Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 12176 | `timezone` | "releaseId": "2026-06-07-external-calendar-profile-timezone", |
| `backend/AppReleases/releases.json` | 12190 | `timezone` | "text": "Ранее подключенные календари Google Calendar и Яндекс.Календаря при следующей синхронизации также обновляются до часового пояса владельца, чтобы события отображались без ручной правки timezone у провайдера." |
| `backend/AppReleases/releases.json` | 12328 | `timezone` | "releaseId": "2026-06-05-auto-detect-profile-timezone", |
| `backend/AppReleases/releases.json` | 12347 | `timezone` | "releaseId": "2026-06-05-server-timezone-fallback", |
| `backend/AppReleases/releases.json` | 12385 | `timezone` | "releaseId": "2026-06-05-timezone-roadmap-regressions", |
| `backend/AppReleases/releases.json` | 12408 | `timezone` | "releaseId": "2026-06-05-timezone-core-rules-regression", |
| `backend/AppReleases/releases.json` | 12427 | `timezone` | "releaseId": "2026-06-05-case-court-deadline-notifications-timezone", |
| `backend/AppReleases/releases.json` | 12446 | `timezone` | "releaseId": "2026-06-05-report-month-presets-task-planning-timezone", |
| `backend/AppReleases/releases.json` | 12465 | `timezone` | "releaseId": "2026-06-05-report-exports-generated-at-timezone", |
| `backend/AppReleases/releases.json` | 12522 | `timezone` | "releaseId": "2026-06-05-client-case-reports-profile-timezone", |
| `backend/AppReleases/releases.json` | 12545 | `timezone` | "releaseId": "2026-06-05-financial-reports-profile-timezone", |
| `backend/AppReleases/releases.json` | 12564 | `timezone` | "releaseId": "2026-06-05-payments-export-profile-timezone", |
| `backend/AppReleases/releases.json` | 12587 | `timezone` | "releaseId": "2026-06-05-case-expenses-profile-timezone", |
| `backend/AppReleases/releases.json` | 12606 | `timezone` | "releaseId": "2026-06-05-client-debt-profile-timezone", |
| `backend/AppReleases/releases.json` | 12625 | `timezone` | "releaseId": "2026-06-05-payment-overdue-profile-timezone", |
| `backend/AppReleases/releases.json` | 12724 | `timezone` | "releaseId": "2026-06-04-client-contact-profile-timezone", |
| `backend/AppReleases/releases.json` | 12743 | `timezone` | "releaseId": "2026-06-04-money-journal-profile-timezone", |
| `backend/AppReleases/releases.json` | 12762 | `timezone` | "releaseId": "2026-06-04-legal-work-export-profile-timezone", |
| `backend/AppReleases/releases.json` | 12781 | `timezone` | "releaseId": "2026-06-04-report-date-timezone-formatting", |
| `backend/AppReleases/releases.json` | 12999 | `timezone` | "releaseId": "2026-06-04-case-reports-timezone", |
| `backend/AppReleases/releases.json` | 13022 | `timezone` | "releaseId": "2026-06-04-case-registry-export-timezone", |
| `backend/AppReleases/releases.json` | 13045 | `timezone` | "releaseId": "2026-06-04-case-document-print-forms-timezone", |
| `backend/AppReleases/releases.json` | 13068 | `timezone` | "releaseId": "2026-06-04-case-documents-uploaded-at-timezone", |
| `backend/AppReleases/releases.json` | 13091 | `timezone` | "releaseId": "2026-06-04-case-history-profile-timezone", |
| `backend/AppReleases/releases.json` | 13114 | `timezone` | "releaseId": "2026-06-04-case-detail-profile-timezone", |
| `backend/AppReleases/releases.json` | 13137 | `timezone` | "releaseId": "2026-06-04-court-sync-source-timezone", |
| `backend/AppReleases/releases.json` | 13160 | `timezone` | "releaseId": "2026-06-04-case-calendar-profile-timezone", |
| `backend/AppReleases/releases.json` | 13183 | `timezone` | "releaseId": "2026-06-04-external-calendar-reverse-sync-timezone-log", |
| `backend/AppReleases/releases.json` | 13206 | `timezone` | "releaseId": "2026-06-04-external-calendar-assignee-timezone-regression", |
| `backend/AppReleases/releases.json` | 13229 | `timezone` | "releaseId": "2026-06-04-task-calendar-assignee-timezone-completed", |
| `backend/AppReleases/releases.json` | 13252 | `timezone` | "releaseId": "2026-06-04-task-calendar-drag-resize-profile-timezone", |
| `backend/AppReleases/releases.json` | 13275 | `timezone` | "releaseId": "2026-06-04-task-calendar-profile-timezone-today", |
| `backend/AppReleases/releases.json` | 13298 | `timezone` | "releaseId": "2026-06-04-task-contractor-payment-exact-date-timezone", |
| `backend/AppReleases/releases.json` | 13317 | `timezone` | "releaseId": "2026-06-04-calendar-sync-log-filter-profile-timezone", |
| `backend/AppReleases/releases.json` | 13336 | `timezone` | "releaseId": "2026-06-04-settings-integrations-profile-timezone", |
| `backend/AppReleases/releases.json` | 13359 | `timezone` | "releaseId": "2026-06-04-admin-users-roles-profile-timezone", |
| `backend/AppReleases/releases.json` | 13386 | `timezone` | "releaseId": "2026-06-04-audit-logs-profile-timezone", |
| `backend/AppReleases/releases.json` | 13409 | `timezone` | "releaseId": "2026-06-04-system-job-diagnostics-profile-timezone", |
| `backend/AppReleases/releases.json` | 13447 | `timezone` | "releaseId": "2026-06-04-task-notifications-recipient-timezone", |
| `backend/AppReleases/releases.json` | 13466 | `timezone` | "releaseId": "2026-06-04-money-reconciliation-profile-timezone", |
| `backend/AppReleases/releases.json` | 13485 | `timezone` | "releaseId": "2026-06-04-invoice-numbering-profile-timezone", |
| `backend/AppReleases/releases.json` | 13504 | `timezone` | "releaseId": "2026-06-04-spreadsheet-import-invoice-status-timezone", |
| `backend/AppReleases/releases.json` | 13519 | `timezone` | "releaseId": "2026-06-04-profile-birth-date-timezone", |
| `backend/AppReleases/releases.json` | 13534 | `timezone` | "releaseId": "2026-06-04-task-contractor-payment-profile-timezone", |
| `backend/AppReleases/releases.json` | 13549 | `timezone` | "releaseId": "2026-06-04-money-transfer-profile-timezone", |
| `backend/AppReleases/releases.json` | 13564 | `timezone` | "releaseId": "2026-06-04-cash-orders-profile-timezone", |
| `backend/AppReleases/releases.json` | 13579 | `timezone` | "releaseId": "2026-06-04-stats-summary-profile-timezone", |
| `backend/AppReleases/releases.json` | 13594 | `timezone` | "releaseId": "2026-06-04-reports-default-period-profile-timezone", |
| `backend/AppReleases/releases.json` | 13613 | `timezone` | "releaseId": "2026-06-04-invoice-summary-profile-timezone", |
| `backend/AppReleases/releases.json` | 13632 | `timezone` | "releaseId": "2026-06-04-payables-dashboard-profile-timezone", |
| `backend/AppReleases/releases.json` | 13647 | `timezone` | "releaseId": "2026-06-04-finance-system-notes-profile-timezone", |
| `backend/AppReleases/releases.json` | 13666 | `timezone` | "releaseId": "2026-06-04-report-generated-time-profile-timezone", |
| `backend/AppReleases/releases.json` | 13723 | `timezone` | "releaseId": "2026-06-03-calendar-utc-timezone-rule", |
| `backend/AppReleases/releases.json` | 14208 | `timezone` | "releaseId": "2026-06-02-app-release-seed-timezone-fix", |
| `backend/Controllers/AccountsController.cs` | 25 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/AccountsController.cs` | 26 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/AccountsController.cs` | 25 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/AccountsController.cs` | 26 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/CalendarSyncController.cs` | 410 | `.ToUniversalTime()` | => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); |
| `backend/Controllers/CalendarSyncController.cs` | 410 | `DateTime.SpecifyKind` | => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); |
| `backend/Controllers/CashOrdersController.cs` | 990 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/CashOrdersController.cs` | 991 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/CashOrdersController.cs` | 990 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/CashOrdersController.cs` | 991 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientPortalController.cs` | 222 | `DateTime.SpecifyKind` | Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)), |
| `backend/Controllers/ClientsController.cs` | 68 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ClientsController.cs` | 69 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientsController.cs` | 68 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ClientsController.cs` | 69 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientsController.cs` | 901 | `timezone` | timezone = result.Timezone, |
| `backend/Controllers/ClientsController.cs` | 901 | `Timezone` | timezone = result.Timezone, |
| `backend/Controllers/ClientsController.cs` | 937 | `timezone` | timezone = (string?)null, |
| `backend/Controllers/ContractsController.cs` | 72 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/DatabaseAdminController.cs` | 384 | `TimeZone` | TimeZone = overview.TimeZone, |
| `backend/Controllers/DictionariesAdminController.cs` | 233 | `Timezone` | (le, v) => le.Timezone = v, |
| `backend/Controllers/DictionariesAdminController.cs` | 234 | `Timezone` | le => le.Timezone, |
| `backend/Controllers/DictionariesAdminController.cs` | 818 | `Timezone` | if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Timezone == e.Name)) |
| `backend/Controllers/FormRequirementsController.cs` | 107 | `Timezone` | new("Timezone", "Часовой пояс", "Реквизиты: Основные"), |
| `backend/Controllers/InvoicesController.cs` | 1240 | `DateTime.SpecifyKind` | var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone); |
| `backend/Controllers/InvoicesController.cs` | 1240 | `TimeZoneInfo.` | var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone); |
| `backend/Controllers/InvoicesController.cs` | 96 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/InvoicesController.cs` | 101 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/InvoicesController.cs` | 100 | `timeZone` | var timeZone = await ResolveInvoiceTimeZoneAsync(ct); |
| `backend/Controllers/InvoicesController.cs` | 101 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/InvoicesController.cs` | 1214 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/InvoicesController.cs` | 1240 | `timeZone` | var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone); |
| `backend/Controllers/LegalEntitiesController.cs` | 397 | `Timezone` | Timezone = entity.Timezone, |
| `backend/Controllers/LegalEntitiesController.cs` | 600 | `Timezone` | entity.Timezone = await ResolveDictionaryValueAsync(_db.Timezones, entity.Timezone, "Добавлено автоматически", "#8B5CF6", ct); |
| `backend/Controllers/LegalEntitiesController.cs` | 1109 | `Timezone` | Timezone = NormalizeOptional(request.Timezone), |
| `backend/Controllers/LegalEntitiesController.cs` | 2050 | `Timezone` | entity.Timezone = NormalizeOptional(request.Timezone); |
| `backend/Controllers/LegalEntitiesController.cs` | 2234 | `Timezone` | Timezone: null); |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 474 | `.ToUniversalTime()` | var occurredAt = request.OccurredAt?.ToUniversalTime() ?? receivedAt; |
| `backend/Controllers/MoneyOperationsController.cs` | 2159 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/MoneyOperationsController.cs` | 2160 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/MoneyOperationsController.cs` | 2159 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/MoneyOperationsController.cs` | 2160 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/NotificationInboxController.cs` | 51 | `DateTime.SpecifyKind` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 440 | `DateTime.SpecifyKind` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 100 | `TimeZoneInfo.` | : TimeZoneInfo.Utc; |
| `backend/Controllers/NotificationInboxController.cs` | 821 | `TimeZoneInfo.` | var effectiveTimeZone = timeZone ?? TimeZoneInfo.Utc; |
| `backend/Controllers/NotificationInboxController.cs` | 99 | `CalendarTimeZoneHelper` | ? await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_context, userId.Value, ct) |
| `backend/Controllers/NotificationInboxController.cs` | 98 | `timeZone` | var timeZone = quietHours.Enabled |
| `backend/Controllers/NotificationInboxController.cs` | 122 | `timeZone` | Items = items.Select(item => Map(item, preferences, quietHours, timeZone)).ToList(), |
| `backend/Controllers/NotificationInboxController.cs` | 816 | `timeZone` | TimeZoneInfo? timeZone = null) |
| `backend/Controllers/NotificationInboxController.cs` | 821 | `timeZone` | var effectiveTimeZone = timeZone ?? TimeZoneInfo.Utc; |
| `backend/Controllers/PaymentsController.cs` | 58 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 173 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 174 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 179 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 181 | `CalendarTimeZoneHelper` | return (nowUtc, CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone), timeZone); |
| `backend/Controllers/ReportsController.cs` | 196 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 197 | `CalendarTimeZoneHelper` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 556 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 557 | `CalendarTimeZoneHelper` | var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date); |
| `backend/Controllers/ReportsController.cs` | 590 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 591 | `CalendarTimeZoneHelper` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 626 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 627 | `CalendarTimeZoneHelper` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 681 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 1302 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 1303 | `CalendarTimeZoneHelper` | var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 1978 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2013 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2033 | `CalendarTimeZoneHelper` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2254 | `CalendarTimeZoneHelper` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2411 | `CalendarTimeZoneHelper` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 4243 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.FormatUserDateTime(generatedAtUtc, timeZone, culture: CultureInfo.GetCultureInfo("ru-RU")); |
| `backend/Controllers/ReportsController.cs` | 4249 | `CalendarTimeZoneHelper` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4262 | `CalendarTimeZoneHelper` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 9155 | `CalendarTimeZoneHelper` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9156 | `CalendarTimeZoneHelper` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 9388 | `CalendarTimeZoneHelper` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9389 | `CalendarTimeZoneHelper` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 173 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 174 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 177 | `TimeZone` | private async Task<(DateTime Utc, DateTime Local, TimeZoneInfo TimeZone)> ResolveReportNowAsync(CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 179 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 181 | `timeZone` | return (nowUtc, CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone), timeZone); |
| `backend/Controllers/ReportsController.cs` | 196 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 197 | `timeZone` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 216 | `timeZone` | cancellationToken => BuildLegalWorkReportAsync(start, end, reportToday, timeZone, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 240 | `TimeZone` | BuildLegalWorkCsv(report!, now.Utc, now.TimeZone), |
| `backend/Controllers/ReportsController.cs` | 263 | `TimeZone` | Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildLegalWorkExcelXml(report!, now.Utc, now.TimeZone))).ToArray(), |
| `backend/Controllers/ReportsController.cs` | 289 | `TimeZone` | var pdf = await _pdfService.RenderAsync(BuildLegalWorkPrintHtml(report!, now.Utc, now.TimeZone), ct); |
| `backend/Controllers/ReportsController.cs` | 348 | `TimeZone` | Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildReportExportExcelXml(document!, now.Utc, now.TimeZone))).ToArray(), |
| `backend/Controllers/ReportsController.cs` | 408 | `TimeZone` | BuildReportExportCsv(document!, now.Utc, now.TimeZone), |
| `backend/Controllers/ReportsController.cs` | 472 | `TimeZone` | var pdf = await _pdfService.RenderAsync(BuildReportExportPrintHtml(document!, now.Utc, now.TimeZone), ct); |
| `backend/Controllers/ReportsController.cs` | 530 | `TimeZone` | return Content(BuildReportExportPrintHtml(document!, now.Utc, now.TimeZone), "text/html; charset=utf-8", Encoding.UTF8); |
| `backend/Controllers/ReportsController.cs` | 556 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 557 | `timeZone` | var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date); |
| `backend/Controllers/ReportsController.cs` | 562 | `timeZone` | cancellationToken => BuildCaseFinanceReportAsync(start, end, timeZone, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 590 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 591 | `timeZone` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 597 | `timeZone` | cancellationToken => BuildCaseSuccessReportAsync(start, end, reportToday, timeZone, filters, effectiveLimit, casesVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 626 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 627 | `timeZone` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 631 | `timeZone` | cancellationToken => BuildLegalWorkReportAsync(start, end, reportToday, timeZone, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 681 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 700 | `timeZone` | cancellationToken => BuildCaseFinanceReportAsync(start, end, timeZone, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1093 | `timeZone` | cancellationToken => BuildBankStatementImportReportAsync(start, end, timeZone, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, cancellati |
| `backend/Controllers/ReportsController.cs` | 1108 | `timeZone` | cancellationToken => BuildSpreadsheetImportReportAsync(start, end, timeZone, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1125 | `timeZone` | cancellationToken => BuildCaseSuccessReportAsync(start, end, reportToday, timeZone, filters, effectiveLimit, casesVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1142 | `timeZone` | cancellationToken => BuildCurrentCasesReportAsync(effectiveAsOf, timeZone, filters, effectiveLimit, casesVisibility, tasksVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1166 | `timeZone` | return (BuildTaskExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1183 | `timeZone` | return (BuildLawyerWorkloadExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1198 | `timeZone` | return (BuildOverdueDeadlinesExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1218 | `timeZone` | return (BuildCaseActivityExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1302 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 1303 | `timeZone` | var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 1313 | `timeZone` | timeZone, |
| `backend/Controllers/ReportsController.cs` | 1978 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 1982 | `timeZone` | timeZone, |
| `backend/Controllers/ReportsController.cs` | 2013 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2014 | `timeZone` | return await BuildSpreadsheetImportReportAsync(start, end, timeZone, cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2026 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 2033 | `timeZone` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2247 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 2254 | `timeZone` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2405 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 2411 | `timeZone` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2454 | `timeZone` | .Select(item => BuildCaseSuccessItem(item, asOf, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 2545 | `timeZone` | private static CaseSuccessItemDto BuildCaseSuccessItem(CaseSuccessQueryRow clientCase, DateTime asOf, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 2550 | `timeZone` | : ResolveReportLocalDate(clientCase.CreatedAt, timeZone); |
| `backend/Controllers/ReportsController.cs` | 3534 | `timeZone` | private static ReportExportDocument BuildTaskExportDocument(TaskReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3557 | `timeZone` | DateTimeCell(item.DueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3578 | `timeZone` | DateTimeCell(item.NearestDueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3582 | `timeZone` | private static ReportExportDocument BuildLawyerWorkloadExportDocument(LawyerWorkloadReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3610 | `timeZone` | DateTimeCell(item.NearestTaskDueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3613 | `timeZone` | : DateTimeCell(item.NearestDeadlineAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3617 | `timeZone` | private static ReportExportDocument BuildOverdueDeadlinesExportDocument(OverdueProceduralDeadlinesReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3636 | `timeZone` | : DateTimeCell(item.DueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3654 | `timeZone` | private static ReportExportDocument BuildCaseActivityExportDocument(CaseActivityReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3676 | `timeZone` | DateTimeCell(item.LastActivityAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3715 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3729 | `timeZone` | AppendLegalWorkExcelRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]); |
| `backend/Controllers/ReportsController.cs` | 3758 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3763 | `timeZone` | AppendCsvRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]); |
| `backend/Controllers/ReportsController.cs` | 3790 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3818 | `timeZone` | .Append(Html(GeneratedAtCell(generatedAtUtc, timeZone))) |
| `backend/Controllers/ReportsController.cs` | 3858 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3885 | `timeZone` | GeneratedAtCell(generatedAtUtc, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3921 | `timeZone` | DateCell(item.CreatedAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3978 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3992 | `timeZone` | AppendLegalWorkExcelRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]); |
| `backend/Controllers/ReportsController.cs` | 4031 | `timeZone` | DateCell(item.CreatedAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 4096 | `timeZone` | LegalWorkDeadlineDateCell(item, timeZone), |
| `backend/Controllers/ReportsController.cs` | 4112 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4141 | `timeZone` | .Append(Html(GeneratedAtCell(generatedAtUtc, timeZone))) |
| `backend/Controllers/ReportsController.cs` | 4192 | `timeZone` | .Append("</td><td>").Append(Html(LegalWorkDeadlineDateCell(item, timeZone))) |
| `backend/Controllers/ReportsController.cs` | 4242 | `timeZone` | private static string GeneratedAtCell(DateTime generatedAtUtc, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4243 | `timeZone` | => CalendarTimeZoneHelper.FormatUserDateTime(generatedAtUtc, timeZone, culture: CultureInfo.GetCultureInfo("ru-RU")); |
| `backend/Controllers/ReportsController.cs` | 4245 | `timeZone` | private static string DateCell(DateTime? value, TimeZoneInfo? timeZone = null) |
| `backend/Controllers/ReportsController.cs` | 4247 | `timeZone` | ? (timeZone is null |
| `backend/Controllers/ReportsController.cs` | 4249 | `timeZone` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4253 | `timeZone` | private static string LegalWorkDeadlineDateCell(LegalWorkDeadlineItemDto item, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4255 | `timeZone` | ? DateCell(item.DueAt, timeZone) |
| `backend/Controllers/ReportsController.cs` | 4258 | `timeZone` | private static string DateTimeCell(DateTime? value, TimeZoneInfo? timeZone = null) |
| `backend/Controllers/ReportsController.cs` | 4260 | `timeZone` | ? (timeZone is null |
| `backend/Controllers/ReportsController.cs` | 4262 | `timeZone` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4740 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 4835 | `timeZone` | timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4898 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4902 | `timeZone` | : ResolveReportLocalDate(clientCase.CreatedAt, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9144 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 9155 | `timeZone` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9156 | `timeZone` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 9383 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 9388 | `timeZone` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9389 | `timeZone` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/StickyNotesController.cs` | 1314 | `.ToUniversalTime()` | DateTimeKind.Local => value.Value.ToUniversalTime(), |
| `backend/Controllers/StickyNotesController.cs` | 1315 | `DateTime.SpecifyKind` | _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), |
| `backend/Controllers/SystemJobsController.cs` | 114 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 333 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 1175 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 1312 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.FormatUserDateTime(value, timeZone, culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU")); |
| `backend/Controllers/SystemJobsController.cs` | 114 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 169 | `timeZone` | await BuildNotificationDeliveryRetentionJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 171 | `timeZone` | await BuildExternalCalendarSyncJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 172 | `timeZone` | await BuildCalendarSyncLogRetentionJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 176 | `timeZone` | await BuildUserActivityRetentionJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 333 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 334 | `timeZone` | var targetDate = MoneyBalanceReconciliationSchedulerService.ResolveReconciliationTargetDate(DateTime.UtcNow, timeZone); |
| `backend/Controllers/SystemJobsController.cs` | 643 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/SystemJobsController.cs` | 687 | `timeZone` | LastActivityDescription = oldestDeliveryAt is null ? null : $"Самая старая техническая запись: {FormatSystemJobDateTime(oldestDeliveryAt.Value, timeZone)}.", |
| `backend/Controllers/SystemJobsController.cs` | 793 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/SystemJobsController.cs` | 841 | `timeZone` | LastActivityDescription = oldestLogAt is null ? null : $"Самая старая техническая запись: {FormatSystemJobDateTime(oldestLogAt.Value, timeZone)}.", |
| `backend/Controllers/SystemJobsController.cs` | 852 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/SystemJobsController.cs` | 914 | `timeZone` | LastActivityDescription = state.OldestCandidateAt is null ? null : $"Ближайшее событие-кандидат: {FormatSystemJobDateTime(state.OldestCandidateAt.Value, timeZone)}.", |
| `backend/Controllers/SystemJobsController.cs` | 1144 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/SystemJobsController.cs` | 1165 | `timeZone` | LastActivityDescription = oldestLogAt is null ? null : $"Самая старая запись журнала: {FormatSystemJobDateTime(oldestLogAt.Value, timeZone)}.", |
| Еще 1283 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Ручные смещения времени

Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 5 | `+07:00` | "releasedAt": "2026-08-05T11:39:17+07:00", |
| `backend/AppReleases/releases.json` | 24 | `+07:00` | "releasedAt": "2026-08-05T11:20:00+07:00", |
| `backend/AppReleases/releases.json` | 43 | `+07:00` | "releasedAt": "2026-08-05T10:00:00+07:00", |
| `backend/AppReleases/releases.json` | 62 | `+07:00` | "releasedAt": "2026-08-05T07:35:00+07:00", |
| `backend/AppReleases/releases.json` | 81 | `+07:00` | "releasedAt": "2026-08-05T07:01:45+07:00", |
| `backend/AppReleases/releases.json` | 104 | `+07:00` | "releasedAt": "2026-08-05T05:45:00+07:00", |
| `backend/AppReleases/releases.json` | 127 | `+07:00` | "releasedAt": "2026-08-05T04:45:00+07:00", |
| `backend/AppReleases/releases.json` | 150 | `+07:00` | "releasedAt": "2026-08-05T03:40:00+07:00", |
| `backend/AppReleases/releases.json` | 173 | `+07:00` | "releasedAt": "2026-08-05T02:36:00+07:00", |
| `backend/AppReleases/releases.json` | 196 | `+07:00` | "releasedAt": "2026-08-05T01:54:00+07:00", |
| `backend/AppReleases/releases.json` | 215 | `+07:00` | "releasedAt": "2026-08-05T01:06:00+07:00", |
| `backend/AppReleases/releases.json` | 234 | `+07:00` | "releasedAt": "2026-08-05T00:45:00+07:00", |
| `backend/AppReleases/releases.json` | 249 | `+07:00` | "releasedAt": "2026-08-05T00:20:00+07:00", |
| `backend/AppReleases/releases.json` | 264 | `+07:00` | "releasedAt": "2026-08-04T17:30:00+07:00", |
| `backend/AppReleases/releases.json` | 279 | `+07:00` | "releasedAt": "2026-08-04T15:35:00+07:00", |
| `backend/AppReleases/releases.json` | 298 | `+07:00` | "releasedAt": "2026-08-04T15:12:00+07:00", |
| `backend/AppReleases/releases.json` | 313 | `+07:00` | "releasedAt": "2026-08-04T14:55:00+07:00", |
| `backend/AppReleases/releases.json` | 328 | `+07:00` | "releasedAt": "2026-08-04T13:20:00+07:00", |
| `backend/AppReleases/releases.json` | 343 | `+07:00` | "releasedAt": "2026-08-04T12:56:00+07:00", |
| `backend/AppReleases/releases.json` | 358 | `+07:00` | "releasedAt": "2026-08-04T12:30:00+07:00", |
| `backend/AppReleases/releases.json` | 381 | `+07:00` | "releasedAt": "2026-08-04T11:45:00+07:00", |
| `backend/AppReleases/releases.json` | 400 | `+07:00` | "releasedAt": "2026-08-04T10:49:00+07:00", |
| `backend/AppReleases/releases.json` | 419 | `+07:00` | "releasedAt": "2026-08-04T10:10:00+07:00", |
| `backend/AppReleases/releases.json` | 438 | `+07:00` | "releasedAt": "2026-08-03T19:30:00+07:00", |
| `backend/AppReleases/releases.json` | 453 | `+07:00` | "releasedAt": "2026-08-01T03:38:46+07:00", |
| `backend/AppReleases/releases.json` | 476 | `+07:00` | "releasedAt": "2026-08-01T03:25:00+07:00", |
| `backend/AppReleases/releases.json` | 499 | `+07:00` | "releasedAt": "2026-08-01T02:40:28+07:00", |
| `backend/AppReleases/releases.json` | 522 | `+07:00` | "releasedAt": "2026-08-01T00:11:00+07:00", |
| `backend/AppReleases/releases.json` | 545 | `+07:00` | "releasedAt": "2026-08-01T00:10:00+07:00", |
| `backend/AppReleases/releases.json` | 564 | `+07:00` | "releasedAt": "2026-08-01T00:09:00+07:00", |
| `backend/AppReleases/releases.json` | 587 | `+07:00` | "releasedAt": "2026-08-01T00:08:00+07:00", |
| `backend/AppReleases/releases.json` | 610 | `+07:00` | "releasedAt": "2026-08-01T00:07:00+07:00", |
| `backend/AppReleases/releases.json` | 633 | `+07:00` | "releasedAt": "2026-08-01T00:06:00+07:00", |
| `backend/AppReleases/releases.json` | 656 | `+07:00` | "releasedAt": "2026-08-01T00:05:00+07:00", |
| `backend/AppReleases/releases.json` | 679 | `+07:00` | "releasedAt": "2026-08-01T00:04:00+07:00", |
| `backend/AppReleases/releases.json` | 698 | `+07:00` | "releasedAt": "2026-08-01T00:03:00+07:00", |
| `backend/AppReleases/releases.json` | 721 | `+07:00` | "releasedAt": "2026-08-01T00:02:00+07:00", |
| `backend/AppReleases/releases.json` | 744 | `+07:00` | "releasedAt": "2026-08-01T00:01:00+07:00", |
| `backend/AppReleases/releases.json` | 767 | `+07:00` | "releasedAt": "2026-08-01T00:00:00+07:00", |
| `backend/AppReleases/releases.json` | 790 | `+07:00` | "releasedAt": "2026-07-31T23:59:30+07:00", |
| `backend/AppReleases/releases.json` | 813 | `+07:00` | "releasedAt": "2026-07-31T23:59:00+07:00", |
| `backend/AppReleases/releases.json` | 836 | `+07:00` | "releasedAt": "2026-07-31T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 859 | `+07:00` | "releasedAt": "2026-07-31T23:35:00+07:00", |
| `backend/AppReleases/releases.json` | 882 | `+07:00` | "releasedAt": "2026-07-31T23:05:00+07:00", |
| `backend/AppReleases/releases.json` | 901 | `+07:00` | "releasedAt": "2026-07-31T22:45:00+07:00", |
| `backend/AppReleases/releases.json` | 928 | `+07:00` | "releasedAt": "2026-07-31T22:15:00+07:00", |
| `backend/AppReleases/releases.json` | 951 | `+07:00` | "releasedAt": "2026-07-31T21:05:00+07:00", |
| `backend/AppReleases/releases.json` | 974 | `+07:00` | "releasedAt": "2026-07-31T20:35:00+07:00", |
| `backend/AppReleases/releases.json` | 997 | `+07:00` | "releasedAt": "2026-07-31T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1020 | `+07:00` | "releasedAt": "2026-07-31T19:10:00+07:00", |
| `backend/AppReleases/releases.json` | 1043 | `+07:00` | "releasedAt": "2026-07-31T18:10:00+07:00", |
| `backend/AppReleases/releases.json` | 1066 | `+07:00` | "releasedAt": "2026-07-31T16:35:00+07:00", |
| `backend/AppReleases/releases.json` | 1089 | `+07:00` | "releasedAt": "2026-07-31T16:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1104 | `+07:00` | "releasedAt": "2026-07-31T15:25:00+07:00", |
| `backend/AppReleases/releases.json` | 1131 | `+07:00` | "releasedAt": "2026-07-31T14:35:00+07:00", |
| `backend/AppReleases/releases.json` | 1158 | `+07:00` | "releasedAt": "2026-07-31T11:15:00+07:00", |
| `backend/AppReleases/releases.json` | 1185 | `+07:00` | "releasedAt": "2026-07-31T10:30:00+07:00", |
| `backend/AppReleases/releases.json` | 1212 | `+07:00` | "releasedAt": "2026-07-31T09:30:00+07:00", |
| `backend/AppReleases/releases.json` | 1239 | `+07:00` | "releasedAt": "2026-07-31T08:04:00+07:00", |
| `backend/AppReleases/releases.json` | 1262 | `+07:00` | "releasedAt": "2026-07-30T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1285 | `+07:00` | "releasedAt": "2026-07-30T19:30:00+07:00", |
| `backend/AppReleases/releases.json` | 1304 | `+07:00` | "releasedAt": "2026-07-30T19:20:00+07:00", |
| `backend/AppReleases/releases.json` | 1327 | `+07:00` | "releasedAt": "2026-07-30T19:10:00+07:00", |
| `backend/AppReleases/releases.json` | 1346 | `+07:00` | "releasedAt": "2026-07-30T18:55:00+07:00", |
| `backend/AppReleases/releases.json` | 1365 | `+07:00` | "releasedAt": "2026-07-30T18:40:00+07:00", |
| `backend/AppReleases/releases.json` | 1384 | `+07:00` | "releasedAt": "2026-07-30T18:25:00+07:00", |
| `backend/AppReleases/releases.json` | 1407 | `+07:00` | "releasedAt": "2026-07-30T17:40:00+07:00", |
| `backend/AppReleases/releases.json` | 1430 | `+07:00` | "releasedAt": "2026-07-30T17:25:00+07:00", |
| `backend/AppReleases/releases.json` | 1453 | `+07:00` | "releasedAt": "2026-07-30T16:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1472 | `+07:00` | "releasedAt": "2026-07-30T16:30:00+07:00", |
| `backend/AppReleases/releases.json` | 1495 | `+07:00` | "releasedAt": "2026-07-30T16:10:00+07:00", |
| `backend/AppReleases/releases.json` | 1518 | `+07:00` | "releasedAt": "2026-07-30T15:30:55+07:00", |
| `backend/AppReleases/releases.json` | 1537 | `+07:00` | "releasedAt": "2026-07-30T15:12:11+07:00", |
| `backend/AppReleases/releases.json` | 1560 | `+07:00` | "releasedAt": "2026-07-30T14:53:30+07:00", |
| `backend/AppReleases/releases.json` | 1583 | `+07:00` | "releasedAt": "2026-07-30T14:39:53+07:00", |
| `backend/AppReleases/releases.json` | 1602 | `+07:00` | "releasedAt": "2026-07-30T14:14:09+07:00", |
| `backend/AppReleases/releases.json` | 1625 | `+07:00` | "releasedAt": "2026-07-30T13:49:39+07:00", |
| `backend/AppReleases/releases.json` | 1648 | `+07:00` | "releasedAt": "2026-07-30T13:14:46+07:00", |
| `backend/AppReleases/releases.json` | 1675 | `+07:00` | "releasedAt": "2026-07-30T12:43:38+07:00", |
| `backend/AppReleases/releases.json` | 1702 | `+07:00` | "releasedAt": "2026-07-30T12:16:34+07:00", |
| `backend/AppReleases/releases.json` | 1725 | `+07:00` | "releasedAt": "2026-07-30T11:51:24+07:00", |
| `backend/AppReleases/releases.json` | 1748 | `+07:00` | "releasedAt": "2026-07-30T11:18:00+07:00", |
| `backend/AppReleases/releases.json` | 1771 | `+07:00` | "releasedAt": "2026-07-30T09:50:00+07:00", |
| `backend/AppReleases/releases.json` | 1798 | `+07:00` | "releasedAt": "2026-07-30T09:30:00+07:00", |
| `backend/AppReleases/releases.json` | 1821 | `+07:00` | "releasedAt": "2026-07-30T08:18:58+07:00", |
| `backend/AppReleases/releases.json` | 1844 | `+07:00` | "releasedAt": "2026-07-30T07:51:49+07:00", |
| `backend/AppReleases/releases.json` | 1867 | `+07:00` | "releasedAt": "2026-07-30T07:13:16+07:00", |
| `backend/AppReleases/releases.json` | 1890 | `+07:00` | "releasedAt": "2026-07-30T06:38:46+07:00", |
| `backend/AppReleases/releases.json` | 1913 | `+07:00` | "releasedAt": "2026-07-30T06:09:38+07:00", |
| `backend/AppReleases/releases.json` | 1936 | `+07:00` | "releasedAt": "2026-07-30T05:30:58+07:00", |
| `backend/AppReleases/releases.json` | 1959 | `+07:00` | "releasedAt": "2026-07-30T05:14:07+07:00", |
| `backend/AppReleases/releases.json` | 1982 | `+07:00` | "releasedAt": "2026-07-30T01:16:48+07:00", |
| `backend/AppReleases/releases.json` | 2005 | `+07:00` | "releasedAt": "2026-07-30T00:55:00+07:00", |
| `backend/AppReleases/releases.json` | 2028 | `+07:00` | "releasedAt": "2026-07-30T00:16:38+07:00", |
| `backend/AppReleases/releases.json` | 2051 | `+07:00` | "releasedAt": "2026-07-29T23:41:44+07:00", |
| `backend/AppReleases/releases.json` | 2074 | `+07:00` | "releasedAt": "2026-07-29T23:21:00+07:00", |
| `backend/AppReleases/releases.json` | 2097 | `+07:00` | "releasedAt": "2026-07-29T22:38:00+07:00", |
| `backend/AppReleases/releases.json` | 2124 | `+07:00` | "releasedAt": "2026-07-29T22:25:00+07:00", |
| `backend/AppReleases/releases.json` | 2147 | `+07:00` | "releasedAt": "2026-07-29T22:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2170 | `+07:00` | "releasedAt": "2026-07-29T21:45:00+07:00", |
| `backend/AppReleases/releases.json` | 2193 | `+07:00` | "releasedAt": "2026-07-29T21:10:00+07:00", |
| `backend/AppReleases/releases.json` | 2212 | `+07:00` | "releasedAt": "2026-07-29T20:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2235 | `+07:00` | "releasedAt": "2026-07-29T20:10:00+07:00", |
| `backend/AppReleases/releases.json` | 2258 | `+07:00` | "releasedAt": "2026-07-29T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 2281 | `+07:00` | "releasedAt": "2026-07-29T19:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2304 | `+07:00` | "releasedAt": "2026-07-29T18:45:00+07:00", |
| `backend/AppReleases/releases.json` | 2327 | `+07:00` | "releasedAt": "2026-07-29T18:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2350 | `+07:00` | "releasedAt": "2026-07-29T16:05:00+07:00", |
| `backend/AppReleases/releases.json` | 2369 | `+07:00` | "releasedAt": "2026-07-27T21:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2392 | `+07:00` | "releasedAt": "2026-07-27T21:15:00+07:00", |
| `backend/AppReleases/releases.json` | 2415 | `+07:00` | "releasedAt": "2026-07-27T20:18:00+07:00", |
| `backend/AppReleases/releases.json` | 2438 | `+07:00` | "releasedAt": "2026-07-27T19:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2461 | `+07:00` | "releasedAt": "2026-07-27T18:42:00+07:00", |
| `backend/AppReleases/releases.json` | 2484 | `+07:00` | "releasedAt": "2026-07-27T18:07:00+07:00", |
| `backend/AppReleases/releases.json` | 2507 | `+07:00` | "releasedAt": "2026-07-27T17:21:09+07:00", |
| `backend/AppReleases/releases.json` | 2530 | `+07:00` | "releasedAt": "2026-07-27T13:45:00+07:00", |
| `backend/AppReleases/releases.json` | 2549 | `+07:00` | "releasedAt": "2026-07-23T15:25:00+07:00", |
| `backend/AppReleases/releases.json` | 2572 | `+07:00` | "releasedAt": "2026-07-23T14:30:31+07:00", |
| `backend/AppReleases/releases.json` | 2591 | `+07:00` | "releasedAt": "2026-07-23T14:09:00+07:00", |
| `backend/AppReleases/releases.json` | 2610 | `+07:00` | "releasedAt": "2026-07-21T15:55:00+07:00", |
| `backend/AppReleases/releases.json` | 2629 | `+07:00` | "releasedAt": "2026-07-21T14:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2652 | `+07:00` | "releasedAt": "2026-07-21T13:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2671 | `+07:00` | "releasedAt": "2026-07-21T12:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2694 | `+07:00` | "releasedAt": "2026-07-21T11:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2717 | `+07:00` | "releasedAt": "2026-07-21T09:30:00+07:00", |
| `backend/AppReleases/releases.json` | 2736 | `+07:00` | "releasedAt": "2026-07-20T21:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2755 | `+07:00` | "releasedAt": "2026-07-20T00:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2770 | `+07:00` | "releasedAt": "2026-07-17T00:50:00+07:00", |
| `backend/AppReleases/releases.json` | 3545 | `+07:00` | "releasedAt": "2026-07-16T21:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3564 | `+07:00` | "releasedAt": "2026-07-16T17:50:00+07:00", |
| `backend/AppReleases/releases.json` | 3583 | `+07:00` | "releasedAt": "2026-07-16T15:35:00+07:00", |
| `backend/AppReleases/releases.json` | 3606 | `+07:00` | "releasedAt": "2026-07-16T15:20:00+07:00", |
| `backend/AppReleases/releases.json` | 3625 | `+07:00` | "releasedAt": "2026-07-16T15:05:00+07:00", |
| `backend/AppReleases/releases.json` | 3640 | `+07:00` | "releasedAt": "2026-07-16T14:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3663 | `+07:00` | "releasedAt": "2026-07-16T13:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3682 | `+07:00` | "releasedAt": "2026-07-16T00:35:00+07:00", |
| `backend/AppReleases/releases.json` | 3701 | `+07:00` | "releasedAt": "2026-07-15T23:40:00+07:00", |
| `backend/AppReleases/releases.json` | 3728 | `+07:00` | "releasedAt": "2026-07-15T22:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3747 | `+07:00` | "releasedAt": "2026-07-15T22:40:00+07:00", |
| `backend/AppReleases/releases.json` | 3766 | `+07:00` | "releasedAt": "2026-07-15T22:10:00+07:00", |
| `backend/AppReleases/releases.json` | 3785 | `+07:00` | "releasedAt": "2026-07-15T22:05:00+07:00", |
| `backend/AppReleases/releases.json` | 3804 | `+07:00` | "releasedAt": "2026-07-15T21:25:00+07:00", |
| `backend/AppReleases/releases.json` | 3823 | `+07:00` | "releasedAt": "2026-07-15T14:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3842 | `+07:00` | "releasedAt": "2026-07-15T13:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3861 | `+07:00` | "releasedAt": "2026-07-15T12:35:00+07:00", |
| `backend/AppReleases/releases.json` | 3876 | `+07:00` | "releasedAt": "2026-07-15T10:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3903 | `+07:00` | "releasedAt": "2026-07-14T14:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3922 | `+07:00` | "releasedAt": "2026-07-14T14:32:00+07:00", |
| `backend/AppReleases/releases.json` | 3941 | `+07:00` | "releasedAt": "2026-07-14T14:31:00+07:00", |
| `backend/AppReleases/releases.json` | 3960 | `+07:00` | "releasedAt": "2026-07-14T14:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3979 | `+07:00` | "releasedAt": "2026-07-14T11:43:00+07:00", |
| `backend/AppReleases/releases.json` | 3998 | `+07:00` | "releasedAt": "2026-07-14T11:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4017 | `+07:00` | "releasedAt": "2026-07-14T10:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4044 | `+07:00` | "releasedAt": "2026-07-14T09:55:00+07:00", |
| `backend/AppReleases/releases.json` | 4063 | `+07:00` | "releasedAt": "2026-07-13T23:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4090 | `+07:00` | "releasedAt": "2026-07-10T23:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4105 | `+07:00` | "releasedAt": "2026-07-10T20:55:00+07:00", |
| `backend/AppReleases/releases.json` | 4128 | `+07:00` | "releasedAt": "2026-07-10T15:35:00+07:00", |
| `backend/AppReleases/releases.json` | 4147 | `+07:00` | "releasedAt": "2026-07-10T14:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4162 | `+07:00` | "releasedAt": "2026-07-10T11:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4177 | `+07:00` | "releasedAt": "2026-07-10T11:25:30+07:00", |
| `backend/AppReleases/releases.json` | 4192 | `+07:00` | "releasedAt": "2026-07-09T23:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4215 | `+07:00` | "releasedAt": "2026-07-09T22:50:00+07:00", |
| `backend/AppReleases/releases.json` | 4234 | `+07:00` | "releasedAt": "2026-07-09T22:40:00+07:00", |
| `backend/AppReleases/releases.json` | 4253 | `+07:00` | "releasedAt": "2026-07-09T22:30:00+07:00", |
| `backend/AppReleases/releases.json` | 4272 | `+07:00` | "releasedAt": "2026-07-09T22:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4287 | `+07:00` | "releasedAt": "2026-07-09T22:19:00+07:00", |
| `backend/AppReleases/releases.json` | 4302 | `+07:00` | "releasedAt": "2026-07-09T22:18:00+07:00", |
| `backend/AppReleases/releases.json` | 4317 | `+07:00` | "releasedAt": "2026-07-09T22:17:00+07:00", |
| `backend/AppReleases/releases.json` | 4332 | `+07:00` | "releasedAt": "2026-07-09T22:16:00+07:00", |
| `backend/AppReleases/releases.json` | 4347 | `+07:00` | "releasedAt": "2026-07-09T22:15:00+07:00", |
| `backend/AppReleases/releases.json` | 4366 | `+07:00` | "releasedAt": "2026-07-09T22:14:00+07:00", |
| `backend/AppReleases/releases.json` | 4385 | `+07:00` | "releasedAt": "2026-07-09T22:13:00+07:00", |
| `backend/AppReleases/releases.json` | 4400 | `+07:00` | "releasedAt": "2026-07-09T22:12:00+07:00", |
| `backend/AppReleases/releases.json` | 4415 | `+07:00` | "releasedAt": "2026-07-09T22:11:00+07:00", |
| `backend/AppReleases/releases.json` | 4430 | `+07:00` | "releasedAt": "2026-07-09T22:10:00+07:00", |
| `backend/AppReleases/releases.json` | 4445 | `+07:00` | "releasedAt": "2026-07-09T22:09:00+07:00", |
| `backend/AppReleases/releases.json` | 4460 | `+07:00` | "releasedAt": "2026-07-09T21:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4475 | `+07:00` | "releasedAt": "2026-07-09T21:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4490 | `+07:00` | "releasedAt": "2026-07-09T20:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4505 | `+07:00` | "releasedAt": "2026-07-09T20:10:00+07:00", |
| `backend/AppReleases/releases.json` | 4528 | `+07:00` | "releasedAt": "2026-07-09T19:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4547 | `+07:00` | "releasedAt": "2026-07-09T18:55:00+07:00", |
| `backend/AppReleases/releases.json` | 4566 | `+07:00` | "releasedAt": "2026-07-09T18:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4585 | `+07:00` | "releasedAt": "2026-07-09T17:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4604 | `+07:00` | "releasedAt": "2026-07-09T17:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4627 | `+07:00` | "releasedAt": "2026-07-09T16:43:00+07:00", |
| `backend/AppReleases/releases.json` | 4650 | `+07:00` | "releasedAt": "2026-07-09T16:35:00+07:00", |
| `backend/AppReleases/releases.json` | 4673 | `+07:00` | "releasedAt": "2026-07-09T16:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4696 | `+07:00` | "releasedAt": "2026-07-08T20:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4719 | `+07:00` | "releasedAt": "2026-07-08T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4742 | `+07:00` | "releasedAt": "2026-07-08T19:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4761 | `+07:00` | "releasedAt": "2026-07-08T19:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4780 | `+07:00` | "releasedAt": "2026-07-08T18:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4803 | `+07:00` | "releasedAt": "2026-07-08T18:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4822 | `+07:00` | "releasedAt": "2026-07-08T17:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4841 | `+07:00` | "releasedAt": "2026-07-08T17:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4860 | `+07:00` | "releasedAt": "2026-07-08T17:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4879 | `+07:00` | "releasedAt": "2026-07-08T16:40:00+07:00", |
| `backend/AppReleases/releases.json` | 4898 | `+07:00` | "releasedAt": "2026-07-08T16:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4921 | `+07:00` | "releasedAt": "2026-07-08T16:00:00+07:00", |
| `backend/AppReleases/releases.json` | 4944 | `+07:00` | "releasedAt": "2026-07-08T15:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4963 | `+07:00` | "releasedAt": "2026-07-08T14:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4986 | `+07:00` | "releasedAt": "2026-07-08T14:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5009 | `+07:00` | "releasedAt": "2026-07-08T14:05:00+07:00", |
| `backend/AppReleases/releases.json` | 5032 | `+07:00` | "releasedAt": "2026-07-08T13:35:00+07:00", |
| `backend/AppReleases/releases.json` | 5051 | `+07:00` | "releasedAt": "2026-07-08T13:10:00+07:00", |
| `backend/AppReleases/releases.json` | 5070 | `+07:00` | "releasedAt": "2026-07-08T12:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5089 | `+07:00` | "releasedAt": "2026-07-08T12:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5112 | `+07:00` | "releasedAt": "2026-07-08T12:10:00+07:00", |
| `backend/AppReleases/releases.json` | 5131 | `+07:00` | "releasedAt": "2026-07-07T18:00:00+07:00", |
| `backend/AppReleases/releases.json` | 5150 | `+07:00` | "releasedAt": "2026-07-06T16:40:00+07:00", |
| `backend/AppReleases/releases.json` | 5181 | `+07:00` | "releasedAt": "2026-07-06T13:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5204 | `+07:00` | "releasedAt": "2026-07-06T12:05:00+07:00", |
| `backend/AppReleases/releases.json` | 5223 | `+07:00` | "releasedAt": "2026-07-06T11:15:00+07:00", |
| `backend/AppReleases/releases.json` | 5242 | `+07:00` | "releasedAt": "2026-07-06T10:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5261 | `+07:00` | "releasedAt": "2026-07-05T09:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5280 | `+07:00` | "releasedAt": "2026-07-04T21:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5299 | `+07:00` | "releasedAt": "2026-07-04T20:35:00+07:00", |
| `backend/AppReleases/releases.json` | 5314 | `+07:00` | "releasedAt": "2026-07-04T20:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5329 | `+07:00` | "releasedAt": "2026-07-03T21:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5348 | `+07:00` | "releasedAt": "2026-07-03T10:20:00+07:00", |
| `backend/AppReleases/releases.json` | 5371 | `+07:00` | "releasedAt": "2026-07-02T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 5386 | `+07:00` | "releasedAt": "2026-07-02T23:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5405 | `+07:00` | "releasedAt": "2026-07-02T23:20:00+07:00", |
| `backend/AppReleases/releases.json` | 5424 | `+07:00` | "releasedAt": "2026-07-02T23:10:00+07:00", |
| `backend/AppReleases/releases.json` | 5443 | `+07:00` | "releasedAt": "2026-07-02T23:05:00+07:00", |
| `backend/AppReleases/releases.json` | 5462 | `+07:00` | "releasedAt": "2026-07-02T20:35:00+07:00", |
| `backend/AppReleases/releases.json` | 5481 | `+07:00` | "releasedAt": "2026-07-02T20:15:00+07:00", |
| `backend/AppReleases/releases.json` | 5500 | `+07:00` | "releasedAt": "2026-07-01T20:05:00+07:00", |
| `backend/AppReleases/releases.json` | 5519 | `+07:00` | "releasedAt": "2026-07-01T17:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5538 | `+07:00` | "releasedAt": "2026-07-01T17:20:00+07:00", |
| `backend/AppReleases/releases.json` | 5557 | `+07:00` | "releasedAt": "2026-07-01T16:55:00+07:00", |
| `backend/AppReleases/releases.json` | 5580 | `+07:00` | "releasedAt": "2026-07-01T16:20:00+07:00", |
| `backend/AppReleases/releases.json` | 5599 | `+07:00` | "releasedAt": "2026-07-01T15:55:00+07:00", |
| `backend/AppReleases/releases.json` | 5622 | `+07:00` | "releasedAt": "2026-07-01T14:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5649 | `+07:00` | "releasedAt": "2026-07-01T14:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5672 | `+07:00` | "releasedAt": "2026-06-30T02:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5699 | `+07:00` | "releasedAt": "2026-06-30T00:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5718 | `+07:00` | "releasedAt": "2026-06-30T00:20:00+07:00", |
| `backend/AppReleases/releases.json` | 5741 | `+07:00` | "releasedAt": "2026-06-29T23:59:00+07:00", |
| `backend/AppReleases/releases.json` | 5756 | `+07:00` | "releasedAt": "2026-06-29T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 5771 | `+07:00` | "releasedAt": "2026-06-29T23:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5790 | `+07:00` | "releasedAt": "2026-06-29T23:05:00+07:00", |
| `backend/AppReleases/releases.json` | 5809 | `+07:00` | "releasedAt": "2026-06-29T22:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5828 | `+07:00` | "releasedAt": "2026-06-29T22:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5851 | `+07:00` | "releasedAt": "2026-06-29T21:54:00+07:00", |
| `backend/AppReleases/releases.json` | 5874 | `+07:00` | "releasedAt": "2026-06-29T21:33:00+07:00", |
| `backend/AppReleases/releases.json` | 5893 | `+07:00` | "releasedAt": "2026-06-29T21:22:00+07:00", |
| `backend/AppReleases/releases.json` | 5912 | `+07:00` | "releasedAt": "2026-06-29T15:34:00+07:00", |
| Еще 620 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Frontend: работа с датами

JS/TS места, где браузер может применить локальную timezone пользователя автоматически.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `public/payplanner-push-worker.js` | 17 | `Date.parse(` | const notificationTimestamp = Date.parse(payload.createdAt \|\| ''); |
| `scripts/run-advance-acceptance-smoke.mjs` | 275 | `new Date(` | const smokeStamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14); |
| `scripts/run-advance-acceptance-smoke.mjs` | 454 | `new Date(` | const generatedAt = new Date(); |
| `scripts/run-advance-acceptance-smoke.mjs` | 275 | `.toISOString(` | const smokeStamp = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14); |
| `scripts/run-advance-acceptance-smoke.mjs` | 455 | `.toISOString(` | const stamp = generatedAt.toISOString().replace(/[:.]/g, '-'); |
| `scripts/run-advance-acceptance-smoke.mjs` | 461 | `.toISOString(` | generatedAtUtc: generatedAt.toISOString(), |
| `scripts/run-legal-e2e-smoke.mjs` | 24 | `new Date(` | const today = new Date(); |
| `scripts/run-legal-e2e-smoke.mjs` | 25 | `new Date(` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-e2e-smoke.mjs` | 26 | `new Date(` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-e2e-smoke.mjs` | 541 | `.toISOString(` | return value.toISOString().slice(0, 10); |
| `scripts/run-legal-e2e-smoke.mjs` | 25 | `formatDate` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-e2e-smoke.mjs` | 26 | `formatDate` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-e2e-smoke.mjs` | 540 | `formatDate` | function formatDate(value) { |
| `scripts/run-legal-load-check.mjs` | 21 | `new Date(` | const today = new Date(); |
| `scripts/run-legal-load-check.mjs` | 22 | `new Date(` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-load-check.mjs` | 23 | `new Date(` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-load-check.mjs` | 336 | `.toISOString(` | return value.toISOString().slice(0, 10); |
| `scripts/run-legal-load-check.mjs` | 22 | `formatDate` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-load-check.mjs` | 23 | `formatDate` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-load-check.mjs` | 335 | `formatDate` | function formatDate(value) { |
| `scripts/run-legal-readiness-report.mjs` | 77 | `new Date(` | const generatedAt = new Date(); |
| `scripts/run-legal-readiness-report.mjs` | 847 | `Date.parse(` | const timestamp = Date.parse(value); |
| `scripts/run-legal-readiness-report.mjs` | 78 | `.toISOString(` | const stamp = generatedAt.toISOString().replace(/[:.]/g, '-'); |
| `scripts/run-legal-readiness-report.mjs` | 85 | `.toISOString(` | generatedAtUtc: generatedAt.toISOString(), |
| `scripts/run-role-permission-smoke.mjs` | 20 | `new Date(` | const suffix = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14); |
| `scripts/run-role-permission-smoke.mjs` | 20 | `.toISOString(` | const suffix = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(0, 14); |
| `src/App.tsx` | 114 | `new Date(` | const now = new Date(); |
| `src/components/Accounts/Accounts.tsx` | 1163 | `new Date(` | badges.push(`Период: ${formatPeriodLabel(period, new Date())}`); |
| `src/components/Accounts/Accounts.tsx` | 1874 | `new Date(` | return new Date(payment.plannedDate) > new Date(acc) ? payment.plannedDate : acc; |
| `src/components/Accounts/Accounts.tsx` | 1880 | `new Date(` | return new Date(payment.paidDate) > new Date(acc) ? payment.paidDate : acc; |
| `src/components/Accounts/Accounts.tsx` | 2313 | `new Date(` | toDateInputValue(row.invoice?.date ?? row.dueDate) \|\| formatLocalYMD(new Date()); |
| `src/components/Accounts/Accounts.tsx` | 171 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/Accounts.tsx` | 1141 | `formatDate` | contract.date ? `от ${formatDate(contract.date)}` : null, |
| `src/components/Accounts/Accounts.tsx` | 2498 | `formatDate` | ? `Счет №${statusChangeInvoice.number} от ${formatDate(statusChangeInvoice.date)}` |
| `src/components/Accounts/Accounts.tsx` | 2995 | `formatDate` | Ближайший срок: {formatDate(supplier.nearestDueDate)} |
| `src/components/Accounts/Accounts.tsx` | 3244 | `formatDate` | {formatDate(invoice?.date)} |
| `src/components/Accounts/Accounts.tsx` | 3347 | `formatDate` | return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.dueDate)}</td>; |
| `src/components/Accounts/Accounts.tsx` | 3349 | `formatDate` | return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.paidDate)}</td>; |
| `src/components/Accounts/Accounts.tsx` | 3377 | `formatDate` | title={`${primaryAct.number ? `Акт №${primaryAct.number}` : t('act') ?? 'Акт'} · ${formatDate(primaryAct.date)} · ${primaryActStatusLabel}`} |
| `src/components/Accounts/Accounts.tsx` | 3383 | `formatDate` | {formatDate(primaryAct.date)} |
| `src/components/Accounts/Accounts.tsx` | 3437 | `formatDate` | <span className="block text-[11px] font-medium text-slate-500">{formatDate(act.date)}</span> |
| `src/components/Accounts/Accounts.tsx` | 3596 | `formatDate` | <td className="px-4 py-2">{formatDate(resolvePaymentOperationDate(payment))}</td> |
| `src/components/Accounts/InstallmentPlanner.tsx` | 145 | `new Date(` | const today = formatLocalYMD(new Date()); |
| `src/components/Accounts/InstallmentPlanner.tsx` | 787 | `type="date"` | type="date" |
| `src/components/Accounts/InstallmentPlanner.tsx` | 898 | `type="date"` | type="date" |
| `src/components/Accounts/Installments.tsx` | 46 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/Installments.tsx` | 241 | `formatDate` | case 'date': return <span className="font-medium text-slate-900">{formatDate(invoice.date)}</span>; |
| `src/components/Accounts/Installments.tsx` | 263 | `formatDate` | case 'dueDate': return formatDate(invoice.dueDate); |
| `src/components/Accounts/Installments.tsx` | 267 | `formatDate` | case 'createdAt': return formatDate(invoice.createdAt); |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 364 | `new Date(` | vi.setSystemTime(new Date('2026-06-04T22:30:00.000Z')); |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 382 | `type="date"` | const dateInput = container.querySelector('input[type="date"]') as HTMLInputElement \| null; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1543 | `type="date"` | const dateInputs = document.querySelectorAll('input[type="date"]') as NodeListOf<HTMLInputElement>; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1587 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1637 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1728 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.tsx` | 1776 | `new Date(` | (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(), |
| `src/components/Accounts/InvoiceModal.tsx` | 2464 | `new Date(` | return next.sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()); |
| `src/components/Accounts/InvoiceModal.tsx` | 3519 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Accounts/InvoiceModal.tsx` | 523 | `Date.parse(` | const leftDate = Date.parse(left.date ?? ''); |
| `src/components/Accounts/InvoiceModal.tsx` | 524 | `Date.parse(` | const rightDate = Date.parse(right.date ?? ''); |
| `src/components/Accounts/InvoiceModal.tsx` | 779 | `Date.parse(` | const fallbackTimestamp = Date.parse(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt ?? '') \|\| 0; |
| `src/components/Accounts/InvoiceModal.tsx` | 3519 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Accounts/InvoiceModal.tsx` | 5065 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5076 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5092 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5674 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5853 | `type="date"` | <SystemDateInput type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" /> |
| `src/components/Accounts/InvoiceModal.tsx` | 532 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/InvoiceModal.tsx` | 590 | `formatDate` | const dateLabel = formatDate(contract.date) \|\| 'без даты'; |
| `src/components/Accounts/InvoiceModal.tsx` | 785 | `formatDate` | timestampLabel: formatDate(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt) \|\| 'Без даты', |
| `src/components/Accounts/InvoiceModal.tsx` | 2785 | `formatDate` | const currentDueDateLabel = formatDate(currentDueDate) \|\| 'не указан'; |
| `src/components/Accounts/InvoiceModal.tsx` | 2786 | `formatDate` | const expectedDueDateLabel = formatDate(expectedDueDate); |
| `src/components/Accounts/InvoiceModal.tsx` | 3827 | `formatDate` | {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full} |
| `src/components/Accounts/InvoiceModal.tsx` | 3889 | `formatDate` | {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full} |
| `src/components/Accounts/InvoiceModal.tsx` | 3947 | `formatDate` | ? `Счёт №${form.number \|\| '—'} от ${formatDate(form.date) \|\| '—'}` |
| `src/components/Accounts/InvoiceModal.tsx` | 4163 | `formatDate` | <span>Ожидаемая дата: {formatDate(expectedPaymentDate) \|\| '—'}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4165 | `formatDate` | <span>Фактическая дата: {formatDate(actualPaymentDate) \|\| actualPaymentDate}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4257 | `formatDate` | <p><span className="text-slate-500">Дата выставления:</span> {formatDate(form.date) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4258 | `formatDate` | <p><span className="text-slate-500">Срок оплаты:</span> {formatDate(form.dueDate) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4261 | `formatDate` | <p><span className="text-slate-500">Дата оплаты:</span> {formatDate(form.paidDate) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4749 | `formatDate` | <span>Ожидаемая дата: {formatDate(expectedPaymentDate) \|\| '—'}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4751 | `formatDate` | <span>Фактическая дата: {formatDate(actualPaymentDate) \|\| actualPaymentDate}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4867 | `formatDate` | <div>{formatDate(operation.date) \|\| 'Без даты'}</div> |
| `src/components/Accounts/InvoiceModal.tsx` | 5196 | `formatDate` | {formatDate(selectedContractPreview.date) \|\| '—'} |
| `src/components/Accounts/InvoiceModal.tsx` | 5204 | `formatDate` | : (formatDate(selectedContractPreview.validUntil) \|\| '—')} |
| `src/components/Accounts/InvoiceModal.tsx` | 5330 | `formatDate` | {formatDate(selectedPaymentPreviewExpectedDate) \|\| '—'} |
| `src/components/Accounts/InvoiceModal.tsx` | 5337 | `formatDate` | {formatDate(selectedPaymentPreviewActualDate) \|\| selectedPaymentPreviewActualDate} |
| `src/components/Acts/ActDetailPage.tsx` | 84 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Acts/ActDetailPage.tsx` | 395 | `formatDate` | ? `Акт №${act.number \|\| act.id} от ${formatDate(act.date)}` |
| `src/components/Acts/ActDetailPage.tsx` | 815 | `formatDate` | Акт №{act.number \|\| '—'} от {formatDate(act.date)} |
| `src/components/Acts/ActDetailPage.tsx` | 953 | `formatDate` | №{correction.number \|\| correction.id} от {formatDate(correction.date)} |
| `src/components/Acts/ActDetailPage.tsx` | 963 | `formatDate` | <p><span className="text-slate-500">Дата акта:</span> {formatDate(act.date)}</p> |
| `src/components/Acts/ActDetailPage.tsx` | 966 | `formatDate` | <p><span className="text-slate-500">Аннулирование:</span> {[formatDate(act.cancelledAt), act.cancelledReason?.trim()].filter(Boolean).join(' · ')}</p> |
| `src/components/Acts/ActModal.tsx` | 1535 | `type="date"` | type="date" |
| `src/components/Acts/Acts.tsx` | 38 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Acts/Acts.tsx` | 626 | `formatDate` | return <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">{formatDate(act.date)}</td>; |
| `src/components/Acts/Acts.tsx` | 739 | `formatDate` | ? `Акт №${statusChangeAct.number \|\| statusChangeAct.id} от ${formatDate(statusChangeAct.date)}` |
| `src/components/AppVersion/AppVersionModal.tsx` | 38 | `new Date(` | const date = new Date(value); |
| `src/components/AppVersion/AppVersionModal.tsx` | 43 | `Intl.DateTimeFormat` | return new Intl.DateTimeFormat('ru-RU', { |
| `src/components/Calculator/Calculator.tsx` | 60 | `new Date(` | const today = new Date().toISOString().slice(0, 10); |
| `src/components/Calculator/Calculator.tsx` | 60 | `.toISOString(` | const today = new Date().toISOString().slice(0, 10); |
| `src/components/Calendar/Calendar.tsx` | 75 | `new Date(` | return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1)); |
| `src/components/Calendar/Calendar.tsx` | 79 | `new Date(` | return formatLocalYMD(new Date(y, m ?? 1, 0)); |
| `src/components/Calendar/Calendar.tsx` | 86 | `new Date(` | const [currentDate, setCurrentDate] = useState(new Date()); |
| `src/components/Calendar/Calendar.tsx` | 207 | `new Date(` | const startOfMonth = new Date(year, m0, 1); |
| `src/components/Calendar/Calendar.tsx` | 208 | `new Date(` | const endOfMonth = new Date(year, m0 + 1, 0); |
| `src/components/Calendar/Calendar.tsx` | 286 | `new Date(` | const d = new Date(prev); |
| `src/components/Calendar/CalendarGrid.tsx` | 83 | `new Date(` | const firstDayOfMonth = new Date(year, month, 1); |
| `src/components/Calendar/CalendarGrid.tsx` | 84 | `new Date(` | const lastDayOfMonth = new Date(year, month + 1, 0); |
| `src/components/Calendar/CalendarGrid.tsx` | 132 | `new Date(` | const isThisMonth = new Date().getFullYear() === year && new Date().getMonth() === month; |
| `src/components/Calendar/CalendarGrid.tsx` | 133 | `new Date(` | const today = isThisMonth ? new Date().getDate() : -1; |
| `src/components/Calendar/PaymentModal.integration.test.tsx` | 350 | `new Date(` | vi.setSystemTime(new Date('2026-06-04T20:30:00.000Z')); |
| `src/components/Calendar/PaymentModal.integration.test.tsx` | 710 | `new Date(` | vi.setSystemTime(new Date('2026-06-04T20:30:00.000Z')); |
| `src/components/Calendar/PaymentModal.tsx` | 3103 | `type="date"` | <div><label className={centeredLabelClass}>{t('expectedPaymentDate') ?? 'Expected date'}</label><SystemDateInput type="date" name="date" value={formData.date} onChange={handleChange} required className={`${centeredFieldC |
| `src/components/Calendar/PaymentModal.tsx` | 3104 | `type="date"` | <div><label className={centeredLabelClass}>{t('plannedScheduleDate') ?? 'Planned date'}</label><SystemDateInput type="date" name="plannedDate" value={formData.plannedDate} onChange={handleChange} className={centeredField |
| `src/components/Calendar/PaymentModal.tsx` | 3147 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3305 | `type="date"` | <SystemDateInput type="date" name="lastPaymentDate" value={formData.lastPaymentDate} onChange={handleChange} className={centeredFieldClass} /> |
| `src/components/Calendar/PaymentModal.tsx` | 3311 | `type="date"` | <SystemDateInput type="date" name="paidDate" value={formData.paidDate} onChange={handleChange} className={centeredFieldClass} /> |
| `src/components/Calendar/PaymentModal.tsx` | 3630 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3661 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3839 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3871 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentsTable.test.tsx` | 10 | `formatDate` | formatDate: (value: string) => value, |
| `src/components/Calendar/PaymentsTable.tsx` | 112 | `formatDate` | const { t, formatDate } = useTranslation(); |
| `src/components/Calendar/PaymentsTable.tsx` | 210 | `formatDate` | ? `Документ: ${payment.paymentOrderNumber}${payment.paymentDocumentDate ? ` от ${formatDate(payment.paymentDocumentDate)}` : ''}` |
| `src/components/Calendar/PaymentsTable.tsx` | 212 | `formatDate` | ? `Дата документа: ${formatDate(payment.paymentDocumentDate)}` |
| `src/components/Calendar/PaymentsTable.tsx` | 215 | `formatDate` | ? `Счет: ${payment.account}${payment.accountDate ? ` от ${formatDate(payment.accountDate)}` : ''}` |
| `src/components/Calendar/PaymentsTable.tsx` | 228 | `formatDate` | return payment.date ? formatDate(payment.date) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 230 | `formatDate` | return payment.plannedDate ? formatDate(payment.plannedDate) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 449 | `formatDate` | ? formatDate(payment.paidDate \|\| payment.lastPaymentDate \|\| '') |
| `src/components/Calendar/PaymentsTable.tsx` | 452 | `formatDate` | return payment.createdAt ? formatDate(payment.createdAt) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 457 | `formatDate` | [canEdit, canOpenCases, canOpenClients, canOpenUsers, formatDate, onAcceptAdvance, onAllocateAdvance, onIgnoreAdvance, onOpenClient, onOpenUser, onRefundAdvance, onStatusChange, statusLabels, t], |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1210 | `new Date(` | lastTaskAt: new Date().toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1210 | `.toISOString(` | lastTaskAt: new Date().toISOString(), |
| `src/components/Cases/CaseDetailPage.tsx` | 405 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 412 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 528 | `new Date(` | const date = new Date(getPaymentCalendarDate(payment)); |
| `src/components/Cases/CaseDetailPage.tsx` | 542 | `new Date(` | const date = new Date(year, month - 1, 1); |
| `src/components/Cases/CaseDetailPage.tsx` | 2767 | `new Date(` | const startAt = caseItem.courtDecisionAt \|\| caseItem.claimFiledAt \|\| caseItem.nextHearingAt \|\| new Date().toISOString(); |
| `src/components/Cases/CaseDetailPage.tsx` | 6038 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 6043 | `new Date(` | const day = new Date(date); |
| `src/components/Cases/CaseDetailPage.tsx` | 6045 | `new Date(` | const today = new Date(); |
| `src/components/Cases/CaseDetailPage.tsx` | 788 | `Date.parse(` | const time = Date.parse(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 853 | `Date.parse(` | const dueAt = Date.parse(task.dueAt); |
| `src/components/Cases/CaseDetailPage.tsx` | 2662 | `Date.parse(` | setCourtEvents((current) => [...current, item].sort((left, right) => Date.parse(left.scheduledAt) - Date.parse(right.scheduledAt))); |
| `src/components/Cases/CaseDetailPage.tsx` | 2749 | `Date.parse(` | setProceduralDeadlines((current) => [...current, item].sort((left, right) => Date.parse(left.dueAt) - Date.parse(right.dueAt))); |
| `src/components/Cases/CaseDetailPage.tsx` | 5280 | `Date.parse(` | const leftTime = Date.parse(left.plannedDate \|\| left.date); |
| `src/components/Cases/CaseDetailPage.tsx` | 5281 | `Date.parse(` | const rightTime = Date.parse(right.plannedDate \|\| right.date); |
| `src/components/Cases/CaseDetailPage.tsx` | 6120 | `Date.parse(` | const leftTime = Date.parse(left.value ?? ''); |
| `src/components/Cases/CaseDetailPage.tsx` | 6121 | `Date.parse(` | const rightTime = Date.parse(right.value ?? ''); |
| `src/components/Cases/CaseDetailPage.tsx` | 414 | `.toISOString(` | return date.toISOString().slice(0, 10); |
| `src/components/Cases/CaseDetailPage.tsx` | 2767 | `.toISOString(` | const startAt = caseItem.courtDecisionAt \|\| caseItem.claimFiledAt \|\| caseItem.nextHearingAt \|\| new Date().toISOString(); |
| `src/components/Cases/CaseDetailPage.tsx` | 543 | `Intl.DateTimeFormat` | return new Intl.DateTimeFormat('ru-RU', { month: 'long', year: 'numeric' }).format(date); |
| `src/components/Cases/CaseDetailPage.tsx` | 4286 | `type="date"` | <SystemDateInput type="date" value={draft.startDate} onChange={(event) => setField('startDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4290 | `type="date"` | <SystemDateInput type="date" value={draft.dueDate} onChange={(event) => setField('dueDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4377 | `type="date"` | <label className="text-sm font-medium text-slate-700">Иск подан<SystemDateInput type="date" value={draft.claimFiledAt} onChange={(event) => setField('claimFiledAt', event.target.value)} className="mt-1 h-10 w-full rounde |
| `src/components/Cases/CaseDetailPage.tsx` | 4378 | `type="date"` | <label className="text-sm font-medium text-slate-700">Решение суда<SystemDateInput type="date" value={draft.courtDecisionAt} onChange={(event) => setField('courtDecisionAt', event.target.value)} className="mt-1 h-10 w-fu |
| `src/components/Cases/CaseDetailPage.tsx` | 4379 | `type="date"` | <label className="text-sm font-medium text-slate-700">Апелляция<SystemDateInput type="date" value={draft.appealDeadlineAt} onChange={(event) => setField('appealDeadlineAt', event.target.value)} className="mt-1 h-10 w-ful |
| `src/components/Cases/CaseDetailPage.tsx` | 4380 | `type="date"` | <label className="text-sm font-medium text-slate-700">Кассация<SystemDateInput type="date" value={draft.cassationDeadlineAt} onChange={(event) => setField('cassationDeadlineAt', event.target.value)} className="mt-1 h-10  |
| `src/components/Cases/CaseDetailPage.tsx` | 4381 | `type="date"` | <label className="text-sm font-medium text-slate-700">Возражения<SystemDateInput type="date" value={draft.objectionDeadlineAt} onChange={(event) => setField('objectionDeadlineAt', event.target.value)} className="mt-1 h-1 |
| `src/components/Cases/CaseDetailPage.tsx` | 5136 | `type="date"` | <WorkOrderTextInput label="Срок" type="date" value={newItemDraft.dueDate} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, dueDate: value })} disabled={isWorkOrderLocked} /> |
| `src/components/Cases/CaseDetailPage.tsx` | 5162 | `type="date"` | <WorkOrderTextInput label="Срок" type="date" value={itemDraft.dueDate} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, dueDate: value })} disabled={isWorkOrderLocked} /> |
| `src/components/Cases/CaseDetailPage.tsx` | 5225 | `type="date"` | type="date" |
| `src/components/Cases/CaseDetailPage.tsx` | 6721 | `type="date"` | <SystemDateInput type="date" value={courtComplaintDraft.filedAt} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, filedAt: event.target.value })} className="w-full rounded-lg border border-slate |
| `src/components/Cases/CaseDetailPage.tsx` | 6966 | `type="date"` | <SystemDateInput type="date" value={deadlineDueAtDraft} onChange={(event) => onDeadlineDueAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4337 | `type="datetime-local"` | <SystemDateInput type="datetime-local" value={draft.nextHearingAt} onChange={(event) => setField('nextHearingAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 6876 | `type="datetime-local"` | <SystemDateInput type="datetime-local" value={courtEventScheduledAtDraft} onChange={(event) => onCourtEventScheduledAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Cases/CasesRegistry.tsx` | 248 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CasesRegistry.tsx` | 257 | `new Date(` | return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 262 | `new Date(` | const date = new Date(latestActivity); |
| `src/components/Cases/CasesRegistry.tsx` | 297 | `new Date(` | const nextControlAt = new Date(item.nextInactivityControlAt); |
| `src/components/Cases/CasesRegistry.tsx` | 730 | `new Date(` | updatedAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1419 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 257 | `.toISOString(` | return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 730 | `.toISOString(` | updatedAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1419 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1647 | `type="date"` | <SystemDateInput type="date" value={from} onChange={(event) => setFrom(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата с" /> |
| `src/components/Cases/CasesRegistry.tsx` | 1648 | `type="date"` | <SystemDateInput type="date" value={to} onChange={(event) => setTo(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата по" /> |
| `src/components/Cases/ClientPortalAccessPanel.tsx` | 132 | `new Date(` | {audit.length > 0 ? <details className="mt-3 rounded-lg border border-slate-200 px-3 py-2"><summary className="cursor-pointer text-sm font-medium text-slate-700">Последние действия ({audit.length})</summary><div classNam |
| `src/components/Clients/CaseModal.tsx` | 128 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 135 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 144 | `new Date(` | return value ? new Date(`${value}T00:00:00`).toISOString() : null; |
| `src/components/Clients/CaseModal.tsx` | 186 | `new Date(` | const daysInTargetMonth = new Date(targetYear, targetMonthIndex + 1, 0).getDate(); |
| `src/components/Clients/CaseModal.tsx` | 187 | `new Date(` | let targetDate = new Date(targetYear, targetMonthIndex, Math.min(day, daysInTargetMonth)); |
| `src/components/Clients/CaseModal.tsx` | 190 | `new Date(` | targetDate = new Date(targetDate.getFullYear(), targetDate.getMonth(), targetDate.getDate() + 1); |
| `src/components/Clients/CaseModal.tsx` | 211 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 228 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 136 | `.toISOString(` | return Number.isNaN(date.getTime()) ? String(value).slice(0, 10) : date.toISOString().slice(0, 10); |
| `src/components/Clients/CaseModal.tsx` | 144 | `.toISOString(` | return value ? new Date(`${value}T00:00:00`).toISOString() : null; |
| `src/components/Clients/CaseModal.tsx` | 1165 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1177 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1570 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1582 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1594 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1606 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1618 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1630 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1524 | `type="datetime-local"` | type="datetime-local" |
| `src/components/Clients/ClientAddressEntriesForm.tsx` | 176 | `type="date"` | type="date" |
| `src/components/Clients/ClientAddressEntriesForm.tsx` | 189 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.integration.test.tsx` | 1892 | `new Date(` | const today = new Date(); |
| `src/components/Clients/ClientDetail.integration.test.tsx` | 2162 | `new Date(` | const today = new Date(); |
| `src/components/Clients/ClientDetail.tsx` | 609 | `new Date(` | return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1)); |
| `src/components/Clients/ClientDetail.tsx` | 613 | `new Date(` | return formatLocalYMD(new Date(y, m ?? 1, 0)); |
| `src/components/Clients/ClientDetail.tsx` | 648 | `new Date(` | return formatter.format(new Date(yearRaw, monthRaw - 1, 1)); |
| `src/components/Clients/ClientDetail.tsx` | 665 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/ClientDetail.tsx` | 946 | `new Date(` | issuedAt: formatLocalYMD(new Date()), |
| `src/components/Clients/ClientDetail.tsx` | 2219 | `new Date(` | const today = useMemo(() => new Date(), []); |
| `src/components/Clients/ClientDetail.tsx` | 3531 | `new Date(` | const leftDate = left.caseCreatedAt ? new Date(left.caseCreatedAt).getTime() : 0; |
| `src/components/Clients/ClientDetail.tsx` | 3532 | `new Date(` | const rightDate = right.caseCreatedAt ? new Date(right.caseCreatedAt).getTime() : 0; |
| `src/components/Clients/ClientDetail.tsx` | 3598 | `new Date(` | new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 3689 | `new Date(` | new Date(b.date).getTime() - new Date(a.date).getTime() \|\| |
| `src/components/Clients/ClientDetail.tsx` | 3698 | `new Date(` | new Date(b.date).getTime() - new Date(a.date).getTime() \|\| |
| `src/components/Clients/ClientDetail.tsx` | 3709 | `new Date(` | new Date(b.date).getTime() - new Date(a.date).getTime() \|\| |
| `src/components/Clients/ClientDetail.tsx` | 5154 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 10805 | `new Date(` | {new Date(item.createdAt).toLocaleString("ru-RU")} |
| `src/components/Clients/ClientDetail.tsx` | 3598 | `.toISOString(` | new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 5154 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 639 | `Intl.DateTimeFormat` | const formatter = new Intl.DateTimeFormat("ru-RU", { |
| `src/components/Clients/ClientDetail.tsx` | 6482 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 7039 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 7054 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 7268 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 7395 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 9660 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 9669 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 10109 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 10118 | `type="date"` | type="date" |
| `src/components/Clients/ClientPassportEntriesForm.tsx` | 91 | `type="date"` | type="date" |
| `src/components/Clients/ClientPassportEntriesForm.tsx` | 114 | `type="date"` | type="date" |
| `src/components/Clients/ClientPassportEntriesForm.tsx` | 125 | `type="date"` | type="date" |
| `src/components/Clients/clientQuickModalUtils.test.ts` | 20 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/clientQuickModalUtils.test.ts` | 20 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/ClientReconciliationAct.tsx` | 786 | `new Date(` | setSavedLabel(`Черновик сохранён ${new Date().toLocaleTimeString("ru-RU")}`); |
| `src/components/Clients/ClientReconciliationAct.tsx` | 905 | `type="date"` | type="date" |
| `src/components/Clients/ClientReconciliationAct.tsx` | 914 | `type="date"` | type="date" |
| `src/components/Clients/ClientReconciliationAct.tsx` | 1305 | `type="date"` | type="date" |
| `src/components/Clients/Clients.tsx` | 1747 | `new Date(` | return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(); |
| `src/components/Clients/Clients.tsx` | 2041 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/Clients.tsx` | 2041 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/Clients.tsx` | 41 | `formatDate` | import { formatClientDate as formatDate } from './clientFormatters'; |
| `src/components/Clients/Clients.tsx` | 1190 | `formatDate` | return <span className="text-sm text-gray-600 block truncate">{formatDate(client.createdAt, profileTimeZoneId)}</span>; |
| `src/components/Clients/Clients.tsx` | 1903 | `formatDate` | {t('created') \|\| 'Создано'}: {formatDate(c.createdAt, profileTimeZoneId)} |
| `src/components/Contacts/UnifiedContacts.tsx` | 25 | `formatDate` | import { formatContactDate as formatDate } from './contactFormatters'; |
| `src/components/Contacts/UnifiedContacts.tsx` | 1057 | `formatDate` | return formatDate(item.createdAt, profileTimeZoneId) \|\| '—'; |
| `src/components/Contacts/UnifiedContacts.tsx` | 1059 | `formatDate` | return formatDate(client?.auditUpdatedAt \|\| legal?.updatedAt \|\| legal?.auditUpdatedAt, profileTimeZoneId) \|\| '—'; |
| `src/components/Contacts/UnifiedContacts.tsx` | 1230 | `formatDate` | <span>{formatDate(item.createdAt, profileTimeZoneId)}</span> |
| `src/components/Contracts/ContractCreatePage.tsx` | 319 | `type="date"` | <SystemDateInput type="date" value={form.date} onChange={(e) => setForm((prev) => ({ ...prev, date: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required /> |
| `src/components/Contracts/ContractCreatePage.tsx` | 323 | `type="date"` | <SystemDateInput type="date" value={form.startDate} onChange={(e) => setForm((prev) => ({ ...prev, startDate: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Contracts/ContractCreatePage.tsx` | 364 | `type="date"` | <SystemDateInput type="date" value={form.validUntil} onChange={(e) => setForm((prev) => ({ ...prev, validUntil: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" disabled={form.isOpenEn |
| Еще 558 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Фильтры и периоды

Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 34 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 35 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 497 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 498 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 34 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 35 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 497 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 498 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 735 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 736 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/ActsController.cs` | 192 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 193 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 764 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 765 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 192 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 193 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 764 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 765 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CalendarSyncController.cs` | 144 | `[FromQuery] DateTime?` | [FromQuery] DateTime? createdFrom = null, |
| `backend/Controllers/CalendarSyncController.cs` | 145 | `[FromQuery] DateTime?` | [FromQuery] DateTime? createdTo = null, |
| `backend/Controllers/CashOrdersController.cs` | 42 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 43 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 78 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 79 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 116 | `[FromQuery] DateTime?` | [FromQuery] DateTime? date, |
| `backend/Controllers/CashOrdersController.cs` | 137 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 138 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 183 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 184 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 253 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 254 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 318 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 319 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 42 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 43 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 78 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 79 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 137 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 138 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 183 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 184 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 253 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 254 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 318 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 319 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 995 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 996 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1192 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 1193 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1818 | `DateTime? from` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/CashOrdersController.cs` | 1818 | `DateTime? to` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/ContractsController.cs` | 308 | `[FromQuery] DateTime?` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 1011 | `[FromQuery] DateTime` | public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 308 | `DateTime? from` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 308 | `DateTime? to` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 2105 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/ContractsController.cs` | 2106 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 241 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 242 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 368 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 369 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 2030 | `[FromQuery] DateTime?` | public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 241 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 242 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 368 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 369 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3284 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 3285 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 4304 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 4305 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 6457 | `startDate` | var startDate = childPayments.Count == 0 |
| `backend/Controllers/InvoicesController.cs` | 6484 | `startDate` | StartDate = startDate, |
| `backend/Controllers/MoneyOperationsController.cs` | 201 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 202 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 241 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 242 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 414 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 415 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 464 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 465 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 511 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 512 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 558 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 559 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 637 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 638 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 743 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 744 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 765 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 766 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 814 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 815 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 201 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 202 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 241 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 242 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 414 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 415 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 464 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 465 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 511 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 512 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 558 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 559 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 637 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 638 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 743 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 744 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 765 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 766 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 814 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 815 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 917 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 918 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1022 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1023 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1359 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1360 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1468 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1469 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3269 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 3270 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/NotificationInboxController.cs` | 72 | `dateFrom` | [FromQuery] string? dateFrom = null, |
| `backend/Controllers/NotificationInboxController.cs` | 73 | `dateTo` | [FromQuery] string? dateTo = null, |
| `backend/Controllers/NotificationInboxController.cs` | 107 | `dateFrom` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 107 | `dateTo` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 134 | `dateFrom` | [FromQuery] string? dateFrom = null, |
| `backend/Controllers/NotificationInboxController.cs` | 135 | `dateTo` | [FromQuery] string? dateTo = null, |
| `backend/Controllers/NotificationInboxController.cs` | 159 | `dateFrom` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 159 | `dateTo` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 481 | `dateFrom` | string? dateFrom, |
| `backend/Controllers/NotificationInboxController.cs` | 482 | `dateTo` | string? dateTo) |
| `backend/Controllers/NotificationInboxController.cs` | 484 | `dateFrom` | if (DateTime.TryParse(dateFrom, out var parsedFrom)) |
| `backend/Controllers/NotificationInboxController.cs` | 490 | `dateTo` | if (DateTime.TryParse(dateTo, out var parsedTo)) |
| `backend/Controllers/ObjectAuditController.cs` | 42 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ObjectAuditController.cs` | 42 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 363 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 364 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 988 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from = null, |
| `backend/Controllers/PaymentsController.cs` | 989 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to = null, |
| `backend/Controllers/PaymentsController.cs` | 1507 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 1508 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 363 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 364 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 988 | `DateTime? from` | [FromQuery] DateTime? from = null, |
| `backend/Controllers/PaymentsController.cs` | 989 | `DateTime? to` | [FromQuery] DateTime? to = null, |
| `backend/Controllers/PaymentsController.cs` | 1507 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 1508 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 4086 | `startDate` | SetPropertyCaseInsensitive(root, "startDate", normalizedDate); |
| `backend/Controllers/ReportsController.cs` | 189 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 190 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 225 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 226 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 247 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 248 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 270 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 271 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 296 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 297 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 305 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 356 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 357 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 365 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 416 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 417 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 425 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 479 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 480 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 488 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 535 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 536 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 571 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 572 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1283 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1327 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1364 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1403 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1440 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1441 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1481 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1482 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1520 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1521 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1541 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1542 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1562 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1563 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1583 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1584 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1604 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1605 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1625 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1626 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1646 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1647 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1667 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1668 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1688 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1689 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1709 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1710 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1730 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1731 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1751 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1752 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1772 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1773 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1793 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1794 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1814 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1815 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1835 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1836 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1856 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1857 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1877 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1878 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1898 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1899 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1919 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1920 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1953 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1954 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1999 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 2000 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 184 | `DateTime Start` | private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 184 | `DateTime End` | private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 184 | `DateTime? from` | private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 184 | `DateTime? to` | private async Task<(DateTime Start, DateTime End)> ResolveReportPeriodAsync(DateTime? from, DateTime? to, CancellationToken ct) |
| `backend/Controllers/ReportsController.cs` | 189 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 190 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 225 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 226 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 247 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 248 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 270 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 271 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 296 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 297 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 356 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 357 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 416 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 417 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 479 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 480 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 535 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 536 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 571 | `DateTime? from` | [FromQuery] DateTime? from, |
| Еще 858 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Экспорты, импорты и файлы

Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 36 | `CSV` | "text": "Печатная версия реестра использует те же фильтры, область видимости, локальное время и право экспорта, что CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 36 | `PDF` | "text": "Печатная версия реестра использует те же фильтры, область видимости, локальное время и право экспорта, что CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 501 | `PDF` | "summary": "Администратор может безопасно отменить ещё не отправленный PDF-снимок прямо в истории электронной подписи.", |
| `backend/AppReleases/releases.json` | 511 | `PDF` | "text": "Отмена не удаляет PDF, контрольную сумму или историю и не применяется к уже отправленной версии без подтверждения внешнего провайдера." |
| `backend/AppReleases/releases.json` | 515 | `PDF` | "text": "Повторный запрос отмены безопасен и не создаёт дублирующиеся события в истории; для каждой версии можно скачать внутренний ZIP-архив CRM с PDF, событиями и проверенными контрольными суммами." |
| `backend/AppReleases/releases.json` | 534 | `PDF` | "text": "Для подписания сохраняется отдельный неизменяемый PDF-снимок с SHA-256; история показывает состояние envelope и каждого подписанта." |
| `backend/AppReleases/releases.json` | 798 | `PDF` | "text": "В блоке внешнего согласования можно отметить до 10 PDF, изображений или офисных документов размером до 25 МБ каждый." |
| `backend/AppReleases/releases.json` | 940 | `PDF` | "text": "Перед подписанием PDF сохраняется как отдельная неизменяемая версия с контрольной суммой; новая версия документа всегда получает отдельный процесс подписания." |
| `backend/AppReleases/releases.json` | 953 | `PDF` | "summary": "Очередь безопасной доставки устойчиво обрабатывает временные задержки провайдера, крупные PDF и запоздалые статусы.", |
| `backend/AppReleases/releases.json` | 963 | `PDF` | "text": "Очередь обрабатывает документы ограниченными пакетами, а PDF до 25 МБ копируются потоково с проверкой формата, размера и контрольной суммы." |
| `backend/AppReleases/releases.json` | 982 | `PDF` | "text": "В карточках счета, акта и договора появилась кнопка «Мессенджер». Перед отправкой CRM формирует штатную PDF-версию и показывает получателя, канал и текст сообщения." |
| `backend/AppReleases/releases.json` | 990 | `PDF` | "text": "Финансовые документы передаются только как PDF размером до 25 МБ через одноразовую защищённую ссылку. Внешняя отправка остаётся выключенной, пока администратор не настроит официальный канал." |
| `backend/AppReleases/releases.json` | 1423 | `CSV` | "text": "Состояние автоконтроля добавлено в CSV, Excel, PDF и печатную форму реестра дел." |
| `backend/AppReleases/releases.json` | 1423 | `PDF` | "text": "Состояние автоконтроля добавлено в CSV, Excel, PDF и печатную форму реестра дел." |
| `backend/AppReleases/releases.json` | 5558 | `PDF` | "title": "Печать счета больше не зависит от PDF-генератора", |
| `backend/AppReleases/releases.json` | 5559 | `PDF` | "summary": "Кнопки печати в карточке счета теперь открывают печатную HTML-форму напрямую. Если на сервере временно недоступно формирование PDF, пользователь все равно может распечатать счет или счет-оферту из браузера.", |
| `backend/AppReleases/releases.json` | 5565 | `PDF` | "text": "В разделе «Счета» кнопка «Печать» больше не обращается к серверному PDF-генератору. Печатная форма открывается как обычная страница документа и сразу вызывает печать в браузере." |
| `backend/AppReleases/releases.json` | 5569 | `PDF` | "text": "Печать счета-оферты работает по той же схеме: CRM формирует HTML-документ и печатает его без промежуточного PDF-файла, поэтому операция не должна падать с ошибкой временной недоступности сервиса PDF." |
| `backend/AppReleases/releases.json` | 5573 | `PDF` | "text": "Скачивание PDF и отправка PDF на Яндекс.Диск по-прежнему используют серверный PDF-генератор. Если он недоступен, эти операции могут показать отдельную ошибку, но обычная печать счета из браузера продолжит работа |
| `backend/AppReleases/releases.json` | 6694 | `PDF` | "summary": "В печатной форме счета и PDF улучшено отображение длинных названий организации, покупателя и услуги. Такие реквизиты теперь аккуратнее переносятся внутри страницы и не должны ломать внешний вид счета.", |
| `backend/AppReleases/releases.json` | 6700 | `PDF` | "text": "В разделе «Счета» печатная форма и PDF стали устойчивее к длинным названиям продавца, покупателя и строк услуг." |
| `backend/AppReleases/releases.json` | 6732 | `CSV` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась кнопка «Печать». Печатная форма открывается в отдельной вкладке и использует те же выбранные фильтры, что и отчет на экране, CSV, Excel и PDF.", |
| `backend/AppReleases/releases.json` | 6732 | `PDF` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась кнопка «Печать». Печатная форма открывается в отдельной вкладке и использует те же выбранные фильтры, что и отчет на экране, CSV, Excel и PDF.", |
| `backend/AppReleases/releases.json` | 6738 | `CSV` | "text": "Для управленческих отчетов добавлена печатная форма рядом с CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 6738 | `PDF` | "text": "Для управленческих отчетов добавлена печатная форма рядом с CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 6754 | `CSV` | "title": "CSV-выгрузка управленческих отчетов", |
| `backend/AppReleases/releases.json` | 6755 | `CSV` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась выгрузка в CSV рядом с Excel и PDF. Файл формируется по тем же выбранным фильтрам, что и отчет на экране, поэтому его можно использовать для дальнейшей об |
| `backend/AppReleases/releases.json` | 6755 | `PDF` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась выгрузка в CSV рядом с Excel и PDF. Файл формируется по тем же выбранным фильтрам, что и отчет на экране, поэтому его можно использовать для дальнейшей об |
| `backend/AppReleases/releases.json` | 6761 | `CSV` | "text": "Для управленческих отчетов добавлена кнопка CSV. Выгрузка доступна там же, где уже были Excel и PDF." |
| `backend/AppReleases/releases.json` | 6761 | `PDF` | "text": "Для управленческих отчетов добавлена кнопка CSV. Выгрузка доступна там же, где уже были Excel и PDF." |
| `backend/AppReleases/releases.json` | 6765 | `CSV` | "text": "CSV-файл учитывает выбранный период, источник денег, счет, клиента, дело, договор, статью дохода или расхода и другие фильтры конкретного отчета." |
| `backend/AppReleases/releases.json` | 6769 | `CSV` | "text": "Выгрузка CSV использует те же права доступа, что и остальные экспорты отчетов: скачать файл могут только пользователи с разрешением на экспорт." |
| `backend/AppReleases/releases.json` | 7290 | `PDF` | "title": "Отчет по задачам можно выгружать в Excel и PDF", |
| `backend/AppReleases/releases.json` | 7291 | `PDF` | "summary": "В разделе «Отчеты» серверная выгрузка управленческого отчета по задачам теперь доступна в Excel и PDF/печатном виде. В файл попадают список задач, сводка по статусам и распределение по исполнителям.", |
| `backend/AppReleases/releases.json` | 7621 | `PDF` | "summary": "В разделе «Отчеты» фильтр по статье дохода или расхода стал применяться к сводкам по статусам актов и договоров. Теперь эти отчеты, а также их Excel/PDF/печатная форма, показывают только документы, связанные  |
| `backend/AppReleases/releases.json` | 7716 | `PDF` | "summary": "В разделе «Отчеты» сводки по статусам счетов, актов и договоров стали лучше совпадать с общей панелью финансовых фильтров. Теперь при выборе источника денег, денежного счета или контрагента эти отчеты и их Ex |
| `backend/AppReleases/releases.json` | 7726 | `PDF` | "text": "Excel/PDF/печатная форма этих отчетов теперь формируется по тем же финансовым фильтрам, что выбраны на странице отчетов." |
| `backend/AppReleases/releases.json` | 7735 | `PDF` | "summary": "В разделе «Отчеты» сводка по импортам банковских выписок стала точнее работать с общей панелью фильтров. Теперь отчет и его Excel/PDF/печатная форма учитывают выбранный расчетный счет, контрагента, клиента, д |
| `backend/AppReleases/releases.json` | 7745 | `PDF` | "text": "Excel/PDF/печатная форма отчета по импортам банка теперь формируется по тем же фильтрам, что пользователь выбрал на странице отчетов." |
| `backend/AppReleases/releases.json` | 7754 | `PDF` | "summary": "В разделе «Отчеты» кассовая книга стала точнее совпадать с общей панелью фильтров. Теперь отчет по кассе и его Excel/PDF/печатная форма учитывают выбранного контрагента, клиента, дело, договор и статью дохода |
| `backend/AppReleases/releases.json` | 7764 | `PDF` | "text": "Excel/PDF/печатная форма отчета по кассе теперь формируется по тем же фильтрам, что пользователь выбрал на странице отчетов." |
| `backend/AppReleases/releases.json` | 7792 | `CSV` | "summary": "Платежные отчеты в разделе «Отчеты» стали точнее соблюдать матрицу прав. Пользователь может просматривать доступные ему финансовые сводки, но CSV, Excel и PDF/печатная форма теперь доступны только при наличии |
| `backend/AppReleases/releases.json` | 7792 | `PDF` | "summary": "Платежные отчеты в разделе «Отчеты» стали точнее соблюдать матрицу прав. Пользователь может просматривать доступные ему финансовые сводки, но CSV, Excel и PDF/печатная форма теперь доступны только при наличии |
| `backend/AppReleases/releases.json` | 7802 | `CSV` | "text": "Если у роли есть просмотр платежей, но нет права выгрузки, кнопки CSV, Excel и PDF в этих отчетах становятся недоступны, а прямой запрос на выгрузку отклоняется сервером." |
| `backend/AppReleases/releases.json` | 7802 | `PDF` | "text": "Если у роли есть просмотр платежей, но нет права выгрузки, кнопки CSV, Excel и PDF в этих отчетах становятся недоступны, а прямой запрос на выгрузку отклоняется сервером." |
| `backend/AppReleases/releases.json` | 7821 | `PDF` | "text": "Excel, PDF/печатная форма и CSV по этому отчету также закрыты для пользователей без административного доступа." |
| `backend/AppReleases/releases.json` | 7821 | `CSV` | "text": "Excel, PDF/печатная форма и CSV по этому отчету также закрыты для пользователей без административного доступа." |
| `backend/AppReleases/releases.json` | 7840 | `PDF` | "text": "Excel, PDF/печатная форма и CSV по этому отчету теперь доступны только при наличии права выгрузки платежей." |
| `backend/AppReleases/releases.json` | 7840 | `CSV` | "text": "Excel, PDF/печатная форма и CSV по этому отчету теперь доступны только при наличии права выгрузки платежей." |
| `backend/AppReleases/releases.json` | 7878 | `PDF` | "text": "В разделе «Отчеты» Excel и PDF/печатная форма отчета по банку теперь учитывают выбранный счет, контрагента, клиента, дело, договор и статью дохода или расхода." |
| `backend/AppReleases/releases.json` | 7897 | `PDF` | "text": "В разделе «Отчеты» Excel и PDF/печатная форма отчета по кассе теперь выгружают данные только по выбранной кассе, если она указана в фильтре." |
| `backend/AppReleases/releases.json` | 7920 | `PDF` | "text": "Excel и PDF/печатная форма отчета по кассовым ордерам выгружают ту же выборку, которую пользователь видит на экране." |
| `backend/AppReleases/releases.json` | 7935 | `PDF` | "text": "В разделе «Отчеты» отчеты по статусам счетов, актов и договоров применяют фильтры клиента, дела и договора на экране, в Excel и PDF/печатной форме." |
| `backend/AppReleases/releases.json` | 7986 | `PDF` | "summary": "В акте сверки клиента уточнено поведение кнопок выгрузки. Excel и PDF становятся доступны после того, как CRM загрузила данные операций и профиль организации, поэтому в документ попадает корректное название к |
| `backend/AppReleases/releases.json` | 7992 | `PDF` | "text": "В карточке клиента в акте сверки кнопки Excel и PDF больше нельзя нажать во время формирования акта. Это предотвращает выгрузку документа с временным названием организации вместо данных из профиля компании." |
| `backend/AppReleases/releases.json` | 10098 | `PDF` | "text": "Исправлен экранный отчет по банку: фильтры по счету, контрагенту, клиенту, делу, договору и статье доходов/расходов теперь применяются не только к Excel и PDF, но и к самой банковской сводке на странице." |
| `backend/AppReleases/releases.json` | 10107 | `PDF` | "summary": "Excel и PDF для отчета по банку теперь формируются по той же выборке, которую пользователь видит на экране.", |
| `backend/AppReleases/releases.json` | 10113 | `PDF` | "text": "В разделе «Отчеты» исправлена выгрузка отчета по банку: выбранные фильтры по счету, контрагенту, клиенту, делу, договору и статье доходов/расходов теперь учитываются при скачивании Excel и PDF." |
| `backend/AppReleases/releases.json` | 10155 | `PDF` | "text": "Фильтр по договору применяется к отчетам по денежным операциям, прогнозу денежных потоков, план-факт отчету и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 10178 | `PDF` | "text": "Фильтр по статье применяется к отчетам по денежным операциям, прогнозу денежных потоков, план-факт отчету и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 10220 | `PDF` | "text": "Фильтр по делу применяется к отчетам по денежным операциям и к Excel/PDF-выгрузкам этих отчетов. Для фактических операций система использует связь со счетом, а для прогноза платежей — связь платежа с делом." |
| `backend/AppReleases/releases.json` | 10243 | `PDF` | "text": "Фильтр по клиенту применяется к отчетам по денежным операциям и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 10312 | `PDF` | "text": "Фильтр применяется к финансовым отчетам по денежным операциям, прогнозу денежных потоков и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 10335 | `PDF` | "text": "Фильтр счета применяется к финансовым отчетам по денежным операциям, прогнозу денежных потоков и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 10358 | `PDF` | "text": "Фильтр применяется к финансовым отчетам по денежным операциям и к их выгрузкам Excel и PDF, чтобы данные на экране и в файлах совпадали." |
| `backend/AppReleases/releases.json` | 10377 | `CSV` | "text": "В разделе «Отчеты» у блоков «Отчет по счетам оплаты», «Отчет по оплаченным счетам», «Отчет по статусам счетов», «Отчет по статусам актов» и «Отчет по статусам договоров» теперь доступны выгрузки CSV, Excel и PDF |
| `backend/AppReleases/releases.json` | 10377 | `PDF` | "text": "В разделе «Отчеты» у блоков «Отчет по счетам оплаты», «Отчет по оплаченным счетам», «Отчет по статусам счетов», «Отчет по статусам актов» и «Отчет по статусам договоров» теперь доступны выгрузки CSV, Excel и PDF |
| `backend/AppReleases/releases.json` | 10389 | `PDF` | "text": "PDF-выгрузки можно использовать как печатные формы для сверки оплат и контроля статусов счетов, актов и договоров." |
| `backend/AppReleases/releases.json` | 10404 | `CSV` | "text": "В разделе «Отчеты» у блоков «План-факт отчет», «Отчет по авансам» и «Отчет по приоритетам платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10404 | `PDF` | "text": "В разделе «Отчеты» у блоков «План-факт отчет», «Отчет по авансам» и «Отчет по приоритетам платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10416 | `PDF` | "text": "PDF-выгрузки можно использовать как печатные формы для сверки план-факта, авансов и распределения операций по приоритетам." |
| `backend/AppReleases/releases.json` | 10431 | `CSV` | "text": "В разделе «Отчеты» у блоков «Отчет по кассе» и «Отчет по банку» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10431 | `PDF` | "text": "В разделе «Отчеты» у блоков «Отчет по кассе» и «Отчет по банку» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10443 | `PDF` | "text": "PDF-выгрузки можно использовать как печатные формы для сверки кассовых и банковских движений за выбранный период." |
| `backend/AppReleases/releases.json` | 10458 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по импортам из таблиц» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10458 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по импортам из таблиц» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10466 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля табличных загрузок, ошибок и откатов с конфликтами." |
| `backend/AppReleases/releases.json` | 10481 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по импортам банковских выписок» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10481 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по импортам банковских выписок» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10489 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля проблемных импортов и заблокированных откатов." |
| `backend/AppReleases/releases.json` | 10504 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по кассовым ордерам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10504 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по кассовым ордерам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10512 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки приходных и расходных кассовых ордеров за выбранный период." |
| `backend/AppReleases/releases.json` | 10527 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по валютам операций» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10527 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по валютам операций» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10535 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки оборотов по валютам операций." |
| `backend/AppReleases/releases.json` | 10550 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по журнальным статусам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10550 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по журнальным статусам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10558 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля операций, которые еще находятся в обработке или требуют исправления." |
| `backend/AppReleases/releases.json` | 10573 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по статусам сверки» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10573 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по статусам сверки» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10581 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля несверенных операций и ручной проверки платежей." |
| `backend/AppReleases/releases.json` | 10596 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по назначениям платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10596 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по назначениям платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10604 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки повторяющихся оснований оплат и списаний." |
| `backend/AppReleases/releases.json` | 10619 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по платежным поручениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10619 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по платежным поручениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10627 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки операций по номерам платежных поручений." |
| `backend/AppReleases/releases.json` | 10642 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по НДС» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10642 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по НДС» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10650 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки начисленного и входящего НДС за выбранный период." |
| `backend/AppReleases/releases.json` | 10665 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по точкам продаж» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10665 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по точкам продаж» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10673 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки финансового результата по точкам продаж и каналам." |
| `backend/AppReleases/releases.json` | 10688 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по направлениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10688 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по направлениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10696 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для обсуждения структуры доходов и расходов по направлениям бизнеса." |
| `backend/AppReleases/releases.json` | 10711 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по проектам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10711 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по проектам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10719 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для обсуждения финансового результата проектов." |
| `backend/AppReleases/releases.json` | 10734 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по контрагентам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10734 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по контрагентам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10742 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки оборотов по контрагентам за выбранный период." |
| `backend/AppReleases/releases.json` | 10757 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по статьям доходов и расходов» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10757 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по статьям доходов и расходов» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10765 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для обсуждения структуры доходов и расходов за выбранный период." |
| `backend/AppReleases/releases.json` | 10780 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет движения денежных средств» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10780 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет движения денежных средств» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10788 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму фактического движения денег за выбранный период." |
| `backend/AppReleases/releases.json` | 10803 | `CSV` | "text": "В разделе «Отчеты» у блока «Прогноз денежных потоков» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10803 | `PDF` | "text": "В разделе «Отчеты» у блока «Прогноз денежных потоков» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10811 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму прогноза для обсуждения плановых поступлений и списаний." |
| `backend/AppReleases/releases.json` | 10857 | `CSV` | "text": "CSV, Excel и PDF-выгрузки финансового отчета по делам также включают расходы, маржу и маржинальность." |
| `backend/AppReleases/releases.json` | 10857 | `PDF` | "text": "CSV, Excel и PDF-выгрузки финансового отчета по делам также включают расходы, маржу и маржинальность." |
| `backend/AppReleases/releases.json` | 10991 | `PDF` | "text": "На экране акта сверки, в Excel и в PDF авансовые строки подписываются как авансы, чтобы их было проще отличить от закрывающих оплат." |
| `backend/AppReleases/releases.json` | 11973 | `PDF` | "text": "В меню «Скачать» появились варианты «PDF счет-оферта» и «Word счет-оферта»." |
| `backend/AppReleases/releases.json` | 12357 | `ICS` | "text": "Для задач, календаря дел, ICS-календарей, заметок платежей, автоматической сверки баланса и проверки просрочки платежей убрана зависимость от часового пояса сервера, если профильный часовой пояс пользователя не  |
| `backend/AppReleases/releases.json` | 12469 | `CSV` | "summary": "В разделе \"Отчеты\" скачанные CSV, Excel и PDF-файлы теперь единообразно показывают, когда отчет был сформирован, с учетом часового пояса пользователя из профиля. Это помогает сотрудникам из разных регионов  |
| `backend/AppReleases/releases.json` | 12469 | `PDF` | "summary": "В разделе \"Отчеты\" скачанные CSV, Excel и PDF-файлы теперь единообразно показывают, когда отчет был сформирован, с учетом часового пояса пользователя из профиля. Это помогает сотрудникам из разных регионов  |
| `backend/AppReleases/releases.json` | 12479 | `CSV` | "text": "CSV и Excel-выгрузки отчета по юридической работе теперь показывают время формирования по часовому поясу профиля так же, как PDF." |
| `backend/AppReleases/releases.json` | 12479 | `PDF` | "text": "CSV и Excel-выгрузки отчета по юридической работе теперь показывают время формирования по часовому поясу профиля так же, как PDF." |
| `backend/AppReleases/releases.json` | 12568 | `CSV` | "summary": "В разделе \"Платежи\" появились выгрузки текущего списка в CSV и Excel. В файл попадают те же платежи, которые пользователь отобрал фильтрами на экране, а служебное время формирования и дата создания платежа  |
| `backend/AppReleases/releases.json` | 12574 | `CSV` | "text": "В журнале платежей добавлены кнопки выгрузки в CSV и Excel для пользователей с правом экспорта платежей." |
| `backend/AppReleases/releases.json` | 12766 | `CSV` | "summary": "В выгрузках отчета по юридической работе уточнены даты и имена файлов. CSV, Excel и PDF теперь показывают точные даты создания дел и судебных заседаний с учетом часового пояса профиля пользователя, а CSV-файл |
| `backend/AppReleases/releases.json` | 12766 | `PDF` | "summary": "В выгрузках отчета по юридической работе уточнены даты и имена файлов. CSV, Excel и PDF теперь показывают точные даты создания дел и судебных заседаний с учетом часового пояса профиля пользователя, а CSV-файл |
| `backend/AppReleases/releases.json` | 12776 | `CSV` | "text": "CSV-файл юридического отчета теперь скачивается с именем, в котором указаны дата и время формирования по часовому поясу профиля пользователя." |
| `backend/AppReleases/releases.json` | 12795 | `CSV` | "text": "CSV-выгрузки юридических отчетов теперь используют то же правило дат, что и экран отчета: дата без времени остается календарной датой, а точное время отображается по часовому поясу профиля." |
| `backend/AppReleases/releases.json` | 12848 | `PDF` | "text": "В HTML, Word и PDF-формах счетов дата счета, срок оплаты, дата договора-основания и дата выдачи паспорта больше не должны отображаться со сдвигом." |
| `backend/AppReleases/releases.json` | 13017 | `PDF` | "text": "Имена файлов PDF и Excel для отчетов теперь используют локальное время профиля, чтобы время формирования совпадало с тем, что пользователь видит внутри отчета." |
| `backend/AppReleases/releases.json` | 13026 | `CSV` | "summary": "В CSV, Excel, PDF и печатной версии реестра дел время заседаний и активности теперь отображается по часовому поясу пользователя из профиля.", |
| `backend/AppReleases/releases.json` | 13026 | `PDF` | "summary": "В CSV, Excel, PDF и печатной версии реестра дел время заседаний и активности теперь отображается по часовому поясу пользователя из профиля.", |
| `backend/AppReleases/releases.json` | 13036 | `CSV` | "text": "Поля последней активности и просмотра ответственным в CSV и Excel используют тот же часовой пояс профиля, что и интерфейс CRM." |
| `backend/AppReleases/releases.json` | 13040 | `PDF` | "text": "PDF и печатная версия реестра дел также показывают дату и время активности в локальном времени пользователя." |
| `backend/AppReleases/releases.json` | 13178 | `ICS` | "text": "ICS-выгрузка календаря дел сохраняет точные моменты времени без двойного сдвига, поэтому внешние календари получают корректное время события." |
| `backend/AppReleases/releases.json` | 13669 | `PDF` | "title": "PDF-отчеты учитывают часовой пояс профиля", |
| `backend/AppReleases/releases.json` | 13670 | `PDF` | "summary": "Уточнено отображение времени формирования PDF-отчетов и подготовлено единое правило для системных отметок времени.", |
| `backend/AppReleases/releases.json` | 13676 | `PDF` | "text": "В PDF-отчетах по юридической работе и управленческих отчетах строка «сформировано» теперь показывает время по часовому поясу пользователя из профиля, а не по локальному времени сервера." |
| `backend/AppReleases/releases.json` | 13761 | `CSV` | "text": "В отчете по загрузке юристов ближайшие задачи теперь показываются и выгружаются в CSV с учетом часового пояса профиля. В карточке дела время последнего просмотра, подтверждения активности и загрузки документов т |
| `backend/AppReleases/releases.json` | 13785 | `CSV` | "text": "Экспорт реестра дел и печатные формы заказ-наряда теперь показывают время формирования по часовому поясу пользователя. Имена файлов CSV, Excel и PDF также получают дату и время из профиля сотрудника." |
| `backend/AppReleases/releases.json` | 13785 | `PDF` | "text": "Экспорт реестра дел и печатные формы заказ-наряда теперь показывают время формирования по часовому поясу пользователя. Имена файлов CSV, Excel и PDF также получают дату и время из профиля сотрудника." |
| `backend/AppReleases/releases.json` | 13804 | `ICS` | "text": "В настройках внешних календарей добавлен блок «Выполненные задачи в календарях». Администратор может оставить выполненные задачи в календарях с отдельной отметкой или скрывать их после выполнения. Правило примен |
| `backend/AppReleases/releases.json` | 13812 | `ICS` | "text": "Исправлена выгрузка календаря дел и календаря задач в ICS: судебные заседания, задачи и другие события с точным временем больше не получают сдвиг при добавлении во внешний календарь." |
| `backend/AppReleases/releases.json` | 14787 | `PDF` | "text": "При выгрузке акта в Word или PDF CRM заменяет символы, которые могут мешать сохранению файла на разных устройствах и серверах. Документ по-прежнему содержит исходный номер акта внутри печатной формы." |
| `backend/AppReleases/releases.json` | 14853 | `PDF` | "summary": "В карточке акта появилась ручная отправка PDF акта на Яндекс.Диск по правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 14859 | `PDF` | "text": "В карточке акта рядом с действиями отправки, печати и скачивания появилась кнопка «Я.Диск». Она формирует PDF акта и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск». |
| `backend/AppReleases/releases.json` | 14872 | `PDF` | "summary": "В карточке счета появилась ручная отправка PDF счета на Яндекс.Диск по правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 14878 | `PDF` | "text": "В карточке счета рядом с действиями отправки, печати и скачивания появилась кнопка «Я.Диск». Она формирует PDF счета и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск |
| `backend/AppReleases/releases.json` | 14891 | `PDF` | "summary": "В карточке договора появилась ручная отправка PDF договора на Яндекс.Диск по настроенным правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 14897 | `PDF` | "text": "В карточке договора рядом с действиями Word, PDF и Email появилась кнопка «Я.Диск». Она формирует PDF договора и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск»." |
| `backend/AppReleases/releases.json` | 15050 | `PDF` | "text": "Акт сверки теперь печатается в фиксированной альбомной форме A4 с размерами колонок, полями, итогами и блоками подписей, приближенными к приложенному бухгалтерскому образцу. Для Excel сохранена структура эталонн |
| `backend/AppReleases/releases.json` | 15077 | `PDF` | "text": "Кнопка Excel выгружает акт сверки в формате .xls с той же бухгалтерской структурой, а кнопка PDF открывает чистую печатную форму без лишних элементов интерфейса." |
| `backend/AppReleases/releases.json` | 15813 | `ICS` | "text": "ICS-выгрузка отдельно отмечена как доступный безопасный контур, а Google Calendar и Яндекс Календарь остаются в подготовительном режиме до закрытия обязательных пунктов." |
| `backend/AppReleases/releases.json` | 15826 | `ICS` | "summary": "В настройках интеграций появился блок состояния календарных провайдеров: видно, что ICS-выгрузка уже доступна, а Google Calendar и Яндекс Календарь ждут отдельной настройки и приемки.", |
| `backend/AppReleases/releases.json` | 15832 | `ICS` | "text": "В разделе «Ключи и внешние сервисы» добавлен блок «Провайдеры календарной синхронизации». Он показывает доступность ICS, Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 15849 | `ICS` | "summary": "Выгрузки календаря задач и календаря дел в ICS теперь фиксируются в общем журнале действий, чтобы администраторы могли видеть, когда и кем был сформирован файл календаря.", |
| `backend/AppReleases/releases.json` | 15855 | `ICS` | "text": "В журнале всех действий появился тип «Синхронизация календаря». В нем отражаются выгрузки ICS-файлов из журнала задач и календаря дел." |
| `backend/AppReleases/releases.json` | 15871 | `ICS` | "title": "Настройка напоминаний в ICS-календарях", |
| `backend/AppReleases/releases.json` | 15872 | `ICS` | "summary": "В журнале задач можно выбрать, за сколько времени напоминать о событиях при выгрузке календаря задач и календаря дел в ICS.", |
| `backend/AppReleases/releases.json` | 15878 | `ICS` | "text": "Перед выгрузкой ICS в журнале задач теперь можно выбрать набор напоминаний: стандартный вариант за 1 час и 15 минут, только за 1 час, за 2 часа и 30 минут или за 1 день." |
| `backend/AppReleases/releases.json` | 15886 | `ICS` | "text": "Если напоминания не выбраны явно, ICS-файлы продолжают выгружаться со стандартными уведомлениями за 1 час и за 15 минут до события." |
| `backend/AppReleases/releases.json` | 16055 | `PDF` | "title": "Excel и PDF выгрузки юридических отчетов", |
| `backend/AppReleases/releases.json` | 16056 | `CSV` | "summary": "В разделе «Отчеты» расширены выгрузки управленческих юридических отчетов: теперь ключевые отчеты по делам можно сохранять не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 16056 | `PDF` | "summary": "В разделе «Отчеты» расширены выгрузки управленческих юридических отчетов: теперь ключевые отчеты по делам можно сохранять не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 16062 | `PDF` | "text": "Для финансового отчета по делам, статистики успешности, текущих дел, загрузки юристов, просроченных процессуальных сроков и диагностики активности добавлены кнопки Excel и PDF." |
| `backend/AppReleases/releases.json` | 16070 | `PDF` | "text": "PDF и Excel показывают сводные показатели и детальные строки отчета, чтобы руководитель мог передать файл дальше без ручной сборки данных из нескольких блоков CRM." |
| `backend/AppReleases/releases.json` | 16093 | `CSV` | "text": "Отчет учитывает доступ пользователя к делам, группирует статистику по ответственным, типам и статусам, а строки можно выгрузить в CSV." |
| `backend/AppReleases/releases.json` | 16112 | `CSV` | "text": "Отчет учитывает доступ пользователя к делам и платежам, группирует суммы по ответственным и статусам дел, а строки отчета можно выгрузить в CSV." |
| `backend/AppReleases/releases.json` | 16406 | `ICS` | "summary": "ICS-файл журнала задач теперь передает во внешние календари напоминания перед началом задачи.", |
| `backend/AppReleases/releases.json` | 16412 | `ICS` | "text": "При выгрузке календаря задач в ICS в каждую задачу добавляются напоминания за 1 час и за 15 минут до срока выполнения." |
| `backend/AppReleases/releases.json` | 16425 | `ICS` | "summary": "ICS-файл календаря дел теперь передает во внешние календари напоминания по событиям с точным временем.", |
| `backend/AppReleases/releases.json` | 16431 | `ICS` | "text": "При выгрузке календаря дел в ICS для судебных заседаний, задач и других событий с указанным временем добавляются напоминания за 1 час и за 15 минут до начала." |
| `backend/AppReleases/releases.json` | 16466 | `PDF` | "title": "Выгрузка реестра дел в Excel и PDF", |
| `backend/AppReleases/releases.json` | 16467 | `CSV` | "summary": "В разделе «Дела» расширены выгрузки реестра: теперь текущую выборку можно скачать не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 16467 | `PDF` | "summary": "В разделе «Дела» расширены выгрузки реестра: теперь текущую выборку можно скачать не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 16473 | `CSV` | "text": "В реестре дел рядом с кнопкой CSV появились кнопки Excel и PDF. Они формируют файл по текущим фильтрам, сортировке и выбранному режиму реестра." |
| `backend/AppReleases/releases.json` | 16473 | `PDF` | "text": "В реестре дел рядом с кнопкой CSV появились кнопки Excel и PDF. Они формируют файл по текущим фильтрам, сортировке и выбранному режиму реестра." |
| `backend/AppReleases/releases.json` | 16477 | `PDF` | "text": "Excel-файл содержит основные данные по делам, задачам, финансам, суду и активности, а PDF использует печатную форму реестра с итогами по выборке." |
| `backend/AppReleases/releases.json` | 16489 | `ICS` | "title": "Выгрузка календаря дел в ICS", |
| `backend/AppReleases/releases.json` | 16490 | `ICS` | "summary": "В календаре дел появилась выгрузка судебных событий и процессуальных сроков в файл ICS для внешних календарей.", |
| `backend/AppReleases/releases.json` | 16496 | `ICS` | "text": "В блоке «Календарь дел» добавлена кнопка ICS. Она формирует файл с судебными заседаниями, процессуальными сроками и другими событиями дел за текущий период календаря." |
| `backend/AppReleases/releases.json` | 16512 | `PDF` | "title": "Выгрузка юридического отчета в Excel и PDF", |
| `backend/AppReleases/releases.json` | 16519 | `CSV` | "text": "В отчете «Юридическая работа» появились серверные выгрузки CSV, Excel и PDF. Кнопки доступны в шапке отчета рядом с обновлением данных." |
| `backend/AppReleases/releases.json` | 16519 | `PDF` | "text": "В отчете «Юридическая работа» появились серверные выгрузки CSV, Excel и PDF. Кнопки доступны в шапке отчета рядом с обновлением данных." |
| `backend/AppReleases/releases.json` | 16601 | `PDF` | "summary": "В журнале задач появился отчет «Планинг по клиентам», который можно выгрузить в Word, PDF или HTML для передачи и печати.", |
| `backend/AppReleases/releases.json` | 16607 | `PDF` | "text": "В «Журнале задач» добавлены кнопки Word, PDF и HTML. Они формируют отчет «Планинг по клиентам» с группировкой задач по клиенту и делу." |
| `backend/AppReleases/releases.json` | 16611 | `PDF` | "text": "В отчете выводятся номер строки, вид задачи, описание, срок, статус, исполнитель и комментарий. Формат подходит для просмотра в браузере, печати, отправки в PDF и открытия в Word." |
| `backend/AppReleases/releases.json` | 17051 | `CSV` | "text": "Номер связанного акта также попадает в выгрузки журнала в CSV, Excel и печатную форму PDF." |
| `backend/AppReleases/releases.json` | 17051 | `PDF` | "text": "Номер связанного акта также попадает в выгрузки журнала в CSV, Excel и печатную форму PDF." |
| `backend/AppReleases/releases.json` | 17203 | `PDF` | "text": "В карточке расторгнутого договора появилась кнопка «Соглашение PDF». Она формирует соглашение о расторжении с данными сторон, датой расторжения, причиной, условиями и финансовыми условиями из карточки договора." |
| `backend/AppReleases/releases.json` | 17230 | `PDF` | "text": "Печать договора, скачивание в Word или PDF и отправка договора по email теперь используют активный шаблон из настроек, поэтому оформление можно поддерживать в одном месте." |
| `backend/AppReleases/releases.json` | 17287 | `PDF` | "text": "В карточке договора появились действия «Печать», «Word», «PDF» и «Email». Теперь договор можно открыть в печатном виде, скачать или отправить клиенту прямо из карточки." |
| `backend/AppReleases/releases.json` | 17318 | `CSV` | "text": "Информация о постановщике также добавлена в карточки доски, подсказки календаря и выгрузку журнала задач в CSV." |
| `backend/Controllers/ActsController.cs` | 1826 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/CashOrdersController.cs` | 1308 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/CashOrdersController.cs` | 889 | `DownloadPdf` | public async Task<IActionResult> DownloadPdf(int id, CancellationToken ct) |
| `backend/Controllers/ClientPortalController.cs` | 133 | `DownloadDocument` | public async Task<IActionResult> DownloadDocument( |
| `backend/Controllers/ContactsController.cs` | 418 | `ImportYealinkXml` | public async Task<ActionResult<YealinkContactImportResultDto>> ImportYealinkXml( |
| `backend/Controllers/ContactsController.cs` | 369 | `DownloadYealinkXml` | public async Task<IActionResult> DownloadYealinkXml(CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 1265 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/DatabaseAdminController.cs` | 218 | `ImportSqliteAsync` | public async Task<ActionResult<DatabaseImportResultDto>> ImportSqliteAsync( |
| `backend/Controllers/DatabaseAdminController.cs` | 236 | `ImportSqliteAsync` | var result = await _databaseImportService.ImportSqliteAsync(tempPath, cancellationToken); |
| `backend/Controllers/DatabaseAdminController.cs` | 110 | `DownloadPath` | DownloadPath = result.DownloadPath, |
| `backend/Controllers/DatabaseAdminController.cs` | 199 | `DownloadBackupAsync` | public IActionResult DownloadBackupAsync([FromQuery] string? relativePath) |
| `backend/Controllers/ElectronicSignaturesController.cs` | 82 | `PDF` | return BadRequest(new { message = "Сформированный PDF не передан." }); |
| `backend/Controllers/ElectronicSignaturesController.cs` | 183 | `DownloadEvidencePackage` | public async Task<IActionResult> DownloadEvidencePackage(Guid publicId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 3159 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 181 | `PDF` | return BadRequest(new { message = "Сформированный PDF не передан." }); |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 257 | `DownloadCount` | item.DownloadCount, |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 388 | `Download` | public async Task<IActionResult> Download( |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 409 | `DownloadSecurely` | public async Task<IActionResult> DownloadSecurely( |
| `backend/Controllers/MoneyOperationsController.cs` | 2185 | `CSV` | return BadRequest("Поддерживаются файлы CSV, TSV, TXT и 1C ClientBankExchange."); |
| `backend/Controllers/MoneyOperationsController.cs` | 3405 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/MoneyOperationsController.cs` | 413 | `ExportCsv` | public async Task<IActionResult> ExportCsv( |
| `backend/Controllers/MoneyOperationsController.cs` | 463 | `ExportExcel` | public async Task<IActionResult> ExportExcel( |
| `backend/Controllers/MoneyOperationsController.cs` | 557 | `ExportPdf` | public async Task<IActionResult> ExportPdf( |
| `backend/Controllers/MoneyOperationsController.cs` | 2166 | `ImportBankStatement` | public async Task<ActionResult<BankStatementImportResultDto>> ImportBankStatement(IFormFile? file, CancellationToken ct) |
| `backend/Controllers/MoneyOperationsController.cs` | 2198 | `ImportBankStatementItemsAsync` | return Ok(await ImportBankStatementItemsAsync( |
| `backend/Controllers/MoneyOperationsController.cs` | 2286 | `ImportBankStatementItemsAsync` | return Ok(await ImportBankStatementItemsAsync(importItems, result, file.FileName, statementBytes.Length, fileHash, manualCategoryRules, ct)); |
| `backend/Controllers/MoneyOperationsController.cs` | 2356 | `ImportBankStatement` | var action = await ImportBankStatement(formFile, ct); |
| `backend/Controllers/MoneyOperationsController.cs` | 2631 | `ImportBankStatementItemsAsync` | return Ok(await ImportBankStatementItemsAsync( |
| `backend/Controllers/MoneyOperationsController.cs` | 3482 | `ImportBankStatementItemsAsync` | private async Task<BankStatementImportResultDto> ImportBankStatementItemsAsync( |
| `backend/Controllers/MoneyOperationsController.cs` | 3903 | `ImportKey` | ImportKey = importKey, |
| `backend/Controllers/PublicWorkOrderApprovalController.cs` | 90 | `DownloadAttachment` | public async Task<IActionResult> DownloadAttachment( |
| `backend/Controllers/PublicWorkOrderApprovalController.cs` | 99 | `DownloadAttachmentAsync` | var file = await _service.DownloadAttachmentAsync( |
| `backend/Controllers/ReportsController.cs` | 279 | `PDF` | return Problem("Сервис формирования PDF недоступен."); |
| `backend/Controllers/ReportsController.cs` | 440 | `PDF` | return Problem("Сервис формирования PDF недоступен."); |
| `backend/Controllers/ReportsController.cs` | 224 | `ExportLegalWorkCsv` | public async Task<IActionResult> ExportLegalWorkCsv( |
| `backend/Controllers/ReportsController.cs` | 246 | `ExportLegalWorkExcel` | public async Task<IActionResult> ExportLegalWorkExcel( |
| `backend/Controllers/ReportsController.cs` | 269 | `ExportLegalWorkPdf` | public async Task<IActionResult> ExportLegalWorkPdf( |
| `backend/Controllers/ReportsController.cs` | 294 | `ExportManagementReportExcel` | public async Task<IActionResult> ExportManagementReportExcel( |
| `backend/Controllers/ReportsController.cs` | 354 | `ExportManagementReportCsv` | public async Task<IActionResult> ExportManagementReportCsv( |
| `backend/Controllers/ReportsController.cs` | 414 | `ExportManagementReportPdf` | public async Task<IActionResult> ExportManagementReportPdf( |
| `backend/Controllers/RolePermissionsController.cs` | 70 | `ExportAuditCsv` | public async Task<IActionResult> ExportAuditCsv(CancellationToken ct) |
| `backend/Controllers/S3StorageController.cs` | 171 | `Download` | public async Task<IActionResult> Download( |
| `backend/Controllers/S3StorageController.cs` | 181 | `DownloadAsync` | var file = await _service.DownloadAsync(id, path, ct); |
| `backend/Controllers/S3StorageController.cs` | 183 | `Downloaded` | "Downloaded", |
| `backend/Controllers/StickyNotesController.cs` | 21 | `ImportantNotePinnedAction` | private const string ImportantNotePinnedAction = "pinned"; |
| `backend/Controllers/StickyNotesController.cs` | 22 | `ImportantNoteUpdatedAction` | private const string ImportantNoteUpdatedAction = "updated"; |
| `backend/Controllers/StickyNotesController.cs` | 23 | `ImportantNoteUnpinnedAction` | private const string ImportantNoteUnpinnedAction = "unpinned"; |
| `backend/Controllers/StickyNotesController.cs` | 24 | `ImportantNoteDeletedAction` | private const string ImportantNoteDeletedAction = "deleted"; |
| Еще 1245 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Интеграции календарей

Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 93 | `Google Calendar` | "text": "Webhook Google Calendar, Telegram и MAX, а также служебные публичные маршруты получили отдельные ограничения частоты и размера запроса. Это снижает лишнюю нагрузку от повторных уведомлений и некорректных запросо |
| `backend/AppReleases/releases.json` | 5170 | `Google Calendar` | "text": "Синхронизация с Google Calendar стала устойчивее к кратковременным сбоям внешнего сервиса. Если Google временно недоступен, CRM повторяет запрос и не показывает лишнее предупреждение пользователям из-за одиночно |
| `backend/AppReleases/releases.json` | 5426 | `Google Calendar` | "summary": "Исправлена ситуация после переноса VPS, когда ранее подключенный Google Calendar не мог синхронизироваться из-за серверных ключей защиты. Уже подключенные календари снова продолжают создавать и обновлять собы |
| `backend/AppReleases/releases.json` | 5432 | `Google Calendar` | "text": "Синхронизация Google Calendar снова работает для ранее подключенных пользователей после переноса сервера: задачи из CRM создаются и обновляются во внешнем календаре без ошибки служебного ключа." |
| `backend/AppReleases/releases.json` | 6073 | `Google Calendar` | "text": "Профиль пользователя стал точнее учитывать право на раздел «Календарь». Если сотруднику закрыт календарь, в личном профиле больше не показывается вкладка подключения Google Calendar и Яндекс.Календаря, а действи |
| `backend/AppReleases/releases.json` | 6930 | `Google Calendar` | "summary": "Настройки личной синхронизации Google Calendar и Яндекс.Календаря теперь учитывают актуальную роль пользователя в CRM. Если управление календарями ограничено администратором, старый открытый сеанс больше не п |
| `backend/AppReleases/releases.json` | 6940 | `Google Calendar` | "text": "Пользователь с актуальной ролью администратора в CRM по-прежнему может менять настройки синхронизации Google Calendar и Яндекс.Календаря." |
| `backend/AppReleases/releases.json` | 9436 | `Google Calendar` | "summary": "Google Calendar и Яндекс.Календарь теперь используют те же права видимости календаря, что и CRM: если роли доступны только выбранные сотрудники, во внешнем аккаунте синхронизируются только их календарные зада |
| `backend/AppReleases/releases.json` | 11765 | `Google Calendar` | "summary": "Улучшена синхронизация задач с Google Calendar и Яндекс.Календарём. Если во внешнем календаре появилось несколько событий одной и той же задачи CRM, PayPlanner оставит актуальное событие и удалит лишние дубли |
| `backend/AppReleases/releases.json` | 11771 | `Google Calendar` | "text": "В Google Calendar и Яндекс.Календаре добавлена дополнительная защита от повторного появления одинаковых событий одной CRM-задачи. При синхронизации лишние внешние события удаляются автоматически." |
| `backend/AppReleases/releases.json` | 11874 | `Google Calendar` | "text": "Синхронизация с SUDRF, Google Calendar, Яндекс.Календарём, Яндекс.Диском, Telegram и S3 получила более предсказуемую обработку отказов доступа, отсутствующих объектов, лимитов запросов и временных сбоев." |
| `backend/AppReleases/releases.json` | 11990 | `Google Calendar` | "summary": "В режиме отдельных календарей сотрудников Google Calendar и Яндекс.Календарь теперь создают календарь для активного одобренного пользователя даже тогда, когда у него еще нет задач в периоде синхронизации. Это |
| `backend/AppReleases/releases.json` | 11996 | `Google Calendar` | "text": "Исправлена ситуация, когда в Google Calendar или Яндекс.Календаре мог отсутствовать календарь активного пользователя, если у него еще не было задач для синхронизации." |
| `backend/AppReleases/releases.json` | 12084 | `Google Calendar` | "title": "Google Calendar не откатывает задачи из-за старых дублей", |
| `backend/AppReleases/releases.json` | 12085 | `Google Calendar` | "summary": "Улучшена синхронизация с Google Calendar после удаления или переноса дублей. Если во внешнем календаре осталось старое событие PayPlanner, которое уже не является актуальной связью задачи, CRM больше не приме |
| `backend/AppReleases/releases.json` | 12091 | `Google Calendar` | "text": "Старые дубли событий Google Calendar больше не могут вернуть задачу CRM на прежнее время или в прежний календарь." |
| `backend/AppReleases/releases.json` | 12103 | `Google Calendar` | "title": "Google Calendar больше не дублирует задачи при переносе между календарями", |
| `backend/AppReleases/releases.json` | 12104 | `Google Calendar` | "summary": "Исправлена синхронизация задач с Google Calendar в режиме отдельных календарей сотрудников. Если задача переходит в другой календарь или меняется ответственный, CRM обновляет существующую связь и убирает стар |
| `backend/AppReleases/releases.json` | 12110 | `Google Calendar` | "text": "При синхронизации с Google Calendar одна задача CRM больше не создает несколько одинаковых событий, если для нее меняется календарь сотрудника." |
| `backend/AppReleases/releases.json` | 12180 | `Google Calendar` | "summary": "При синхронизации задач с Google Calendar и Яндекс.Календарем система теперь проверяет часовой пояс внешнего календаря и приводит его к часовому поясу владельца из профиля. Это помогает избежать ситуации, ког |
| `backend/AppReleases/releases.json` | 12190 | `Google Calendar` | "text": "Ранее подключенные календари Google Calendar и Яндекс.Календаря при следующей синхронизации также обновляются до часового пояса владельца, чтобы события отображались без ручной правки timezone у провайдера." |
| `backend/AppReleases/releases.json` | 13187 | `Google Calendar` | "summary": "Синхронизация Google Calendar и Яндекс Календаря дополнительно проверяет переносы задач обратно в CRM и показывает время изменений в журнале по часовому поясу исполнителя.", |
| `backend/AppReleases/releases.json` | 13201 | `Google Calendar` | "text": "Обновление уже созданных событий из CRM в Google Calendar и Яндекс Календаре дополнительно проверено для задач около полуночи." |
| `backend/AppReleases/releases.json` | 13210 | `Google Calendar` | "summary": "Синхронизация задач с Google Calendar и Яндекс Календарем дополнительно проверена для сотрудников из других регионов и событий около полуночи.", |
| `backend/AppReleases/releases.json` | 13220 | `Google Calendar` | "text": "События Google Calendar и Яндекс Календаря получают служебную отметку часового пояса PayPlanner, чтобы последующие синхронизации устойчивее распознавали регион, для которого рассчитано время задачи." |
| `backend/AppReleases/releases.json` | 13331 | `Google Calendar` | "text": "Администраторам из разных регионов стало проще сверять журнал Google Calendar и Яндекс Календаря: один и тот же выбранный период больше не смещается при применении фильтра." |
| `backend/AppReleases/releases.json` | 13727 | `Google Calendar` | "summary": "Задачи, судебные заседания и события календарей теперь обрабатываются по единому правилу: система хранит точный момент времени и показывает его в часовом поясе пользователя из профиля. Это помогает избежать с |
| `backend/AppReleases/releases.json` | 13733 | `Google Calendar` | "text": "Исправлена обработка времени задач при синхронизации с Google Calendar и Яндекс Календарем. Перенос задачи во внешнем календаре теперь сохраняется в CRM как точный момент времени и не пересчитывается повторно пр |
| `backend/AppReleases/releases.json` | 13804 | `Google Calendar` | "text": "В настройках внешних календарей добавлен блок «Выполненные задачи в календарях». Администратор может оставить выполненные задачи в календарях с отдельной отметкой или скрывать их после выполнения. Правило примен |
| `backend/AppReleases/releases.json` | 13808 | `Google Calendar` | "text": "Исправлена выгрузка времени задач в Google Calendar и Яндекс Календарь: задачи теперь передаются по часовому поясу владельца календаря или исполнителя, а не по фиксированному времени CRM." |
| `backend/AppReleases/releases.json` | 13829 | `Google Calendar` | "summary": "Администратор может задать паузу перед повторной полной синхронизацией внешних календарей. Это помогает не создавать лишнюю нагрузку на Google Calendar и Яндекс.Календарь, когда пользователь несколько раз наж |
| `backend/AppReleases/releases.json` | 13839 | `Google Calendar` | "text": "В профиле пользователя кнопка ручной синхронизации Google Calendar и Яндекс.Календаря временно отключается, если полная синхронизация уже запускалась недавно. В это время обычная фоновая синхронизация продолжает |
| `backend/AppReleases/releases.json` | 13848 | `Google Calendar` | "summary": "Исправлена синхронизация даты и времени задач при переносе событий во внешних календарях. Изменения, сделанные в Google Calendar, теперь корректно сохраняются в CRM и не перетираются следующим циклом обмена." |
| `backend/AppReleases/releases.json` | 13854 | `Google Calendar` | "text": "При переносе задачи в Google Calendar новое время теперь корректно применяется к задаче в CRM и сохраняется при следующих циклах синхронизации." |
| `backend/AppReleases/releases.json` | 13858 | `Google Calendar` | "text": "Исправлена обработка времени задач, сохраненных в базе без признака часового пояса: Google Calendar и Яндекс.Календарь больше не получают такие задачи со сдвигом времени." |
| `backend/AppReleases/releases.json` | 13862 | `Google Calendar` | "text": "Если Google Calendar временно отвечает ограничением запросов, синхронизация делает повторные попытки и не показывает ошибку сразу после первого ответа Rate Limit Exceeded." |
| `backend/AppReleases/releases.json` | 13877 | `Google Calendar` | "text": "В разделе настроек внешних календарей появились отдельные интервалы фоновой синхронизации для Google Calendar и Яндекс.Календаря. По умолчанию обмен выполняется каждые 10 минут, а администратор может увеличить и |
| `backend/AppReleases/releases.json` | 13881 | `Google Calendar` | "text": "После сохранения интервала фоновые сервисы Google Calendar и Яндекс.Календаря применяют новые настройки без перезапуска приложения и начинают следующий цикл уже с обновленным расписанием." |
| `backend/AppReleases/releases.json` | 13889 | `Google Calendar` | "text": "Для изменений, пришедших из Google Calendar или Яндекс.Календаря, журнал показывает, что именно поменялось в задаче CRM: срок, длительность, статус или название." |
| `backend/AppReleases/releases.json` | 13908 | `Google Calendar` | "text": "В разделе «Аудит объектов» поля календарной синхронизации теперь отображаются по-русски: «Внешняя синхронизация календарей», «Синхронизация Google Calendar» и «Синхронизация Яндекс.Календаря»." |
| `backend/AppReleases/releases.json` | 13925 | `Google Calendar` | "summary": "В подсказках настройки Google Calendar и Яндекс.Календаря теперь сразу показаны готовые Redirect URI, которые нужно внести в кабинеты OAuth.", |
| `backend/AppReleases/releases.json` | 13931 | `Google Calendar` | "text": "В разделе «Настройки» → «Ключи интеграций» → блок внешних календарей вопросительные знаки рядом с Google Calendar и Яндекс.Календарем теперь показывают отдельный блок «Redirect URI для кабинета провайдера»." |
| `backend/AppReleases/releases.json` | 13935 | `Google Calendar` | "text": "Администратору больше не нужно искать callback URL в профиле пользователя: адрес для Google Calendar и Яндекс.Календаря можно скопировать прямо из окна подсказки." |
| `backend/AppReleases/releases.json` | 13986 | `Google Calendar` | "summary": "В настройках внешних календарей появились инструкции по подключению Google Calendar и Яндекс.Календаря, а профиль пользователя больше не показывает отключенные администратором провайдеры.", |
| `backend/AppReleases/releases.json` | 13992 | `Google Calendar` | "text": "В блоке «Модель синхронизации и конфликты» вопросительные знаки рядом с Google Calendar и Яндекс.Календарем теперь открывают инструкцию по настройке OAuth-приложения и ссылки на нужные кабинеты провайдеров." |
| `backend/AppReleases/releases.json` | 13996 | `Google Calendar` | "text": "Если администратор выключил Google Calendar или Яндекс.Календарь в настройках, соответствующая карточка больше не отображается в профиле пользователя." |
| `backend/AppReleases/releases.json` | 14015 | `Google Calendar` | "text": "В полном журнале синхронизации появилась постраничная навигация, чтобы администратор мог просматривать не только последние события Google Calendar и Яндекс.Календаря." |
| `backend/AppReleases/releases.json` | 14057 | `Google Calendar` | "text": "Исправлена ошибка загрузки статуса Google Calendar и Яндекс Календаря в профиле пользователя. Страница снова корректно показывает состояние подключения и предупреждение о настройке OAuth." |
| `backend/AppReleases/releases.json` | 14065 | `Google Calendar` | "text": "Пояснение помогает администратору понять, как работают отдельные включатели Google Calendar и Яндекс Календаря, главный источник событий, правила конфликтов, дата начала синхронизации, действия при потере доступ |
| `backend/AppReleases/releases.json` | 14073 | `Google Calendar` | "title": "Google Calendar и Яндекс Календарь включаются отдельно", |
| `backend/AppReleases/releases.json` | 14074 | `Google Calendar` | "summary": "В настройках внешних календарей администратор теперь может включать Google Calendar и Яндекс Календарь независимо друг от друга.", |
| `backend/AppReleases/releases.json` | 14080 | `Google Calendar` | "text": "В разделе внешних календарей вместо одного общего переключателя появились отдельные переключатели для Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 14084 | `Google Calendar` | "text": "Карточки провайдеров и чек-лист готовности теперь учитывают выбранный календарь отдельно. Если включен только Google Calendar, выключенный Яндекс Календарь не будет блокировать подготовку подключения." |
| `backend/AppReleases/releases.json` | 14088 | `Google Calendar` | "text": "Фоновые проверки запускаются только для включенных провайдеров: Google Calendar и Яндекс Календарь больше не зависят от одного общего включателя." |
| `backend/AppReleases/releases.json` | 14097 | `Google Calendar` | "summary": "В настройках внешних календарей появился понятный переключатель для включения Google Calendar и Яндекс Календаря.", |
| `backend/AppReleases/releases.json` | 14103 | `Google Calendar` | "text": "В разделе внешних календарей добавлен главный переключатель «Включить Google Calendar и Яндекс Календарь». Администратор может включить календарный контур прямо из настроек без изменения серверного конфига." |
| `backend/AppReleases/releases.json` | 14120 | `Google Calendar` | "summary": "В настройках внешних календарей администратор теперь может задавать общие правила синхронизации Google Calendar и Яндекс Календаря, а также смотреть полный журнал событий.", |
| `backend/AppReleases/releases.json` | 14130 | `Google Calendar` | "text": "Добавлена дата начала синхронизации по умолчанию. Если включен режим управления администратором, эта дата применяется к подключениям Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 14138 | `Google Calendar` | "text": "В настройках добавлен полный журнал синхронизации по Google Calendar и Яндекс Календарю. Администратор видит провайдера, сотрудника, действие, статус, время и сообщение по каждому событию." |
| `backend/AppReleases/releases.json` | 14147 | `Google Calendar` | "summary": "В настройках Google Calendar и Яндекс.Календаря можно указать, с какой даты задачи CRM должны попадать во внешний календарь.", |
| `backend/AppReleases/releases.json` | 14153 | `Google Calendar` | "text": "В профиле пользователя в настройках Google Calendar и Яндекс.Календаря добавлено поле «Синхронизировать задачи с даты». Если указать дату, во внешний календарь будут попадать только задачи со сроком выполнения н |
| `backend/AppReleases/releases.json` | 14170 | `Google Calendar` | "summary": "При отключении Google Calendar или Яндекс.Календаря CRM теперь сначала удаляет созданные PayPlanner календари и события во внешнем календаре.", |
| `backend/AppReleases/releases.json` | 14176 | `Google Calendar` | "text": "В профиле пользователя отключение Google Calendar и Яндекс.Календаря теперь работает с обязательной очисткой: CRM удаляет созданные PayPlanner календари и события во внешнем календаре, а только потом убирает под |
| `backend/AppReleases/releases.json` | 14189 | `Google Calendar` | "summary": "В профиле пользователя появился блок Яндекс.Календаря. Он работает по тем же правилам, что Google Calendar, и позволяет синхронизировать задачи CRM с календарем Яндекса.", |
| `backend/AppReleases/releases.json` | 14199 | `Google Calendar` | "text": "Если у пользователя подключены Google Calendar и Яндекс.Календарь, изменения проходят через CRM и передаются во второй календарь без зацикливания. Например, изменение события в Google обновит задачу CRM, а затем |
| `backend/AppReleases/releases.json` | 14253 | `Google Calendar` | "title": "Исправлена загрузка блока Google Calendar в профиле", |
| `backend/AppReleases/releases.json` | 14254 | `Google Calendar` | "summary": "Блок Google Calendar в профиле пользователя теперь стабильно открывается и показывает состояние подключения, настройки синхронизации, список календарей и последние события.", |
| `backend/AppReleases/releases.json` | 14260 | `Google Calendar` | "text": "Исправлена ошибка, из-за которой раздел Google Calendar в профиле мог не получить статус подключения и не запустить синхронизацию из-за внутренней ошибки сервиса." |
| `backend/AppReleases/releases.json` | 14264 | `Google Calendar` | "text": "Добавлена дополнительная проверка, которая контролирует, что сервис Google Calendar корректно создаётся при запуске приложения." |
| `backend/AppReleases/releases.json` | 14345 | `Google Calendar` | "title": "Улучшено удаление задач и календарей Google Calendar", |
| `backend/AppReleases/releases.json` | 14346 | `Google Calendar` | "summary": "Синхронизация Google Calendar теперь аккуратнее очищает внешние события и календари, чтобы после удаления задач или сотрудников в Google не оставались лишние записи PayPlanner.", |
| `backend/AppReleases/releases.json` | 14352 | `Google Calendar` | "text": "При удалении задачи CRM связанное событие PayPlanner удаляется из Google Calendar. Если событие не удалось удалить, задача не удаляется сразу, а пользователь видит ошибку и может проверить журнал синхронизации." |
| `backend/AppReleases/releases.json` | 14360 | `Google Calendar` | "text": "Журнал Google Calendar теперь фиксирует удаление событий и календарей, а также ошибки очистки, чтобы администратор видел, что именно произошло при удалении задачи или пользователя." |
| `backend/AppReleases/releases.json` | 14364 | `Google Calendar` | "text": "Права на удаление задач по-прежнему управляются ролями в разделе задач. Пользователь без права удаления не сможет удалить задачу и, соответственно, связанное событие в Google Calendar." |
| `backend/AppReleases/releases.json` | 14475 | `Google Calendar` | "title": "Добавлена синхронизация задач с Google Calendar", |
| `backend/AppReleases/releases.json` | 14476 | `Google Calendar` | "summary": "Пользователи могут подключить личный Google Calendar в профиле и синхронизировать с ним задачи CRM. Для администратора поддержан отдельный режим, при котором задачи раскладываются по календарям сотрудников.", |
| `backend/AppReleases/releases.json` | 14482 | `Google Calendar` | "text": "В профиле появился блок Google Calendar. Пользователь может подключить календарь через Google, включить или остановить синхронизацию, запустить обмен вручную и посмотреть последние события синхронизации." |
| `backend/AppReleases/releases.json` | 14486 | `Google Calendar` | "text": "Задачи, назначенные пользователю, создаются и обновляются в Google Calendar. Изменения времени, длительности, названия и статуса события могут возвращаться обратно в CRM по выбранному правилу конфликтов." |
| `backend/AppReleases/releases.json` | 14490 | `Google Calendar` | "text": "Для администратора доступен режим отдельных календарей по исполнителям: при синхронизации все задачи CRM попадают в Google Calendar аккуратно, с разделением по сотрудникам." |
| `backend/AppReleases/releases.json` | 15619 | `Google Calendar` | "summary": "Администратор теперь видит отдельный фоновый сервис для подготовки синхронизации Google Calendar и Яндекс Календаря. Сервис проверяет готовность подключения и события-кандидаты, не меняя внешние календари до  |
| `backend/AppReleases/releases.json` | 15633 | `Google Calendar` | "text": "Google Calendar и Яндекс Календарь по-прежнему не включаются автоматически. До настройки OAuth и приемки модели конфликтов сервис работает как диагностический и подготовительный контур." |
| `backend/AppReleases/releases.json` | 15757 | `Google Calendar` | "summary": "Изменения правил подключения Google Calendar и Яндекс Календаря теперь фиксируются в общем журнале действий. Администраторы могут увидеть, кто изменил модель синхронизации, правило конфликтов, ручное подтверж |
| `backend/AppReleases/releases.json` | 15790 | `Google Calendar` | "text": "Чек-лист готовности теперь учитывает сохраненные правила конфликтов и отметку о dry-run на копии базы. После сохранения этих пунктов видно, какие обязательные шаги еще остаются перед подключением Google Calendar |
| `backend/AppReleases/releases.json` | 15803 | `Google Calendar` | "summary": "В настройках интеграций появился чек-лист готовности к подключению Google Calendar и Яндекс Календаря. Он показывает, какие обязательные шаги уже закрыты, а что еще нужно согласовать перед запуском внешней си |
| `backend/AppReleases/releases.json` | 15813 | `Google Calendar` | "text": "ICS-выгрузка отдельно отмечена как доступный безопасный контур, а Google Calendar и Яндекс Календарь остаются в подготовительном режиме до закрытия обязательных пунктов." |
| `backend/AppReleases/releases.json` | 15826 | `Google Calendar` | "summary": "В настройках интеграций появился блок состояния календарных провайдеров: видно, что ICS-выгрузка уже доступна, а Google Calendar и Яндекс Календарь ждут отдельной настройки и приемки.", |
| `backend/AppReleases/releases.json` | 15832 | `Google Calendar` | "text": "В разделе «Ключи и внешние сервисы» добавлен блок «Провайдеры календарной синхронизации». Он показывает доступность ICS, Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 15840 | `Google Calendar` | "text": "Google Calendar и Яндекс Календарь не запускаются автоматически: до настройки OAuth и утверждения модели конфликтов они отображаются как недоступные или требующие настройки." |
| `backend/AppReleases/releases.json` | 17673 | `Google Calendar` | "text": "Исправлена синхронизация с Google Calendar: при повторном или параллельном запуске CRM больше не создает несколько одинаковых календарей PayPlanner для одного сотрудника. Если такие дубли уже появились, ближайша |
| `backend/Controllers/CalendarSyncController.cs` | 231 | `Google Calendar` | ProviderLabel = "Google Calendar", |
| `backend/Controllers/SystemJobsController.cs` | 798 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:Enabled", true)); |
| `backend/Controllers/SystemJobsController.cs` | 801 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:RetentionDays", CalendarSyncLogRetentionService.DefaultRetentionDays))); |
| `backend/Controllers/SystemJobsController.cs` | 804 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:IntervalHours", CalendarSyncLogRetentionService.DefaultIntervalHours))); |
| `backend/Controllers/SystemJobsController.cs` | 807 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:BatchSize", CalendarSyncLogRetentionService.DefaultBatchSize))); |
| `backend/Controllers/SystemJobsController.cs` | 857 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:Enabled", false)); |
| `backend/Controllers/SystemJobsController.cs` | 860 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", CalendarSyncSchedulerService.DefaultIntervalMinutes))); |
| `backend/Controllers/SystemJobsController.cs` | 863 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:BatchSize", CalendarSyncSchedulerService.DefaultBatchSize))); |
| `backend/Controllers/SystemJobsController.cs` | 866 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", CalendarSyncSchedulerService.DefaultLookAheadDays))); |
| `backend/Controllers/TasksController.cs` | 3684 | `ExternalCalendar` | Console.Error.WriteLine($"[ExternalCalendar] Task sync failed for task {taskId}: {ex.Message}"); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 493 | `Google Calendar` | Assert.Equal("Google Calendar", item.ProviderLabel); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 496 | `Google Calendar` | Assert.Equal("CRM → Google Calendar", item.DirectionLabel); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 545 | `Google Calendar` | Message = "Календарь «PayPlanner - Иван Иванов» удален из Google Calendar.", |
| `backend/PayPlanner.Api.Tests/ExternalCalendarSyncCoordinatorTests.cs` | 120 | `Google Calendar` | Assert.Contains("Google Calendar", notification.Title); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1423 | `GoogleCalendar` | var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1"); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 106 | `Google Calendar` | Assert.Contains("Google Calendar отключен администратором", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 730 | `Google Calendar` | Assert.Contains("Повторную полную синхронизацию Google Calendar можно запустить после", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1066 | `Google Calendar` | var task = await CreateTaskAsync(db, user.Id, "Отключить Google Calendar"); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1114 | `Google Calendar` | Assert.Contains("Не удалось отключить Google Calendar", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1151 | `Google Calendar` | new TaskCanceledException("Google Calendar timeout.")))); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1156 | `Google Calendar` | Assert.Contains("Не удалось отключить Google Calendar", error.Message); |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 6505 | `Google Calendar` | "Google Calendar", |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 6510 | `Google Calendar` | "Google Calendar", |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 6520 | `Google Calendar` | Assert.Contains("Google Calendar", notification.Title); |
| `backend/PayPlanner.Api.Tests/YandexCalendarSyncServiceTests.cs` | 1099 | `YandexCalendar` | var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1"); |
| `backend/Services/CalendarSyncLogRetentionService.cs` | 225 | `CalendarSync` | ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("CalendarSync:LogRetention:Enabled", true)), |
| `backend/Services/CalendarSyncLogRetentionService.cs` | 226 | `CalendarSync` | NormalizeRetentionDays(ParseInt(settings.GetValueOrDefault(RetentionDaysSettingKey), _configuration.GetValue("CalendarSync:LogRetention:RetentionDays", DefaultRetentionDays))), |
| `backend/Services/CalendarSyncLogRetentionService.cs` | 227 | `CalendarSync` | NormalizeIntervalHours(ParseInt(settings.GetValueOrDefault(IntervalHoursSettingKey), _configuration.GetValue("CalendarSync:LogRetention:IntervalHours", DefaultIntervalHours))), |
| `backend/Services/CalendarSyncLogRetentionService.cs` | 228 | `CalendarSync` | NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("CalendarSync:LogRetention:BatchSize", DefaultBatchSize)))); |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 133 | `Google Calendar` | Label = "Google Calendar", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 143 | `Google Calendar` | Message = BuildProviderMessage("Google Calendar", googleCalendarSyncEnabled), |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 199 | `Google Calendar` | Label = "Google Calendar в админке", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 205 | `Google Calendar` | ? "Google Calendar включен администратором для диагностики и подготовки подключения." |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 206 | `Google Calendar` | : "Google Calendar выключен в админских настройках и не блокирует подключение других провайдеров.", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 223 | `Google Calendar` | Label = "Google Calendar OAuth", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 229 | `Google Calendar` | ? "Google Calendar включен, но перед подключением нужно утвердить OAuth-приложение, redirect URI и набор разрешений." |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 230 | `Google Calendar` | : "Проверка OAuth Google Calendar пропущена, пока провайдер выключен в админке.", |
| `backend/Services/CalendarSyncSchedulerService.cs` | 315 | `CalendarSync` | ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("CalendarSync:Scheduler:Enabled", false)), |
| `backend/Services/CalendarSyncSchedulerService.cs` | 316 | `CalendarSync` | NormalizeInterval(ParseInt(settings.GetValueOrDefault(IntervalMinutesSettingKey), _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", DefaultIntervalMinutes))), |
| `backend/Services/CalendarSyncSchedulerService.cs` | 317 | `CalendarSync` | NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("CalendarSync:Scheduler:BatchSize", DefaultBatchSize))), |
| `backend/Services/CalendarSyncSchedulerService.cs` | 318 | `CalendarSync` | NormalizeLookAheadDays(ParseInt(settings.GetValueOrDefault(LookAheadDaysSettingKey), _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", DefaultLookAheadDays)))); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 36 | `Google Calendar` | "Google Calendar", |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 74 | `Google Calendar` | failures.Add($"Google Calendar: {ex.Message}"); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 75 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar task deletion failed for task {TaskId}", taskId); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 122 | `Google Calendar` | failures.Add($"Google Calendar: {ex.Message}"); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 123 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar cleanup failed for deleted user {UserId}", userId); |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 57 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar background sync failed."); |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 60 | `Google Calendar` | "Фоновая синхронизация Google Calendar завершилась ошибкой.", |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 118 | `Google Calendar` | "Google Calendar", |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 126 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar admin warning notification failed."); |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 157 | `Google Calendar` | _logger.LogWarning("Stored calendar sync settings are invalid. Google Calendar background sync will use feature flag fallback."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 70 | `GoogleCalendar` | _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1"); |
| `backend/Services/GoogleCalendarSyncService.cs` | 92 | `Google Calendar` | throw new InvalidOperationException("Google Calendar отключен администратором."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 141 | `Google Calendar` | throw new KeyNotFoundException("Запрос подключения Google Calendar не найден."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 178 | `Google Calendar` | return BuildPopupHtml(false, "Запрос подключения Google Calendar не найден."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 243 | `Google Calendar` | return BuildPopupHtml(true, "Google Calendar подключён. Вернитесь в CRM.", authRequest.ReturnUrl); |
| `backend/Services/GoogleCalendarSyncService.cs` | 258 | `Google Calendar` | "completed" => BuildPopupHtml(true, "Google Calendar уже подключён. Вернитесь в CRM.", authRequest.ReturnUrl), |
| `backend/Services/GoogleCalendarSyncService.cs` | 259 | `Google Calendar` | "failed" or "expired" => BuildPopupHtml(false, authRequest.Error ?? "Подключение Google Calendar не завершено.", authRequest.ReturnUrl), |
| `backend/Services/GoogleCalendarSyncService.cs` | 260 | `Google Calendar` | _ => BuildPopupHtml(false, "Запрос подключения Google Calendar уже обрабатывается.", authRequest.ReturnUrl), |
| `backend/Services/GoogleCalendarSyncService.cs` | 273 | `Google Calendar` | throw new KeyNotFoundException("Подключение Google Calendar не найдено."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 282 | `Google Calendar` | await AddLogAsync(connection.Id, null, null, "system", "settings-updated", "success", "Настройки Google Calendar сохранены.", BuildConnectionSettingsDetails(connection), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 303 | `Google Calendar` | "Подключение Google Calendar отключается пользователем.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 311 | `Google Calendar` | "Не удалось отключить Google Calendar: сначала нужно удалить созданные PayPlanner календари и события во внешнем календаре. " |
| `backend/Services/GoogleCalendarSyncService.cs` | 334 | `Google Calendar` | ? "Синхронизация Google Calendar выполнена." |
| `backend/Services/GoogleCalendarSyncService.cs` | 335 | `Google Calendar` | : "Синхронизация Google Calendar завершена с ошибками. Проверьте журнал.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 365 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar scheduled sync failed for user {UserId}", userId); |
| `backend/Services/GoogleCalendarSyncService.cs` | 387 | `Google Calendar` | $"Повторную полную синхронизацию Google Calendar можно запустить после {FormatLogDateTime(availableAt.Value)}. До этого времени работает обычная фоновая синхронизация.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 426 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar task sync failed for task {TaskId} and user {UserId}", taskId, item.UserId); |
| `backend/Services/GoogleCalendarSyncService.cs` | 463 | `Google Calendar` | ? "Событие удалено из Google Calendar после удаления задачи в CRM." |
| `backend/Services/GoogleCalendarSyncService.cs` | 464 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при удалении задачи в CRM.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 479 | `Google Calendar` | "Не удалось удалить событие из Google Calendar после удаления задачи в CRM.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 524 | `Google Calendar` | $"Сотрудник #{userId} удаляется из CRM, календарь сотрудника убирается из админского Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 549 | `Google Calendar` | throw new UnauthorizedAccessException("Google Calendar webhook не содержит channel id или token."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 558 | `Google Calendar` | throw new UnauthorizedAccessException("Google Calendar webhook не прошёл проверку канала."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 599 | `Google Calendar` | await AddLogAsync(connection.Id, null, null, "system", "ensure-calendars", "error", "Не удалось подготовить календари сотрудников в Google Calendar.", ex.Message, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 610 | `Google Calendar` | await AddLogAsync(connection.Id, null, null, "google-to-crm", "import", "error", "Не удалось загрузить изменения из Google Calendar.", ex.Message, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 643 | `Google Calendar` | await AddLogAsync(connection.Id, null, task.Id, "crm-to-google", "upsert", "error", "Не удалось синхронизировать задачу с Google Calendar.", ex.Message, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 775 | `Google Calendar` | ? $"Дублирующий календарь «{duplicate.Name}» удален из Google Calendar." |
| `backend/Services/GoogleCalendarSyncService.cs` | 776 | `Google Calendar` | : $"Дублирующий календарь «{duplicate.Name}» уже отсутствовал в Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 794 | `Google Calendar` | $"Не удалось удалить дублирующий календарь «{duplicate.Name}» из Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 834 | `Google Calendar` | "Не удалось получить токен Google Calendar для очистки календарей удаляемого пользователя.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 853 | `Google Calendar` | ? $"Календарь «{calendar.Name}» удален из Google Calendar." |
| `backend/Services/GoogleCalendarSyncService.cs` | 854 | `Google Calendar` | : $"Календарь «{calendar.Name}» уже отсутствовал в Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 882 | `Google Calendar` | $"Не удалось удалить календарь «{calendar.Name}» из Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 957 | `Google Calendar` | ? "Устаревший дубль события Google Calendar удален, задача CRM оставлена без изменений." |
| `backend/Services/GoogleCalendarSyncService.cs` | 958 | `Google Calendar` | : "Устаревший дубль события Google Calendar уже отсутствует, задача CRM оставлена без изменений.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 972 | `Google Calendar` | "Не удалось удалить устаревший дубль события Google Calendar, задача CRM оставлена без изменений.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 999 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "google-to-crm", "detach", "success", "Событие удалено в Google Calendar, связь с задачей помечена как отключённая.", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1015 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "google-to-crm", "skip", "success", "Изменения в Google Calendar прочитаны, но правило конфликтов оставляет CRM главным источником.", null, cancellati |
| `backend/Services/GoogleCalendarSyncService.cs` | 1072 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "google-to-crm", "update-task", "success", "Изменения из Google Calendar применены к задаче.", string.Join("; ", changes), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1139 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "delete-event", "success", "Старое событие Google Calendar удалено после переноса задачи в другой календарь.", BuildTaskSnapshotDetails(task, timeZo |
| `backend/Services/GoogleCalendarSyncService.cs` | 1143 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "delete-event", "error", "Не удалось удалить старое событие Google Calendar после переноса задачи в другой календарь.", ex.Message, cancellationToke |
| `backend/Services/GoogleCalendarSyncService.cs` | 1146 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "create-event", "success", "Задача создана в Google Calendar.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1156 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "update-event", "success", "Задача обновлена в Google Calendar.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1324 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, null, "system", "create-calendar", "success", $"В Google Calendar создан календарь «{name}».", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1478 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1479 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при очистке задач раньше даты начала синхронизации.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1488 | `Google Calendar` | await AddLogAsync(connection.Id, link.CalendarSyncCalendarId, link.TaskJournalItemId, "crm-to-google", "delete-event", "error", "Не удалось удалить событие Google Calendar для задачи раньше даты начала синхронизации.", e |
| `backend/Services/GoogleCalendarSyncService.cs` | 1524 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1525 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при очистке задачи раньше даты начала синхронизации.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1554 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что в настройках включено скрытие выполненных задач." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1555 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при скрытии выполненной задачи.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1584 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что доступ к календарю сотрудника отозван в правах роли." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1585 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при отзыве доступа к календарю сотрудника.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1710 | `Google Calendar` | await AddLogAsync(connection.Id == 0 ? null : connection.Id, null, null, "system", "oauth-connected", "success", "Google Calendar подключён к профилю.", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1723 | `Google Calendar` | throw new InvalidOperationException("Нет refresh token для Google Calendar. Подключите календарь заново."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1868 | `Google Calendar` | ? "Лишний дубль события Google Calendar для задачи CRM удалён." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1869 | `Google Calendar` | : "Лишний дубль события Google Calendar для задачи CRM уже отсутствовал.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1882 | `Google Calendar` | "Не удалось удалить лишний дубль события Google Calendar для задачи CRM.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 2013 | `Google Calendar` | throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2029 | `Google Calendar` | throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку при удалении."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2553 | `Google Calendar` | var title = success ? "Google Calendar подключён" : "Ошибка подключения Google Calendar"; |
| `backend/Services/GoogleCalendarSyncService.cs` | 2787 | `Google Calendar` | "googleWins" => "Google Calendar главный", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1814 | `calendarId` | private async Task<bool> DeleteGoogleCalendarAsync(string accessToken, string calendarId, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1816 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}", accessToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1820 | `calendarId` | private async Task<GoogleEventWriteResult> CreateGoogleEventAsync(string accessToken, string calendarId, GoogleEventPayload payload, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1822 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Post, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", accessToken, JsonSerializer.Serialize(payload, JsonOptions)); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1829 | `calendarId` | private async Task<GoogleEventWriteResult> UpdateGoogleEventAsync(string accessToken, string calendarId, string eventId, GoogleEventPayload payload, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1831 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Put, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken, JsonSerializer.Serialize(payload, JsonOptions |
| `backend/Services/GoogleCalendarSyncService.cs` | 1838 | `calendarId` | private async Task<bool> DeleteGoogleEventAsync(string accessToken, string calendarId, string eventId, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1840 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1891 | `calendarId` | string calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 1911 | `calendarId` | var url = QueryHelpers.AddQueryString($"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", query!); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2221 | `calendarId` | int? calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 2230 | `calendarId` | var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2235 | `calendarId` | CalendarSyncCalendarId = calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 2250 | `calendarId` | int? calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 2277 | `calendarId` | if (calendarId.HasValue) |
| `backend/Services/GoogleCalendarSyncService.cs` | 2281 | `calendarId` | .Where(item => item.Id == calendarId.Value) |
| `backend/Services/YandexCalendarSyncService.cs` | 71 | `YandexCalendar` | _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1"); |
| `backend/Services/YandexCalendarSyncService.cs` | 2217 | `calendarId` | int? calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 2226 | `calendarId` | var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken); |
| `backend/Services/YandexCalendarSyncService.cs` | 2231 | `calendarId` | CalendarSyncCalendarId = calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 2246 | `calendarId` | int? calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 2273 | `calendarId` | if (calendarId.HasValue) |
| `backend/Services/YandexCalendarSyncService.cs` | 2277 | `calendarId` | .Where(item => item.Id == calendarId.Value) |
| `docs/anonymous-integration-security.md` | 7 | `Google Calendar` | - OAuth-callback Google Calendar, Яндекс.Календаря и Яндекс.Диска ограничены 30 запросами в минуту на внешний IP и конкретный маршрут. |
| `docs/anonymous-integration-security.md` | 8 | `Google Calendar` | - Webhook Google Calendar, Telegram и MAX ограничены 300 запросами в минуту на внешний IP и маршрут. Очередь rate limiter отключена, поэтому лишние запросы сразу получают `429` и не удерживают ресурсы приложения. |
| `docs/archive/roadmaps/audit-roadmap-2026-06-10.md` | 492 | `Google Calendar` | - Google Calendar; |
| `docs/archive/roadmaps/audit-roadmap-2026-06-10.md` | 497 | `Google Calendar` | - Выполнено: добавлены/расширены mocked tests для DaData, Sudrf, Yandex Disk, Google Calendar, Yandex Calendar, Telegram и S3; матрица покрытия зафиксирована в `docs/http-integration-error-coverage-2026-06-10.md`; target |
| `docs/archive/roadmaps/audit-roadmap-2026-06-10.md` | 504 | `Google Calendar` | - Google Calendar webhook; |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 63 | `Google Calendar` | - Синхронизация выбранных сотрудников во внешний Google Calendar секретаря или другой роли. |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 213 | `Google Calendar` | - Выполнено: внешний сценарий реализован для Google Calendar и Яндекс.Календаря на базе фактической видимости календаря роли. |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 239 | `Google Calendar` | - [x] Проверить Google Calendar. |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 241 | `Google Calendar` | - Выполнено: покрыто unit-тестами Google Calendar sync; реальный аккаунт остается acceptance после деплоя. |
| `docs/archive/roadmaps/remaining-roadmap.md` | 251 | `Google Calendar` | - `[x]` 2026-06-23: пользовательские действия Google Calendar и Яндекс.Календаря в режиме `adminManaged` больше не доверяют старому `admin` claim и проверяют актуальную роль пользователя в БД перед изменением личной синх |
| `docs/batch-operations-audit-coverage-2026-06-10.md` | 22 | `Google Calendar` | \| `backend/Services/GoogleCalendarSyncService.cs:190` \| `ExecuteUpdateAsync` \| Один ожидающий OAuth-запрос подключения Google Calendar \| Персональный объектный аудит не создаётся: техническая строка хранит собственный ст |
| `docs/crm-legal-module-gap-report-2026-05-25.md` | 117 | `Google Calendar` | - диагностика календарных провайдеров в настройках интеграций: ICS активен, Google Calendar и Яндекс Календарь явно показывают статус feature flag/OAuth до запуска двусторонней синхронизации; |
| `docs/crm-legal-module-gap-report-2026-05-25.md` | 129 | `Google Calendar` | - Google Calendar и Яндекс Календарь; |
| `docs/full-project-audit-2026-06-10.md` | 1601 | `Google Calendar` | \| Google Calendar \| services + background sync \| Есть OAuth/calendar sync/manual sync/webhook paths. \| |
| `docs/full-system-quality-roadmap-2026-08-04.md` | 115 | `Google Calendar` | Оставшиеся анонимные маршруты Google Calendar, Яндекс.Календаря, Яндекс.Диска, Telegram, MAX, feature flags и health получили централизованные ограничения по IP и маршруту; тела webhook ограничены до разбора JSON. OAuth- |
| `docs/google-calendar-sync-guide.md` | 1 | `Google Calendar` | # Google Calendar: подключение и синхронизация задач |
| `docs/google-calendar-sync-guide.md` | 5 | `Google Calendar` | Подключение Google Calendar добавлено в профиль пользователя, а не в общие настройки CRM. |
| `docs/google-calendar-sync-guide.md` | 7 | `Google Calendar` | Путь для пользователя: профиль пользователя -> блок "Google Calendar". |
| `docs/google-calendar-sync-guide.md` | 11 | `Google Calendar` | При отключении Google Calendar CRM сначала удаляет созданные PayPlanner календари и события во внешнем календаре. Если Google не подтвердил удаление хотя бы одного календаря или события, подключение не отключается, а при |
| `docs/google-calendar-sync-guide.md` | 13 | `Google Calendar` | В настройках подключения можно указать дату начала синхронизации. Если дата задана, CRM создаёт и обновляет в Google Calendar только задачи со сроком выполнения начиная с этой даты. Уже созданные события по задачам раньш |
| `docs/google-calendar-sync-guide.md` | 15 | `Google Calendar` | Такой вариант выбран потому, что Google Calendar подключается к личному Google-аккаунту конкретного сотрудника. У каждого пользователя может быть свой календарь и свои OAuth-токены. Администратор тоже подключает Google C |
| `docs/google-calendar-sync-guide.md` | 22 | `Google Calendar` | 2. Включить Google Calendar API. |
| `docs/google-calendar-sync-guide.md` | 39 | `Google Calendar` | Эти же настройки используются существующим механизмом Google OAuth в CRM. Для календаря дополнительно требуется, чтобы в Google Cloud был включен Google Calendar API и OAuth-приложение разрешало scope календаря. |
| `docs/google-calendar-sync-guide.md` | 54 | `Google Calendar` | 2. В блоке "Google Calendar" нажимает кнопку подключения. |
| `docs/google-calendar-sync-guide.md` | 69 | `Google Calendar` | При создании или изменении задачи в CRM сервис создает или обновляет событие в Google Calendar. В событии сохраняется служебная связь с задачей CRM, поэтому последующие изменения обновляют уже существующее событие, а не  |
| Еще 75 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

## Правила разбора

- Совпадение в отчете не всегда означает ошибку: это место, которое надо проверить при переводе на UTC + timezone ID.
- Жесткие смещения вроде `+07:00`, `UTC+7`, `Asia/Novosibirsk` и `AddHours(7)` считаются приоритетными для исправления.
- Для фильтров периода нужно отдельно проверить границы дня: начало и конец должны считаться в timezone пользователя, а храниться и сравниваться в UTC.
- Для календарей Google/Yandex/ICS нужно передавать timezone ID события или календаря, а не полагаться на timezone сервера.

_Корень проверки: `.`_
