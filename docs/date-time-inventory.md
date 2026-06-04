# Инвентаризация дат и времени

Сгенерировано: 2026-06-04T03:19:40.075Z

Этот отчет собирает места, где проект работает с датами, временем, периодами, экспортами и календарными интеграциями. Его задача - помочь переводить систему на единое правило: хранение в UTC плюс явный timezone ID пользователя или организации.

## Как обновить

```powershell
npm run audit:dates
```

## Сводка

- Проверено файлов: 1308
- Найдено совпадений: 6296

| Категория | Совпадений | Что проверить |
| --- | ---: | --- |
| Backend: поля и свойства дат | 1443 | Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID. |
| Backend: текущее время сервера | 1498 | Вызовы Now/UtcNow, которые надо привести к единому источнику времени. |
| Backend: преобразования timezone | 442 | Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги. |
| Ручные смещения времени | 214 | Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов. |
| Frontend: работа с датами | 642 | JS/TS места, где браузер может применить локальную timezone пользователя автоматически. |
| Фильтры и периоды | 910 | Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя. |
| Экспорты, импорты и файлы | 900 | Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени. |
| Интеграции календарей | 247 | Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID. |

## Файлы с наибольшим количеством совпадений

| Файл | Совпадений |
| --- | ---: |
| `backend/Controllers/ReportsController.cs` | 381 |
| `backend/AppReleases/releases.json` | 303 |
| `backend/Controllers/V2/CasesV2Controller.cs` | 280 |
| `backend/PayPlanner.Api.Tests/MoneyOperationsControllerTests.cs` | 279 |
| `backend/Services/GoogleCalendarSyncService.cs` | 152 |
| `backend/PayPlanner.Api.Tests/CasesV2ControllerTests.cs` | 141 |
| `backend/Controllers/MoneyOperationsController.cs` | 140 |
| `backend/Controllers/TasksController.cs` | 126 |
| `backend/PayPlanner.Api.Tests/TasksControllerTests.cs` | 118 |
| `src/components/Tasks/TaskJournal.tsx` | 105 |
| `backend/Controllers/InvoicesController.cs` | 94 |
| `backend/Services/YandexCalendarSyncService.cs` | 90 |
| `backend/PayPlanner.Api.Tests/PaymentsControllerTests.cs` | 79 |
| `src/types/index.ts` | 71 |
| `src/components/Money/MoneyOperations.tsx` | 68 |
| `backend/Controllers/CashOrdersController.cs` | 66 |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 66 |
| `backend/Services/DatabaseImportService.cs` | 65 |
| `src/components/Tasks/TaskJournal.test.tsx` | 56 |
| `src/components/Accounts/InvoiceModal.tsx` | 51 |
| `backend/Services/CalendarTimeZoneHelper.cs` | 48 |
| `backend/PayPlanner.Api.Tests/InvoicesControllerCoverageTests.cs` | 47 |
| `backend/PayPlanner.Api.Tests/InvoicesControllerCrudTests.cs` | 47 |
| `src/components/Cases/CaseDetailPage.tsx` | 45 |
| `backend/Controllers/PaymentsController.cs` | 42 |
| `src/components/Clients/ClientDetail.tsx` | 40 |
| `src/components/Reports/Reports.tsx` | 40 |
| `backend/PayPlanner.Api.Tests/ReportsControllerTests.cs` | 39 |
| `backend/Controllers/AccountsController.cs` | 36 |
| `backend/Controllers/ContractsController.cs` | 34 |

## Детализация

### Backend: поля и свойства дат

Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 1642 | `DateTime? EffectivePlannedDate` | private static DateTime? EffectivePlannedDate(PaymentAggregateItem payment) |
| `backend/Controllers/AccountsController.cs` | 1712 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1713 | `DateTime? DueDate` | public DateTime? DueDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1714 | `DateTime? PlannedDate` | public DateTime? PlannedDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1720 | `DateTime? SentForSigningAt` | public DateTime? SentForSigningAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1721 | `DateTime? SignedAt` | public DateTime? SignedAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1722 | `DateTime? CancelledAt` | public DateTime? CancelledAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1726 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1757 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1766 | `DateTime? AccountDate` | public DateTime? AccountDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1767 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1768 | `DateTime PlannedDate` | public DateTime PlannedDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1769 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1793 | `DateTime DueDate` | DateTime DueDate, |
| `backend/Controllers/AccountsController.cs` | 1816 | `DateTime? DueDate` | public DateTime? DueDate { get; set; } |
| `backend/Controllers/AccountsController.cs` | 1817 | `DateTime? PaidDate` | public DateTime? PaidDate { get; set; } |
| `backend/Controllers/AccountsController.cs` | 25 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 26 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 488 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 489 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 726 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 727 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 1023 | `DateTime today)` | DateTime today) |
| `backend/Controllers/AccountsController.cs` | 1651 | `DateTime accountDate,` | DateTime accountDate, |
| `backend/Controllers/AccountsController.cs` | 1669 | `DateTime? accountDate,` | DateTime? accountDate, |
| `backend/Controllers/ActsController.cs` | 57 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 58 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 410 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 411 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 1621 | `DateTime now)` | private static string AppendCancellationComment(string? comment, string reason, DateTime now) |
| `backend/Controllers/AppVersionController.cs` | 293 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/AppVersionController.cs` | 302 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/AppVersionController.cs` | 308 | `DateTime CreatedAt` | DateTime CreatedAt, |
| `backend/Controllers/AppVersionController.cs` | 309 | `DateTime UpdatedAt` | DateTime UpdatedAt); |
| `backend/Controllers/AppVersionController.cs` | 318 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/CalendarSyncController.cs` | 312 | `DateTime NormalizeFilterDate` | private static DateTime NormalizeFilterDate(DateTime value) |
| `backend/Controllers/CalendarSyncController.cs` | 557 | `DateTime CreatedAt` | public DateTime CreatedAt { get; set; } |
| `backend/Controllers/CalendarSyncController.cs` | 312 | `DateTime value)` | private static DateTime NormalizeFilterDate(DateTime value) |
| `backend/Controllers/CashOrdersController.cs` | 31 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 32 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 41 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 42 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 53 | `DateTime? date,` | [FromQuery] DateTime? date, |
| `backend/Controllers/CashOrdersController.cs` | 68 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 69 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 103 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 104 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 162 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 163 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 216 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 217 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 666 | `DateTime? from,` | private async Task<CashBookResponseDto> BuildCashBookAsync(DateTime? from, DateTime? to, string? accountName, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 666 | `DateTime? to,` | private async Task<CashBookResponseDto> BuildCashBookAsync(DateTime? from, DateTime? to, string? accountName, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 742 | `DateTime date,` | private Task<decimal> CalculateCashBalanceAsync(DateTime date, string accountName, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 751 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 752 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 819 | `DateTime date,` | private async Task<string> GenerateNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 837 | `DateTime date,` | private async Task<string> GenerateInventoryNumberAsync(DateTime date, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 1313 | `DateTime? from,` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/CashOrdersController.cs` | 1313 | `DateTime? to,` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/ClientsController.cs` | 1484 | `DateTime? PassportIssueDate` | public DateTime? PassportIssueDate { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1518 | `DateTime? IssueDate` | public DateTime? IssueDate { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1522 | `DateTime? ValidFrom` | public DateTime? ValidFrom { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1523 | `DateTime? ValidTo` | public DateTime? ValidTo { get; init; } |
| `backend/Controllers/ContractsController.cs` | 1397 | `DateTime? ValidUntil` | private async Task<(ActionResult? Error, string? Number, string? BillingMode, bool IsOpenEnded, decimal? Amount, DateTime? ValidUntil, List<(int ClientId, string? Comment)> Clients, List<(int LegalEntityId, string? Comme |
| `backend/Controllers/ContractsController.cs` | 91 | `DateTime? from,` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 91 | `DateTime? to,` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 700 | `DateTime date,` | public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 1195 | `DateTime now)` | private void ApplyContractCancellationSideEffects(Contract contract, string reason, DateTime now) |
| `backend/Controllers/ContractsController.cs` | 1519 | `DateTime date,` | private async Task<string> GenerateNextContractNumberAsync(DateTime date, int? excludeId, CancellationToken ct, ContractNumberingSettingsDto? settings = null) |
| `backend/Controllers/ContractsController.cs` | 1563 | `DateTime date,` | private static string FormatGeneratedContractNumber(int sequence, DateTime date, string template) |
| `backend/Controllers/ContractsController.cs` | 1580 | `DateTime date,` | private static bool TryParseGeneratedContractNumber(string? number, DateTime date, string template, out int sequence) |
| `backend/Controllers/ContractsController.cs` | 1630 | `DateTime nowUtc,` | private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 1674 | `DateTime date,` | private static string RenderContractTemplate(string template, DateTime date, string sequenceText) |
| `backend/Controllers/ContractsController.cs` | 1770 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/ContractsController.cs` | 1771 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3170 | `DateTime EffectivePlannedDate` | private static DateTime EffectivePlannedDate(Payment payment) |
| `backend/Controllers/InvoicesController.cs` | 3275 | `DateTime InvoiceDate` | DateTime InvoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 4624 | `DateTime ResolveInvoiceDueDate` | private static DateTime ResolveInvoiceDueDate(UpsertInvoiceRequest request) |
| `backend/Controllers/InvoicesController.cs` | 5857 | `DateTime ResolveLegacyInstallmentDate` | private static DateTime ResolveLegacyInstallmentDate(Payment payment) |
| `backend/Controllers/InvoicesController.cs` | 6317 | `DateTime StartDate` | public DateTime StartDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6331 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6338 | `DateTime Date` | private sealed record InstallmentScheduleItem(int Sequence, DateTime Date, decimal Amount, int Id); |
| `backend/Controllers/InvoicesController.cs` | 6345 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6346 | `DateTime? DueDate` | public DateTime? DueDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6352 | `DateTime? SentForSigningAt` | public DateTime? SentForSigningAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6353 | `DateTime? SignedAt` | public DateTime? SignedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6354 | `DateTime? CancelledAt` | public DateTime? CancelledAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6358 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6394 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6397 | `DateTime? AuditUpdatedAt` | public DateTime? AuditUpdatedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 6401 | `DateTime? AuditDeletedAt` | public DateTime? AuditDeletedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 63 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 64 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 185 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 186 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 855 | `DateTime now,` | private async Task CancelLinkedActsAsync(Payment invoicePayment, string reason, DateTime now, int? actorId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 879 | `DateTime now)` | private static string AppendActCancellationComment(string? comment, string invoiceNumber, string reason, DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 1277 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 1278 | `DateTime dueDate,` | DateTime dueDate, |
| `backend/Controllers/InvoicesController.cs` | 1557 | `DateTime? date,` | public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 2766 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 2767 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3021 | `DateTime? paidDate)` | private static void ApplyPaidFlags(Payment payment, DateTime? paidDate) |
| `backend/Controllers/InvoicesController.cs` | 3113 | `DateTime accountDate,` | DateTime accountDate, |
| `backend/Controllers/InvoicesController.cs` | 3703 | `DateTime now,` | private void ApplyInvoiceWorkflowStatus(Payment payment, string workflowStatus, DateTime now, string? reason) |
| `backend/Controllers/InvoicesController.cs` | 3747 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 3771 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 3772 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 4396 | `DateTime now)` | DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 4446 | `DateTime now)` | DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 4462 | `DateTime now)` | DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 4495 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 4588 | `DateTime date,` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5277 | `DateTime targetDate,` | private async Task<int> GetNextInvoiceNumberAsync(DateTime targetDate, int startNumber, string allocationStrategy, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5312 | `DateTime accountDate,` | private async Task<string?> ResolveInvoiceNumberForSaveAsync(string number, DateTime accountDate, int? excludeId, bool allowDuplicateOnImport, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5344 | `DateTime accountDate,` | private async Task<HashSet<string>> LoadReservedInvoiceNumberLookupAsync(DateTime accountDate, int? excludeId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5366 | `DateTime accountDate,` | private async Task<ActionResult?> EnsureInvoiceNumberIsUniqueAsync(string number, DateTime accountDate, int? excludeId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5406 | `DateTime nowUtc,` | private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5445 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 5492 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 5507 | `DateTime invoiceDate)` | private static string BuildReservedInvoiceNumberKey(string number, DateTime invoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 5512 | `DateTime invoiceDate)` | private static void AssignLegacyAccountSource(Payment payment, string number, DateTime invoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 5646 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 5702 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 5862 | `DateTime nowUtc,` | private async Task UpsertUserSettingAsync(int userId, string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/LegalEntitiesController.cs` | 2359 | `DateTimeOffset? LiquidationDate` | public DateTimeOffset? LiquidationDate { get; init; } |
| `backend/Controllers/LegalEntitiesController.cs` | 2371 | `DateTimeOffset? RegistrationDate` | public DateTimeOffset? RegistrationDate { get; init; } |
| `backend/Controllers/LegalEntitiesController.cs` | 52 | `DateTime? invoiceDate,` | DateTime? invoiceDate, |
| `backend/Controllers/MoneyOperationsController.cs` | 4647 | `DateTime? LastCheckedAt` | public DateTime? LastCheckedAt { get; set; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4720 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4721 | `DateTime? RecognitionDate` | public DateTime? RecognitionDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4733 | `DateTime? LinkedInvoiceDate` | public DateTime? LinkedInvoiceDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4738 | `DateTime? LinkedActDate` | public DateTime? LinkedActDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4740 | `DateTime? MatchedAt` | public DateTime? MatchedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4744 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4745 | `DateTime? UpdatedAt` | public DateTime? UpdatedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4750 | `DateTime? CashOrderDate` | public DateTime? CashOrderDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4785 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4808 | `DateTime Date` | DateTime Date, |
| `backend/Controllers/MoneyOperationsController.cs` | 194 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 195 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 234 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 235 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 407 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 408 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 457 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 458 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 504 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 505 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 551 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 552 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 630 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 631 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 736 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 737 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 758 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 759 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 802 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 803 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 900 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 901 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1005 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1006 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1342 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1343 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1451 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1452 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3161 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 3162 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3340 | `DateTime date)` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date) |
| `backend/Controllers/MoneyOperationsController.cs` | 4073 | `DateTime date,` | DateTime date, |
| `backend/Controllers/MoneyOperationsController.cs` | 4672 | `DateTime date)` | private static bool TryParseStatementDate(string value, out DateTime date) |
| `backend/Controllers/ObjectAuditController.cs` | 42 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 2020 | `DateTime RemainderDate` | DateTime RemainderDate); |
| `backend/Controllers/PaymentsController.cs` | 2915 | `DateTime Date` | private static List<(string Number, DateTime Date)> BuildLookupCandidates(Payment payment) |
| `backend/Controllers/PaymentsController.cs` | 2917 | `DateTime Date` | var candidates = new List<(string Number, DateTime Date)>(); |
| `backend/Controllers/PaymentsController.cs` | 54 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 55 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 592 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 593 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 680 | `DateTime today,` | DateTime today, |
| `backend/Controllers/PaymentsController.cs` | 1374 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 1414 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 1582 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 1653 | `DateTime refundDate,` | DateTime refundDate, |
| `backend/Controllers/PaymentsController.cs` | 1657 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 1715 | `DateTime now,` | private async Task SyncPaymentCashOrderAsync(Payment payment, DateTime now, CancellationToken ct) |
| `backend/Controllers/PaymentsController.cs` | 1942 | `DateTime date,` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/PaymentsController.cs` | 1975 | `DateTime now)` | private void AppendOverpaymentDecisionNote(Payment payment, string message, DateTime now) |
| `backend/Controllers/PaymentsController.cs` | 1985 | `DateTime suggestedDate)` | private static bool TryInferSuggestedRemainderDate(Payment payment, out DateTime suggestedDate) |
| `backend/Controllers/PaymentsController.cs` | 2119 | `DateTime remainderDate,` | DateTime remainderDate, |
| `backend/Controllers/PaymentsController.cs` | 2120 | `DateTime now)` | DateTime now) |
| `backend/Controllers/PaymentsController.cs` | 2276 | `DateTime remainderDate,` | DateTime remainderDate, |
| `backend/Controllers/PaymentsController.cs` | 2277 | `DateTime now,` | DateTime now, |
| `backend/Controllers/PaymentsController.cs` | 2293 | `DateTime remainderDate,` | DateTime remainderDate, |
| `backend/Controllers/PaymentsController.cs` | 2294 | `DateTime now)` | DateTime now) |
| `backend/Controllers/PaymentsController.cs` | 2666 | `DateTime lookupDate,` | private async Task<Payment?> ResolveInvoiceByPaymentHistoryAsync(Payment payment, string lookupNumber, DateTime lookupDate, CancellationToken ct) |
| `backend/Controllers/PaymentsController.cs` | 2919 | `DateTime? date)` | void Add(string? number, DateTime? date) |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime Start` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime End` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 3658 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt(ClientCase clientCase, params DateTime?[] activitySignals) |
| `backend/Controllers/ReportsController.cs` | 6011 | `DateTime DueAt` | DateTime DueAt, |
| `backend/Controllers/ReportsController.cs` | 6057 | `DateTime LastActivityAt` | DateTime LastActivityAt); |
| `backend/Controllers/ReportsController.cs` | 6061 | `DateTime LastActivityAt` | DateTime LastActivityAt); |
| `backend/Controllers/ReportsController.cs` | 6072 | `DateTime DueAt` | DateTime DueAt); |
| `backend/Controllers/ReportsController.cs` | 6077 | `DateTime DueAt` | DateTime DueAt); |
| `backend/Controllers/ReportsController.cs` | 6095 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/ReportsController.cs` | 6104 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/ReportsController.cs` | 106 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 107 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 139 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 140 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 157 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 158 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 179 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 180 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 204 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 205 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 206 | `DateTime? asOf,` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 249 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 250 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 251 | `DateTime? asOf,` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 296 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 297 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 330 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 331 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 362 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 363 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 394 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 395 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 396 | `DateTime? asOf,` | DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime? from,` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime? to)` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 578 | `DateTime? asOf,` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 620 | `DateTime? asOf,` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 657 | `DateTime? asOf,` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 696 | `DateTime? asOf,` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 733 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 734 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 765 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 766 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 786 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 787 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 807 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 808 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 828 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 829 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 849 | `DateTime? from,` | [FromQuery] DateTime? from, |
| Еще 1193 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Backend: текущее время сервера

Вызовы Now/UtcNow, которые надо привести к единому источнику времени.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 578 | `DateTime.UtcNow` | var today = DateTime.UtcNow.Date; |
| `backend/Controllers/ActsController.cs` | 250 | `DateTime.UtcNow` | act.SentForSigningAt ??= DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 251 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 265 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 288 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 326 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 395 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 610 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 611 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 690 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 886 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1385 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/AppVersionController.cs` | 100 | `DateTime.UtcNow` | SeenAt = DateTime.UtcNow, |
| `backend/Controllers/AppVersionController.cs` | 137 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/AppVersionController.cs` | 188 | `DateTime.UtcNow` | release.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/BanksController.cs` | 84 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/BanksController.cs` | 121 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CalendarSyncController.cs` | 90 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/CalendarSyncController.cs` | 92 | `DateTime.UtcNow` | normalized.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CalendarSyncController.cs` | 500 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 300 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 315 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 385 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 432 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 459 | `DateTime.UtcNow` | model.CancelledAt = model.Status == CashOrderStatus.Cancelled ? model.CancelledAt ?? DateTime.UtcNow : null; |
| `backend/Controllers/CashOrdersController.cs` | 461 | `DateTime.UtcNow` | model.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 502 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 551 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 570 | `DateTime.UtcNow` | order.CancelledAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 572 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 606 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientBalancesController.cs` | 82 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/ClientPassportController.cs` | 66 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ClientsController.cs` | 910 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 911 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1002 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1003 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1063 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1077 | `DateTime.UtcNow` | current.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 223 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 306 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 427 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 806 | `DateTime.UtcNow` | contact.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 310 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 422 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 436 | `DateTime.UtcNow` | contract.SentForSigningAt ??= DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 437 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 459 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 479 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 522 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 619 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 652 | `DateTime.UtcNow` | contract.CancelledAt ??= DateTime.UtcNow.Date; |
| `backend/Controllers/ContractsController.cs` | 654 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 655 | `DateTime.UtcNow` | ApplyContractCancellationSideEffects(contract, reason, DateTime.UtcNow); |
| `backend/Controllers/ContractsController.cs` | 675 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 692 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 748 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 805 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 426 | `DateTime.UtcNow` | m.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 437 | `DateTime.UtcNow` | user.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 470 | `DateTime.UtcNow` | user.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 1183 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/DictionariesAdminController.cs` | 1213 | `DateTime.UtcNow` | "countries" => new CountryDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1214 | `DateTime.UtcNow` | "base-currencies" => new BaseCurrencyDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1215 | `DateTime.UtcNow` | "service-languages" => new ServiceLanguageDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1216 | `DateTime.UtcNow` | "organization-forms" => new OrganizationFormDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1217 | `DateTime.UtcNow` | "timezones" => new TimezoneDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1218 | `DateTime.UtcNow` | "tax-systems" => new TaxSystemDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1219 | `DateTime.UtcNow` | "tax-system-modes" => new TaxSystemModeDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1220 | `DateTime.UtcNow` | "invoice-units" => new InvoiceUnitDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/DictionariesAdminController.cs` | 1221 | `DateTime.UtcNow` | "invoice-item-names" => new InvoiceItemNameDictionaryItem { Name = name, Description = description, ColorHex = colorHex, IsActive = isActive, CreatedAt = DateTime.UtcNow }, |
| `backend/Controllers/FormRequirementsController.cs` | 314 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/GoogleCalendarController.cs` | 132 | `DateTime.UtcNow` | Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((ex.AvailableAt - DateTime.UtcNow).TotalSeconds)).ToString(); |
| `backend/Controllers/InvoicesController.cs` | 214 | `DateTime.UtcNow` | var today = DateTime.UtcNow.Date; |
| `backend/Controllers/InvoicesController.cs` | 414 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 492 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 509 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 525 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 574 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 665 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 964 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 996 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 999 | `DateTime.UtcNow` | ApplyInvoiceWorkflowStatus(payment, workflowStatus, DateTime.UtcNow, reason: null); |
| `backend/Controllers/InvoicesController.cs` | 1165 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1445 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 1543 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1565 | `DateTime.UtcNow` | var targetDate = (date ?? DateTime.UtcNow).Date; |
| `backend/Controllers/InvoicesController.cs` | 1603 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1639 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1785 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 2286 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2339 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2549 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 3038 | `DateTime.UtcNow` | payment.Status = payment.Date.Date < DateTime.UtcNow.Date |
| `backend/Controllers/InvoicesController.cs` | 3925 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 4228 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 4684 | `DateTime.UtcNow` | PaymentDomainService.ApplyStatusRules(payment, DateTime.UtcNow); |
| `backend/Controllers/InvoicesController.cs` | 5166 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 5187 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 5615 | `DateTime.UtcNow` | InvoicePaymentStateService.CalculateRepresentativeState(representative, resultingInstallments, DateTime.UtcNow)); |
| `backend/Controllers/InvoicesController.cs` | 5817 | `DateTime.UtcNow` | ? DateTime.UtcNow.Date |
| `backend/Controllers/LegalEntitiesController.cs` | 177 | `DateTime.UtcNow` | var nowUtc = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 530 | `DateTime.UtcNow` | SetProperty(created, "CreatedAt", DateTime.UtcNow); |
| `backend/Controllers/LegalEntitiesController.cs` | 1041 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 1042 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 1945 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 2114 | `DateTime.UtcNow` | existingAddress.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 2162 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 2163 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyAccountsController.cs` | 79 | `DateTime.UtcNow` | normalized.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 123 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 151 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 178 | `DateTime.UtcNow` | item.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyAccountsController.cs` | 201 | `DateTime.UtcNow` | fallback.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 452 | `DateTime.UtcNow` | return File(bytes, "text/csv; charset=utf-8", $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"); |
| `backend/Controllers/MoneyOperationsController.cs` | 499 | `DateTime.UtcNow` | $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xls"); |
| `backend/Controllers/MoneyOperationsController.cs` | 593 | `DateTime.UtcNow` | return File(pdfBytes, "application/pdf", $"money-operations-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf"); |
| `backend/Controllers/MoneyOperationsController.cs` | 832 | `DateTime.UtcNow` | var checkedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1882 | `DateTime.UtcNow` | operation.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1916 | `DateTime.UtcNow` | operation.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1920 | `DateTime.UtcNow` | operation.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1977 | `DateTime.UtcNow` | model.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 1984 | `DateTime.UtcNow` | model.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2033 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2243 | `DateTime.UtcNow` | var checkedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2460 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2470 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2479 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2489 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2497 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2502 | `DateTime.UtcNow` | batch.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2565 | `DateTime.UtcNow` | $"{batch.FileHash}:reprocess:{batch.Id}:{DateTime.UtcNow:O}", |
| `backend/Controllers/MoneyOperationsController.cs` | 2673 | `DateTime.UtcNow` | entity.MatchedAt = entity.MatchedAt ?? DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2690 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2727 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2750 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2764 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2783 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 2795 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3399 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 3548 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 3566 | `DateTime.UtcNow` | operation.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3653 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3805 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 4515 | `DateTime.UtcNow` | var note = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — зачтено {movementLabel} из раздела \"Деньги\" ({sourceLabel}) на сумму {appliedAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}."; |
| `backend/Controllers/MoneyOperationsController.cs` | 4539 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 4553 | `DateTime.UtcNow` | var overpaymentNote = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — переплата {overpaymentAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} зачислена на баланс клиента."; |
| `backend/Controllers/MoneyOperationsController.cs` | 4557 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, payment, DateTime.UtcNow, ct); |
| `backend/Controllers/NotificationInboxController.cs` | 197 | `DateTime.UtcNow` | item.ArchivedAt = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 213 | `DateTime.UtcNow` | item.DeletedAt ??= DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 441 | `DateTime.UtcNow` | item.ReadAt = DateTime.UtcNow; |
| `backend/Controllers/NotificationsController.cs` | 188 | `DateTime.UtcNow` | var nowUtc = DateTime.UtcNow; |
| `backend/Controllers/NotificationsController.cs` | 544 | `DateTime.UtcNow` | user.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 115 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 180 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 348 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 445 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 607 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 803 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 1038 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 1098 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PaymentsController.cs` | 3280 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, invoiceId.Value, DateTime.UtcNow, ct); |
| `backend/Controllers/PowerOfAttorneyController.cs` | 64 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ProfileController.cs` | 208 | `DateTime.UtcNow` | var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}"; |
| `backend/Controllers/ReportsController.cs` | 170 | `DateTime.UtcNow` | var fileName = $"legal-work-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.xls"; |
| `backend/Controllers/ReportsController.cs` | 197 | `DateTime.UtcNow` | var pdf = await _pdfService.RenderAsync(BuildLegalWorkPrintHtml(report!, DateTime.UtcNow), ct); |
| `backend/Controllers/ReportsController.cs` | 198 | `DateTime.UtcNow` | return File(pdf, "application/pdf", $"legal-work-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf"); |
| `backend/Controllers/ReportsController.cs` | 243 | `DateTime.UtcNow` | $"{document!.Slug}-{DateTime.UtcNow:yyyyMMdd-HHmm}.xls"); |
| `backend/Controllers/ReportsController.cs` | 290 | `DateTime.UtcNow` | var pdf = await _pdfService.RenderAsync(BuildReportExportPrintHtml(document!, DateTime.UtcNow), ct); |
| `backend/Controllers/ReportsController.cs` | 291 | `DateTime.UtcNow` | return File(pdf, "application/pdf", $"{document!.Slug}-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf"); |
| `backend/Controllers/ReportsController.cs` | 415 | `DateTime.UtcNow` | var effectiveAsOf = asOf?.Date ?? DateTime.UtcNow.Date; |
| `backend/Controllers/ReportsController.cs` | 597 | `DateTime.UtcNow` | var effectiveAsOf = asOf?.Date ?? DateTime.UtcNow.Date; |
| `backend/Controllers/ReportsController.cs` | 637 | `DateTime.UtcNow` | var effectiveAsOf = asOf?.Date ?? DateTime.UtcNow.Date; |
| `backend/Controllers/ReportsController.cs` | 675 | `DateTime.UtcNow` | var effectiveAsOf = asOf?.Date ?? DateTime.UtcNow.Date; |
| `backend/Controllers/ReportsController.cs` | 713 | `DateTime.UtcNow` | var effectiveAsOf = (asOf?.Date ?? DateTime.UtcNow.Date); |
| `backend/Controllers/ReportsController.cs` | 1249 | `DateTime.UtcNow` | var today = DateTime.UtcNow.Date; |
| `backend/Controllers/ReportsController.cs` | 1497 | `DateTime.UtcNow` | GeneratedAt = DateTime.UtcNow, |
| `backend/Controllers/ReportsController.cs` | 1567 | `DateTime.UtcNow` | var asOf = DateTime.UtcNow.Date; |
| `backend/Controllers/ReportsController.cs` | 1608 | `DateTime.UtcNow` | GeneratedAt = DateTime.UtcNow, |
| `backend/Controllers/ReportsController.cs` | 2625 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ReportsController.cs` | 2819 | `DateTime.UtcNow` | GeneratedAt = DateTime.UtcNow, |
| `backend/Controllers/ReportsController.cs` | 2972 | `DateTime.UtcNow` | GeneratedAt = DateTime.UtcNow, |
| `backend/Controllers/ReportsController.cs` | 3117 | `DateTime.UtcNow` | GeneratedAt = DateTime.UtcNow, |
| `backend/Controllers/ReportsController.cs` | 3311 | `DateTime.UtcNow` | GeneratedAt = DateTime.UtcNow, |
| `backend/Controllers/RolePermissionsController.cs` | 52 | `DateTime.UtcNow` | GeneratedAtUtc = DateTime.UtcNow, |
| `backend/Controllers/RolePermissionsController.cs` | 84 | `DateTime.UtcNow` | var fileName = $"role-permissions-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"; |
| `backend/Controllers/RolePermissionsController.cs` | 538 | `DateTime.UtcNow` | role.PermissionsUpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/RolesController.cs` | 115 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/RolesController.cs` | 172 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/S3StorageController.cs` | 335 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/SystemJobsController.cs` | 89 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 166 | `DateTime.UtcNow` | var startedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 273 | `DateTime.UtcNow` | var targetDate = DateTime.UtcNow.Date.AddDays(-1); |
| `backend/Controllers/SystemJobsController.cs` | 303 | `DateTime.UtcNow` | var finishedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 557 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddDays(-retentionDays); |
| `backend/Controllers/SystemJobsController.cs` | 899 | `DateTime.UtcNow` | SystemJobKeys.TaskReminders => await BuildTaskReminderJobAsync(DateTime.UtcNow, ct), |
| `backend/Controllers/SystemJobsController.cs` | 904 | `DateTime.UtcNow` | SystemJobKeys.CaseInactivityControl => await BuildCaseControlJobAsync(settings, DateTime.UtcNow, ct), |
| `backend/Controllers/SystemJobsController.cs` | 922 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes); |
| `backend/Controllers/SystemJobsController.cs` | 943 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes); |
| `backend/Controllers/TasksController.cs` | 179 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 294 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 337 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 552 | `DateTime.UtcNow` | var html = BuildTaskPlanningReportHtml(tasks, BuildTaskPlanningReportFilterText(status, type, priority, from, to), DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 553 | `DateTime.UtcNow` | var fileStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture); |
| `backend/Controllers/TasksController.cs` | 604 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 770 | `DateTime.UtcNow` | var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - lastActivity).TotalDays)); |
| `backend/Controllers/TasksController.cs` | 813 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 814 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 817 | `DateTime.UtcNow` | AddEscalationObserver(entity, escalationUser, assignee.UserId, DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 853 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddDays(-settings.CaseInactivityDays); |
| `backend/Controllers/TasksController.cs` | 1028 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1095 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1230 | `DateTime.UtcNow` | UploadedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 1267 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1372 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1457 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 1458 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 1562 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1662 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 1797 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2051 | `DateTime.UtcNow` | participant.RespondedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2052 | `DateTime.UtcNow` | participant.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 2067 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3113 | `DateTime.UtcNow` | && GetApprovalDueAt(entity) < DateTime.UtcNow) |
| `backend/Controllers/TasksController.cs` | 3275 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3403 | `DateTime.UtcNow` | ? changedAt?.Date ?? DateTime.UtcNow.Date |
| `backend/Controllers/TasksController.cs` | 3485 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3639 | `DateTime.UtcNow` | var localNow = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone); |
| `backend/Controllers/TasksController.cs` | 3655 | `DateTime.UtcNow` | var daysWithoutActiveTask = Math.Max(0, (int)Math.Floor((DateTime.UtcNow - lastActivity).TotalDays)); |
| `backend/Controllers/TasksController.cs` | 3679 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3680 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3684 | `DateTime.UtcNow` | AddEscalationObserver(task, isEscalated ? escalationUser : null, assignee.UserId, DateTime.UtcNow); |
| `backend/Controllers/TasksController.cs` | 3741 | `DateTime.UtcNow` | : DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3742 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3743 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3796 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/TasksController.cs` | 3927 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/TasksController.cs` | 3928 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/UserActivityController.cs` | 134 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/UserActivityController.cs` | 326 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/UserSettingsController.cs` | 59 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/V1/PaymentsV1Controller.cs` | 65 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, model, DateTime.UtcNow, ct); |
| `backend/Controllers/V1/PaymentsV1Controller.cs` | 106 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, e, DateTime.UtcNow, ct); |
| `backend/Controllers/V1/PaymentsV1Controller.cs` | 111 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, DateTime.UtcNow, ct); |
| `backend/Controllers/V1/PaymentsV1Controller.cs` | 128 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, invoiceId.Value, DateTime.UtcNow, ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 728 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1001 | `DateTime.UtcNow` | var eventKey = $"case:{clientCase.Id}:document:{document.Id}:{action}:{DateTime.UtcNow.Ticks}"; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1880 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1932 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1989 | `DateTime.UtcNow` | item.UpdatedAt = DateTime.UtcNow; |
| Еще 1248 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Backend: преобразования timezone

Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 41 | `timezone` | "releaseId": "2026-06-03-calendar-utc-timezone-rule", |
| `backend/AppReleases/releases.json` | 526 | `timezone` | "releaseId": "2026-06-02-app-release-seed-timezone-fix", |
| `backend/Controllers/CalendarSyncController.cs` | 313 | `.ToUniversalTime()` | => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); |
| `backend/Controllers/CalendarSyncController.cs` | 313 | `DateTime.SpecifyKind` | => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); |
| `backend/Controllers/ClientsController.cs` | 668 | `timezone` | timezone = result.Timezone, |
| `backend/Controllers/ClientsController.cs` | 668 | `Timezone` | timezone = result.Timezone, |
| `backend/Controllers/DatabaseAdminController.cs` | 366 | `TimeZone` | TimeZone = overview.TimeZone, |
| `backend/Controllers/DictionariesAdminController.cs` | 223 | `Timezone` | (le, v) => le.Timezone = v, |
| `backend/Controllers/DictionariesAdminController.cs` | 224 | `Timezone` | le => le.Timezone, |
| `backend/Controllers/DictionariesAdminController.cs` | 765 | `Timezone` | if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Timezone == e.Name)) |
| `backend/Controllers/FormRequirementsController.cs` | 106 | `Timezone` | new("Timezone", "Часовой пояс", "Реквизиты: Основные"), |
| `backend/Controllers/LegalEntitiesController.cs` | 348 | `Timezone` | Timezone = entity.Timezone, |
| `backend/Controllers/LegalEntitiesController.cs` | 496 | `Timezone` | entity.Timezone = await ResolveDictionaryValueAsync(_db.Timezones, entity.Timezone, "Добавлено автоматически", "#8B5CF6", ct); |
| `backend/Controllers/LegalEntitiesController.cs` | 1000 | `Timezone` | Timezone = NormalizeOptional(request.Timezone), |
| `backend/Controllers/LegalEntitiesController.cs` | 1904 | `Timezone` | entity.Timezone = NormalizeOptional(request.Timezone); |
| `backend/Controllers/LegalEntitiesController.cs` | 2088 | `Timezone` | Timezone: null); |
| `backend/Controllers/ReportsController.cs` | 2116 | `.ToLocalTime()` | .Append(Html(generatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")))) |
| `backend/Controllers/ReportsController.cs` | 2412 | `.ToLocalTime()` | .Append(Html(generatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU")))) |
| `backend/Controllers/TasksController.cs` | 2572 | `DateTime.SpecifyKind` | var utcValue = DateTime.SpecifyKind(value, DateTimeKind.Utc); |
| `backend/Controllers/TasksController.cs` | 3132 | `DateTime.SpecifyKind` | => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O"); |
| `backend/Controllers/TasksController.cs` | 4380 | `DateTime.SpecifyKind` | => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("dd.MM.yyyy HH:mm"); |
| `backend/Controllers/TasksController.cs` | 3487 | `TimeZoneInfo.` | var calendarTimeZone = timeZone ?? TimeZoneInfo.Local; |
| `backend/Controllers/TasksController.cs` | 475 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/TasksController.cs` | 3147 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.NormalizeUtcInstant(value); |
| `backend/Controllers/TasksController.cs` | 3498 | `CalendarTimeZoneHelper` | var start = CalendarTimeZoneHelper.ToExternalUtc(task.DueAt, calendarTimeZone); |
| `backend/Controllers/TasksController.cs` | 3538 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, timeZone); |
| `backend/Controllers/TasksController.cs` | 3638 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, userId, ct); |
| `backend/Controllers/TasksController.cs` | 3639 | `CalendarTimeZoneHelper` | var localNow = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone); |
| `backend/Controllers/TasksController.cs` | 3641 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.FromUserLocalToUtc(localDueAt, timeZone); |
| `backend/Controllers/TasksController.cs` | 475 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/TasksController.cs` | 476 | `timeZone` | var calendar = BuildIcsCalendar(tasks, reminders, timeZone); |
| `backend/Controllers/TasksController.cs` | 3483 | `timeZone` | private static string BuildIcsCalendar(IEnumerable<TaskJournalItem> tasks, IReadOnlyList<int>? reminderMinutes = null, TimeZoneInfo? timeZone = null) |
| `backend/Controllers/TasksController.cs` | 3487 | `timeZone` | var calendarTimeZone = timeZone ?? TimeZoneInfo.Local; |
| `backend/Controllers/TasksController.cs` | 3537 | `timeZone` | private static string FormatIcsDate(DateTime value, TimeZoneInfo timeZone) |
| `backend/Controllers/TasksController.cs` | 3538 | `timeZone` | => CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, timeZone); |
| `backend/Controllers/TasksController.cs` | 3638 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, userId, ct); |
| `backend/Controllers/TasksController.cs` | 3639 | `timeZone` | var localNow = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone); |
| `backend/Controllers/TasksController.cs` | 3641 | `timeZone` | return CalendarTimeZoneHelper.FromUserLocalToUtc(localDueAt, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 9638 | `.ToUniversalTime()` | : value.ToUniversalTime(); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4391 | `DateTime.SpecifyKind` | var start = DateTime.SpecifyKind(item.StartAt.Date, DateTimeKind.Utc); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4392 | `DateTime.SpecifyKind` | var end = DateTime.SpecifyKind(item.EndAt.Date, DateTimeKind.Utc); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 7527 | `DateTime.SpecifyKind` | .Select(static item => DateTime.SpecifyKind(DateTime.ParseExact(item, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc)) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 9637 | `DateTime.SpecifyKind` | ? DateTime.SpecifyKind(value, DateTimeKind.Utc) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4374 | `TimeZoneInfo.` | var calendarTimeZone = timeZone ?? TimeZoneInfo.Local; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1722 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1753 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1790 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4193 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4194 | `CalendarTimeZoneHelper` | var nowLocal = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4212 | `CalendarTimeZoneHelper` | .Where(item => item.UserId == userId.Value && item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4216 | `CalendarTimeZoneHelper` | var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4217 | `CalendarTimeZoneHelper` | var nowLocal = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4226 | `CalendarTimeZoneHelper` | var localToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4235 | `CalendarTimeZoneHelper` | CalendarTimeZoneHelper.FromUserLocalToUtc(fromDate, timeZone), |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4236 | `CalendarTimeZoneHelper` | CalendarTimeZoneHelper.FromUserLocalToUtc(toDateExclusive, timeZone), |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4455 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1722 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1723 | `timeZone` | var range = ResolveCaseCalendarDateRange(from, to, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1753 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1754 | `timeZone` | var range = ResolveCaseCalendarDateRange(from, to, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1790 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1791 | `timeZone` | var range = ResolveCaseCalendarDateRange(from, to, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1816 | `timeZone` | Encoding.UTF8.GetBytes(BuildCaseCalendarIcs(events, reminders, timeZone)), |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4193 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4194 | `timeZone` | var nowLocal = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4216 | `timeZone` | var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4217 | `timeZone` | var nowLocal = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4223 | `timeZone` | private static CaseCalendarDateRange ResolveCaseCalendarDateRange(DateTime? from, DateTime? to, TimeZoneInfo timeZone) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4226 | `timeZone` | var localToday = CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone).Date; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4235 | `timeZone` | CalendarTimeZoneHelper.FromUserLocalToUtc(fromDate, timeZone), |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4236 | `timeZone` | CalendarTimeZoneHelper.FromUserLocalToUtc(toDateExclusive, timeZone), |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4370 | `timeZone` | private static string BuildCaseCalendarIcs(IEnumerable<CaseCalendarEventDto> events, IReadOnlyList<int>? reminderMinutes = null, TimeZoneInfo? timeZone = null) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4374 | `timeZone` | var calendarTimeZone = timeZone ?? TimeZoneInfo.Local; |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4454 | `timeZone` | private static string FormatIcsDateTime(DateTime value, TimeZoneInfo timeZone) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 4455 | `timeZone` | => CalendarTimeZoneHelper.FormatUtcIcsDateTime(value, timeZone); |
| `backend/Data/PaymentContext.cs` | 126 | `DateTime.SpecifyKind` | : DateTime.SpecifyKind(value, DateTimeKind.Unspecified); |
| `backend/Data/PaymentContext.cs` | 493 | `Timezone` | entity.Property(e => e.Timezone).HasMaxLength(100); |
| `backend/Migrations/20260216022940_AddCompanyProfileFieldsToLegalEntity.cs` | 49 | `Timezone` | name: "Timezone", |
| `backend/Migrations/20260216022940_AddCompanyProfileFieldsToLegalEntity.cs` | 80 | `Timezone` | name: "Timezone", |
| `backend/Migrations/20260216022940_AddCompanyProfileFieldsToLegalEntity.Designer.cs` | 482 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260216083951_AddContractRelationsAndClientContractComment.Designer.cs` | 531 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260216085649_AddContractStartAndTerminationDates.Designer.cs` | 537 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260218075427_AddAddressRecordsAndClientReferenceEntities.Designer.cs` | 684 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260218092520_NormalizeClientPassportsToOneToMany.Designer.cs` | 706 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260219092616_AddServicesDictionaryForInvoices.Designer.cs` | 706 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260224082152_AddInvoiceResponsibles.Designer.cs` | 706 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260303090640_AddCanonicalContractAndInvoiceLinks.Designer.cs` | 712 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260305092835_AddLegalEntityCompanyProfileAssetsAndListsAndProperties.Designer.cs` | 913 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260305111713_AddFormFieldRequirements.Designer.cs` | 951 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260316045601_AddInvoicesEntityAndPaymentLink.Designer.cs` | 987 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260317024837_AddInvoiceLineDictionaries.Designer.cs` | 1078 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260317044425_AddClientResponsibleUser.Designer.cs` | 1084 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260317054247_AddInvoiceClientCases.Designer.cs` | 1100 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260317074504_AddInvoiceClientCaseReference.Designer.cs` | 1100 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260318110641_AddInvoiceRulesAndLegacyAccountSources.Designer.cs` | 1100 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260401170027_AddExternalAuthSupportSqlite.Designer.cs` | 1161 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260403042518_AddRoleManagementAuditAndPermissions.Designer.cs` | 1161 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260406074612_AddExternalAuthLinkingAndProfileFlows.Designer.cs` | 1168 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260407103333_AddContactsDirectory.Designer.cs` | 1247 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260408045520_AddExpenseClientAttribution.Designer.cs` | 1247 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260408071526_AddInvoiceLineItems.Designer.cs` | 1294 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260408110938_AddObjectAuditMetadata.Designer.cs` | 1664 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260408155354_AddObjectAuditEvents.Designer.cs` | 1664 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260409011513_AddUserAndRoleAuditMetadata.Designer.cs` | 1664 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260410004946_ExpandInvoiceLineItemNameLength.Designer.cs` | 1664 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260410121539_AddAreaAndSettlementToAddressRecords.Designer.cs` | 1672 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260413060351_AddUserCompanyAccesses.Designer.cs` | 1672 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260413081214_AddBanksDirectory.Designer.cs` | 1720 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260414095555_AddServiceCategoriesAndServiceFields.Designer.cs` | 1720 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260414160832_AddContractBillingModeAndOpenEndedFlag.Designer.cs` | 1732 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260414185430_AddActLineItemsAndCreationModes.Designer.cs` | 1800 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260414194626_AddInvoicePrintOptions.Designer.cs` | 1800 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260415154737_AddInvoiceLineItemServiceReference.Designer.cs` | 1806 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260417052602_AddSpreadsheetImportBatches.Designer.cs` | 1806 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260424034024_AddPaymentDocumentMetadata.Designer.cs` | 1806 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260424082329_AddClientStatusSystemKey.Designer.cs` | 1813 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260428074215_AddClientBalanceTransactions.Designer.cs` | 1876 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260428193709_AddCashOrders.Designer.cs` | 2009 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260429100403_AddCashOrderDocumentFields.Designer.cs` | 2024 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260429104900_AddMoneyOperationInvoiceMatching.Designer.cs` | 2024 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260429112034_AddBankStatementImportBatches.Designer.cs` | 2166 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260430032712_AddMoneyOperationAccountingTypeIndex.Designer.cs` | 2166 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260430045437_AddMoneyAccounts.Designer.cs` | 2166 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260430073757_BackfillMoneyAccountsFromOperations.Designer.cs` | 2166 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260430102400_LinkMoneyOperationsToIncomeTypes.Designer.cs` | 2166 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260502114208_AddDocumentWorkflowStatuses.Designer.cs` | 2205 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260502132119_AddDocumentSigningWorkflow.Designer.cs` | 2217 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260502133353_AddInvoiceCancellationFields.Designer.cs` | 2217 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260502140322_AddActCancellationFields.Designer.cs` | 2227 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260502195416_AddMoneyBalanceReconciliationResults.Designer.cs` | 2227 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260504025059_SyncSqliteModelSnapshot.Designer.cs` | 2326 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260504110401_AddTaskJournal.Designer.cs` | 2326 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260504190534_AddLegalEntityDocumentOwners.Designer.cs` | 2344 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260505033313_AddLegalEntityBalanceTransactions.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260505080330_AddLegalEntityContracts.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260505204500_AddNotificationDeliveries.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260505211044_AddNotificationSenders.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260506035335_AddNotificationBotSubscriptions.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260507105933_AddPaymentRemainderTransfers.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260508074146_AddTaskClientLinks.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260508101150_AddTaskJournalParticipants.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260508143232_AddTaskJournalComments.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260508145720_AddTaskJournalFiles.Designer.cs` | 2353 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260512055031_AddCounterpartyDualRole.Designer.cs` | 2369 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260512071704_AddTaskLegalEntityLinks.Designer.cs` | 2369 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260512180049_AddFinancialActionReasonSetting.Designer.cs` | 2369 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260512185946_AddDocumentStatusDictionary.Designer.cs` | 2431 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260513184545_AddDepartmentDictionary.Designer.cs` | 2478 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260515151144_ExpandServiceDictionaryNameLength.Designer.cs` | 2478 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260515172858_ExpandServiceDictionaryTextFields.Designer.cs` | 2478 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260515185151_AddUserNotifications.Designer.cs` | 2478 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260517173021_AddClientCaseRegistryFields.Designer.cs` | 2549 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260517190821_AddClientCaseDetailFields.Designer.cs` | 2623 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260517194648_AddClientCaseDocuments.Designer.cs` | 2704 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260517231152_AddClientCaseActivityFields.Designer.cs` | 2737 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260518002320_AddClientCaseRelatedParties.Designer.cs` | 2821 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260518012055_AddClientCaseProceduralDeadlines.Designer.cs` | 2833 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260519062422_AddTaskApprovalDueAt.Designer.cs` | 2833 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260519122501_AddClientCaseHiddenAccessFields.Designer.cs` | 2871 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260520071250_AddClientCaseWorkOrders.Designer.cs` | 2996 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260520083111_AddClientCaseWorkOrderApproval.Designer.cs` | 3029 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260520085851_AddClientCaseRelatedPartyArchive.Designer.cs` | 3051 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260524093232_AddClientCaseCourtInstances.Designer.cs` | 3924 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260525074120_AddTaskContractorPaymentResultDelay.Designer.cs` | 3924 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260525092333_AddUserPresenceFields.Designer.cs` | 3924 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260529064341_ExpandActCommentLength.Designer.cs` | 3923 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260601144520_AddGoogleCalendarSync.Designer.cs` | 4287 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/20260602041228_AddYandexCalendarSync.Designer.cs` | 4287 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PaymentContextModelSnapshot.cs` | 4287 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260401081905_InitialPostgreSqlBaseline.cs` | 533 | `Timezone` | Timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true), |
| `backend/Migrations/PostgreSql/20260401081905_InitialPostgreSqlBaseline.Designer.cs` | 1136 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260401165131_AddExternalAuthSupport.Designer.cs` | 1200 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260403042518_AddRoleManagementAuditAndPermissions.Designer.cs` | 1200 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260406074652_AddExternalAuthLinkingAndProfileFlows.Designer.cs` | 1207 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260407103411_AddContactsDirectory.Designer.cs` | 1288 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260408045602_AddExpenseClientAttribution.Designer.cs` | 1288 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260408071611_AddInvoiceLineItems.Designer.cs` | 1337 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260408111047_AddObjectAuditMetadata.Designer.cs` | 1707 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260408155452_AddObjectAuditEvents.Designer.cs` | 1707 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260409011619_AddUserAndRoleAuditMetadata.Designer.cs` | 1707 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260410004943_ExpandInvoiceLineItemNameLength.Designer.cs` | 1707 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260410121435_AddAreaAndSettlementToAddressRecords.Designer.cs` | 1715 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260413060351_AddUserCompanyAccesses.Designer.cs` | 1715 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260413081212_AddBanksDirectory.Designer.cs` | 1765 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260414103148_AddServiceCategoriesAndServiceFields.Designer.cs` | 1765 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260414160832_AddContractBillingModeAndOpenEndedFlag.Designer.cs` | 1777 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260414185429_AddActLineItemsAndCreationModes.Designer.cs` | 1847 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260414194537_AddInvoicePrintOptions.Designer.cs` | 1847 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260415154738_AddInvoiceLineItemServiceReference.Designer.cs` | 1853 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260417052543_AddSpreadsheetImportBatches.Designer.cs` | 1853 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260424034051_AddPaymentDocumentMetadata.Designer.cs` | 1853 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260424082356_AddClientStatusSystemKey.Designer.cs` | 1860 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260428075947_AddClientBalanceTransactions.Designer.cs` | 1925 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260428193743_AddCashOrders.Designer.cs` | 2060 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260429102114_AddCashOrderDocumentFields.Designer.cs` | 2075 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260429104933_AddMoneyOperationInvoiceMatching.Designer.cs` | 2075 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260429112034_AddBankStatementImportBatches.Designer.cs` | 2221 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260430032746_AddMoneyOperationAccountingTypeIndex.Designer.cs` | 2221 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260430045509_AddMoneyAccounts.Designer.cs` | 2221 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260430073824_BackfillMoneyAccountsFromOperations.Designer.cs` | 2221 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260430102735_LinkMoneyOperationsToIncomeTypesPostgres.Designer.cs` | 2221 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260502114300_AddDocumentWorkflowStatusesPostgres.Designer.cs` | 2260 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260502132156_AddDocumentSigningWorkflowPostgres.Designer.cs` | 2272 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260502133417_AddInvoiceCancellationFieldsPostgres.Designer.cs` | 2272 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260502140343_AddActCancellationFieldsPostgres.Designer.cs` | 2282 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260502195448_AddMoneyBalanceReconciliationResultsPostgres.Designer.cs` | 2282 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260504024859_SyncPostgreSqlModelSnapshot.Designer.cs` | 2383 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260504094814_RepairPostgreSqlMoneySchema.Designer.cs` | 2383 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260504110423_AddTaskJournalPostgres.Designer.cs` | 2383 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260504190603_AddLegalEntityDocumentOwnersPostgres.Designer.cs` | 2401 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260505033827_AddLegalEntityBalanceTransactionsPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260505080331_AddLegalEntityContractsPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260505204535_AddNotificationDeliveriesPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260505211117_AddNotificationSendersPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260506035403_AddNotificationBotSubscriptionsPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260507110101_AddPaymentRemainderTransfersPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260508074227_AddTaskClientLinksPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260508101215_AddTaskJournalParticipantsPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260508143247_AddTaskJournalCommentsPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260508145720_AddTaskJournalFilesPostgres.Designer.cs` | 2410 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260512055059_AddCounterpartyDualRolePostgres.Designer.cs` | 2426 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260512071821_AddTaskLegalEntityLinksPostgres.Designer.cs` | 2426 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260512180103_AddFinancialActionReasonSettingPostgres.Designer.cs` | 2426 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260512190023_AddDocumentStatusDictionaryPostgres.Designer.cs` | 2490 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260513185108_AddDepartmentDictionaryPostgres.Designer.cs` | 2539 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260515151139_ExpandServiceDictionaryNameLengthPostgres.Designer.cs` | 2539 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260515173000_ExpandServiceDictionaryTextFieldsPostgres.Designer.cs` | 2539 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260515185307_AddUserNotificationsPostgres.Designer.cs` | 2539 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260517173131_AddClientCaseRegistryFieldsPostgres.Designer.cs` | 2610 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260517190944_AddClientCaseDetailFieldsPostgres.Designer.cs` | 2684 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260517194755_AddClientCaseDocumentsPostgres.Designer.cs` | 2767 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260517231301_AddClientCaseActivityFieldsPostgres.Designer.cs` | 2800 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260518002437_AddClientCaseRelatedPartiesPostgres.Designer.cs` | 2886 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260518012203_AddClientCaseProceduralDeadlinesPostgres.Designer.cs` | 2898 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260519062530_AddTaskApprovalDueAtPostgres.Designer.cs` | 2898 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260519122626_AddClientCaseHiddenAccessFieldsPostgres.Designer.cs` | 2936 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260520071340_AddClientCaseWorkOrdersPostgres.Designer.cs` | 3065 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260520083207_AddClientCaseWorkOrderApprovalPostgres.Designer.cs` | 3098 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260520085946_AddClientCaseRelatedPartyArchivePostgres.Designer.cs` | 3120 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260524093337_AddClientCaseCourtInstancesPostgres.Designer.cs` | 4013 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260525074224_AddTaskContractorPaymentResultDelayPostgres.Designer.cs` | 4013 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260525092434_AddUserPresenceFieldsPostgres.Designer.cs` | 4013 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260529064455_ExpandActCommentLengthPostgres.Designer.cs` | 4012 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260601144644_AddGoogleCalendarSyncPostgres.Designer.cs` | 4386 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/20260602041322_AddYandexCalendarSyncPostgres.Designer.cs` | 4386 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Migrations/PostgreSql/PostgreSqlPaymentContextModelSnapshot.cs` | 4386 | `Timezone` | b.Property<string>("Timezone") |
| `backend/Models/LegalEntity.cs` | 61 | `Timezone` | public string? Timezone { get; set; } |
| `backend/Models/Requests/LegalEntityRequest.cs` | 59 | `Timezone` | public string? Timezone { get; set; } |
| `backend/Models/Responses/DatabaseBackupOverviewDto.cs` | 74 | `TimeZone` | public string TimeZone { get; init; } = string.Empty; |
| `backend/Models/Responses/LegalEntityDto.cs` | 37 | `Timezone` | public string? Timezone { get; set; } |
| Еще 192 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Ручные смещения времени

Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 5 | `+07:00` | "releasedAt": "2026-06-04T10:17:33+07:00", |
| `backend/AppReleases/releases.json` | 24 | `+07:00` | "releasedAt": "2026-06-04T03:50:00+07:00", |
| `backend/AppReleases/releases.json` | 43 | `+07:00` | "releasedAt": "2026-06-03T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 114 | `+07:00` | "releasedAt": "2026-06-03T16:50:00+07:00", |
| `backend/AppReleases/releases.json` | 145 | `+07:00` | "releasedAt": "2026-06-03T15:58:00+07:00", |
| `backend/AppReleases/releases.json` | 164 | `+07:00` | "releasedAt": "2026-06-03T15:24:00+07:00", |
| `backend/AppReleases/releases.json` | 187 | `+07:00` | "releasedAt": "2026-06-03T15:06:55+07:00", |
| `backend/AppReleases/releases.json` | 218 | `+07:00` | "releasedAt": "2026-06-03T10:10:00+07:00", |
| `backend/AppReleases/releases.json` | 241 | `+07:00` | "releasedAt": "2026-06-03T09:30:00+07:00", |
| `backend/AppReleases/releases.json` | 260 | `+07:00` | "releasedAt": "2026-06-02T17:50:00+07:00", |
| `backend/AppReleases/releases.json` | 279 | `+07:00` | "releasedAt": "2026-06-02T17:20:00+07:00", |
| `backend/AppReleases/releases.json` | 302 | `+07:00` | "releasedAt": "2026-06-02T17:05:00+07:00", |
| `backend/AppReleases/releases.json` | 325 | `+07:00` | "releasedAt": "2026-06-02T16:40:00+07:00", |
| `backend/AppReleases/releases.json` | 348 | `+07:00` | "releasedAt": "2026-06-02T16:10:00+07:00", |
| `backend/AppReleases/releases.json` | 367 | `+07:00` | "releasedAt": "2026-06-02T15:40:00+07:00", |
| `backend/AppReleases/releases.json` | 390 | `+07:00` | "releasedAt": "2026-06-02T14:45:00+07:00", |
| `backend/AppReleases/releases.json` | 413 | `+07:00` | "releasedAt": "2026-06-02T14:25:00+07:00", |
| `backend/AppReleases/releases.json` | 436 | `+07:00` | "releasedAt": "2026-06-02T13:20:00+07:00", |
| `backend/AppReleases/releases.json` | 463 | `+07:00` | "releasedAt": "2026-06-02T12:45:00+07:00", |
| `backend/AppReleases/releases.json` | 486 | `+07:00` | "releasedAt": "2026-06-02T12:17:00+07:00", |
| `backend/AppReleases/releases.json` | 505 | `+07:00` | "releasedAt": "2026-06-02T11:20:00+07:00", |
| `backend/AppReleases/releases.json` | 528 | `+07:00` | "releasedAt": "2026-06-02T10:45:00+07:00", |
| `backend/AppReleases/releases.json` | 547 | `+07:00` | "releasedAt": "2026-06-02T10:20:00+07:00", |
| `backend/AppReleases/releases.json` | 570 | `+07:00` | "releasedAt": "2026-06-02T10:00:00+07:00", |
| `backend/AppReleases/releases.json` | 589 | `+07:00` | "releasedAt": "2026-06-01T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 616 | `+07:00` | "releasedAt": "2026-06-01T23:45:00+07:00", |
| `backend/AppReleases/releases.json` | 635 | `+07:00` | "releasedAt": "2026-06-01T23:30:00+07:00", |
| `backend/AppReleases/releases.json` | 662 | `+07:00` | "releasedAt": "2026-06-01T23:20:00+07:00", |
| `backend/AppReleases/releases.json` | 689 | `+07:00` | "releasedAt": "2026-06-01T23:20:00+07:00", |
| `backend/AppReleases/releases.json` | 704 | `+07:00` | "releasedAt": "2026-06-01T23:00:00+07:00", |
| `backend/AppReleases/releases.json` | 727 | `+07:00` | "releasedAt": "2026-06-01T22:30:00+07:00", |
| `backend/AppReleases/releases.json` | 750 | `+07:00` | "releasedAt": "2026-06-01T22:25:00+07:00", |
| `backend/AppReleases/releases.json` | 765 | `+07:00` | "releasedAt": "2026-06-01T22:00:00+07:00", |
| `backend/AppReleases/releases.json` | 792 | `+07:00` | "releasedAt": "2026-06-01T21:55:00+07:00", |
| `backend/AppReleases/releases.json` | 819 | `+07:00` | "releasedAt": "2026-06-01T21:00:00+07:00", |
| `backend/AppReleases/releases.json` | 846 | `+07:00` | "releasedAt": "2026-06-01T20:00:00+07:00", |
| `backend/AppReleases/releases.json` | 873 | `+07:00` | "releasedAt": "2026-06-01T19:00:00+07:00", |
| `backend/AppReleases/releases.json` | 896 | `+07:00` | "releasedAt": "2026-06-01T18:00:00+07:00", |
| `backend/AppReleases/releases.json` | 911 | `+07:00` | "releasedAt": "2026-06-01T17:30:00+07:00", |
| `backend/AppReleases/releases.json` | 938 | `+07:00` | "releasedAt": "2026-06-01T17:02:13+07:00", |
| `backend/AppReleases/releases.json` | 953 | `+07:00` | "releasedAt": "2026-06-01T17:00:00+07:00", |
| `backend/AppReleases/releases.json` | 968 | `+07:00` | "releasedAt": "2026-06-01T16:54:00+07:00", |
| `backend/AppReleases/releases.json` | 987 | `+07:00` | "releasedAt": "2026-06-01T16:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1002 | `+07:00` | "releasedAt": "2026-06-01T15:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1017 | `+07:00` | "releasedAt": "2026-06-01T14:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1036 | `+07:00` | "releasedAt": "2026-06-01T13:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1055 | `+07:00` | "releasedAt": "2026-06-01T12:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1074 | `+07:00` | "releasedAt": "2026-06-01T11:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1097 | `+07:00` | "releasedAt": "2026-06-01T10:08:50+07:00", |
| `backend/AppReleases/releases.json` | 1112 | `+07:00` | "releasedAt": "2026-06-01T10:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1131 | `+07:00` | "releasedAt": "2026-06-01T09:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1150 | `+07:00` | "releasedAt": "2026-06-01T08:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1169 | `+07:00` | "releasedAt": "2026-06-01T07:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1188 | `+07:00` | "releasedAt": "2026-06-01T06:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1207 | `+07:00` | "releasedAt": "2026-06-01T05:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1226 | `+07:00` | "releasedAt": "2026-06-01T04:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1245 | `+07:00` | "releasedAt": "2026-06-01T03:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1264 | `+07:00` | "releasedAt": "2026-06-01T02:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1287 | `+07:00` | "releasedAt": "2026-06-01T01:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1314 | `+07:00` | "releasedAt": "2026-06-01T00:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1341 | `+07:00` | "releasedAt": "2026-05-30T13:42:54+07:00", |
| `backend/AppReleases/releases.json` | 1360 | `+07:00` | "releasedAt": "2026-05-30T01:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1383 | `+07:00` | "releasedAt": "2026-05-30T00:50:00+07:00", |
| `backend/AppReleases/releases.json` | 1402 | `+07:00` | "releasedAt": "2026-05-30T00:15:00+07:00", |
| `backend/AppReleases/releases.json` | 1417 | `+07:00` | "releasedAt": "2026-05-29T23:35:00+07:00", |
| `backend/AppReleases/releases.json` | 1440 | `+07:00` | "releasedAt": "2026-05-29T22:55:00+07:00", |
| `backend/AppReleases/releases.json` | 1455 | `+07:00` | "releasedAt": "2026-05-29T22:35:00+07:00", |
| `backend/AppReleases/releases.json` | 1470 | `+07:00` | "releasedAt": "2026-05-29T17:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1485 | `+07:00` | "releasedAt": "2026-05-29T16:50:00+07:00", |
| `backend/AppReleases/releases.json` | 1504 | `+07:00` | "releasedAt": "2026-05-29T15:22:00+07:00", |
| `backend/AppReleases/releases.json` | 1519 | `+07:00` | "releasedAt": "2026-05-29T14:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1534 | `+07:00` | "releasedAt": "2026-05-29T14:15:00+07:00", |
| `backend/AppReleases/releases.json` | 1549 | `+07:00` | "releasedAt": "2026-05-29T14:00:00+07:00", |
| `backend/AppReleases/releases.json` | 1568 | `+07:00` | "releasedAt": "2026-05-28T14:20:00+07:00", |
| `backend/AppReleases/releases.json` | 1587 | `+07:00` | "releasedAt": "2026-05-27T12:31:18+07:00", |
| `backend/AppReleases/releases.json` | 1606 | `+07:00` | "releasedAt": "2026-05-27T11:56:24+07:00", |
| `backend/AppReleases/releases.json` | 1625 | `+07:00` | "releasedAt": "2026-05-27T11:38:29+07:00", |
| `backend/AppReleases/releases.json` | 1644 | `+07:00` | "releasedAt": "2026-05-27T11:23:04+07:00", |
| `backend/AppReleases/releases.json` | 1667 | `+07:00` | "releasedAt": "2026-05-27T11:05:43+07:00", |
| `backend/AppReleases/releases.json` | 1686 | `+07:00` | "releasedAt": "2026-05-27T10:26:39+07:00", |
| `backend/AppReleases/releases.json` | 1705 | `+07:00` | "releasedAt": "2026-05-27T10:10:16+07:00", |
| `backend/AppReleases/releases.json` | 1728 | `+07:00` | "releasedAt": "2026-05-27T09:54:33+07:00", |
| `backend/AppReleases/releases.json` | 1751 | `+07:00` | "releasedAt": "2026-05-26T14:30:02+07:00", |
| `backend/AppReleases/releases.json` | 1774 | `+07:00` | "releasedAt": "2026-05-26T11:35:53+07:00", |
| `backend/AppReleases/releases.json` | 1797 | `+07:00` | "releasedAt": "2026-05-26T11:28:34+07:00", |
| `backend/AppReleases/releases.json` | 1820 | `+07:00` | "releasedAt": "2026-05-26T11:15:40+07:00", |
| `backend/AppReleases/releases.json` | 1843 | `+07:00` | "releasedAt": "2026-05-26T11:01:13+07:00", |
| `backend/AppReleases/releases.json` | 1866 | `+07:00` | "releasedAt": "2026-05-26T10:46:38+07:00", |
| `backend/AppReleases/releases.json` | 1889 | `+07:00` | "releasedAt": "2026-05-26T10:34:25+07:00", |
| `backend/AppReleases/releases.json` | 1912 | `+07:00` | "releasedAt": "2026-05-26T10:20:02+07:00", |
| `backend/AppReleases/releases.json` | 1935 | `+07:00` | "releasedAt": "2026-05-26T10:11:33+07:00", |
| `backend/AppReleases/releases.json` | 1958 | `+07:00` | "releasedAt": "2026-05-26T10:00:49+07:00", |
| `backend/AppReleases/releases.json` | 1981 | `+07:00` | "releasedAt": "2026-05-26T09:49:17+07:00", |
| `backend/AppReleases/releases.json` | 2004 | `+07:00` | "releasedAt": "2026-05-26T09:39:59+07:00", |
| `backend/AppReleases/releases.json` | 2027 | `+07:00` | "releasedAt": "2026-05-26T09:28:09+07:00", |
| `backend/AppReleases/releases.json` | 2050 | `+07:00` | "releasedAt": "2026-05-26T09:12:28+07:00", |
| `backend/AppReleases/releases.json` | 2073 | `+07:00` | "releasedAt": "2026-05-26T08:54:22+07:00", |
| `backend/AppReleases/releases.json` | 2096 | `+07:00` | "releasedAt": "2026-05-26T08:44:37+07:00", |
| `backend/AppReleases/releases.json` | 2119 | `+07:00` | "releasedAt": "2026-05-26T08:33:56+07:00", |
| `backend/AppReleases/releases.json` | 2142 | `+07:00` | "releasedAt": "2026-05-26T08:19:53+07:00", |
| `backend/AppReleases/releases.json` | 2165 | `+07:00` | "releasedAt": "2026-05-26T08:11:53+07:00", |
| `backend/AppReleases/releases.json` | 2188 | `+07:00` | "releasedAt": "2026-05-26T08:02:12+07:00", |
| `backend/AppReleases/releases.json` | 2211 | `+07:00` | "releasedAt": "2026-05-26T07:46:16+07:00", |
| `backend/AppReleases/releases.json` | 2234 | `+07:00` | "releasedAt": "2026-05-26T07:36:18+07:00", |
| `backend/AppReleases/releases.json` | 2257 | `+07:00` | "releasedAt": "2026-05-26T07:24:20+07:00", |
| `backend/AppReleases/releases.json` | 2280 | `+07:00` | "releasedAt": "2026-05-26T03:02:23+07:00", |
| `backend/AppReleases/releases.json` | 2303 | `+07:00` | "releasedAt": "2026-05-26T02:49:05+07:00", |
| `backend/AppReleases/releases.json` | 2326 | `+07:00` | "releasedAt": "2026-05-26T02:34:56+07:00", |
| `backend/AppReleases/releases.json` | 2349 | `+07:00` | "releasedAt": "2026-05-26T02:22:09+07:00", |
| `backend/AppReleases/releases.json` | 2372 | `+07:00` | "releasedAt": "2026-05-26T02:07:55+07:00", |
| `backend/AppReleases/releases.json` | 2395 | `+07:00` | "releasedAt": "2026-05-26T01:52:00+07:00", |
| `backend/AppReleases/releases.json` | 2418 | `+07:00` | "releasedAt": "2026-05-26T01:38:03+07:00", |
| `backend/AppReleases/releases.json` | 2437 | `+07:00` | "releasedAt": "2026-05-26T01:26:46+07:00", |
| `backend/AppReleases/releases.json` | 2456 | `+07:00` | "releasedAt": "2026-05-26T01:10:28+07:00", |
| `backend/AppReleases/releases.json` | 2475 | `+07:00` | "releasedAt": "2026-05-26T01:00:22+07:00", |
| `backend/AppReleases/releases.json` | 2494 | `+07:00` | "releasedAt": "2026-05-26T00:48:02+07:00", |
| `backend/AppReleases/releases.json` | 2513 | `+07:00` | "releasedAt": "2026-05-26T00:35:43+07:00", |
| `backend/AppReleases/releases.json` | 2532 | `+07:00` | "releasedAt": "2026-05-26T00:28:20+07:00", |
| `backend/AppReleases/releases.json` | 2551 | `+07:00` | "releasedAt": "2026-05-26T00:15:14+07:00", |
| `backend/AppReleases/releases.json` | 2570 | `+07:00` | "releasedAt": "2026-05-26T00:07:01+07:00", |
| `backend/AppReleases/releases.json` | 2589 | `+07:00` | "releasedAt": "2026-05-25T23:58:01+07:00", |
| `backend/AppReleases/releases.json` | 2608 | `+07:00` | "releasedAt": "2026-05-25T20:50:48+07:00", |
| `backend/AppReleases/releases.json` | 2627 | `+07:00` | "releasedAt": "2026-05-25T20:11:37+07:00", |
| `backend/AppReleases/releases.json` | 2646 | `+07:00` | "releasedAt": "2026-05-25T19:26:54+07:00", |
| `backend/AppReleases/releases.json` | 2665 | `+07:00` | "releasedAt": "2026-05-25T19:20:25+07:00", |
| `backend/AppReleases/releases.json` | 2684 | `+07:00` | "releasedAt": "2026-05-25T19:15:06+07:00", |
| `backend/AppReleases/releases.json` | 2703 | `+07:00` | "releasedAt": "2026-05-25T19:09:18+07:00", |
| `backend/AppReleases/releases.json` | 2722 | `+07:00` | "releasedAt": "2026-05-25T19:03:12+07:00", |
| `backend/AppReleases/releases.json` | 2741 | `+07:00` | "releasedAt": "2026-05-25T18:59:20+07:00", |
| `backend/AppReleases/releases.json` | 2760 | `+07:00` | "releasedAt": "2026-05-25T18:55:10+07:00", |
| `backend/AppReleases/releases.json` | 2783 | `+07:00` | "releasedAt": "2026-05-25T18:48:47+07:00", |
| `backend/AppReleases/releases.json` | 2806 | `+07:00` | "releasedAt": "2026-05-25T18:35:23+07:00", |
| `backend/AppReleases/releases.json` | 2829 | `+07:00` | "releasedAt": "2026-05-25T18:25:06+07:00", |
| `backend/AppReleases/releases.json` | 2852 | `+07:00` | "releasedAt": "2026-05-25T17:37:30+07:00", |
| `backend/AppReleases/releases.json` | 2875 | `+07:00` | "releasedAt": "2026-05-25T14:46:38+07:00", |
| `backend/AppReleases/releases.json` | 2898 | `+07:00` | "releasedAt": "2026-05-25T14:33:38+07:00", |
| `backend/AppReleases/releases.json` | 2917 | `+07:00` | "releasedAt": "2026-05-25T14:11:57+07:00", |
| `backend/AppReleases/releases.json` | 2940 | `+07:00` | "releasedAt": "2026-05-24T16:58:18+07:00", |
| `backend/AppReleases/releases.json` | 2963 | `+07:00` | "releasedAt": "2026-05-22T17:18:59+07:00", |
| `backend/AppReleases/releases.json` | 2982 | `+07:00` | "releasedAt": "2026-05-22T17:06:41+07:00", |
| `backend/AppReleases/releases.json` | 3001 | `+07:00` | "releasedAt": "2026-05-22T15:35:15+07:00", |
| `backend/AppReleases/releases.json` | 3032 | `+07:00` | "releasedAt": "2026-05-22T15:11:56+07:00", |
| `backend/AppReleases/releases.json` | 3055 | `+07:00` | "releasedAt": "2026-05-22T14:55:35+07:00", |
| `backend/AppReleases/releases.json` | 3078 | `+07:00` | "releasedAt": "2026-05-22T14:49:30+07:00", |
| `backend/AppReleases/releases.json` | 3101 | `+07:00` | "releasedAt": "2026-05-22T14:44:16+07:00", |
| `backend/AppReleases/releases.json` | 3124 | `+07:00` | "releasedAt": "2026-05-22T14:39:08+07:00", |
| `backend/AppReleases/releases.json` | 3147 | `+07:00` | "releasedAt": "2026-05-22T14:31:46+07:00", |
| `backend/AppReleases/releases.json` | 3174 | `+07:00` | "releasedAt": "2026-05-22T11:45:42+07:00", |
| `backend/AppReleases/releases.json` | 3197 | `+07:00` | "releasedAt": "2026-05-22T11:13:30+07:00", |
| `backend/AppReleases/releases.json` | 3220 | `+07:00` | "releasedAt": "2026-05-22T11:00:11+07:00", |
| `backend/AppReleases/releases.json` | 3243 | `+07:00` | "releasedAt": "2026-05-22T10:52:26+07:00", |
| `backend/AppReleases/releases.json` | 3262 | `+07:00` | "releasedAt": "2026-05-22T10:31:18+07:00", |
| `backend/AppReleases/releases.json` | 3285 | `+07:00` | "releasedAt": "2026-05-22T10:01:20+07:00", |
| `backend/AppReleases/releases.json` | 3308 | `+07:00` | "releasedAt": "2026-05-22T02:46:15+07:00", |
| `backend/AppReleases/releases.json` | 3327 | `+07:00` | "releasedAt": "2026-05-22T02:30:10+07:00", |
| `backend/AppReleases/releases.json` | 3342 | `+07:00` | "releasedAt": "2026-05-22T02:19:07+07:00", |
| `backend/AppReleases/releases.json` | 3357 | `+07:00` | "releasedAt": "2026-05-22T02:13:18+07:00", |
| `backend/AppReleases/releases.json` | 3376 | `+07:00` | "releasedAt": "2026-05-22T02:05:50+07:00", |
| `backend/AppReleases/releases.json` | 3395 | `+07:00` | "releasedAt": "2026-05-22T01:59:39+07:00", |
| `backend/AppReleases/releases.json` | 3414 | `+07:00` | "releasedAt": "2026-05-22T01:41:18+07:00", |
| `backend/AppReleases/releases.json` | 3433 | `+07:00` | "releasedAt": "2026-05-22T01:14:19+07:00", |
| `backend/AppReleases/releases.json` | 3452 | `+07:00` | "releasedAt": "2026-05-22T01:07:56+07:00", |
| `backend/AppReleases/releases.json` | 3475 | `+07:00` | "releasedAt": "2026-05-22T00:59:19+07:00", |
| `backend/AppReleases/releases.json` | 3494 | `+07:00` | "releasedAt": "2026-05-22T00:53:36+07:00", |
| `backend/AppReleases/releases.json` | 3513 | `+07:00` | "releasedAt": "2026-05-22T00:38:04+07:00", |
| `backend/AppReleases/releases.json` | 3536 | `+07:00` | "releasedAt": "2026-05-22T00:21:47+07:00", |
| `backend/AppReleases/releases.json` | 3559 | `+07:00` | "releasedAt": "2026-05-22T00:12:34+07:00", |
| `backend/AppReleases/releases.json` | 3578 | `+07:00` | "releasedAt": "2026-05-22T00:02:52+07:00", |
| `backend/AppReleases/releases.json` | 3597 | `+07:00` | "releasedAt": "2026-05-22T00:02:52+07:00", |
| `backend/AppReleases/releases.json` | 3620 | `+07:00` | "releasedAt": "2026-05-21T21:27:12+07:00", |
| `backend/AppReleases/releases.json` | 3643 | `+07:00` | "releasedAt": "2026-05-21T21:16:05+07:00", |
| `backend/AppReleases/releases.json` | 3662 | `+07:00` | "releasedAt": "2026-05-21T21:01:48+07:00", |
| `backend/AppReleases/releases.json` | 3685 | `+07:00` | "releasedAt": "2026-05-21T20:14:08+07:00", |
| `backend/AppReleases/releases.json` | 3708 | `+07:00` | "releasedAt": "2026-05-21T19:39:17+07:00", |
| `backend/AppReleases/releases.json` | 3731 | `+07:00` | "releasedAt": "2026-05-21T19:19:20+07:00", |
| `backend/AppReleases/releases.json` | 3750 | `+07:00` | "releasedAt": "2026-05-21T19:12:29+07:00", |
| `backend/AppReleases/releases.json` | 3773 | `+07:00` | "releasedAt": "2026-05-21T19:03:27+07:00", |
| `backend/AppReleases/releases.json` | 3796 | `+07:00` | "releasedAt": "2026-05-21T18:51:32+07:00", |
| `backend/AppReleases/releases.json` | 3823 | `+07:00` | "releasedAt": "2026-05-21T18:32:30+07:00", |
| `backend/AppReleases/releases.json` | 3858 | `+07:00` | "releasedAt": "2026-05-21T17:53:20+07:00", |
| `backend/AppReleases/releases.json` | 3881 | `+07:00` | "releasedAt": "2026-05-21T17:41:39+07:00", |
| `backend/AppReleases/releases.json` | 3904 | `+07:00` | "releasedAt": "2026-05-21T17:02:57+07:00", |
| `backend/AppReleases/releases.json` | 3927 | `+07:00` | "releasedAt": "2026-05-21T14:34:44+07:00", |
| `backend/AppReleases/releases.json` | 3994 | `+07:00` | "releasedAt": "2026-05-21T14:34:44+07:00", |
| `backend/PayPlanner.Api.Tests/AppVersionControllerTests.cs` | 119 | `TimeSpan.FromHours(7)` | var localZone = TimeZoneInfo.CreateCustomTimeZone("PayPlanner Test +07", TimeSpan.FromHours(7), "PayPlanner Test +07", "PayPlanner Test +07"); |
| `backend/PayPlanner.Api.Tests/LegalEntitiesControllerTests.cs` | 873 | `Asia/Novosibirsk` | Timezone = "Asia/Novosibirsk", |
| `backend/PayPlanner.Api.Tests/TimezoneDictionaryCatalogTests.cs` | 25 | `Asia/Novosibirsk` | Assert.Contains(all, item => item.Name == "Asia/Novosibirsk"); |
| `backend/PayPlanner.Api.Tests/TimezoneDictionaryCatalogTests.cs` | 61 | `Asia/Novosibirsk` | Assert.Contains(all, item => item.Name == "Asia/Novosibirsk"); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2255 | `Asia/Novosibirsk` | public string TimeZone { get; set; } = "Asia/Novosibirsk"; |
| `backend/Services/ReferenceData/TimezoneDictionaryCatalog.cs` | 267 | `Asia/Novosibirsk` | "Asia/Novosibirsk", |
| `src/components/S3Storage/S3Storage.test.tsx` | 89 | `+07:00` | createdAt: '2026-06-01T10:00:00+07:00', |
| `src/components/S3Storage/S3Storage.test.tsx` | 90 | `+07:00` | updatedAt: '2026-06-01T11:00:00+07:00', |
| `src/components/S3Storage/S3Storage.test.tsx` | 122 | `+07:00` | { name: 'readme.txt', path: '/readme.txt', key: 'payplanner/readme.txt', type: 'file', size: 12, lastModified: '2026-06-01T12:00:00+07:00' }, |
| `src/components/S3Storage/S3Storage.test.tsx` | 125 | `+07:00` | { name: 'note.txt', path: '/docs/note.txt', key: 'payplanner/docs/note.txt', type: 'file', size: 5, lastModified: '2026-06-01T12:10:00+07:00' }, |
| `src/components/S3Storage/S3Storage.test.tsx` | 146 | `+07:00` | checkedAt: '2026-06-01T12:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 103 | `+07:00` | createdAt: '2026-06-01T10:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 104 | `+07:00` | updatedAt: '2026-06-01T10:01:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 144 | `+07:00` | createdAt: '2026-06-01T10:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 145 | `+07:00` | updatedAt: '2026-06-01T10:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 153 | `+07:00` | updatedAt: '2026-06-01T10:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 228 | `+07:00` | createdAt: '2026-06-01T10:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 229 | `+07:00` | updatedAt: '2026-06-01T10:01:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 271 | `+07:00` | createdAt: '2026-06-01T10:00:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 272 | `+07:00` | updatedAt: '2026-06-01T10:01:00+07:00', |
| `src/components/Settings/DatabaseMaintenanceSettings.test.tsx` | 87 | `Asia/Novosibirsk` | timeZone: 'Asia/Novosibirsk', |
| `src/components/YandexDisk/YandexDisk.test.tsx` | 91 | `+07:00` | updatedAt: '2026-06-01T11:00:00+07:00', |
| `src/components/YandexDisk/YandexDisk.test.tsx` | 109 | `+07:00` | updatedAt: '2026-06-01T11:00:00+07:00', |
| `src/components/YandexDisk/YandexDisk.test.tsx` | 135 | `+07:00` | checkedAt: '2026-06-01T12:00:00+07:00', |
| `src/components/YandexDisk/YandexDisk.test.tsx` | 145 | `+07:00` | { name: 'readme.txt', path: '/readme.txt', fullPath: 'disk:/PayPlanner/readme.txt', type: 'file', size: 12, modified: '2026-06-01T12:00:00+07:00' }, |
| `src/utils/timeZoneDate.test.ts` | 11 | `Asia/Novosibirsk` | expect(toTimeZoneDateTimeInput('2026-06-05T12:00:00Z', 'Asia/Novosibirsk')).toBe('2026-06-05T19:00'); |
| `src/utils/timeZoneDate.test.ts` | 16 | `Asia/Novosibirsk` | expect(fromTimeZoneDateTimeInput('2026-06-05T19:00', 'Asia/Novosibirsk')).toBe('2026-06-05T12:00:00.000Z'); |
| `src/utils/timeZoneOptions.ts` | 11 | `UTC+7` | { value: 'Asia/Novosibirsk', label: 'Новосибирск (UTC+7)' }, |
| `src/utils/timeZoneOptions.ts` | 12 | `UTC+7` | { value: 'Asia/Krasnoyarsk', label: 'Красноярск (UTC+7)' }, |
| `src/utils/timeZoneOptions.ts` | 11 | `Asia/Novosibirsk` | { value: 'Asia/Novosibirsk', label: 'Новосибирск (UTC+7)' }, |

### Frontend: работа с датами

JS/TS места, где браузер может применить локальную timezone пользователя автоматически.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `scripts/run-legal-e2e-smoke.mjs` | 24 | `new Date(` | const today = new Date(); |
| `scripts/run-legal-e2e-smoke.mjs` | 25 | `new Date(` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-e2e-smoke.mjs` | 26 | `new Date(` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-e2e-smoke.mjs` | 541 | `.toISOString(` | return value.toISOString().slice(0, 10); |
| `scripts/run-legal-e2e-smoke.mjs` | 25 | `formatDate` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-e2e-smoke.mjs` | 26 | `formatDate` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-e2e-smoke.mjs` | 540 | `formatDate` | function formatDate(value) { |
| `scripts/run-legal-load-check.mjs` | 26 | `new Date(` | const today = new Date(); |
| `scripts/run-legal-load-check.mjs` | 27 | `new Date(` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-load-check.mjs` | 28 | `new Date(` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-load-check.mjs` | 346 | `.toISOString(` | return value.toISOString().slice(0, 10); |
| `scripts/run-legal-load-check.mjs` | 27 | `formatDate` | const from = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))); |
| `scripts/run-legal-load-check.mjs` | 28 | `formatDate` | const to = formatDate(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth() + 1, 0))); |
| `scripts/run-legal-load-check.mjs` | 345 | `formatDate` | function formatDate(value) { |
| `scripts/run-legal-readiness-report.mjs` | 76 | `new Date(` | const generatedAt = new Date(); |
| `scripts/run-legal-readiness-report.mjs` | 839 | `Date.parse(` | const timestamp = Date.parse(value); |
| `scripts/run-legal-readiness-report.mjs` | 77 | `.toISOString(` | const stamp = generatedAt.toISOString().replace(/[:.]/g, '-'); |
| `scripts/run-legal-readiness-report.mjs` | 84 | `.toISOString(` | generatedAtUtc: generatedAt.toISOString(), |
| `src/App.tsx` | 68 | `new Date(` | const now = new Date(); |
| `src/components/Accounts/Accounts.tsx` | 1117 | `new Date(` | badges.push(`Период: ${formatPeriodLabel(period, new Date())}`); |
| `src/components/Accounts/Accounts.tsx` | 1820 | `new Date(` | return new Date(payment.plannedDate) > new Date(acc) ? payment.plannedDate : acc; |
| `src/components/Accounts/Accounts.tsx` | 1826 | `new Date(` | return new Date(payment.paidDate) > new Date(acc) ? payment.paidDate : acc; |
| `src/components/Accounts/Accounts.tsx` | 2250 | `new Date(` | toDateInputValue(row.invoice?.date ?? row.dueDate) \|\| formatLocalYMD(new Date()); |
| `src/components/Accounts/Accounts.tsx` | 167 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/Accounts.tsx` | 1095 | `formatDate` | contract.date ? `от ${formatDate(contract.date)}` : null, |
| `src/components/Accounts/Accounts.tsx` | 2431 | `formatDate` | ? `Счет №${statusChangeInvoice.number} от ${formatDate(statusChangeInvoice.date)}` |
| `src/components/Accounts/Accounts.tsx` | 2913 | `formatDate` | Ближайший срок: {formatDate(supplier.nearestDueDate)} |
| `src/components/Accounts/Accounts.tsx` | 3162 | `formatDate` | {formatDate(invoice?.date)} |
| `src/components/Accounts/Accounts.tsx` | 3265 | `formatDate` | return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.dueDate)}</td>; |
| `src/components/Accounts/Accounts.tsx` | 3267 | `formatDate` | return <td key={column.key} className="px-4 py-3 text-center align-middle">{formatDate(row.paidDate)}</td>; |
| `src/components/Accounts/Accounts.tsx` | 3288 | `formatDate` | title={`${primaryAct.number ? `Акт №${primaryAct.number}` : t('act') ?? 'Акт'} · ${formatDate(primaryAct.date)} · ${primaryActStatusLabel}`} |
| `src/components/Accounts/Accounts.tsx` | 3294 | `formatDate` | {formatDate(primaryAct.date)} |
| `src/components/Accounts/Accounts.tsx` | 3348 | `formatDate` | <span className="block text-[11px] font-medium text-slate-500">{formatDate(act.date)}</span> |
| `src/components/Accounts/Accounts.tsx` | 3507 | `formatDate` | <td className="px-4 py-2">{formatDate(resolvePaymentOperationDate(payment))}</td> |
| `src/components/Accounts/InstallmentPlanner.tsx` | 139 | `new Date(` | const today = formatLocalYMD(new Date()); |
| `src/components/Accounts/InstallmentPlanner.tsx` | 758 | `type="date"` | type="date" |
| `src/components/Accounts/InstallmentPlanner.tsx` | 869 | `type="date"` | type="date" |
| `src/components/Accounts/Installments.tsx` | 45 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/Installments.tsx` | 240 | `formatDate` | case 'date': return <span className="font-medium text-slate-900">{formatDate(invoice.date)}</span>; |
| `src/components/Accounts/Installments.tsx` | 262 | `formatDate` | case 'dueDate': return formatDate(invoice.dueDate); |
| `src/components/Accounts/Installments.tsx` | 266 | `formatDate` | case 'createdAt': return formatDate(invoice.createdAt); |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1142 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.tsx` | 301 | `new Date(` | const today = formatLocalYMD(new Date()); |
| `src/components/Accounts/InvoiceModal.tsx` | 681 | `new Date(` | if (normalizedDueDate && normalizedDueDate < formatLocalYMD(new Date())) { |
| `src/components/Accounts/InvoiceModal.tsx` | 942 | `new Date(` | date: formatLocalYMD(new Date()), |
| `src/components/Accounts/InvoiceModal.tsx` | 995 | `new Date(` | const [cancelRefundDate, setCancelRefundDate] = useState(() => formatLocalYMD(new Date())); |
| `src/components/Accounts/InvoiceModal.tsx` | 1702 | `new Date(` | (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(), |
| `src/components/Accounts/InvoiceModal.tsx` | 2348 | `new Date(` | return next.sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()); |
| `src/components/Accounts/InvoiceModal.tsx` | 2431 | `new Date(` | date: form.date \|\| formatLocalYMD(new Date()), |
| `src/components/Accounts/InvoiceModal.tsx` | 2700 | `new Date(` | date: draftAct.date \|\| form.date \|\| formatLocalYMD(new Date()), |
| `src/components/Accounts/InvoiceModal.tsx` | 3143 | `new Date(` | setCancelRefundDate(formatLocalYMD(new Date())); |
| `src/components/Accounts/InvoiceModal.tsx` | 3179 | `new Date(` | refundDate = cancelRefundDate \|\| formatLocalYMD(new Date()); |
| `src/components/Accounts/InvoiceModal.tsx` | 3320 | `new Date(` | ? (form.paidDate \|\| payment.paidDate \|\| formatLocalYMD(new Date())) |
| `src/components/Accounts/InvoiceModal.tsx` | 3397 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Accounts/InvoiceModal.tsx` | 512 | `Date.parse(` | const leftDate = Date.parse(left.date ?? ''); |
| `src/components/Accounts/InvoiceModal.tsx` | 513 | `Date.parse(` | const rightDate = Date.parse(right.date ?? ''); |
| `src/components/Accounts/InvoiceModal.tsx` | 767 | `Date.parse(` | const fallbackTimestamp = Date.parse(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt ?? '') \|\| 0; |
| `src/components/Accounts/InvoiceModal.tsx` | 3397 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Accounts/InvoiceModal.tsx` | 4904 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 4915 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 4925 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5500 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5653 | `type="date"` | <input type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" /> |
| `src/components/Accounts/InvoiceModal.tsx` | 521 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/InvoiceModal.tsx` | 579 | `formatDate` | const dateLabel = formatDate(contract.date) \|\| 'без даты'; |
| `src/components/Accounts/InvoiceModal.tsx` | 773 | `formatDate` | timestampLabel: formatDate(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt) \|\| 'Без даты', |
| `src/components/Accounts/InvoiceModal.tsx` | 2647 | `formatDate` | const currentDueDateLabel = formatDate(currentDueDate) \|\| 'не указан'; |
| `src/components/Accounts/InvoiceModal.tsx` | 2648 | `formatDate` | const expectedDueDateLabel = formatDate(expectedDueDate); |
| `src/components/Accounts/InvoiceModal.tsx` | 3705 | `formatDate` | {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full} |
| `src/components/Accounts/InvoiceModal.tsx` | 3767 | `formatDate` | {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full} |
| `src/components/Accounts/InvoiceModal.tsx` | 3825 | `formatDate` | ? `Счёт №${form.number \|\| '—'} от ${formatDate(form.date) \|\| '—'}` |
| `src/components/Accounts/InvoiceModal.tsx` | 4041 | `formatDate` | <span>Ожидаемая дата: {formatDate(expectedPaymentDate) \|\| '—'}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4043 | `formatDate` | <span>Фактическая дата: {formatDate(actualPaymentDate) \|\| actualPaymentDate}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4135 | `formatDate` | <p><span className="text-slate-500">Дата выставления:</span> {formatDate(form.date) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4136 | `formatDate` | <p><span className="text-slate-500">Срок оплаты:</span> {formatDate(form.dueDate) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4139 | `formatDate` | <p><span className="text-slate-500">Дата оплаты:</span> {formatDate(form.paidDate) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4604 | `formatDate` | <span>Ожидаемая дата: {formatDate(expectedPaymentDate) \|\| '—'}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4606 | `formatDate` | <span>Фактическая дата: {formatDate(actualPaymentDate) \|\| actualPaymentDate}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4722 | `formatDate` | <div>{formatDate(operation.date) \|\| 'Без даты'}</div> |
| `src/components/Accounts/InvoiceModal.tsx` | 5029 | `formatDate` | {formatDate(selectedContractPreview.date) \|\| '—'} |
| `src/components/Accounts/InvoiceModal.tsx` | 5037 | `formatDate` | : (formatDate(selectedContractPreview.validUntil) \|\| '—')} |
| `src/components/Accounts/InvoiceModal.tsx` | 5163 | `formatDate` | {formatDate(selectedPaymentPreviewExpectedDate) \|\| '—'} |
| `src/components/Accounts/InvoiceModal.tsx` | 5170 | `formatDate` | {formatDate(selectedPaymentPreviewActualDate) \|\| selectedPaymentPreviewActualDate} |
| `src/components/Acts/ActDetailPage.tsx` | 82 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Acts/ActDetailPage.tsx` | 388 | `formatDate` | ? `Акт №${act.number \|\| act.id} от ${formatDate(act.date)}` |
| `src/components/Acts/ActDetailPage.tsx` | 757 | `formatDate` | Акт №{act.number \|\| '—'} от {formatDate(act.date)} |
| `src/components/Acts/ActDetailPage.tsx` | 878 | `formatDate` | <p><span className="text-slate-500">Дата акта:</span> {formatDate(act.date)}</p> |
| `src/components/Acts/ActDetailPage.tsx` | 881 | `formatDate` | <p><span className="text-slate-500">Аннулирование:</span> {[formatDate(act.cancelledAt), act.cancelledReason?.trim()].filter(Boolean).join(' · ')}</p> |
| `src/components/Acts/ActModal.tsx` | 412 | `new Date(` | const today = toDateInputValue(new Date()); |
| `src/components/Acts/ActModal.tsx` | 449 | `new Date(` | date: toDateInputValue(act.date) \|\| toDateInputValue(new Date()), |
| `src/components/Acts/ActModal.tsx` | 1510 | `type="date"` | type="date" |
| `src/components/Acts/Acts.tsx` | 37 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Acts/Acts.tsx` | 623 | `formatDate` | return <td key={key} className="whitespace-nowrap px-4 py-3 text-sm text-slate-700">{formatDate(act.date)}</td>; |
| `src/components/Acts/Acts.tsx` | 736 | `formatDate` | ? `Акт №${statusChangeAct.number \|\| statusChangeAct.id} от ${formatDate(statusChangeAct.date)}` |
| `src/components/AppVersion/AppVersionModal.tsx` | 38 | `new Date(` | const date = new Date(value); |
| `src/components/AppVersion/AppVersionModal.tsx` | 43 | `Intl.DateTimeFormat` | return new Intl.DateTimeFormat('ru-RU', { |
| `src/components/Calculator/Calculator.tsx` | 42 | `new Date(` | const today = new Date().toISOString().slice(0, 10); |
| `src/components/Calculator/Calculator.tsx` | 42 | `.toISOString(` | const today = new Date().toISOString().slice(0, 10); |
| `src/components/Calendar/Calendar.tsx` | 73 | `new Date(` | return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1)); |
| `src/components/Calendar/Calendar.tsx` | 77 | `new Date(` | return formatLocalYMD(new Date(y, m ?? 1, 0)); |
| `src/components/Calendar/Calendar.tsx` | 84 | `new Date(` | const [currentDate, setCurrentDate] = useState(new Date()); |
| `src/components/Calendar/Calendar.tsx` | 181 | `new Date(` | const startOfMonth = new Date(year, m0, 1); |
| `src/components/Calendar/Calendar.tsx` | 182 | `new Date(` | const endOfMonth = new Date(year, m0 + 1, 0); |
| `src/components/Calendar/Calendar.tsx` | 254 | `new Date(` | const d = new Date(prev); |
| `src/components/Calendar/CalendarGrid.tsx` | 81 | `new Date(` | const firstDayOfMonth = new Date(year, month, 1); |
| `src/components/Calendar/CalendarGrid.tsx` | 82 | `new Date(` | const lastDayOfMonth = new Date(year, month + 1, 0); |
| `src/components/Calendar/CalendarGrid.tsx` | 130 | `new Date(` | const isThisMonth = new Date().getFullYear() === year && new Date().getMonth() === month; |
| `src/components/Calendar/CalendarGrid.tsx` | 131 | `new Date(` | const today = isThisMonth ? new Date().getDate() : -1; |
| `src/components/Calendar/PaymentModal.tsx` | 2884 | `type="date"` | <div><label className={centeredLabelClass}>{t('expectedPaymentDate') ?? 'Expected date'}</label><input type="date" name="date" value={formData.date} onChange={handleChange} required className={`${centeredFieldClass} ${is |
| `src/components/Calendar/PaymentModal.tsx` | 2885 | `type="date"` | <div><label className={centeredLabelClass}>{t('plannedScheduleDate') ?? 'Planned date'}</label><input type="date" name="plannedDate" value={formData.plannedDate} onChange={handleChange} className={centeredFieldClass} />< |
| `src/components/Calendar/PaymentModal.tsx` | 2928 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3067 | `type="date"` | <input type="date" name="lastPaymentDate" value={formData.lastPaymentDate} onChange={handleChange} className={centeredFieldClass} /> |
| `src/components/Calendar/PaymentModal.tsx` | 3073 | `type="date"` | <input type="date" name="paidDate" value={formData.paidDate} onChange={handleChange} className={centeredFieldClass} /> |
| `src/components/Calendar/PaymentModal.tsx` | 3392 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3423 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3599 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3631 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentsTable.test.tsx` | 10 | `formatDate` | formatDate: (value: string) => value, |
| `src/components/Calendar/PaymentsTable.tsx` | 89 | `formatDate` | const { t, formatDate } = useTranslation(); |
| `src/components/Calendar/PaymentsTable.tsx` | 167 | `formatDate` | ? `Документ: ${payment.paymentOrderNumber}${payment.paymentDocumentDate ? ` от ${formatDate(payment.paymentDocumentDate)}` : ''}` |
| `src/components/Calendar/PaymentsTable.tsx` | 169 | `formatDate` | ? `Дата документа: ${formatDate(payment.paymentDocumentDate)}` |
| `src/components/Calendar/PaymentsTable.tsx` | 172 | `formatDate` | ? `Счет: ${payment.account}${payment.accountDate ? ` от ${formatDate(payment.accountDate)}` : ''}` |
| `src/components/Calendar/PaymentsTable.tsx` | 179 | `formatDate` | return payment.date ? formatDate(payment.date) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 181 | `formatDate` | return payment.plannedDate ? formatDate(payment.plannedDate) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 290 | `formatDate` | ? formatDate(payment.paidDate \|\| payment.lastPaymentDate \|\| '') |
| `src/components/Calendar/PaymentsTable.tsx` | 293 | `formatDate` | return payment.createdAt ? formatDate(payment.createdAt) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 298 | `formatDate` | [canEdit, formatDate, onOpenClient, onStatusChange, statusLabels, t], |
| `src/components/Cases/CaseDetailPage.test.tsx` | 890 | `new Date(` | lastTaskAt: new Date().toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 890 | `.toISOString(` | lastTaskAt: new Date().toISOString(), |
| `src/components/Cases/CaseDetailPage.tsx` | 367 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 374 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 488 | `new Date(` | const today = new Date(); |
| `src/components/Cases/CaseDetailPage.tsx` | 499 | `new Date(` | const date = new Date(getPaymentCalendarDate(payment)); |
| `src/components/Cases/CaseDetailPage.tsx` | 513 | `new Date(` | const date = new Date(year, month - 1, 1); |
| `src/components/Cases/CaseDetailPage.tsx` | 2315 | `new Date(` | const startAt = caseItem.courtDecisionAt \|\| caseItem.claimFiledAt \|\| caseItem.nextHearingAt \|\| new Date().toISOString(); |
| `src/components/Cases/CaseDetailPage.tsx` | 4938 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 4943 | `new Date(` | const day = new Date(date); |
| `src/components/Cases/CaseDetailPage.tsx` | 4945 | `new Date(` | const today = new Date(); |
| `src/components/Cases/CaseDetailPage.tsx` | 486 | `Date.parse(` | const planned = Date.parse(getPaymentCalendarDate(payment)); |
| `src/components/Cases/CaseDetailPage.tsx` | 752 | `Date.parse(` | const time = Date.parse(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 817 | `Date.parse(` | const dueAt = Date.parse(task.dueAt); |
| `src/components/Cases/CaseDetailPage.tsx` | 2210 | `Date.parse(` | setCourtEvents((current) => [...current, item].sort((left, right) => Date.parse(left.scheduledAt) - Date.parse(right.scheduledAt))); |
| `src/components/Cases/CaseDetailPage.tsx` | 2297 | `Date.parse(` | setProceduralDeadlines((current) => [...current, item].sort((left, right) => Date.parse(left.dueAt) - Date.parse(right.dueAt))); |
| `src/components/Cases/CaseDetailPage.tsx` | 4309 | `Date.parse(` | const leftTime = Date.parse(left.plannedDate \|\| left.date); |
| `src/components/Cases/CaseDetailPage.tsx` | 4310 | `Date.parse(` | const rightTime = Date.parse(right.plannedDate \|\| right.date); |
| `src/components/Cases/CaseDetailPage.tsx` | 5020 | `Date.parse(` | const leftTime = Date.parse(left.value ?? ''); |
| `src/components/Cases/CaseDetailPage.tsx` | 5021 | `Date.parse(` | const rightTime = Date.parse(right.value ?? ''); |
| `src/components/Cases/CaseDetailPage.tsx` | 376 | `.toISOString(` | return date.toISOString().slice(0, 10); |
| `src/components/Cases/CaseDetailPage.tsx` | 2315 | `.toISOString(` | const startAt = caseItem.courtDecisionAt \|\| caseItem.claimFiledAt \|\| caseItem.nextHearingAt \|\| new Date().toISOString(); |
| `src/components/Cases/CaseDetailPage.tsx` | 514 | `Intl.DateTimeFormat` | return new Intl.DateTimeFormat('ru-RU', { month: 'long', year: 'numeric' }).format(date); |
| `src/components/Cases/CaseDetailPage.tsx` | 3580 | `type="date"` | <input type="date" value={draft.startDate} onChange={(event) => setField('startDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 3584 | `type="date"` | <input type="date" value={draft.dueDate} onChange={(event) => setField('dueDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 3651 | `type="date"` | <label className="text-sm font-medium text-slate-700">Иск подан<input type="date" value={draft.claimFiledAt} onChange={(event) => setField('claimFiledAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg borde |
| `src/components/Cases/CaseDetailPage.tsx` | 3652 | `type="date"` | <label className="text-sm font-medium text-slate-700">Решение суда<input type="date" value={draft.courtDecisionAt} onChange={(event) => setField('courtDecisionAt', event.target.value)} className="mt-1 h-10 w-full rounded |
| `src/components/Cases/CaseDetailPage.tsx` | 3653 | `type="date"` | <label className="text-sm font-medium text-slate-700">Апелляция<input type="date" value={draft.appealDeadlineAt} onChange={(event) => setField('appealDeadlineAt', event.target.value)} className="mt-1 h-10 w-full rounded- |
| `src/components/Cases/CaseDetailPage.tsx` | 3654 | `type="date"` | <label className="text-sm font-medium text-slate-700">Кассация<input type="date" value={draft.cassationDeadlineAt} onChange={(event) => setField('cassationDeadlineAt', event.target.value)} className="mt-1 h-10 w-full rou |
| `src/components/Cases/CaseDetailPage.tsx` | 3655 | `type="date"` | <label className="text-sm font-medium text-slate-700">Возражения<input type="date" value={draft.objectionDeadlineAt} onChange={(event) => setField('objectionDeadlineAt', event.target.value)} className="mt-1 h-10 w-full r |
| `src/components/Cases/CaseDetailPage.tsx` | 4177 | `type="date"` | <WorkOrderTextInput label="Срок" type="date" value={newItemDraft.dueDate} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, dueDate: value })} disabled={isWorkOrderLocked} /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4203 | `type="date"` | <WorkOrderTextInput label="Срок" type="date" value={itemDraft.dueDate} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, dueDate: value })} disabled={isWorkOrderLocked} /> |
| `src/components/Cases/CaseDetailPage.tsx` | 5380 | `type="date"` | <input type="date" value={courtComplaintDraft.filedAt} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, filedAt: event.target.value })} className="w-full rounded-lg border border-slate-200 px-3  |
| `src/components/Cases/CaseDetailPage.tsx` | 5622 | `type="date"` | <input type="date" value={deadlineDueAtDraft} onChange={(event) => onDeadlineDueAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 3611 | `type="datetime-local"` | <input type="datetime-local" value={draft.nextHearingAt} onChange={(event) => setField('nextHearingAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 5534 | `type="datetime-local"` | <input type="datetime-local" value={courtEventScheduledAtDraft} onChange={(event) => onCourtEventScheduledAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Cases/CasesRegistry.tsx` | 236 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CasesRegistry.tsx` | 245 | `new Date(` | return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 250 | `new Date(` | const date = new Date(latestActivity); |
| `src/components/Cases/CasesRegistry.tsx` | 615 | `new Date(` | updatedAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1223 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 245 | `.toISOString(` | return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 615 | `.toISOString(` | updatedAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1223 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1446 | `type="date"` | <input type="date" value={from} onChange={(event) => setFrom(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата с" /> |
| `src/components/Cases/CasesRegistry.tsx` | 1447 | `type="date"` | <input type="date" value={to} onChange={(event) => setTo(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата по" /> |
| `src/components/Clients/CaseModal.tsx` | 122 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 129 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 138 | `new Date(` | return value ? new Date(`${value}T00:00:00`).toISOString() : null; |
| `src/components/Clients/CaseModal.tsx` | 180 | `new Date(` | const daysInTargetMonth = new Date(targetYear, targetMonthIndex + 1, 0).getDate(); |
| `src/components/Clients/CaseModal.tsx` | 181 | `new Date(` | let targetDate = new Date(targetYear, targetMonthIndex, Math.min(day, daysInTargetMonth)); |
| `src/components/Clients/CaseModal.tsx` | 184 | `new Date(` | targetDate = new Date(targetDate.getFullYear(), targetDate.getMonth(), targetDate.getDate() + 1); |
| `src/components/Clients/CaseModal.tsx` | 205 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 222 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 130 | `.toISOString(` | return Number.isNaN(date.getTime()) ? String(value).slice(0, 10) : date.toISOString().slice(0, 10); |
| `src/components/Clients/CaseModal.tsx` | 138 | `.toISOString(` | return value ? new Date(`${value}T00:00:00`).toISOString() : null; |
| `src/components/Clients/CaseModal.tsx` | 1073 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1085 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1470 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1482 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1494 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1506 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1518 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1530 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1424 | `type="datetime-local"` | type="datetime-local" |
| `src/components/Clients/ClientAddressEntriesForm.tsx` | 173 | `type="date"` | type="date" |
| `src/components/Clients/ClientAddressEntriesForm.tsx` | 186 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.integration.test.tsx` | 1624 | `new Date(` | const today = new Date(); |
| `src/components/Clients/ClientDetail.integration.test.tsx` | 1841 | `new Date(` | const today = new Date(); |
| `src/components/Clients/ClientDetail.tsx` | 557 | `new Date(` | return formatLocalYMD(new Date(y, (m ?? 1) - 1, 1)); |
| `src/components/Clients/ClientDetail.tsx` | 561 | `new Date(` | return formatLocalYMD(new Date(y, m ?? 1, 0)); |
| `src/components/Clients/ClientDetail.tsx` | 596 | `new Date(` | return formatter.format(new Date(yearRaw, monthRaw - 1, 1)); |
| `src/components/Clients/ClientDetail.tsx` | 613 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/ClientDetail.tsx` | 894 | `new Date(` | issuedAt: formatLocalYMD(new Date()), |
| `src/components/Clients/ClientDetail.tsx` | 2091 | `new Date(` | const today = useMemo(() => new Date(), []); |
| `src/components/Clients/ClientDetail.tsx` | 3385 | `new Date(` | const leftDate = left.caseCreatedAt ? new Date(left.caseCreatedAt).getTime() : 0; |
| `src/components/Clients/ClientDetail.tsx` | 3386 | `new Date(` | const rightDate = right.caseCreatedAt ? new Date(right.caseCreatedAt).getTime() : 0; |
| `src/components/Clients/ClientDetail.tsx` | 3428 | `new Date(` | new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 3519 | `new Date(` | new Date(b.date).getTime() - new Date(a.date).getTime() \|\| |
| `src/components/Clients/ClientDetail.tsx` | 3528 | `new Date(` | new Date(b.date).getTime() - new Date(a.date).getTime() \|\| |
| `src/components/Clients/ClientDetail.tsx` | 3539 | `new Date(` | new Date(b.date).getTime() - new Date(a.date).getTime() \|\| |
| `src/components/Clients/ClientDetail.tsx` | 4973 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 10509 | `new Date(` | {new Date(item.createdAt).toLocaleString("ru-RU")} |
| `src/components/Clients/ClientDetail.tsx` | 3428 | `.toISOString(` | new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 4973 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/ClientDetail.tsx` | 587 | `Intl.DateTimeFormat` | const formatter = new Intl.DateTimeFormat("ru-RU", { |
| `src/components/Clients/ClientDetail.tsx` | 6224 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 6771 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 6786 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 7000 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 7127 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 9369 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 9378 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 9818 | `type="date"` | type="date" |
| `src/components/Clients/ClientDetail.tsx` | 9827 | `type="date"` | type="date" |
| `src/components/Clients/ClientPassportEntriesForm.tsx` | 90 | `type="date"` | type="date" |
| `src/components/Clients/ClientPassportEntriesForm.tsx` | 113 | `type="date"` | type="date" |
| `src/components/Clients/ClientPassportEntriesForm.tsx` | 124 | `type="date"` | type="date" |
| `src/components/Clients/clientQuickModalUtils.test.ts` | 20 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/clientQuickModalUtils.test.ts` | 20 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/ClientReconciliationAct.tsx` | 765 | `new Date(` | setSavedLabel(`Черновик сохранён ${new Date().toLocaleTimeString("ru-RU")}`); |
| `src/components/Clients/ClientReconciliationAct.tsx` | 884 | `type="date"` | type="date" |
| `src/components/Clients/ClientReconciliationAct.tsx` | 893 | `type="date"` | type="date" |
| `src/components/Clients/ClientReconciliationAct.tsx` | 1282 | `type="date"` | type="date" |
| `src/components/Clients/Clients.tsx` | 98 | `new Date(` | return new Date(d).toLocaleDateString(undefined, { |
| `src/components/Clients/Clients.tsx` | 1727 | `new Date(` | return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(); |
| `src/components/Clients/Clients.tsx` | 2021 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/Clients.tsx` | 2021 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Clients/Clients.tsx` | 95 | `formatDate` | function formatDate(d?: string) { |
| `src/components/Clients/Clients.tsx` | 1170 | `formatDate` | return <span className="text-sm text-gray-600 block truncate">{formatDate(client.createdAt)}</span>; |
| `src/components/Clients/Clients.tsx` | 1883 | `formatDate` | {t('created') \|\| 'Создано'}: {formatDate(c.createdAt)} |
| `src/components/Contacts/UnifiedContacts.tsx` | 233 | `new Date(` | return new Date(value).toLocaleDateString('ru-RU'); |
| `src/components/Contacts/UnifiedContacts.tsx` | 231 | `formatDate` | function formatDate(value?: string \| null) { |
| `src/components/Contacts/UnifiedContacts.tsx` | 946 | `formatDate` | return formatDate(item.createdAt) \|\| '—'; |
| `src/components/Contacts/UnifiedContacts.tsx` | 948 | `formatDate` | return formatDate(client?.auditUpdatedAt \|\| legal?.updatedAt \|\| legal?.auditUpdatedAt) \|\| '—'; |
| `src/components/Contacts/UnifiedContacts.tsx` | 1119 | `formatDate` | <span>{formatDate(item.createdAt)}</span> |
| `src/components/Contracts/ContractCreateForm.tsx` | 32 | `new Date(` | date: toDateInputValue(new Date()), |
| `src/components/Contracts/ContractCreateForm.tsx` | 203 | `type="date"` | type="date" |
| `src/components/Contracts/ContractCreateForm.tsx` | 282 | `type="date"` | type="date" |
| `src/components/Contracts/ContractCreatePage.tsx` | 31 | `new Date(` | const today = toDateInputValue(new Date()); |
| `src/components/Contracts/ContractCreatePage.tsx` | 292 | `type="date"` | <input type="date" value={form.date} onChange={(e) => setForm((prev) => ({ ...prev, date: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" required /> |
| `src/components/Contracts/ContractCreatePage.tsx` | 296 | `type="date"` | <input type="date" value={form.startDate} onChange={(e) => setForm((prev) => ({ ...prev, startDate: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Contracts/ContractCreatePage.tsx` | 337 | `type="date"` | <input type="date" value={form.validUntil} onChange={(e) => setForm((prev) => ({ ...prev, validUntil: e.target.value }))} className="rounded-lg border border-slate-200 px-3 py-2 text-sm" disabled={form.isOpenEnded} /> |
| Еще 392 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Фильтры и периоды

Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 25 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 26 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 488 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 489 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 25 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 26 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 488 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 489 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 726 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 727 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/ActsController.cs` | 57 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 58 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 410 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 411 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 57 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 58 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 410 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 411 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CalendarSyncController.cs` | 123 | `[FromQuery] DateTime?` | [FromQuery] DateTime? createdFrom = null, |
| `backend/Controllers/CalendarSyncController.cs` | 124 | `[FromQuery] DateTime?` | [FromQuery] DateTime? createdTo = null, |
| `backend/Controllers/CashOrdersController.cs` | 31 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 32 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 41 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 42 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 53 | `[FromQuery] DateTime?` | [FromQuery] DateTime? date, |
| `backend/Controllers/CashOrdersController.cs` | 68 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 69 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 103 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 104 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 162 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 163 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 216 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 217 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 31 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 32 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 41 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 42 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 68 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 69 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 103 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 104 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 162 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 163 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 216 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 217 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 666 | `DateTime? from` | private async Task<CashBookResponseDto> BuildCashBookAsync(DateTime? from, DateTime? to, string? accountName, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 666 | `DateTime? to` | private async Task<CashBookResponseDto> BuildCashBookAsync(DateTime? from, DateTime? to, string? accountName, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 751 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 752 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1313 | `DateTime? from` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/CashOrdersController.cs` | 1313 | `DateTime? to` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/ContractsController.cs` | 91 | `[FromQuery] DateTime?` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 700 | `[FromQuery] DateTime` | public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 91 | `DateTime? from` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 91 | `DateTime? to` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 1770 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/ContractsController.cs` | 1771 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 63 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 64 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 185 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 186 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 1557 | `[FromQuery] DateTime?` | public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 63 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 64 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 185 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 186 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 2766 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 2767 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3771 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 3772 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 5816 | `startDate` | var startDate = childPayments.Count == 0 |
| `backend/Controllers/InvoicesController.cs` | 5843 | `startDate` | StartDate = startDate, |
| `backend/Controllers/MoneyOperationsController.cs` | 194 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 195 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 234 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 235 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 407 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 408 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 457 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 458 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 504 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 505 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 551 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 552 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 630 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 631 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 736 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 737 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 758 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 759 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 802 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 803 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 194 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 195 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 234 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 235 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 407 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 408 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 457 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 458 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 504 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 505 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 551 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 552 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 630 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 631 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 736 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 737 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 758 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 759 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 802 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 803 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 900 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 901 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1005 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1006 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1342 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1343 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 1451 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 1452 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3161 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 3162 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/NotificationInboxController.cs` | 59 | `dateFrom` | [FromQuery] string? dateFrom = null, |
| `backend/Controllers/NotificationInboxController.cs` | 60 | `dateTo` | [FromQuery] string? dateTo = null, |
| `backend/Controllers/NotificationInboxController.cs` | 86 | `dateFrom` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 86 | `dateTo` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 113 | `dateFrom` | [FromQuery] string? dateFrom = null, |
| `backend/Controllers/NotificationInboxController.cs` | 114 | `dateTo` | [FromQuery] string? dateTo = null, |
| `backend/Controllers/NotificationInboxController.cs` | 134 | `dateFrom` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 134 | `dateTo` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 291 | `dateFrom` | string? dateFrom, |
| `backend/Controllers/NotificationInboxController.cs` | 292 | `dateTo` | string? dateTo) |
| `backend/Controllers/NotificationInboxController.cs` | 294 | `dateFrom` | if (DateTime.TryParse(dateFrom, out var parsedFrom)) |
| `backend/Controllers/NotificationInboxController.cs` | 300 | `dateTo` | if (DateTime.TryParse(dateTo, out var parsedTo)) |
| `backend/Controllers/ObjectAuditController.cs` | 42 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ObjectAuditController.cs` | 42 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 54 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 55 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 592 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 593 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 54 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 55 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/PaymentsController.cs` | 592 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/PaymentsController.cs` | 593 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 106 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 107 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 139 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 140 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 157 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 158 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 179 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 180 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 204 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 205 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 206 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 249 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 250 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 251 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 296 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 297 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 330 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 331 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 578 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 620 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 657 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 696 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 733 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 734 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 765 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 766 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 786 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 787 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 807 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 808 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 828 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 829 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 849 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 850 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 870 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 871 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 891 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 892 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 912 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 913 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 933 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 934 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 954 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 955 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 975 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 976 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 996 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 997 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1016 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1017 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1036 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1037 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1056 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1057 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1077 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1078 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1098 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1099 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1119 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1120 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1140 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1141 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1161 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1162 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1188 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1189 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1215 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1216 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 106 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 107 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 139 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 140 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 157 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 158 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 179 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 180 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 204 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 205 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 249 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 250 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 296 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 297 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 330 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 331 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 362 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 363 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 394 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 395 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime Start` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime End` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime? from` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 529 | `DateTime? to` | private static (DateTime Start, DateTime End) ResolveLegalWorkPeriod(DateTime? from, DateTime? to) |
| `backend/Controllers/ReportsController.cs` | 733 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 734 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 765 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 766 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 786 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 787 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 807 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 808 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 828 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 829 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 849 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 850 | `DateTime? to` | [FromQuery] DateTime? to, |
| Еще 660 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Экспорты, импорты и файлы

Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 79 | `CSV` | "text": "В отчете по загрузке юристов ближайшие задачи теперь показываются и выгружаются в CSV с учетом часового пояса профиля. В карточке дела время последнего просмотра, подтверждения активности и загрузки документов т |
| `backend/AppReleases/releases.json` | 103 | `CSV` | "text": "Экспорт реестра дел и печатные формы заказ-наряда теперь показывают время формирования по часовому поясу пользователя. Имена файлов CSV, Excel и PDF также получают дату и время из профиля сотрудника." |
| `backend/AppReleases/releases.json` | 103 | `PDF` | "text": "Экспорт реестра дел и печатные формы заказ-наряда теперь показывают время формирования по часовому поясу пользователя. Имена файлов CSV, Excel и PDF также получают дату и время из профиля сотрудника." |
| `backend/AppReleases/releases.json` | 122 | `ICS` | "text": "В настройках внешних календарей добавлен блок «Выполненные задачи в календарях». Администратор может оставить выполненные задачи в календарях с отдельной отметкой или скрывать их после выполнения. Правило примен |
| `backend/AppReleases/releases.json` | 130 | `ICS` | "text": "Исправлена выгрузка календаря дел и календаря задач в ICS: судебные заседания, задачи и другие события с точным временем больше не получают сдвиг при добавлении во внешний календарь." |
| `backend/AppReleases/releases.json` | 1105 | `PDF` | "text": "При выгрузке акта в Word или PDF CRM заменяет символы, которые могут мешать сохранению файла на разных устройствах и серверах. Документ по-прежнему содержит исходный номер акта внутри печатной формы." |
| `backend/AppReleases/releases.json` | 1171 | `PDF` | "summary": "В карточке акта появилась ручная отправка PDF акта на Яндекс.Диск по правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 1177 | `PDF` | "text": "В карточке акта рядом с действиями отправки, печати и скачивания появилась кнопка «Я.Диск». Она формирует PDF акта и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск». |
| `backend/AppReleases/releases.json` | 1190 | `PDF` | "summary": "В карточке счета появилась ручная отправка PDF счета на Яндекс.Диск по правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 1196 | `PDF` | "text": "В карточке счета рядом с действиями отправки, печати и скачивания появилась кнопка «Я.Диск». Она формирует PDF счета и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск |
| `backend/AppReleases/releases.json` | 1209 | `PDF` | "summary": "В карточке договора появилась ручная отправка PDF договора на Яндекс.Диск по настроенным правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 1215 | `PDF` | "text": "В карточке договора рядом с действиями Word, PDF и Email появилась кнопка «Я.Диск». Она формирует PDF договора и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск»." |
| `backend/AppReleases/releases.json` | 1368 | `PDF` | "text": "Акт сверки теперь печатается в фиксированной альбомной форме A4 с размерами колонок, полями, итогами и блоками подписей, приближенными к приложенному бухгалтерскому образцу. Для Excel сохранена структура эталонн |
| `backend/AppReleases/releases.json` | 1395 | `PDF` | "text": "Кнопка Excel выгружает акт сверки в формате .xls с той же бухгалтерской структурой, а кнопка PDF открывает чистую печатную форму без лишних элементов интерфейса." |
| `backend/AppReleases/releases.json` | 2131 | `ICS` | "text": "ICS-выгрузка отдельно отмечена как доступный безопасный контур, а Google Calendar и Яндекс Календарь остаются в подготовительном режиме до закрытия обязательных пунктов." |
| `backend/AppReleases/releases.json` | 2144 | `ICS` | "summary": "В настройках интеграций появился блок состояния календарных провайдеров: видно, что ICS-выгрузка уже доступна, а Google Calendar и Яндекс Календарь ждут отдельной настройки и приемки.", |
| `backend/AppReleases/releases.json` | 2150 | `ICS` | "text": "В разделе «Ключи и внешние сервисы» добавлен блок «Провайдеры календарной синхронизации». Он показывает доступность ICS, Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 2167 | `ICS` | "summary": "Выгрузки календаря задач и календаря дел в ICS теперь фиксируются в общем журнале действий, чтобы администраторы могли видеть, когда и кем был сформирован файл календаря.", |
| `backend/AppReleases/releases.json` | 2173 | `ICS` | "text": "В журнале всех действий появился тип «Синхронизация календаря». В нем отражаются выгрузки ICS-файлов из журнала задач и календаря дел." |
| `backend/AppReleases/releases.json` | 2189 | `ICS` | "title": "Настройка напоминаний в ICS-календарях", |
| `backend/AppReleases/releases.json` | 2190 | `ICS` | "summary": "В журнале задач можно выбрать, за сколько времени напоминать о событиях при выгрузке календаря задач и календаря дел в ICS.", |
| `backend/AppReleases/releases.json` | 2196 | `ICS` | "text": "Перед выгрузкой ICS в журнале задач теперь можно выбрать набор напоминаний: стандартный вариант за 1 час и 15 минут, только за 1 час, за 2 часа и 30 минут или за 1 день." |
| `backend/AppReleases/releases.json` | 2204 | `ICS` | "text": "Если напоминания не выбраны явно, ICS-файлы продолжают выгружаться со стандартными уведомлениями за 1 час и за 15 минут до события." |
| `backend/AppReleases/releases.json` | 2373 | `PDF` | "title": "Excel и PDF выгрузки юридических отчетов", |
| `backend/AppReleases/releases.json` | 2374 | `CSV` | "summary": "В разделе «Отчеты» расширены выгрузки управленческих юридических отчетов: теперь ключевые отчеты по делам можно сохранять не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 2374 | `PDF` | "summary": "В разделе «Отчеты» расширены выгрузки управленческих юридических отчетов: теперь ключевые отчеты по делам можно сохранять не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 2380 | `PDF` | "text": "Для финансового отчета по делам, статистики успешности, текущих дел, загрузки юристов, просроченных процессуальных сроков и диагностики активности добавлены кнопки Excel и PDF." |
| `backend/AppReleases/releases.json` | 2388 | `PDF` | "text": "PDF и Excel показывают сводные показатели и детальные строки отчета, чтобы руководитель мог передать файл дальше без ручной сборки данных из нескольких блоков CRM." |
| `backend/AppReleases/releases.json` | 2411 | `CSV` | "text": "Отчет учитывает доступ пользователя к делам, группирует статистику по ответственным, типам и статусам, а строки можно выгрузить в CSV." |
| `backend/AppReleases/releases.json` | 2430 | `CSV` | "text": "Отчет учитывает доступ пользователя к делам и платежам, группирует суммы по ответственным и статусам дел, а строки отчета можно выгрузить в CSV." |
| `backend/AppReleases/releases.json` | 2724 | `ICS` | "summary": "ICS-файл журнала задач теперь передает во внешние календари напоминания перед началом задачи.", |
| `backend/AppReleases/releases.json` | 2730 | `ICS` | "text": "При выгрузке календаря задач в ICS в каждую задачу добавляются напоминания за 1 час и за 15 минут до срока выполнения." |
| `backend/AppReleases/releases.json` | 2743 | `ICS` | "summary": "ICS-файл календаря дел теперь передает во внешние календари напоминания по событиям с точным временем.", |
| `backend/AppReleases/releases.json` | 2749 | `ICS` | "text": "При выгрузке календаря дел в ICS для судебных заседаний, задач и других событий с указанным временем добавляются напоминания за 1 час и за 15 минут до начала." |
| `backend/AppReleases/releases.json` | 2784 | `PDF` | "title": "Выгрузка реестра дел в Excel и PDF", |
| `backend/AppReleases/releases.json` | 2785 | `CSV` | "summary": "В разделе «Дела» расширены выгрузки реестра: теперь текущую выборку можно скачать не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 2785 | `PDF` | "summary": "В разделе «Дела» расширены выгрузки реестра: теперь текущую выборку можно скачать не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 2791 | `CSV` | "text": "В реестре дел рядом с кнопкой CSV появились кнопки Excel и PDF. Они формируют файл по текущим фильтрам, сортировке и выбранному режиму реестра." |
| `backend/AppReleases/releases.json` | 2791 | `PDF` | "text": "В реестре дел рядом с кнопкой CSV появились кнопки Excel и PDF. Они формируют файл по текущим фильтрам, сортировке и выбранному режиму реестра." |
| `backend/AppReleases/releases.json` | 2795 | `PDF` | "text": "Excel-файл содержит основные данные по делам, задачам, финансам, суду и активности, а PDF использует печатную форму реестра с итогами по выборке." |
| `backend/AppReleases/releases.json` | 2807 | `ICS` | "title": "Выгрузка календаря дел в ICS", |
| `backend/AppReleases/releases.json` | 2808 | `ICS` | "summary": "В календаре дел появилась выгрузка судебных событий и процессуальных сроков в файл ICS для внешних календарей.", |
| `backend/AppReleases/releases.json` | 2814 | `ICS` | "text": "В блоке «Календарь дел» добавлена кнопка ICS. Она формирует файл с судебными заседаниями, процессуальными сроками и другими событиями дел за текущий период календаря." |
| `backend/AppReleases/releases.json` | 2830 | `PDF` | "title": "Выгрузка юридического отчета в Excel и PDF", |
| `backend/AppReleases/releases.json` | 2837 | `CSV` | "text": "В отчете «Юридическая работа» появились серверные выгрузки CSV, Excel и PDF. Кнопки доступны в шапке отчета рядом с обновлением данных." |
| `backend/AppReleases/releases.json` | 2837 | `PDF` | "text": "В отчете «Юридическая работа» появились серверные выгрузки CSV, Excel и PDF. Кнопки доступны в шапке отчета рядом с обновлением данных." |
| `backend/AppReleases/releases.json` | 2919 | `PDF` | "summary": "В журнале задач появился отчет «Планинг по клиентам», который можно выгрузить в Word, PDF или HTML для передачи и печати.", |
| `backend/AppReleases/releases.json` | 2925 | `PDF` | "text": "В «Журнале задач» добавлены кнопки Word, PDF и HTML. Они формируют отчет «Планинг по клиентам» с группировкой задач по клиенту и делу." |
| `backend/AppReleases/releases.json` | 2929 | `PDF` | "text": "В отчете выводятся номер строки, вид задачи, описание, срок, статус, исполнитель и комментарий. Формат подходит для просмотра в браузере, печати, отправки в PDF и открытия в Word." |
| `backend/AppReleases/releases.json` | 3369 | `CSV` | "text": "Номер связанного акта также попадает в выгрузки журнала в CSV, Excel и печатную форму PDF." |
| `backend/AppReleases/releases.json` | 3369 | `PDF` | "text": "Номер связанного акта также попадает в выгрузки журнала в CSV, Excel и печатную форму PDF." |
| `backend/AppReleases/releases.json` | 3521 | `PDF` | "text": "В карточке расторгнутого договора появилась кнопка «Соглашение PDF». Она формирует соглашение о расторжении с данными сторон, датой расторжения, причиной, условиями и финансовыми условиями из карточки договора." |
| `backend/AppReleases/releases.json` | 3548 | `PDF` | "text": "Печать договора, скачивание в Word или PDF и отправка договора по email теперь используют активный шаблон из настроек, поэтому оформление можно поддерживать в одном месте." |
| `backend/AppReleases/releases.json` | 3605 | `PDF` | "text": "В карточке договора появились действия «Печать», «Word», «PDF» и «Email». Теперь договор можно открыть в печатном виде, скачать или отправить клиенту прямо из карточки." |
| `backend/AppReleases/releases.json` | 3636 | `CSV` | "text": "Информация о постановщике также добавлена в карточки доски, подсказки календаря и выгрузку журнала задач в CSV." |
| `backend/Controllers/ActsController.cs` | 1395 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/CashOrdersController.cs` | 867 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/CashOrdersController.cs` | 632 | `DownloadPdf` | public async Task<IActionResult> DownloadPdf(int id, CancellationToken ct) |
| `backend/Controllers/ContactsController.cs` | 385 | `ImportYealinkXml` | public async Task<ActionResult<YealinkContactImportResultDto>> ImportYealinkXml( |
| `backend/Controllers/ContactsController.cs` | 337 | `DownloadYealinkXml` | public async Task<IActionResult> DownloadYealinkXml(CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 931 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/DatabaseAdminController.cs` | 200 | `ImportSqliteAsync` | public async Task<ActionResult<DatabaseImportResultDto>> ImportSqliteAsync( |
| `backend/Controllers/DatabaseAdminController.cs` | 218 | `ImportSqliteAsync` | var result = await _databaseImportService.ImportSqliteAsync(tempPath, cancellationToken); |
| `backend/Controllers/DatabaseAdminController.cs` | 92 | `DownloadPath` | DownloadPath = result.DownloadPath, |
| `backend/Controllers/DatabaseAdminController.cs` | 181 | `DownloadBackupAsync` | public IActionResult DownloadBackupAsync([FromQuery] string? relativePath) |
| `backend/Controllers/InvoicesController.cs` | 2658 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/MoneyOperationsController.cs` | 2129 | `CSV` | return BadRequest("Поддерживаются файлы CSV, TSV, TXT и 1C ClientBankExchange."); |
| `backend/Controllers/MoneyOperationsController.cs` | 3297 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/MoneyOperationsController.cs` | 406 | `ExportCsv` | public async Task<IActionResult> ExportCsv( |
| `backend/Controllers/MoneyOperationsController.cs` | 456 | `ExportExcel` | public async Task<IActionResult> ExportExcel( |
| `backend/Controllers/MoneyOperationsController.cs` | 550 | `ExportPdf` | public async Task<IActionResult> ExportPdf( |
| `backend/Controllers/MoneyOperationsController.cs` | 2115 | `ImportBankStatement` | public async Task<ActionResult<BankStatementImportResultDto>> ImportBankStatement(IFormFile? file, CancellationToken ct) |
| `backend/Controllers/MoneyOperationsController.cs` | 2142 | `ImportBankStatementItemsAsync` | return Ok(await ImportBankStatementItemsAsync( |
| `backend/Controllers/MoneyOperationsController.cs` | 2230 | `ImportBankStatementItemsAsync` | return Ok(await ImportBankStatementItemsAsync(importItems, result, file.FileName, statementBytes.Length, fileHash, manualCategoryRules, ct)); |
| `backend/Controllers/MoneyOperationsController.cs` | 2295 | `ImportBankStatement` | var action = await ImportBankStatement(formFile, ct); |
| `backend/Controllers/MoneyOperationsController.cs` | 2560 | `ImportBankStatementItemsAsync` | return Ok(await ImportBankStatementItemsAsync( |
| `backend/Controllers/MoneyOperationsController.cs` | 3374 | `ImportBankStatementItemsAsync` | private async Task<BankStatementImportResultDto> ImportBankStatementItemsAsync( |
| `backend/Controllers/MoneyOperationsController.cs` | 3794 | `ImportKey` | ImportKey = importKey, |
| `backend/Controllers/ReportsController.cs` | 188 | `PDF` | return Problem("Сервис формирования PDF недоступен."); |
| `backend/Controllers/ReportsController.cs` | 266 | `PDF` | return Problem("Сервис формирования PDF недоступен."); |
| `backend/Controllers/ReportsController.cs` | 138 | `ExportLegalWorkCsv` | public async Task<IActionResult> ExportLegalWorkCsv( |
| `backend/Controllers/ReportsController.cs` | 156 | `ExportLegalWorkExcel` | public async Task<IActionResult> ExportLegalWorkExcel( |
| `backend/Controllers/ReportsController.cs` | 178 | `ExportLegalWorkPdf` | public async Task<IActionResult> ExportLegalWorkPdf( |
| `backend/Controllers/ReportsController.cs` | 202 | `ExportManagementReportExcel` | public async Task<IActionResult> ExportManagementReportExcel( |
| `backend/Controllers/ReportsController.cs` | 247 | `ExportManagementReportPdf` | public async Task<IActionResult> ExportManagementReportPdf( |
| `backend/Controllers/RolePermissionsController.cs` | 64 | `ExportAuditCsv` | public async Task<IActionResult> ExportAuditCsv(CancellationToken ct) |
| `backend/Controllers/S3StorageController.cs` | 169 | `Download` | public async Task<IActionResult> Download( |
| `backend/Controllers/S3StorageController.cs` | 179 | `DownloadAsync` | var file = await _service.DownloadAsync(id, path, ct); |
| `backend/Controllers/S3StorageController.cs` | 181 | `Downloaded` | "Downloaded", |
| `backend/Controllers/TasksController.cs` | 480 | `ICS` | $"Выгружен ICS-календарь задач: {tasks.Count} событий.", |
| `backend/Controllers/TasksController.cs` | 559 | `PDF` | return Problem("Сервис формирования PDF недоступен."); |
| `backend/Controllers/TasksController.cs` | 426 | `ExportCalendar` | public async Task<IActionResult> ExportCalendar( |
| `backend/Controllers/TasksController.cs` | 497 | `ExportPlanningReport` | public async Task<IActionResult> ExportPlanningReport( |
| `backend/Controllers/TasksController.cs` | 1297 | `DownloadFile` | public async Task<IActionResult> DownloadFile(int id, int fileId, CancellationToken ct) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 431 | `PDF` | return Problem("Сервис формирования PDF недоступен."); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1802 | `ICS` | $"Выгружен ICS-календарь дел: {events.Count} событий.", |
| `backend/Controllers/V2/CasesV2Controller.cs` | 289 | `ExportRegistryCsv` | public async Task<IActionResult> ExportRegistryCsv( |
| `backend/Controllers/V2/CasesV2Controller.cs` | 338 | `ExportRegistryExcel` | public async Task<IActionResult> ExportRegistryExcel( |
| `backend/Controllers/V2/CasesV2Controller.cs` | 387 | `ExportRegistryPdf` | public async Task<IActionResult> ExportRegistryPdf( |
| `backend/Controllers/V2/CasesV2Controller.cs` | 1765 | `ExportCaseCalendarIcs` | public async Task<IActionResult> ExportCaseCalendarIcs( |
| `backend/Controllers/V2/CasesV2Controller.cs` | 3732 | `DownloadDocument` | public async Task<IActionResult> DownloadDocument(int id, int documentId, CancellationToken ct) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 3761 | `Downloaded` | AddCaseDocumentAuditEvent(clientCase, document, "Downloaded", $"Документ «{document.OriginalFileName}» скачан."); |
| `backend/Controllers/V2/CasesV2Controller.cs` | 3769 | `DownloadDocumentById` | public async Task<IActionResult> DownloadDocumentById(int documentId, CancellationToken ct) |
| `backend/Controllers/V2/CasesV2Controller.cs` | 3798 | `Downloaded` | AddCaseDocumentAuditEvent(clientCase, document, "Downloaded", $"Документ «{document.OriginalFileName}» скачан."); |
| `backend/Controllers/YandexDiskController.cs` | 485 | `Download` | public async Task<IActionResult> Download([FromQuery] string? path, CancellationToken ct) |
| `backend/Controllers/YandexDiskController.cs` | 492 | `DownloadAsync` | var file = await _service.DownloadAsync(path, ct); |
| `backend/Controllers/YandexDiskController.cs` | 494 | `Downloaded` | "Downloaded", |
| `backend/Controllers/YandexDiskController.cs` | 507 | `Download` | public async Task<IActionResult> Download(string id, [FromQuery] string? path, CancellationToken ct) |
| `backend/Controllers/YandexDiskController.cs` | 514 | `DownloadAsync` | var file = await _service.DownloadAsync(id, path, ct); |
| `backend/Controllers/YandexDiskController.cs` | 516 | `Downloaded` | "Downloaded", |
| `backend/Data/PaymentContext.cs` | 953 | `ImportKey` | entity.Property(e => e.ImportKey).HasMaxLength(256).IsRequired(); |
| `backend/Data/PaymentContext.cs` | 970 | `ImportKey` | entity.HasIndex(e => e.ImportKey).HasDatabaseName("IX_BankStatementImportRows_ImportKey"); |
| `backend/Migrations/20260429112034_AddBankStatementImportBatches.cs` | 52 | `ImportKey` | ImportKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false), |
| `backend/Migrations/20260429112034_AddBankStatementImportBatches.cs` | 111 | `ImportKey` | column: "ImportKey"); |
| `backend/Migrations/20260429112034_AddBankStatementImportBatches.Designer.cs` | 499 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260429112034_AddBankStatementImportBatches.Designer.cs` | 535 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260430032712_AddMoneyOperationAccountingTypeIndex.Designer.cs` | 499 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260430032712_AddMoneyOperationAccountingTypeIndex.Designer.cs` | 535 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260430045437_AddMoneyAccounts.Designer.cs` | 499 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260430045437_AddMoneyAccounts.Designer.cs` | 535 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260430073757_BackfillMoneyAccountsFromOperations.Designer.cs` | 499 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260430073757_BackfillMoneyAccountsFromOperations.Designer.cs` | 535 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260430102400_LinkMoneyOperationsToIncomeTypes.Designer.cs` | 499 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260430102400_LinkMoneyOperationsToIncomeTypes.Designer.cs` | 535 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260502114208_AddDocumentWorkflowStatuses.Designer.cs` | 509 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260502114208_AddDocumentWorkflowStatuses.Designer.cs` | 545 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260502132119_AddDocumentSigningWorkflow.Designer.cs` | 515 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260502132119_AddDocumentSigningWorkflow.Designer.cs` | 551 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260502133353_AddInvoiceCancellationFields.Designer.cs` | 515 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260502133353_AddInvoiceCancellationFields.Designer.cs` | 551 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260502140322_AddActCancellationFields.Designer.cs` | 525 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260502140322_AddActCancellationFields.Designer.cs` | 561 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260502195416_AddMoneyBalanceReconciliationResults.Designer.cs` | 525 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260502195416_AddMoneyBalanceReconciliationResults.Designer.cs` | 561 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260504025059_SyncSqliteModelSnapshot.Designer.cs` | 525 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260504025059_SyncSqliteModelSnapshot.Designer.cs` | 561 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260504110401_AddTaskJournal.Designer.cs` | 525 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260504110401_AddTaskJournal.Designer.cs` | 561 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260504190534_AddLegalEntityDocumentOwners.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260504190534_AddLegalEntityDocumentOwners.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260505033313_AddLegalEntityBalanceTransactions.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260505033313_AddLegalEntityBalanceTransactions.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260505080330_AddLegalEntityContracts.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260505080330_AddLegalEntityContracts.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260505204500_AddNotificationDeliveries.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260505204500_AddNotificationDeliveries.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260505211044_AddNotificationSenders.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260505211044_AddNotificationSenders.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260506035335_AddNotificationBotSubscriptions.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260506035335_AddNotificationBotSubscriptions.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260507105933_AddPaymentRemainderTransfers.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260507105933_AddPaymentRemainderTransfers.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260508074146_AddTaskClientLinks.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260508074146_AddTaskClientLinks.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260508101150_AddTaskJournalParticipants.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260508101150_AddTaskJournalParticipants.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260508143232_AddTaskJournalComments.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260508143232_AddTaskJournalComments.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260508145720_AddTaskJournalFiles.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260508145720_AddTaskJournalFiles.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260512055031_AddCounterpartyDualRole.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260512055031_AddCounterpartyDualRole.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260512071704_AddTaskLegalEntityLinks.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260512071704_AddTaskLegalEntityLinks.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260512180049_AddFinancialActionReasonSetting.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260512180049_AddFinancialActionReasonSetting.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260512185946_AddDocumentStatusDictionary.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260512185946_AddDocumentStatusDictionary.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260513184545_AddDepartmentDictionary.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260513184545_AddDepartmentDictionary.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260515151144_ExpandServiceDictionaryNameLength.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260515151144_ExpandServiceDictionaryNameLength.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260515172858_ExpandServiceDictionaryTextFields.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260515172858_ExpandServiceDictionaryTextFields.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260515185151_AddUserNotifications.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260515185151_AddUserNotifications.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260517173021_AddClientCaseRegistryFields.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260517173021_AddClientCaseRegistryFields.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260517190821_AddClientCaseDetailFields.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260517190821_AddClientCaseDetailFields.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260517194648_AddClientCaseDocuments.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260517194648_AddClientCaseDocuments.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260517231152_AddClientCaseActivityFields.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260517231152_AddClientCaseActivityFields.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260518002320_AddClientCaseRelatedParties.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260518002320_AddClientCaseRelatedParties.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260518012055_AddClientCaseProceduralDeadlines.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260518012055_AddClientCaseProceduralDeadlines.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260519062422_AddTaskApprovalDueAt.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260519062422_AddTaskApprovalDueAt.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260519122501_AddClientCaseHiddenAccessFields.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260519122501_AddClientCaseHiddenAccessFields.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260520071250_AddClientCaseWorkOrders.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260520071250_AddClientCaseWorkOrders.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260520083111_AddClientCaseWorkOrderApproval.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260520083111_AddClientCaseWorkOrderApproval.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260520085851_AddClientCaseRelatedPartyArchive.Designer.cs` | 531 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260520085851_AddClientCaseRelatedPartyArchive.Designer.cs` | 567 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260524093232_AddClientCaseCourtInstances.Designer.cs` | 665 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260524093232_AddClientCaseCourtInstances.Designer.cs` | 701 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260525074120_AddTaskContractorPaymentResultDelay.Designer.cs` | 665 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260525074120_AddTaskContractorPaymentResultDelay.Designer.cs` | 701 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260525092333_AddUserPresenceFields.Designer.cs` | 665 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260525092333_AddUserPresenceFields.Designer.cs` | 701 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260529064341_ExpandActCommentLength.Designer.cs` | 664 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260529064341_ExpandActCommentLength.Designer.cs` | 700 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260601144520_AddGoogleCalendarSync.Designer.cs` | 664 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260601144520_AddGoogleCalendarSync.Designer.cs` | 700 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/20260602041228_AddYandexCalendarSync.Designer.cs` | 664 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/20260602041228_AddYandexCalendarSync.Designer.cs` | 700 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PaymentContextModelSnapshot.cs` | 661 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PaymentContextModelSnapshot.cs` | 697 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260429112034_AddBankStatementImportBatches.cs` | 53 | `ImportKey` | ImportKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false), |
| `backend/Migrations/PostgreSql/20260429112034_AddBankStatementImportBatches.cs` | 112 | `ImportKey` | column: "ImportKey"); |
| `backend/Migrations/PostgreSql/20260429112034_AddBankStatementImportBatches.Designer.cs` | 516 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260429112034_AddBankStatementImportBatches.Designer.cs` | 552 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260430032746_AddMoneyOperationAccountingTypeIndex.Designer.cs` | 516 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260430032746_AddMoneyOperationAccountingTypeIndex.Designer.cs` | 552 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260430045509_AddMoneyAccounts.Designer.cs` | 516 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260430045509_AddMoneyAccounts.Designer.cs` | 552 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260430073824_BackfillMoneyAccountsFromOperations.Designer.cs` | 516 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260430073824_BackfillMoneyAccountsFromOperations.Designer.cs` | 552 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260430102735_LinkMoneyOperationsToIncomeTypesPostgres.Designer.cs` | 516 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260430102735_LinkMoneyOperationsToIncomeTypesPostgres.Designer.cs` | 552 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260502114300_AddDocumentWorkflowStatusesPostgres.Designer.cs` | 526 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260502114300_AddDocumentWorkflowStatusesPostgres.Designer.cs` | 562 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260502132156_AddDocumentSigningWorkflowPostgres.Designer.cs` | 532 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260502132156_AddDocumentSigningWorkflowPostgres.Designer.cs` | 568 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260502133417_AddInvoiceCancellationFieldsPostgres.Designer.cs` | 532 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260502133417_AddInvoiceCancellationFieldsPostgres.Designer.cs` | 568 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260502140343_AddActCancellationFieldsPostgres.Designer.cs` | 542 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260502140343_AddActCancellationFieldsPostgres.Designer.cs` | 578 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260502195448_AddMoneyBalanceReconciliationResultsPostgres.Designer.cs` | 542 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260502195448_AddMoneyBalanceReconciliationResultsPostgres.Designer.cs` | 578 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260504024859_SyncPostgreSqlModelSnapshot.Designer.cs` | 542 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260504024859_SyncPostgreSqlModelSnapshot.Designer.cs` | 578 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260504094814_RepairPostgreSqlMoneySchema.Designer.cs` | 542 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260504094814_RepairPostgreSqlMoneySchema.Designer.cs` | 578 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260504110423_AddTaskJournalPostgres.Designer.cs` | 542 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260504110423_AddTaskJournalPostgres.Designer.cs` | 578 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260504190603_AddLegalEntityDocumentOwnersPostgres.Designer.cs` | 548 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260504190603_AddLegalEntityDocumentOwnersPostgres.Designer.cs` | 584 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260505033827_AddLegalEntityBalanceTransactionsPostgres.Designer.cs` | 548 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260505033827_AddLegalEntityBalanceTransactionsPostgres.Designer.cs` | 584 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260505080331_AddLegalEntityContractsPostgres.Designer.cs` | 548 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260505080331_AddLegalEntityContractsPostgres.Designer.cs` | 584 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260505204535_AddNotificationDeliveriesPostgres.Designer.cs` | 548 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260505204535_AddNotificationDeliveriesPostgres.Designer.cs` | 584 | `ImportKey` | b.HasIndex("ImportKey") |
| `backend/Migrations/PostgreSql/20260505211117_AddNotificationSendersPostgres.Designer.cs` | 548 | `ImportKey` | b.Property<string>("ImportKey") |
| `backend/Migrations/PostgreSql/20260505211117_AddNotificationSendersPostgres.Designer.cs` | 584 | `ImportKey` | b.HasIndex("ImportKey") |
| Еще 650 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Интеграции календарей

Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 45 | `Google Calendar` | "summary": "Задачи, судебные заседания и события календарей теперь обрабатываются по единому правилу: система хранит точный момент времени и показывает его в часовом поясе пользователя из профиля. Это помогает избежать с |
| `backend/AppReleases/releases.json` | 51 | `Google Calendar` | "text": "Исправлена обработка времени задач при синхронизации с Google Calendar и Яндекс Календарем. Перенос задачи во внешнем календаре теперь сохраняется в CRM как точный момент времени и не пересчитывается повторно пр |
| `backend/AppReleases/releases.json` | 122 | `Google Calendar` | "text": "В настройках внешних календарей добавлен блок «Выполненные задачи в календарях». Администратор может оставить выполненные задачи в календарях с отдельной отметкой или скрывать их после выполнения. Правило примен |
| `backend/AppReleases/releases.json` | 126 | `Google Calendar` | "text": "Исправлена выгрузка времени задач в Google Calendar и Яндекс Календарь: задачи теперь передаются по часовому поясу владельца календаря или исполнителя, а не по фиксированному времени CRM." |
| `backend/AppReleases/releases.json` | 147 | `Google Calendar` | "summary": "Администратор может задать паузу перед повторной полной синхронизацией внешних календарей. Это помогает не создавать лишнюю нагрузку на Google Calendar и Яндекс.Календарь, когда пользователь несколько раз наж |
| `backend/AppReleases/releases.json` | 157 | `Google Calendar` | "text": "В профиле пользователя кнопка ручной синхронизации Google Calendar и Яндекс.Календаря временно отключается, если полная синхронизация уже запускалась недавно. В это время обычная фоновая синхронизация продолжает |
| `backend/AppReleases/releases.json` | 166 | `Google Calendar` | "summary": "Исправлена синхронизация даты и времени задач при переносе событий во внешних календарях. Изменения, сделанные в Google Calendar, теперь корректно сохраняются в CRM и не перетираются следующим циклом обмена." |
| `backend/AppReleases/releases.json` | 172 | `Google Calendar` | "text": "При переносе задачи в Google Calendar новое время теперь корректно применяется к задаче в CRM и сохраняется при следующих циклах синхронизации." |
| `backend/AppReleases/releases.json` | 176 | `Google Calendar` | "text": "Исправлена обработка времени задач, сохраненных в базе без признака часового пояса: Google Calendar и Яндекс.Календарь больше не получают такие задачи со сдвигом времени." |
| `backend/AppReleases/releases.json` | 180 | `Google Calendar` | "text": "Если Google Calendar временно отвечает ограничением запросов, синхронизация делает повторные попытки и не показывает ошибку сразу после первого ответа Rate Limit Exceeded." |
| `backend/AppReleases/releases.json` | 195 | `Google Calendar` | "text": "В разделе настроек внешних календарей появились отдельные интервалы фоновой синхронизации для Google Calendar и Яндекс.Календаря. По умолчанию обмен выполняется каждые 10 минут, а администратор может увеличить и |
| `backend/AppReleases/releases.json` | 199 | `Google Calendar` | "text": "После сохранения интервала фоновые сервисы Google Calendar и Яндекс.Календаря применяют новые настройки без перезапуска приложения и начинают следующий цикл уже с обновленным расписанием." |
| `backend/AppReleases/releases.json` | 207 | `Google Calendar` | "text": "Для изменений, пришедших из Google Calendar или Яндекс.Календаря, журнал показывает, что именно поменялось в задаче CRM: срок, длительность, статус или название." |
| `backend/AppReleases/releases.json` | 226 | `Google Calendar` | "text": "В разделе «Аудит объектов» поля календарной синхронизации теперь отображаются по-русски: «Внешняя синхронизация календарей», «Синхронизация Google Calendar» и «Синхронизация Яндекс.Календаря»." |
| `backend/AppReleases/releases.json` | 243 | `Google Calendar` | "summary": "В подсказках настройки Google Calendar и Яндекс.Календаря теперь сразу показаны готовые Redirect URI, которые нужно внести в кабинеты OAuth.", |
| `backend/AppReleases/releases.json` | 249 | `Google Calendar` | "text": "В разделе «Настройки» → «Ключи интеграций» → блок внешних календарей вопросительные знаки рядом с Google Calendar и Яндекс.Календарем теперь показывают отдельный блок «Redirect URI для кабинета провайдера»." |
| `backend/AppReleases/releases.json` | 253 | `Google Calendar` | "text": "Администратору больше не нужно искать callback URL в профиле пользователя: адрес для Google Calendar и Яндекс.Календаря можно скопировать прямо из окна подсказки." |
| `backend/AppReleases/releases.json` | 304 | `Google Calendar` | "summary": "В настройках внешних календарей появились инструкции по подключению Google Calendar и Яндекс.Календаря, а профиль пользователя больше не показывает отключенные администратором провайдеры.", |
| `backend/AppReleases/releases.json` | 310 | `Google Calendar` | "text": "В блоке «Модель синхронизации и конфликты» вопросительные знаки рядом с Google Calendar и Яндекс.Календарем теперь открывают инструкцию по настройке OAuth-приложения и ссылки на нужные кабинеты провайдеров." |
| `backend/AppReleases/releases.json` | 314 | `Google Calendar` | "text": "Если администратор выключил Google Calendar или Яндекс.Календарь в настройках, соответствующая карточка больше не отображается в профиле пользователя." |
| `backend/AppReleases/releases.json` | 333 | `Google Calendar` | "text": "В полном журнале синхронизации появилась постраничная навигация, чтобы администратор мог просматривать не только последние события Google Calendar и Яндекс.Календаря." |
| `backend/AppReleases/releases.json` | 375 | `Google Calendar` | "text": "Исправлена ошибка загрузки статуса Google Calendar и Яндекс Календаря в профиле пользователя. Страница снова корректно показывает состояние подключения и предупреждение о настройке OAuth." |
| `backend/AppReleases/releases.json` | 383 | `Google Calendar` | "text": "Пояснение помогает администратору понять, как работают отдельные включатели Google Calendar и Яндекс Календаря, главный источник событий, правила конфликтов, дата начала синхронизации, действия при потере доступ |
| `backend/AppReleases/releases.json` | 391 | `Google Calendar` | "title": "Google Calendar и Яндекс Календарь включаются отдельно", |
| `backend/AppReleases/releases.json` | 392 | `Google Calendar` | "summary": "В настройках внешних календарей администратор теперь может включать Google Calendar и Яндекс Календарь независимо друг от друга.", |
| `backend/AppReleases/releases.json` | 398 | `Google Calendar` | "text": "В разделе внешних календарей вместо одного общего переключателя появились отдельные переключатели для Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 402 | `Google Calendar` | "text": "Карточки провайдеров и чек-лист готовности теперь учитывают выбранный календарь отдельно. Если включен только Google Calendar, выключенный Яндекс Календарь не будет блокировать подготовку подключения." |
| `backend/AppReleases/releases.json` | 406 | `Google Calendar` | "text": "Фоновые проверки запускаются только для включенных провайдеров: Google Calendar и Яндекс Календарь больше не зависят от одного общего включателя." |
| `backend/AppReleases/releases.json` | 415 | `Google Calendar` | "summary": "В настройках внешних календарей появился понятный переключатель для включения Google Calendar и Яндекс Календаря.", |
| `backend/AppReleases/releases.json` | 421 | `Google Calendar` | "text": "В разделе внешних календарей добавлен главный переключатель «Включить Google Calendar и Яндекс Календарь». Администратор может включить календарный контур прямо из настроек без изменения серверного конфига." |
| `backend/AppReleases/releases.json` | 438 | `Google Calendar` | "summary": "В настройках внешних календарей администратор теперь может задавать общие правила синхронизации Google Calendar и Яндекс Календаря, а также смотреть полный журнал событий.", |
| `backend/AppReleases/releases.json` | 448 | `Google Calendar` | "text": "Добавлена дата начала синхронизации по умолчанию. Если включен режим управления администратором, эта дата применяется к подключениям Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 456 | `Google Calendar` | "text": "В настройках добавлен полный журнал синхронизации по Google Calendar и Яндекс Календарю. Администратор видит провайдера, сотрудника, действие, статус, время и сообщение по каждому событию." |
| `backend/AppReleases/releases.json` | 465 | `Google Calendar` | "summary": "В настройках Google Calendar и Яндекс.Календаря можно указать, с какой даты задачи CRM должны попадать во внешний календарь.", |
| `backend/AppReleases/releases.json` | 471 | `Google Calendar` | "text": "В профиле пользователя в настройках Google Calendar и Яндекс.Календаря добавлено поле «Синхронизировать задачи с даты». Если указать дату, во внешний календарь будут попадать только задачи со сроком выполнения н |
| `backend/AppReleases/releases.json` | 488 | `Google Calendar` | "summary": "При отключении Google Calendar или Яндекс.Календаря CRM теперь сначала удаляет созданные PayPlanner календари и события во внешнем календаре.", |
| `backend/AppReleases/releases.json` | 494 | `Google Calendar` | "text": "В профиле пользователя отключение Google Calendar и Яндекс.Календаря теперь работает с обязательной очисткой: CRM удаляет созданные PayPlanner календари и события во внешнем календаре, а только потом убирает под |
| `backend/AppReleases/releases.json` | 507 | `Google Calendar` | "summary": "В профиле пользователя появился блок Яндекс.Календаря. Он работает по тем же правилам, что Google Calendar, и позволяет синхронизировать задачи CRM с календарем Яндекса.", |
| `backend/AppReleases/releases.json` | 517 | `Google Calendar` | "text": "Если у пользователя подключены Google Calendar и Яндекс.Календарь, изменения проходят через CRM и передаются во второй календарь без зацикливания. Например, изменение события в Google обновит задачу CRM, а затем |
| `backend/AppReleases/releases.json` | 571 | `Google Calendar` | "title": "Исправлена загрузка блока Google Calendar в профиле", |
| `backend/AppReleases/releases.json` | 572 | `Google Calendar` | "summary": "Блок Google Calendar в профиле пользователя теперь стабильно открывается и показывает состояние подключения, настройки синхронизации, список календарей и последние события.", |
| `backend/AppReleases/releases.json` | 578 | `Google Calendar` | "text": "Исправлена ошибка, из-за которой раздел Google Calendar в профиле мог не получить статус подключения и не запустить синхронизацию из-за внутренней ошибки сервиса." |
| `backend/AppReleases/releases.json` | 582 | `Google Calendar` | "text": "Добавлена дополнительная проверка, которая контролирует, что сервис Google Calendar корректно создаётся при запуске приложения." |
| `backend/AppReleases/releases.json` | 663 | `Google Calendar` | "title": "Улучшено удаление задач и календарей Google Calendar", |
| `backend/AppReleases/releases.json` | 664 | `Google Calendar` | "summary": "Синхронизация Google Calendar теперь аккуратнее очищает внешние события и календари, чтобы после удаления задач или сотрудников в Google не оставались лишние записи PayPlanner.", |
| `backend/AppReleases/releases.json` | 670 | `Google Calendar` | "text": "При удалении задачи CRM связанное событие PayPlanner удаляется из Google Calendar. Если событие не удалось удалить, задача не удаляется сразу, а пользователь видит ошибку и может проверить журнал синхронизации." |
| `backend/AppReleases/releases.json` | 678 | `Google Calendar` | "text": "Журнал Google Calendar теперь фиксирует удаление событий и календарей, а также ошибки очистки, чтобы администратор видел, что именно произошло при удалении задачи или пользователя." |
| `backend/AppReleases/releases.json` | 682 | `Google Calendar` | "text": "Права на удаление задач по-прежнему управляются ролями в разделе задач. Пользователь без права удаления не сможет удалить задачу и, соответственно, связанное событие в Google Calendar." |
| `backend/AppReleases/releases.json` | 793 | `Google Calendar` | "title": "Добавлена синхронизация задач с Google Calendar", |
| `backend/AppReleases/releases.json` | 794 | `Google Calendar` | "summary": "Пользователи могут подключить личный Google Calendar в профиле и синхронизировать с ним задачи CRM. Для администратора поддержан отдельный режим, при котором задачи раскладываются по календарям сотрудников.", |
| `backend/AppReleases/releases.json` | 800 | `Google Calendar` | "text": "В профиле появился блок Google Calendar. Пользователь может подключить календарь через Google, включить или остановить синхронизацию, запустить обмен вручную и посмотреть последние события синхронизации." |
| `backend/AppReleases/releases.json` | 804 | `Google Calendar` | "text": "Задачи, назначенные пользователю, создаются и обновляются в Google Calendar. Изменения времени, длительности, названия и статуса события могут возвращаться обратно в CRM по выбранному правилу конфликтов." |
| `backend/AppReleases/releases.json` | 808 | `Google Calendar` | "text": "Для администратора доступен режим отдельных календарей по исполнителям: при синхронизации все задачи CRM попадают в Google Calendar аккуратно, с разделением по сотрудникам." |
| `backend/AppReleases/releases.json` | 1937 | `Google Calendar` | "summary": "Администратор теперь видит отдельный фоновый сервис для подготовки синхронизации Google Calendar и Яндекс Календаря. Сервис проверяет готовность подключения и события-кандидаты, не меняя внешние календари до  |
| `backend/AppReleases/releases.json` | 1951 | `Google Calendar` | "text": "Google Calendar и Яндекс Календарь по-прежнему не включаются автоматически. До настройки OAuth и приемки модели конфликтов сервис работает как диагностический и подготовительный контур." |
| `backend/AppReleases/releases.json` | 2075 | `Google Calendar` | "summary": "Изменения правил подключения Google Calendar и Яндекс Календаря теперь фиксируются в общем журнале действий. Администраторы могут увидеть, кто изменил модель синхронизации, правило конфликтов, ручное подтверж |
| `backend/AppReleases/releases.json` | 2108 | `Google Calendar` | "text": "Чек-лист готовности теперь учитывает сохраненные правила конфликтов и отметку о dry-run на копии базы. После сохранения этих пунктов видно, какие обязательные шаги еще остаются перед подключением Google Calendar |
| `backend/AppReleases/releases.json` | 2121 | `Google Calendar` | "summary": "В настройках интеграций появился чек-лист готовности к подключению Google Calendar и Яндекс Календаря. Он показывает, какие обязательные шаги уже закрыты, а что еще нужно согласовать перед запуском внешней си |
| `backend/AppReleases/releases.json` | 2131 | `Google Calendar` | "text": "ICS-выгрузка отдельно отмечена как доступный безопасный контур, а Google Calendar и Яндекс Календарь остаются в подготовительном режиме до закрытия обязательных пунктов." |
| `backend/AppReleases/releases.json` | 2144 | `Google Calendar` | "summary": "В настройках интеграций появился блок состояния календарных провайдеров: видно, что ICS-выгрузка уже доступна, а Google Calendar и Яндекс Календарь ждут отдельной настройки и приемки.", |
| `backend/AppReleases/releases.json` | 2150 | `Google Calendar` | "text": "В разделе «Ключи и внешние сервисы» добавлен блок «Провайдеры календарной синхронизации». Он показывает доступность ICS, Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 2158 | `Google Calendar` | "text": "Google Calendar и Яндекс Календарь не запускаются автоматически: до настройки OAuth и утверждения модели конфликтов они отображаются как недоступные или требующие настройки." |
| `backend/Controllers/CalendarSyncController.cs` | 139 | `Google Calendar` | ProviderLabel = "Google Calendar", |
| `backend/Controllers/SystemJobsController.cs` | 598 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:Enabled", false)); |
| `backend/Controllers/SystemJobsController.cs` | 601 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", CalendarSyncSchedulerService.DefaultIntervalMinutes))); |
| `backend/Controllers/SystemJobsController.cs` | 604 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:BatchSize", CalendarSyncSchedulerService.DefaultBatchSize))); |
| `backend/Controllers/SystemJobsController.cs` | 607 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", CalendarSyncSchedulerService.DefaultLookAheadDays))); |
| `backend/Controllers/TasksController.cs` | 2171 | `ExternalCalendar` | Console.Error.WriteLine($"[ExternalCalendar] Task sync failed for task {taskId}: {ex.Message}"); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 467 | `Google Calendar` | Assert.Equal("Google Calendar", item.ProviderLabel); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 470 | `Google Calendar` | Assert.Equal("CRM → Google Calendar", item.DirectionLabel); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 507 | `Google Calendar` | Message = "Календарь «PayPlanner - Иван Иванов» удален из Google Calendar.", |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 715 | `GoogleCalendar` | var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1"); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 106 | `Google Calendar` | Assert.Contains("Google Calendar отключен администратором", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 317 | `Google Calendar` | Assert.Contains("Повторную полную синхронизацию Google Calendar можно запустить после", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 472 | `Google Calendar` | var task = await CreateTaskAsync(db, user.Id, "Отключить Google Calendar"); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 515 | `Google Calendar` | Assert.Contains("Не удалось отключить Google Calendar", error.Message); |
| `backend/PayPlanner.Api.Tests/YandexCalendarSyncServiceTests.cs` | 322 | `YandexCalendar` | var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1"); |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 133 | `Google Calendar` | Label = "Google Calendar", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 143 | `Google Calendar` | Message = BuildProviderMessage("Google Calendar", googleCalendarSyncEnabled), |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 199 | `Google Calendar` | Label = "Google Calendar в админке", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 205 | `Google Calendar` | ? "Google Calendar включен администратором для диагностики и подготовки подключения." |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 206 | `Google Calendar` | : "Google Calendar выключен в админских настройках и не блокирует подключение других провайдеров.", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 223 | `Google Calendar` | Label = "Google Calendar OAuth", |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 229 | `Google Calendar` | ? "Google Calendar включен, но перед подключением нужно утвердить OAuth-приложение, redirect URI и набор разрешений." |
| `backend/Services/CalendarSyncProviderRegistry.cs` | 230 | `Google Calendar` | : "Проверка OAuth Google Calendar пропущена, пока провайдер выключен в админке.", |
| `backend/Services/CalendarSyncSchedulerService.cs` | 292 | `CalendarSync` | ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("CalendarSync:Scheduler:Enabled", false)), |
| `backend/Services/CalendarSyncSchedulerService.cs` | 293 | `CalendarSync` | NormalizeInterval(ParseInt(settings.GetValueOrDefault(IntervalMinutesSettingKey), _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", DefaultIntervalMinutes))), |
| `backend/Services/CalendarSyncSchedulerService.cs` | 294 | `CalendarSync` | NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("CalendarSync:Scheduler:BatchSize", DefaultBatchSize))), |
| `backend/Services/CalendarSyncSchedulerService.cs` | 295 | `CalendarSync` | NormalizeLookAheadDays(ParseInt(settings.GetValueOrDefault(LookAheadDaysSettingKey), _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", DefaultLookAheadDays)))); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 35 | `Google Calendar` | "Google Calendar", |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 69 | `Google Calendar` | failures.Add($"Google Calendar: {ex.Message}"); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 70 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar task deletion failed for task {TaskId}", taskId); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 111 | `Google Calendar` | failures.Add($"Google Calendar: {ex.Message}"); |
| `backend/Services/ExternalCalendarSyncCoordinator.cs` | 112 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar cleanup failed for deleted user {UserId}", userId); |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 56 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar background sync failed."); |
| `backend/Services/GoogleCalendarBackgroundSyncService.cs` | 113 | `Google Calendar` | _logger.LogWarning("Stored calendar sync settings are invalid. Google Calendar background sync will use feature flag fallback."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 65 | `GoogleCalendar` | _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1"); |
| `backend/Services/GoogleCalendarSyncService.cs` | 87 | `Google Calendar` | throw new InvalidOperationException("Google Calendar отключен администратором."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 136 | `Google Calendar` | throw new KeyNotFoundException("Запрос подключения Google Calendar не найден."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 173 | `Google Calendar` | return BuildPopupHtml(false, "Запрос подключения Google Calendar не найден."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 221 | `Google Calendar` | return BuildPopupHtml(true, "Google Calendar подключён. Вернитесь в CRM.", authRequest.ReturnUrl); |
| `backend/Services/GoogleCalendarSyncService.cs` | 243 | `Google Calendar` | throw new KeyNotFoundException("Подключение Google Calendar не найдено."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 252 | `Google Calendar` | await AddLogAsync(connection.Id, null, null, "system", "settings-updated", "success", "Настройки Google Calendar сохранены.", BuildConnectionSettingsDetails(connection), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 272 | `Google Calendar` | "Подключение Google Calendar отключается пользователем.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 280 | `Google Calendar` | "Не удалось отключить Google Calendar: сначала нужно удалить созданные PayPlanner календари и события во внешнем календаре. " |
| `backend/Services/GoogleCalendarSyncService.cs` | 303 | `Google Calendar` | ? "Синхронизация Google Calendar выполнена." |
| `backend/Services/GoogleCalendarSyncService.cs` | 304 | `Google Calendar` | : "Синхронизация Google Calendar завершена с ошибками. Проверьте журнал.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 334 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar scheduled sync failed for user {UserId}", userId); |
| `backend/Services/GoogleCalendarSyncService.cs` | 356 | `Google Calendar` | $"Повторную полную синхронизацию Google Calendar можно запустить после {FormatLogDateTime(availableAt.Value)}. До этого времени работает обычная фоновая синхронизация.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 395 | `Google Calendar` | _logger.LogWarning(ex, "Google Calendar task sync failed for task {TaskId} and user {UserId}", taskId, item.UserId); |
| `backend/Services/GoogleCalendarSyncService.cs` | 432 | `Google Calendar` | ? "Событие удалено из Google Calendar после удаления задачи в CRM." |
| `backend/Services/GoogleCalendarSyncService.cs` | 433 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при удалении задачи в CRM.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 448 | `Google Calendar` | "Не удалось удалить событие из Google Calendar после удаления задачи в CRM.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 493 | `Google Calendar` | $"Сотрудник #{userId} удаляется из CRM, календарь сотрудника убирается из админского Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 518 | `Google Calendar` | throw new UnauthorizedAccessException("Google Calendar webhook не содержит channel id или token."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 527 | `Google Calendar` | throw new UnauthorizedAccessException("Google Calendar webhook не прошёл проверку канала."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 552 | `Google Calendar` | await AddLogAsync(connection.Id, null, null, "google-to-crm", "import", "error", "Не удалось загрузить изменения из Google Calendar.", ex.Message, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 579 | `Google Calendar` | await AddLogAsync(connection.Id, null, task.Id, "crm-to-google", "upsert", "error", "Не удалось синхронизировать задачу с Google Calendar.", ex.Message, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 667 | `Google Calendar` | "Не удалось получить токен Google Calendar для очистки календарей удаляемого пользователя.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 686 | `Google Calendar` | ? $"Календарь «{calendar.Name}» удален из Google Calendar." |
| `backend/Services/GoogleCalendarSyncService.cs` | 687 | `Google Calendar` | : $"Календарь «{calendar.Name}» уже отсутствовал в Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 714 | `Google Calendar` | $"Не удалось удалить календарь «{calendar.Name}» из Google Calendar.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 790 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "google-to-crm", "detach", "success", "Событие удалено в Google Calendar, связь с задачей помечена как отключённая.", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 806 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, link.TaskJournalItemId, "google-to-crm", "skip", "success", "Изменения в Google Calendar прочитаны, но правило конфликтов оставляет CRM главным источником.", null, cancellati |
| `backend/Services/GoogleCalendarSyncService.cs` | 863 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "google-to-crm", "update-task", "success", "Изменения из Google Calendar применены к задаче.", string.Join("; ", changes), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 911 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "create-event", "success", "Задача создана в Google Calendar.", BuildTaskSnapshotDetails(task), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 916 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "update-event", "success", "Задача обновлена в Google Calendar.", BuildTaskSnapshotDetails(task), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 966 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, null, "system", "create-calendar", "success", $"В Google Calendar создан календарь «{name}».", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1047 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1048 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при очистке задач раньше даты начала синхронизации.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1057 | `Google Calendar` | await AddLogAsync(connection.Id, link.CalendarSyncCalendarId, link.TaskJournalItemId, "crm-to-google", "delete-event", "error", "Не удалось удалить событие Google Calendar для задачи раньше даты начала синхронизации.", e |
| `backend/Services/GoogleCalendarSyncService.cs` | 1093 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1094 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при очистке задачи раньше даты начала синхронизации.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1123 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что в настройках включено скрытие выполненных задач." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1124 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при скрытии выполненной задачи.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1247 | `Google Calendar` | await AddLogAsync(connection.Id == 0 ? null : connection.Id, null, null, "system", "oauth-connected", "success", "Google Calendar подключён к профилю.", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1260 | `Google Calendar` | throw new InvalidOperationException("Нет refresh token для Google Calendar. Подключите календарь заново."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1447 | `Google Calendar` | throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1463 | `Google Calendar` | throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку при удалении."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1919 | `Google Calendar` | var title = success ? "Google Calendar подключён" : "Ошибка подключения Google Calendar"; |
| `backend/Services/GoogleCalendarSyncService.cs` | 2150 | `Google Calendar` | "googleWins" => "Google Calendar главный", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1351 | `calendarId` | private async Task<bool> DeleteGoogleCalendarAsync(string accessToken, string calendarId, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1353 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}", accessToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1357 | `calendarId` | private async Task<GoogleEventWriteResult> CreateGoogleEventAsync(string accessToken, string calendarId, GoogleEventPayload payload, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1359 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Post, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", accessToken, JsonSerializer.Serialize(payload, JsonOptions)); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1366 | `calendarId` | private async Task<GoogleEventWriteResult> UpdateGoogleEventAsync(string accessToken, string calendarId, string eventId, GoogleEventPayload payload, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1368 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Put, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken, JsonSerializer.Serialize(payload, JsonOptions |
| `backend/Services/GoogleCalendarSyncService.cs` | 1375 | `calendarId` | private async Task<bool> DeleteGoogleEventAsync(string accessToken, string calendarId, string eventId, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1377 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1634 | `calendarId` | int? calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 1643 | `calendarId` | var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1648 | `calendarId` | CalendarSyncCalendarId = calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 1663 | `calendarId` | int? calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 1690 | `calendarId` | if (calendarId.HasValue) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1694 | `calendarId` | .Where(item => item.Id == calendarId.Value) |
| `backend/Services/YandexCalendarSyncService.cs` | 64 | `YandexCalendar` | _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1"); |
| `backend/Services/YandexCalendarSyncService.cs` | 1625 | `calendarId` | int? calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 1634 | `calendarId` | var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken); |
| `backend/Services/YandexCalendarSyncService.cs` | 1639 | `calendarId` | CalendarSyncCalendarId = calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 1654 | `calendarId` | int? calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 1681 | `calendarId` | if (calendarId.HasValue) |
| `backend/Services/YandexCalendarSyncService.cs` | 1685 | `calendarId` | .Where(item => item.Id == calendarId.Value) |
| `docs/crm-legal-module-gap-report-2026-05-25.md` | 118 | `Google Calendar` | - диагностика календарных провайдеров в настройках интеграций: ICS активен, Google Calendar и Яндекс Календарь явно показывают статус feature flag/OAuth до запуска двусторонней синхронизации; |
| `docs/crm-legal-module-gap-report-2026-05-25.md` | 130 | `Google Calendar` | - Google Calendar и Яндекс Календарь; |
| `docs/crm-legal-module-implementation-plan.md` | 2957 | `Google Calendar` | - Google Calendar; |
| `docs/crm-legal-module-implementation-plan.md` | 3015 | `Google Calendar` | ### 9.8. Синхронизация Google Calendar |
| `docs/crm-legal-module-implementation-plan.md` | 3017 | `Google Calendar` | - [ ] Реализовать отдельный provider-адаптер Google Calendar. |
| `docs/crm-legal-module-implementation-plan.md` | 4062 | `Google Calendar` | - [~] Добавить OAuth/ключи и настройки подключения: добавлена диагностика провайдеров и чек-лист готовности в настройках интеграций; ICS активен, Google Calendar и Яндекс Календарь показывают статус feature flag/OAuth, с |
| `docs/crm-legal-module-roadmap-status.md` | 74 | `Google Calendar` | - [ ] Добавить синхронизацию с Google Calendar. |
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
| `docs/google-calendar-sync-guide.md` | 71 | `Google Calendar` | Из Google Calendar обратно в CRM могут возвращаться изменения времени, длительности, названия и статуса события. Правило обработки конфликтов задается в настройках подключения: |
| `docs/google-calendar-sync-guide.md` | 78 | `Google Calendar` | Администратор при подключении Google Calendar может синхронизировать все задачи CRM. |
| `docs/google-calendar-sync-guide.md` | 91 | `Google Calendar` | - автоматически при удалении задачи в CRM, чтобы связанное событие PayPlanner исчезло из Google Calendar; |
| `docs/google-calendar-sync-guide.md` | 92 | `Google Calendar` | - вручную из блока "Google Calendar" в профиле; |
| `docs/google-calendar-sync-guide.md` | 94 | `Google Calendar` | - при получении webhook-события Google Calendar, если для календаря зарегистрирован канал уведомлений. |
| `docs/google-calendar-sync-guide.md` | 96 | `Google Calendar` | Даже если Google Calendar временно недоступен, сохранение задачи в CRM не блокируется. Ошибка синхронизации записывается в журнал, а пользователь может повторить обмен вручную. |
| `docs/google-calendar-sync-guide.md` | 98 | `Google Calendar` | Удаление задач работает строже: если у задачи уже есть связанное событие Google Calendar и CRM не смогла удалить его через Google API, удаление задачи останавливается. Это защищает календарь от оставшихся событий, которы |
| `docs/google-calendar-sync-guide.md` | 102 | `Google Calendar` | Если пользователь удаляется из CRM, система сначала очищает созданные для него календари PayPlanner в Google Calendar: |
| `docs/google-calendar-sync-guide.md` | 105 | `Google Calendar` | - календарь сотрудника в Google Calendar администратора, если администратор синхронизировал все задачи в режиме отдельных календарей по сотрудникам. |
| `docs/google-calendar-sync-guide.md` | 107 | `Google Calendar` | Если Google Calendar недоступен или токен пользователя не позволяет удалить календарь, удаление пользователя останавливается. Администратор видит ошибку, а подробность сохраняется в журнале Google Calendar. После восстан |
| `docs/google-calendar-sync-guide.md` | 113 | `Google Calendar` | - подключения Google Calendar пользователей; |
| `docs/google-calendar-sync-guide.md` | 116 | `Google Calendar` | - связи задач CRM с событиями Google Calendar; |
| `docs/google-calendar-sync-guide.md` | 126 | `Google Calendar` | 4. Открыть профиль и подключить Google Calendar. |
| `docs/google-calendar-sync-guide.md` | 129 | `Google Calendar` | 7. Проверить, что событие появилось в Google Calendar. |
| `docs/google-calendar-sync-guide.md` | 130 | `Google Calendar` | 8. Изменить время события в Google Calendar. |
| `docs/google-calendar-sync-guide.md` | 134 | `Google Calendar` | Для проверки администратора нужно войти под администратором, подключить его Google Calendar, создать задачи на разных сотрудников и убедиться, что события разложены по отдельным календарям сотрудников. |
| `docs/google-calendar-sync-guide.md` | 138 | `Google Calendar` | Первые ошибки подключения видны в блоке "Google Calendar" в профиле пользователя. |
| `docs/google-calendar-sync-guide.md` | 144 | `Google Calendar` | - ошибки доступа к Google Calendar API; |
| `docs/google-calendar-sync-guide.md` | 150 | `Google Calendar` | Если ошибка связана с OAuth, в первую очередь нужно проверить Client ID, Client Secret, redirect URI и включение Google Calendar API в Google Cloud. |
| `docs/yandex-calendar-sync-guide.md` | 5 | `Google Calendar` | Подключение Яндекс.Календаря находится в профиле пользователя рядом с блоком Google Calendar. |
| `docs/yandex-calendar-sync-guide.md` | 47 | `Google Calendar` | Яндекс.Календарь работает по тем же правилам, что Google Calendar: |
| `docs/yandex-calendar-sync-guide.md` | 61 | `Google Calendar` | 1. Пользователь меняет событие в Google Calendar. |
| `docs/yandex-calendar-sync-guide.md` | 72 | `Google Calendar` | 4. CRM отправляет новое состояние в Google Calendar. |
| `docs/yandex-calendar-sync-guide.md` | 85 | `Google Calendar` | Для Google Calendar используется аналогичное правило "Google главный". |
| `src/components/Profile/CalendarPanels.test.tsx` | 34 | `Google Calendar` | it('hides Google Calendar card when provider is disabled by admin', async () => { |
| `src/components/Profile/CalendarPanels.test.tsx` | 46 | `Google Calendar` | await waitFor(() => expect(screen.queryByText('Google Calendar')).toBeNull()); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 65 | `Google Calendar` | .catch((error) => toast.error(extractDisplayErrorMessage(error, 'Не удалось загрузить статус Google Calendar.'))) |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 99 | `Google Calendar` | toast.success('Google Calendar подключён.'); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 103 | `Google Calendar` | toast.error(authStatus.detail \|\| 'Не удалось завершить подключение Google Calendar.'); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 107 | `Google Calendar` | toast.error(extractDisplayErrorMessage(error, 'Не удалось проверить подключение Google Calendar.')); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 112 | `Google Calendar` | toast.error(extractDisplayErrorMessage(error, 'Не удалось начать подключение Google Calendar.')); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 121 | `Google Calendar` | toast.success('Настройки Google Calendar сохранены.'); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 123 | `Google Calendar` | toast.error(extractDisplayErrorMessage(error, 'Не удалось сохранить настройки Google Calendar.')); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 136 | `Google Calendar` | toast.error(extractDisplayErrorMessage(error, 'Не удалось выполнить синхронизацию Google Calendar.')); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 144 | `Google Calendar` | title: 'Отключить Google Calendar?', |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 145 | `Google Calendar` | description: 'CRM сначала удалит созданные PayPlanner календари и события в Google Calendar. Если очистка не пройдёт, подключение останется активным.', |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 156 | `Google Calendar` | toast.success('Google Calendar отключён.'); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 158 | `Google Calendar` | toast.error(extractDisplayErrorMessage(error, 'Не удалось отключить Google Calendar.')); |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 169 | `Google Calendar` | Загружаем Google Calendar... |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 195 | `Google Calendar` | Google Calendar |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 328 | `Google Calendar` | title="Отключить Google Calendar" |
| `src/components/Profile/GoogleCalendarPanel.tsx` | 329 | `Google Calendar` | aria-label="Отключить Google Calendar" |
| `src/components/Settings/AppSettings.tsx` | 106 | `Google Calendar` | description: 'Google Calendar, Яндекс Календарь и правила синхронизации.', |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 46 | `Google Calendar` | label: 'Google Calendar', |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 77 | `Google Calendar` | label: 'Google Calendar в админке', |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 130 | `Google Calendar` | providerLabel: 'Google Calendar', |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 148 | `Google Calendar` | directionLabel: 'CRM → Google Calendar', |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 191 | `Google Calendar` | expect(screen.getAllByText('Google Calendar').length).toBeGreaterThan(0); |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 208 | `Google Calendar` | fireEvent.click(screen.getAllByLabelText(/Включить Google Calendar/i)[0]); |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 249 | `Google Calendar` | it('opens Google Calendar setup instruction from provider help', async () => { |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 253 | `Google Calendar` | fireEvent.click(screen.getByRole('button', { name: 'Что делает настройка «Включить Google Calendar»' })); |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 278 | `Google Calendar` | providerLabel: 'Google Calendar', |
| `src/components/Settings/CalendarSyncProviderSettings.test.tsx` | 296 | `Google Calendar` | directionLabel: 'CRM → Google Calendar', |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 285 | `Google Calendar` | label="Включить Google Calendar" |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 286 | `Google Calendar` | description="Google Calendar перейдет из состояния «Выключен» в подготовку подключения. После этого нужно заполнить OAuth Google и закрыть чек-лист готовности." |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 311 | `Google Calendar` | label="Google Calendar" |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 567 | `Google Calendar` | Последние события по Google Calendar и Яндекс Календарю: создание, обновление, удаление, отключение и ошибки синхронизации. |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 595 | `Google Calendar` | <option value="google-calendar">Google Calendar</option> |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 1102 | `Google Calendar` | title: 'Включить Google Calendar', |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 1103 | `Google Calendar` | description: 'Разрешает подготовку и запуск синхронизации с Google Calendar. После включения нужно настроить OAuth Google и закрыть обязательные пункты готовности.', |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 1107 | `Google Calendar` | 'Включите Google Calendar API для выбранного проекта.', |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 1115 | `Google Calendar` | { label: 'Google Calendar API: авторизация и scopes', url: 'https://developers.google.com/calendar/api/auth' }, |
| `src/components/Settings/CalendarSyncProviderSettings.tsx` | 1123 | `Google Calendar` | description: 'Разрешает подготовку и запуск синхронизации с Яндекс Календарем. Провайдер можно включить независимо от Google Calendar.', |
| `src/services/api.test.ts` | 224 | `Google Calendar` | it('loads Google Calendar status from the backend', async () => { |
| `src/services/api.test.ts` | 255 | `Google Calendar` | it('starts Google Calendar OAuth flow', async () => { |
| `src/services/api.test.ts` | 275 | `Google Calendar` | it('runs Google Calendar sync now', async () => { |
| `src/utils/objectAudit.test.ts` | 62 | `Google Calendar` | expect(getObjectAuditFieldLabel('calendar-sync', 'Google Calendar Sync Enabled')).toBe('Синхронизация Google Calendar'); |
| `src/utils/objectAudit.ts` | 309 | `Google Calendar` | GoogleCalendarSyncEnabled: 'Синхронизация Google Calendar', |

## Правила разбора

- Совпадение в отчете не всегда означает ошибку: это место, которое надо проверить при переводе на UTC + timezone ID.
- Жесткие смещения вроде `+07:00`, `UTC+7`, `Asia/Novosibirsk` и `AddHours(7)` считаются приоритетными для исправления.
- Для фильтров периода нужно отдельно проверить границы дня: начало и конец должны считаться в timezone пользователя, а храниться и сравниваться в UTC.
- Для календарей Google/Yandex/ICS нужно передавать timezone ID события или календаря, а не полагаться на timezone сервера.

_Корень проверки: `.`_
