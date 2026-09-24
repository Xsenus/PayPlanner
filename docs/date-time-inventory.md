# Инвентаризация дат и времени

Сгенерировано: 2026-09-24T05:49:10.870Z

Этот отчет собирает места, где проект работает с датами, временем, периодами, экспортами и календарными интеграциями. Его задача - помочь переводить систему на единое правило: хранение в UTC плюс явный timezone ID пользователя или организации.

## Как обновить

```powershell
npm run audit:dates
```

## Сводка

- Проверено файлов: 2220
- Найдено совпадений: 12290

| Категория | Совпадений | Что проверить |
| --- | ---: | --- |
| Backend: поля и свойства дат | 2313 | Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID. |
| Backend: текущее время сервера | 2965 | Вызовы Now/UtcNow, которые надо привести к единому источнику времени. |
| Backend: преобразования timezone | 1738 | Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги. |
| Ручные смещения времени | 1092 | Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов. |
| Frontend: работа с датами | 912 | JS/TS места, где браузер может применить локальную timezone пользователя автоматически. |
| Фильтры и периоды | 1202 | Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя. |
| Экспорты, импорты и файлы | 1732 | Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени. |
| Интеграции календарей | 336 | Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID. |

## Файлы с наибольшим количеством совпадений

| Файл | Совпадений |
| --- | ---: |
| `backend/AppReleases/releases.json` | 1362 |
| `backend/Controllers/ReportsController.cs` | 588 |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 548 |
| `backend/Controllers/V2/CasesV2Controller.cs` | 459 |
| `backend/PayPlanner.Api.Tests/TasksControllerTests.cs` | 432 |
| `backend/PayPlanner.Api.Tests/MoneyOperationsControllerTests.cs` | 303 |
| `backend/PayPlanner.Api.Tests/CasesV2ControllerTests.cs` | 294 |
| `backend/Controllers/TasksController.cs` | 238 |
| `backend/PayPlanner.Api.Tests/ReportsControllerTests.cs` | 192 |
| `backend/Services/GoogleCalendarSyncService.cs` | 190 |
| `backend/Services/Notifications/NotificationService.cs` | 168 |
| `src/components/Tasks/TaskJournal.tsx` | 165 |
| `backend/Controllers/MoneyOperationsController.cs` | 145 |
| `backend/PayPlanner.Api.Tests/PaymentsControllerTests.cs` | 142 |
| `backend/Services/YandexCalendarSyncService.cs` | 128 |
| `backend/Controllers/InvoicesController.cs` | 108 |
| `src/components/Tasks/TaskJournal.test.tsx` | 92 |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 84 |
| `backend/PayPlanner.Api.Tests/SystemJobsControllerTests.cs` | 81 |
| `src/types/index.ts` | 74 |
| `backend/Controllers/CashOrdersController.cs` | 73 |
| `backend/PayPlanner.Api.Tests/InvoicesControllerCoverageTests.cs` | 69 |
| `src/components/Money/MoneyOperations.tsx` | 68 |
| `backend/Services/DatabaseImportService.cs` | 65 |
| `src/components/Cases/CaseDetailPage.tsx` | 64 |
| `backend/Controllers/SystemJobsController.cs` | 60 |
| `backend/PayPlanner.Api.Tests/InvoicesControllerCrudTests.cs` | 60 |
| `backend/Controllers/PaymentsController.cs` | 59 |
| `backend/PayPlanner.Api.Tests/YandexCalendarSyncServiceTests.cs` | 58 |
| `backend/Controllers/V2/PaymentsV2Controller.cs` | 57 |

## Детализация

### Backend: поля и свойства дат

Места, где модель или DTO хранит дату/время и может требовать UTC + timezone ID.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 1662 | `DateTime? EffectivePlannedDate` | private static DateTime? EffectivePlannedDate(PaymentAggregateItem payment) |
| `backend/Controllers/AccountsController.cs` | 1732 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1733 | `DateTime? DueDate` | public DateTime? DueDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1734 | `DateTime? PlannedDate` | public DateTime? PlannedDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1740 | `DateTime? SentForSigningAt` | public DateTime? SentForSigningAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1741 | `DateTime? SignedAt` | public DateTime? SignedAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1742 | `DateTime? CancelledAt` | public DateTime? CancelledAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1746 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1777 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1786 | `DateTime? AccountDate` | public DateTime? AccountDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1787 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1788 | `DateTime PlannedDate` | public DateTime PlannedDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1789 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/AccountsController.cs` | 1813 | `DateTime DueDate` | DateTime DueDate, |
| `backend/Controllers/AccountsController.cs` | 1836 | `DateTime? DueDate` | public DateTime? DueDate { get; set; } |
| `backend/Controllers/AccountsController.cs` | 1837 | `DateTime? PaidDate` | public DateTime? PaidDate { get; set; } |
| `backend/Controllers/AccountsController.cs` | 34 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 35 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 508 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 509 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 746 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 747 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 1043 | `DateTime today)` | DateTime today) |
| `backend/Controllers/AccountsController.cs` | 1671 | `DateTime accountDate,` | DateTime accountDate, |
| `backend/Controllers/AccountsController.cs` | 1689 | `DateTime? accountDate,` | DateTime? accountDate, |
| `backend/Controllers/ActsController.cs` | 195 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 196 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 767 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 768 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 2175 | `DateTime now)` | private static string AppendCancellationComment(string? comment, string reason, DateTime now) |
| `backend/Controllers/AppearanceSettingsController.cs` | 102 | `DateTime? UpdatedAt` | public sealed record AppearanceSettingsDto(string ThemeKey, DateTime? UpdatedAt); |
| `backend/Controllers/AppVersionController.cs` | 424 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/AppVersionController.cs` | 433 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/AppVersionController.cs` | 439 | `DateTime CreatedAt` | DateTime CreatedAt, |
| `backend/Controllers/AppVersionController.cs` | 440 | `DateTime UpdatedAt` | DateTime UpdatedAt); |
| `backend/Controllers/AppVersionController.cs` | 449 | `DateTime ReleasedAt` | DateTime ReleasedAt, |
| `backend/Controllers/CalendarSyncController.cs` | 455 | `DateTime NormalizeFilterDate` | private static DateTime NormalizeFilterDate(DateTime value) |
| `backend/Controllers/CalendarSyncController.cs` | 731 | `DateTime CreatedAt` | public DateTime CreatedAt { get; set; } |
| `backend/Controllers/CalendarSyncController.cs` | 286 | `DateTime? createdFrom,` | DateTime? createdFrom, |
| `backend/Controllers/CalendarSyncController.cs` | 287 | `DateTime? createdTo)` | DateTime? createdTo) |
| `backend/Controllers/CalendarSyncController.cs` | 455 | `DateTime value)` | private static DateTime NormalizeFilterDate(DateTime value) |
| `backend/Controllers/CashOrdersController.cs` | 48 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 49 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 84 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 85 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 122 | `DateTime? date,` | [FromQuery] DateTime? date, |
| `backend/Controllers/CashOrdersController.cs` | 143 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 144 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 189 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 190 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 259 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 260 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 324 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 325 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1143 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 1144 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1330 | `DateTime date,` | private Task<decimal> CalculateCashBalanceAsync(DateTime date, string accountName, DataVisibilityAccess visibility, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 1340 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 1341 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1408 | `DateTime date,` | private async Task<string> GenerateNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 1426 | `DateTime date,` | private async Task<string> GenerateInventoryNumberAsync(DateTime date, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 2427 | `DateTime? from,` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/CashOrdersController.cs` | 2427 | `DateTime? to,` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/ClientPortalController.cs` | 268 | `DateTime ExpiresAt` | public DateTime ExpiresAt { get; set; } |
| `backend/Controllers/ClientPortalController.cs` | 215 | `DateTime expiresAt)` | private CookieOptions BuildCookieOptions(DateTime expiresAt) |
| `backend/Controllers/ClientsController.cs` | 1146 | `DateTime? ParseClientBirthDate` | private static DateTime? ParseClientBirthDate(string? notes) |
| `backend/Controllers/ClientsController.cs` | 1827 | `DateTime? PassportIssueDate` | public DateTime? PassportIssueDate { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1861 | `DateTime? IssueDate` | public DateTime? IssueDate { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1865 | `DateTime? ValidFrom` | public DateTime? ValidFrom { get; init; } |
| `backend/Controllers/ClientsController.cs` | 1866 | `DateTime? ValidTo` | public DateTime? ValidTo { get; init; } |
| `backend/Controllers/ClientsController.cs` | 769 | `DateTime today)` | DateTime today) |
| `backend/Controllers/ClientsController.cs` | 812 | `DateTime today,` | DateTime today, |
| `backend/Controllers/ContractsController.cs` | 1995 | `DateTime? ValidUntil` | private async Task<(ActionResult? Error, string? Number, string? BillingMode, bool IsOpenEnded, decimal? Amount, DateTime? ValidUntil, List<(int ClientId, string? Comment)> Clients, List<(int LegalEntityId, string? Comme |
| `backend/Controllers/ContractsController.cs` | 235 | `DateTime? originalValidUntil,` | DateTime? originalValidUntil, |
| `backend/Controllers/ContractsController.cs` | 236 | `DateTime? currentValidUntil,` | DateTime? currentValidUntil, |
| `backend/Controllers/ContractsController.cs` | 261 | `DateTime? date)` | private static string FormatContractDate(DateTime? date) |
| `backend/Controllers/ContractsController.cs` | 310 | `DateTime? from,` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 310 | `DateTime? to,` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 1118 | `DateTime date,` | public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 1792 | `DateTime now,` | private void ApplyContractCancellationSideEffects(Contract contract, string reason, DateTime now, TimeZoneInfo contractTimeZone) |
| `backend/Controllers/ContractsController.cs` | 2117 | `DateTime date,` | private async Task<string> GenerateNextContractNumberAsync(DateTime date, int? excludeId, CancellationToken ct, ContractNumberingSettingsDto? settings = null) |
| `backend/Controllers/ContractsController.cs` | 2161 | `DateTime date,` | private static string FormatGeneratedContractNumber(int sequence, DateTime date, string template) |
| `backend/Controllers/ContractsController.cs` | 2178 | `DateTime date,` | private static bool TryParseGeneratedContractNumber(string? number, DateTime date, string template, out int sequence) |
| `backend/Controllers/ContractsController.cs` | 2228 | `DateTime nowUtc,` | private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 2272 | `DateTime date,` | private static string RenderContractTemplate(string template, DateTime date, string sequenceText) |
| `backend/Controllers/ContractsController.cs` | 2368 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/ContractsController.cs` | 2369 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/DadataIntegrationSettingsController.cs` | 195 | `DateTime? UpdatedAt` | public DateTime? UpdatedAt { get; init; } |
| `backend/Controllers/DadataIntegrationSettingsController.cs` | 147 | `DateTime now,` | private async Task UpsertSettingAsync(string key, string value, DateTime now, CancellationToken ct) |
| `backend/Controllers/InternalChatController.cs` | 731 | `DateTime AsUtc` | private static DateTime AsUtc(DateTime value) => value.Kind switch |
| `backend/Controllers/InternalChatController.cs` | 738 | `DateTime? AsUtc` | private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null; |
| `backend/Controllers/InternalChatController.cs` | 783 | `DateTime? LastMessageAt` | public sealed record InternalChatThreadDto(int Id, string Kind, string Name, int? TaskJournalItemId, string Role, string NotificationMode, IReadOnlyList<int> MemberUserIds, int UnreadCount, string? LastMessagePreview, Da |
| `backend/Controllers/InternalChatController.cs` | 783 | `DateTime CreatedAt` | public sealed record InternalChatThreadDto(int Id, string Kind, string Name, int? TaskJournalItemId, string Role, string NotificationMode, IReadOnlyList<int> MemberUserIds, int UnreadCount, string? LastMessagePreview, Da |
| `backend/Controllers/InternalChatController.cs` | 785 | `DateTime? EditedAt` | public sealed record InternalChatMessageDto(int Id, int ThreadId, int? AuthorUserId, string AuthorName, string Kind, string Text, int? ReplyToMessageId, string? ReplyPreview, string? ReplyAuthorName, int? CreatedTaskJour |
| `backend/Controllers/InternalChatController.cs` | 785 | `DateTime? DeletedAt` | public sealed record InternalChatMessageDto(int Id, int ThreadId, int? AuthorUserId, string AuthorName, string Kind, string Text, int? ReplyToMessageId, string? ReplyPreview, string? ReplyAuthorName, int? CreatedTaskJour |
| `backend/Controllers/InternalChatController.cs` | 785 | `DateTime CreatedAt` | public sealed record InternalChatMessageDto(int Id, int ThreadId, int? AuthorUserId, string AuthorName, string Kind, string Text, int? ReplyToMessageId, string? ReplyPreview, string? ReplyAuthorName, int? CreatedTaskJour |
| `backend/Controllers/InternalChatController.cs` | 798 | `DateTime DueAt` | public sealed class CreateTaskFromInternalChatMessageRequest { public string Title { get; set; } = string.Empty; public string? Description { get; set; } public int? AssigneeUserId { get; set; } public DateTime DueAt { g |
| `backend/Controllers/InternalChatController.cs` | 262 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InternalChatController.cs` | 263 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InternalChatController.cs` | 694 | `DateTime now)` | private static void SyncTaskMembers(InternalChatThread thread, IReadOnlySet<int> desiredIds, DateTime now) |
| `backend/Controllers/InternalChatController.cs` | 731 | `DateTime value)` | private static DateTime AsUtc(DateTime value) => value.Kind switch |
| `backend/Controllers/InternalChatController.cs` | 738 | `DateTime? value)` | private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null; |
| `backend/Controllers/InvoicesController.cs` | 3839 | `DateTime EffectivePlannedDate` | private static DateTime EffectivePlannedDate(Payment payment) |
| `backend/Controllers/InvoicesController.cs` | 3944 | `DateTime InvoiceDate` | DateTime InvoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 5357 | `DateTime ResolveInvoiceDueDate` | private static DateTime ResolveInvoiceDueDate(UpsertInvoiceRequest request) |
| `backend/Controllers/InvoicesController.cs` | 5372 | `DateTime ResolveInvoiceDueDate` | private static DateTime ResolveInvoiceDueDate( |
| `backend/Controllers/InvoicesController.cs` | 6669 | `DateTime ResolveLegacyInstallmentDate` | private static DateTime ResolveLegacyInstallmentDate(Payment payment) |
| `backend/Controllers/InvoicesController.cs` | 7147 | `DateTime StartDate` | public DateTime StartDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7161 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7168 | `DateTime Date` | private sealed record InstallmentScheduleItem(int Sequence, DateTime Date, decimal Amount, int Id); |
| `backend/Controllers/InvoicesController.cs` | 7175 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7176 | `DateTime? DueDate` | public DateTime? DueDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7182 | `DateTime? SentForSigningAt` | public DateTime? SentForSigningAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7183 | `DateTime? SignedAt` | public DateTime? SignedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7184 | `DateTime? CancelledAt` | public DateTime? CancelledAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7188 | `DateTime? PaidDate` | public DateTime? PaidDate { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7224 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7227 | `DateTime? AuditUpdatedAt` | public DateTime? AuditUpdatedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 7231 | `DateTime? AuditDeletedAt` | public DateTime? AuditDeletedAt { get; init; } |
| `backend/Controllers/InvoicesController.cs` | 243 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 244 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 373 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 374 | `DateTime? to,` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 1173 | `DateTime now,` | private async Task CancelLinkedActsAsync(Payment invoicePayment, string reason, DateTime now, int? actorId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 1197 | `DateTime now)` | private static string AppendActCancellationComment(string? comment, string invoiceNumber, string reason, DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 1220 | `DateTime utcNow,` | DateTime utcNow, |
| `backend/Controllers/InvoicesController.cs` | 1281 | `DateTime localNow)` | private static void AppendCaseEvent(ClientCase clientCase, string eventText, DateTime localNow) |
| `backend/Controllers/InvoicesController.cs` | 1757 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 1758 | `DateTime dueDate,` | DateTime dueDate, |
| `backend/Controllers/InvoicesController.cs` | 2037 | `DateTime? date,` | public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 3434 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 3435 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3689 | `DateTime? paidDate,` | private static void ApplyPaidFlags(Payment payment, DateTime? paidDate, DateTime today) |
| `backend/Controllers/InvoicesController.cs` | 3689 | `DateTime today)` | private static void ApplyPaidFlags(Payment payment, DateTime? paidDate, DateTime today) |
| `backend/Controllers/InvoicesController.cs` | 3782 | `DateTime accountDate,` | DateTime accountDate, |
| `backend/Controllers/InvoicesController.cs` | 4386 | `DateTime now,` | private void ApplyInvoiceWorkflowStatus(Payment payment, string workflowStatus, DateTime now, string? reason) |
| `backend/Controllers/InvoicesController.cs` | 4430 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 4454 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 4455 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 5126 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 5178 | `DateTime now)` | DateTime now) |
| `backend/Controllers/InvoicesController.cs` | 5194 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 5228 | `DateTime now,` | DateTime now, |
| `backend/Controllers/InvoicesController.cs` | 5321 | `DateTime date,` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 5375 | `DateTime nextInvoiceDate)` | DateTime nextInvoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 6083 | `DateTime targetDate,` | private async Task<int> GetNextInvoiceNumberAsync(DateTime targetDate, int startNumber, string allocationStrategy, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6118 | `DateTime accountDate,` | private async Task<string?> ResolveInvoiceNumberForSaveAsync(string number, DateTime accountDate, int? excludeId, bool allowDuplicateOnImport, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6154 | `DateTime accountDate,` | private async Task<HashSet<string>> LoadReservedInvoiceNumberLookupAsync(DateTime accountDate, int? excludeId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6176 | `DateTime accountDate,` | private async Task<ActionResult?> EnsureInvoiceNumberIsUniqueAsync(string number, DateTime accountDate, int? excludeId, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6216 | `DateTime nowUtc,` | private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 6255 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6302 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6317 | `DateTime invoiceDate)` | private static string BuildReservedInvoiceNumberKey(string number, DateTime invoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 6322 | `DateTime invoiceDate)` | private static void AssignLegacyAccountSource(Payment payment, string number, DateTime invoiceDate) |
| `backend/Controllers/InvoicesController.cs` | 6341 | `DateTime localToday)` | DateTime localToday) |
| `backend/Controllers/InvoicesController.cs` | 6457 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6513 | `DateTime invoiceDate,` | DateTime invoiceDate, |
| `backend/Controllers/InvoicesController.cs` | 6623 | `DateTime localToday)` | DateTime localToday) |
| `backend/Controllers/InvoicesController.cs` | 6674 | `DateTime nowUtc,` | private async Task UpsertUserSettingAsync(int userId, string key, string value, DateTime nowUtc, CancellationToken ct) |
| `backend/Controllers/LegalEntitiesController.cs` | 2518 | `DateTimeOffset? LiquidationDate` | public DateTimeOffset? LiquidationDate { get; init; } |
| `backend/Controllers/LegalEntitiesController.cs` | 2530 | `DateTimeOffset? RegistrationDate` | public DateTimeOffset? RegistrationDate { get; init; } |
| `backend/Controllers/LegalEntitiesController.cs` | 81 | `DateTime? invoiceDate,` | DateTime? invoiceDate, |
| `backend/Controllers/MoneyOperationsController.cs` | 4766 | `DateTime? LastCheckedAt` | public DateTime? LastCheckedAt { get; set; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4839 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4840 | `DateTime? RecognitionDate` | public DateTime? RecognitionDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4852 | `DateTime? LinkedInvoiceDate` | public DateTime? LinkedInvoiceDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4857 | `DateTime? LinkedActDate` | public DateTime? LinkedActDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4859 | `DateTime? MatchedAt` | public DateTime? MatchedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4863 | `DateTime CreatedAt` | public DateTime CreatedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4864 | `DateTime? UpdatedAt` | public DateTime? UpdatedAt { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4869 | `DateTime? CashOrderDate` | public DateTime? CashOrderDate { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4904 | `DateTime Date` | public DateTime Date { get; init; } |
| `backend/Controllers/MoneyOperationsController.cs` | 4927 | `DateTime Date` | DateTime Date, |
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
| `backend/Controllers/MoneyOperationsController.cs` | 3279 | `DateTime? from,` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 3280 | `DateTime? to,` | DateTime? to, |
| `backend/Controllers/MoneyOperationsController.cs` | 3458 | `DateTime date)` | private async Task<string> GenerateCashOrderNumberAsync(CashOrderType type, DateTime date) |
| `backend/Controllers/MoneyOperationsController.cs` | 4192 | `DateTime date,` | DateTime date, |
| `backend/Controllers/MoneyOperationsController.cs` | 4791 | `DateTime date)` | private static bool TryParseStatementDate(string value, out DateTime date) |
| `backend/Controllers/NavigationSettingsController.cs` | 207 | `DateTime? UpdatedAt` | DateTime? UpdatedAt); |
| `backend/Controllers/NavigationSettingsController.cs` | 90 | `DateTime updatedAt)` | private static NavigationSettingsDto Parse(string raw, DateTime updatedAt) |
| `backend/Controllers/NavigationSettingsController.cs` | 107 | `DateTime? updatedAt)` | DateTime? updatedAt) |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `DateTime? from,` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 44 | `DateTime? to,` | [FromQuery] DateTime? to, |
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
| `backend/Controllers/ReportsController.cs` | 5606 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt(CurrentCaseQueryRow clientCase) |
| `backend/Controllers/ReportsController.cs` | 5612 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt( |
| `backend/Controllers/ReportsController.cs` | 5627 | `DateTime ResolveCaseLastActivityAt` | private static DateTime ResolveCaseLastActivityAt( |
| Еще 2063 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Backend: текущее время сервера

Вызовы Now/UtcNow, которые надо привести к единому источнику времени.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 26 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ActsController.cs` | 458 | `DateTime.UtcNow` | act.SentForSigningAt ??= DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 459 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 481 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 512 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 558 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 639 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 695 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 975 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 976 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1069 | `DateTime.UtcNow` | act.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ActsController.cs` | 1144 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1189 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1361 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 1921 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ActsController.cs` | 2204 | `DateTime.UtcNow` | return $"{baseNumber}-К{DateTime.UtcNow:yyyyMMddHHmmss}"; |
| `backend/Controllers/AppearanceSettingsController.cs` | 70 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/AppVersionController.cs` | 116 | `DateTime.UtcNow` | SeenAt = DateTime.UtcNow, |
| `backend/Controllers/AppVersionController.cs` | 258 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/AppVersionController.cs` | 314 | `DateTime.UtcNow` | release.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/BanksController.cs` | 106 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/BanksController.cs` | 147 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CalendarSyncController.cs` | 111 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/CalendarSyncController.cs` | 113 | `DateTime.UtcNow` | normalized.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CalendarSyncController.cs` | 674 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 425 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 442 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 537 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 598 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 634 | `DateTime.UtcNow` | model.CancelledAt = model.Status == CashOrderStatus.Cancelled ? model.CancelledAt ?? DateTime.UtcNow : null; |
| `backend/Controllers/CashOrdersController.cs` | 636 | `DateTime.UtcNow` | model.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 692 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 769 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 803 | `DateTime.UtcNow` | order.CancelledAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 805 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/CashOrdersController.cs` | 854 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/CashOrdersController.cs` | 1069 | `DateTime.UtcNow` | CancelledAt = payment.Status == PaymentStatus.Cancelled ? payment.AuditUpdatedAt ?? DateTime.UtcNow : null, |
| `backend/Controllers/CashOrdersController.cs` | 1139 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientBalancesController.cs` | 141 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/ClientPassportController.cs` | 104 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ClientsController.cs` | 69 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientsController.cs` | 1203 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1204 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1295 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1296 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1356 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ClientsController.cs` | 1370 | `DateTime.UtcNow` | current.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ClientsController.cs` | 164 | `DateTimeOffset.UtcNow` | var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); |
| `backend/Controllers/ContactsController.cs` | 382 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 508 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 608 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContactsController.cs` | 613 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 660 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContactsController.cs` | 665 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 835 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.cs` | 1326 | `DateTime.UtcNow` | contact.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.Merge.cs` | 127 | `DateTime.UtcNow` | archived.Property("DeletedAt").CurrentValue = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.Merge.cs` | 147 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContactsController.Merge.cs` | 462 | `DateTime.UtcNow` | replacement.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.Merge.cs` | 493 | `DateTime.UtcNow` | legalReplacement.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.Merge.cs` | 552 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContactsController.Merge.cs` | 593 | `DateTime.UtcNow` | primaryLegal.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.Merge.cs` | 656 | `DateTime.UtcNow` | primaryLegal.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContactsController.Merge.cs` | 679 | `DateTime.UtcNow` | primaryContact.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 549 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 666 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 761 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 783 | `DateTime.UtcNow` | contract.SentForSigningAt ??= DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 784 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 818 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 850 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 905 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1018 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1062 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1093 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1110 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ContractsController.cs` | 1171 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/ContractsController.cs` | 1237 | `DateTime.UtcNow` | contract.UpdatedAt = DateTime.UtcNow; |
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
| `backend/Controllers/InternalChatController.cs` | 131 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 168 | `DateTime.UtcNow` | member.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 229 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 340 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 413 | `DateTime.UtcNow` | message.EditedAt = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 427 | `DateTime.UtcNow` | message.DeletedAt = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 473 | `DateTime.UtcNow` | message.PinnedAt = request.Pinned ? DateTime.UtcNow : null; |
| `backend/Controllers/InternalChatController.cs` | 499 | `DateTime.UtcNow` | member.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 516 | `DateTime.UtcNow` | member.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/InternalChatController.cs` | 547 | `DateTime.UtcNow` | if (request.DueAt <= DateTime.UtcNow.AddMinutes(-1)) return BadRequest("Срок задачи должен быть в будущем."); |
| `backend/Controllers/InternalChatController.cs` | 556 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 103 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/InvoicesController.cs` | 604 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 719 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 749 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 778 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 842 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 949 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1409 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 1441 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 1444 | `DateTime.UtcNow` | ApplyInvoiceWorkflowStatus(payment, workflowStatus, DateTime.UtcNow, reason: null); |
| `backend/Controllers/InvoicesController.cs` | 1470 | `DateTime.UtcNow` | var linkedAt = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1642 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 1925 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 2023 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2083 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2119 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2214 | `DateTime.UtcNow` | DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 2361 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 2880 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 2933 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 3177 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 4631 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 4938 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 5454 | `DateTime.UtcNow` | PaymentDomainService.ApplyStatusRules(payment, DateTime.UtcNow, invoiceTimeZone); |
| `backend/Controllers/InvoicesController.cs` | 5972 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/InvoicesController.cs` | 5993 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/InvoicesController.cs` | 6426 | `DateTime.UtcNow` | InvoicePaymentStateService.CalculateRepresentativeState(representative, resultingInstallments, DateTime.UtcNow)); |
| `backend/Controllers/LegalEntitiesController.cs` | 211 | `DateTime.UtcNow` | var nowUtc = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 634 | `DateTime.UtcNow` | SetProperty(created, "CreatedAt", DateTime.UtcNow); |
| `backend/Controllers/LegalEntitiesController.cs` | 1150 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 1151 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 2104 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 2273 | `DateTime.UtcNow` | existingAddress.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/LegalEntitiesController.cs` | 2321 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntitiesController.cs` | 2322 | `DateTime.UtcNow` | UpdatedAt = DateTime.UtcNow, |
| `backend/Controllers/LegalEntityBalancesController.cs` | 141 | `DateTime.UtcNow` | DateTime.UtcNow, |
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
| `backend/Controllers/MoneyOperationsController.cs` | 2019 | `DateTime.UtcNow` | model.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2027 | `DateTime.UtcNow` | model.CreatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2081 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2165 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/MoneyOperationsController.cs` | 2309 | `DateTime.UtcNow` | var checkedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2531 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2541 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2550 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2560 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2568 | `DateTime.UtcNow` | row.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2573 | `DateTime.UtcNow` | batch.RolledBackAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2641 | `DateTime.UtcNow` | $"{batch.FileHash}:reprocess:{batch.Id}:{DateTime.UtcNow:O}", |
| `backend/Controllers/MoneyOperationsController.cs` | 2759 | `DateTime.UtcNow` | entity.MatchedAt = entity.MatchedAt ?? DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2777 | `DateTime.UtcNow` | entity.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2819 | `DateTime.UtcNow` | order.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2842 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2856 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 2875 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 2887 | `DateTime.UtcNow` | existing.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3517 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 3666 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 3684 | `DateTime.UtcNow` | operation.MatchedAt = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3772 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/MoneyOperationsController.cs` | 3924 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 4634 | `DateTime.UtcNow` | var note = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — зачтено {movementLabel} из раздела \"Деньги\" ({sourceLabel}) на сумму {appliedAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}."; |
| `backend/Controllers/MoneyOperationsController.cs` | 4658 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/MoneyOperationsController.cs` | 4672 | `DateTime.UtcNow` | var overpaymentNote = $"Системно: {DateTime.UtcNow:dd.MM.yyyy HH:mm:ss} — переплата {overpaymentAmount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} зачислена на баланс клиента."; |
| `backend/Controllers/MoneyOperationsController.cs` | 4676 | `DateTime.UtcNow` | await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, payment, DateTime.UtcNow, ct); |
| `backend/Controllers/NavigationSettingsController.cs` | 63 | `DateTime.UtcNow` | var normalized = Normalize(request.Order, request.Labels, request.Groups, DateTime.UtcNow); |
| `backend/Controllers/NavigationSettingsController.cs` | 67 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 51 | `DateTime.UtcNow` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 189 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 247 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 304 | `DateTime.UtcNow` | item.ArchivedAt = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 339 | `DateTime.UtcNow` | item.SnoozedUntil = DateTime.UtcNow.AddMinutes(minutes); |
| `backend/Controllers/NotificationInboxController.cs` | 355 | `DateTime.UtcNow` | item.DeletedAt ??= DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 485 | `DateTime.UtcNow` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 860 | `DateTime.UtcNow` | item.ReadAt = DateTime.UtcNow; |
| `backend/Controllers/NotificationInboxController.cs` | 891 | `DateTime.UtcNow` | DateTime.UtcNow, |
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
| `backend/Controllers/ProfileController.cs` | 210 | `DateTime.UtcNow` | var fileName = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}"; |
| `backend/Controllers/PushSubscriptionsController.cs` | 156 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PushSubscriptionsController.cs` | 242 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PushSubscriptionsController.cs` | 275 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/PushSubscriptionsController.cs` | 298 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ReportsController.cs` | 174 | `DateTime.UtcNow` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 180 | `DateTime.UtcNow` | var nowUtc = NormalizeReportInstant(DateTime.UtcNow); |
| `backend/Controllers/ReportsController.cs` | 197 | `DateTime.UtcNow` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 576 | `DateTime.UtcNow` | var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date); |
| `backend/Controllers/ReportsController.cs` | 610 | `DateTime.UtcNow` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 646 | `DateTime.UtcNow` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 1322 | `DateTime.UtcNow` | var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 2345 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 2487 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 4485 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/ReportsController.cs` | 4711 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 4886 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 5065 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/ReportsController.cs` | 5258 | `DateTime.UtcNow` | GeneratedAt = NormalizeReportInstant(DateTime.UtcNow), |
| `backend/Controllers/RolePermissionsController.cs` | 58 | `DateTime.UtcNow` | GeneratedAtUtc = DateTime.UtcNow, |
| `backend/Controllers/RolePermissionsController.cs` | 90 | `DateTime.UtcNow` | var fileName = $"role-permissions-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv"; |
| `backend/Controllers/RolePermissionsController.cs` | 537 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/RolePermissionsController.cs` | 766 | `DateTime.UtcNow` | role.PermissionsUpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/RolesController.cs` | 115 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/RolesController.cs` | 172 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/S3StorageController.cs` | 337 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/StickyNotesController.cs` | 837 | `DateTime.UtcNow` | CreatedAt = DateTime.UtcNow, |
| `backend/Controllers/StorageAdminController.cs` | 139 | `DateTime.UtcNow` | operation.NextAttemptAt = DateTime.UtcNow; |
| `backend/Controllers/StorageAdminController.cs` | 144 | `DateTime.UtcNow` | operation.UpdatedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 111 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 204 | `DateTime.UtcNow` | var startedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 335 | `DateTime.UtcNow` | var targetDate = MoneyBalanceReconciliationSchedulerService.ResolveReconciliationTargetDate(DateTime.UtcNow, timeZone); |
| `backend/Controllers/SystemJobsController.cs` | 365 | `DateTime.UtcNow` | var finishedAt = DateTime.UtcNow; |
| `backend/Controllers/SystemJobsController.cs` | 590 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes); |
| `backend/Controllers/SystemJobsController.cs` | 659 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddDays(-retentionDays); |
| `backend/Controllers/SystemJobsController.cs` | 717 | `DateTime.UtcNow` | var criticalCutoff = DateTime.UtcNow.Date.AddDays(-criticalAgeDays); |
| `backend/Controllers/SystemJobsController.cs` | 809 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddDays(-retentionDays); |
| `backend/Controllers/SystemJobsController.cs` | 1243 | `DateTime.UtcNow` | SystemJobKeys.TaskReminders => await BuildTaskReminderJobAsync(DateTime.UtcNow, ct), |
| `backend/Controllers/SystemJobsController.cs` | 1250 | `DateTime.UtcNow` | SystemJobKeys.CaseInactivityControl => await BuildCaseControlJobAsync(settings, DateTime.UtcNow, ct), |
| `backend/Controllers/SystemJobsController.cs` | 1268 | `DateTime.UtcNow` | var cutoff = DateTime.UtcNow.AddMinutes(-delayMinutes); |
| `backend/Controllers/TasksController.cs` | 269 | `DateTime.UtcNow` | var now = DateTime.UtcNow; |
| Еще 2715 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Backend: преобразования timezone

Ручные преобразования и явное назначение Kind, где чаще всего появляются сдвиги.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 1739 | `timezone` | "releaseId": "2026-09-16-automation-assignee-timezone-alignment", |
| `backend/AppReleases/releases.json` | 16037 | `timezone` | "releaseId": "2026-06-07-external-calendar-profile-timezone", |
| `backend/AppReleases/releases.json` | 16051 | `timezone` | "text": "Ранее подключенные календари Google Calendar и Яндекс.Календаря при следующей синхронизации также обновляются до часового пояса владельца, чтобы события отображались без ручной правки timezone у провайдера." |
| `backend/AppReleases/releases.json` | 16189 | `timezone` | "releaseId": "2026-06-05-auto-detect-profile-timezone", |
| `backend/AppReleases/releases.json` | 16208 | `timezone` | "releaseId": "2026-06-05-server-timezone-fallback", |
| `backend/AppReleases/releases.json` | 16246 | `timezone` | "releaseId": "2026-06-05-timezone-roadmap-regressions", |
| `backend/AppReleases/releases.json` | 16269 | `timezone` | "releaseId": "2026-06-05-timezone-core-rules-regression", |
| `backend/AppReleases/releases.json` | 16288 | `timezone` | "releaseId": "2026-06-05-case-court-deadline-notifications-timezone", |
| `backend/AppReleases/releases.json` | 16307 | `timezone` | "releaseId": "2026-06-05-report-month-presets-task-planning-timezone", |
| `backend/AppReleases/releases.json` | 16326 | `timezone` | "releaseId": "2026-06-05-report-exports-generated-at-timezone", |
| `backend/AppReleases/releases.json` | 16383 | `timezone` | "releaseId": "2026-06-05-client-case-reports-profile-timezone", |
| `backend/AppReleases/releases.json` | 16406 | `timezone` | "releaseId": "2026-06-05-financial-reports-profile-timezone", |
| `backend/AppReleases/releases.json` | 16425 | `timezone` | "releaseId": "2026-06-05-payments-export-profile-timezone", |
| `backend/AppReleases/releases.json` | 16448 | `timezone` | "releaseId": "2026-06-05-case-expenses-profile-timezone", |
| `backend/AppReleases/releases.json` | 16467 | `timezone` | "releaseId": "2026-06-05-client-debt-profile-timezone", |
| `backend/AppReleases/releases.json` | 16486 | `timezone` | "releaseId": "2026-06-05-payment-overdue-profile-timezone", |
| `backend/AppReleases/releases.json` | 16585 | `timezone` | "releaseId": "2026-06-04-client-contact-profile-timezone", |
| `backend/AppReleases/releases.json` | 16604 | `timezone` | "releaseId": "2026-06-04-money-journal-profile-timezone", |
| `backend/AppReleases/releases.json` | 16623 | `timezone` | "releaseId": "2026-06-04-legal-work-export-profile-timezone", |
| `backend/AppReleases/releases.json` | 16642 | `timezone` | "releaseId": "2026-06-04-report-date-timezone-formatting", |
| `backend/AppReleases/releases.json` | 16860 | `timezone` | "releaseId": "2026-06-04-case-reports-timezone", |
| `backend/AppReleases/releases.json` | 16883 | `timezone` | "releaseId": "2026-06-04-case-registry-export-timezone", |
| `backend/AppReleases/releases.json` | 16906 | `timezone` | "releaseId": "2026-06-04-case-document-print-forms-timezone", |
| `backend/AppReleases/releases.json` | 16929 | `timezone` | "releaseId": "2026-06-04-case-documents-uploaded-at-timezone", |
| `backend/AppReleases/releases.json` | 16952 | `timezone` | "releaseId": "2026-06-04-case-history-profile-timezone", |
| `backend/AppReleases/releases.json` | 16975 | `timezone` | "releaseId": "2026-06-04-case-detail-profile-timezone", |
| `backend/AppReleases/releases.json` | 16998 | `timezone` | "releaseId": "2026-06-04-court-sync-source-timezone", |
| `backend/AppReleases/releases.json` | 17021 | `timezone` | "releaseId": "2026-06-04-case-calendar-profile-timezone", |
| `backend/AppReleases/releases.json` | 17044 | `timezone` | "releaseId": "2026-06-04-external-calendar-reverse-sync-timezone-log", |
| `backend/AppReleases/releases.json` | 17067 | `timezone` | "releaseId": "2026-06-04-external-calendar-assignee-timezone-regression", |
| `backend/AppReleases/releases.json` | 17090 | `timezone` | "releaseId": "2026-06-04-task-calendar-assignee-timezone-completed", |
| `backend/AppReleases/releases.json` | 17113 | `timezone` | "releaseId": "2026-06-04-task-calendar-drag-resize-profile-timezone", |
| `backend/AppReleases/releases.json` | 17136 | `timezone` | "releaseId": "2026-06-04-task-calendar-profile-timezone-today", |
| `backend/AppReleases/releases.json` | 17159 | `timezone` | "releaseId": "2026-06-04-task-contractor-payment-exact-date-timezone", |
| `backend/AppReleases/releases.json` | 17178 | `timezone` | "releaseId": "2026-06-04-calendar-sync-log-filter-profile-timezone", |
| `backend/AppReleases/releases.json` | 17197 | `timezone` | "releaseId": "2026-06-04-settings-integrations-profile-timezone", |
| `backend/AppReleases/releases.json` | 17220 | `timezone` | "releaseId": "2026-06-04-admin-users-roles-profile-timezone", |
| `backend/AppReleases/releases.json` | 17247 | `timezone` | "releaseId": "2026-06-04-audit-logs-profile-timezone", |
| `backend/AppReleases/releases.json` | 17270 | `timezone` | "releaseId": "2026-06-04-system-job-diagnostics-profile-timezone", |
| `backend/AppReleases/releases.json` | 17308 | `timezone` | "releaseId": "2026-06-04-task-notifications-recipient-timezone", |
| `backend/AppReleases/releases.json` | 17327 | `timezone` | "releaseId": "2026-06-04-money-reconciliation-profile-timezone", |
| `backend/AppReleases/releases.json` | 17346 | `timezone` | "releaseId": "2026-06-04-invoice-numbering-profile-timezone", |
| `backend/AppReleases/releases.json` | 17365 | `timezone` | "releaseId": "2026-06-04-spreadsheet-import-invoice-status-timezone", |
| `backend/AppReleases/releases.json` | 17380 | `timezone` | "releaseId": "2026-06-04-profile-birth-date-timezone", |
| `backend/AppReleases/releases.json` | 17395 | `timezone` | "releaseId": "2026-06-04-task-contractor-payment-profile-timezone", |
| `backend/AppReleases/releases.json` | 17410 | `timezone` | "releaseId": "2026-06-04-money-transfer-profile-timezone", |
| `backend/AppReleases/releases.json` | 17425 | `timezone` | "releaseId": "2026-06-04-cash-orders-profile-timezone", |
| `backend/AppReleases/releases.json` | 17440 | `timezone` | "releaseId": "2026-06-04-stats-summary-profile-timezone", |
| `backend/AppReleases/releases.json` | 17455 | `timezone` | "releaseId": "2026-06-04-reports-default-period-profile-timezone", |
| `backend/AppReleases/releases.json` | 17474 | `timezone` | "releaseId": "2026-06-04-invoice-summary-profile-timezone", |
| `backend/AppReleases/releases.json` | 17493 | `timezone` | "releaseId": "2026-06-04-payables-dashboard-profile-timezone", |
| `backend/AppReleases/releases.json` | 17508 | `timezone` | "releaseId": "2026-06-04-finance-system-notes-profile-timezone", |
| `backend/AppReleases/releases.json` | 17527 | `timezone` | "releaseId": "2026-06-04-report-generated-time-profile-timezone", |
| `backend/AppReleases/releases.json` | 17584 | `timezone` | "releaseId": "2026-06-03-calendar-utc-timezone-rule", |
| `backend/AppReleases/releases.json` | 18069 | `timezone` | "releaseId": "2026-06-02-app-release-seed-timezone-fix", |
| `backend/Controllers/AccountsController.cs` | 25 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/AccountsController.cs` | 26 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/AccountsController.cs` | 25 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/AccountsController.cs` | 26 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/CalendarSyncController.cs` | 456 | `.ToUniversalTime()` | => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); |
| `backend/Controllers/CalendarSyncController.cs` | 456 | `DateTime.SpecifyKind` | => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(); |
| `backend/Controllers/CashOrdersController.cs` | 1138 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/CashOrdersController.cs` | 1139 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/CashOrdersController.cs` | 1138 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/CashOrdersController.cs` | 1139 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientPortalController.cs` | 222 | `DateTime.SpecifyKind` | Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)), |
| `backend/Controllers/ClientsController.cs` | 68 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ClientsController.cs` | 69 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientsController.cs` | 68 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ClientsController.cs` | 69 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ClientsController.cs` | 901 | `timezone` | timezone = result.Timezone, |
| `backend/Controllers/ClientsController.cs` | 901 | `Timezone` | timezone = result.Timezone, |
| `backend/Controllers/ClientsController.cs` | 937 | `timezone` | timezone = (string?)null, |
| `backend/Controllers/ContractsController.cs` | 74 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/DatabaseAdminController.cs` | 419 | `TimeZone` | TimeZone = overview.TimeZone, |
| `backend/Controllers/DictionariesAdminController.cs` | 233 | `Timezone` | (le, v) => le.Timezone = v, |
| `backend/Controllers/DictionariesAdminController.cs` | 234 | `Timezone` | le => le.Timezone, |
| `backend/Controllers/DictionariesAdminController.cs` | 818 | `Timezone` | if (await _db.LegalEntities.AsNoTracking().AnyAsync(le => le.Timezone == e.Name)) |
| `backend/Controllers/FormRequirementsController.cs` | 107 | `Timezone` | new("Timezone", "Часовой пояс", "Реквизиты: Основные"), |
| `backend/Controllers/InternalChatController.cs` | 282 | `.ToUniversalTime()` | if (from.HasValue) query = query.Where(message => message.CreatedAt >= from.Value.ToUniversalTime()); |
| `backend/Controllers/InternalChatController.cs` | 283 | `.ToUniversalTime()` | if (to.HasValue) query = query.Where(message => message.CreatedAt <= to.Value.ToUniversalTime()); |
| `backend/Controllers/InternalChatController.cs` | 566 | `.ToUniversalTime()` | DueAt = request.DueAt.ToUniversalTime(), |
| `backend/Controllers/InternalChatController.cs` | 734 | `.ToUniversalTime()` | DateTimeKind.Local => value.ToUniversalTime(), |
| `backend/Controllers/InternalChatController.cs` | 735 | `DateTime.SpecifyKind` | _ => DateTime.SpecifyKind(value, DateTimeKind.Utc) |
| `backend/Controllers/InvoicesController.cs` | 1247 | `DateTime.SpecifyKind` | var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone); |
| `backend/Controllers/InvoicesController.cs` | 1247 | `TimeZoneInfo.` | var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone); |
| `backend/Controllers/InvoicesController.cs` | 98 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/InvoicesController.cs` | 103 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/InvoicesController.cs` | 102 | `timeZone` | var timeZone = await ResolveInvoiceTimeZoneAsync(ct); |
| `backend/Controllers/InvoicesController.cs` | 103 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/InvoicesController.cs` | 1221 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/InvoicesController.cs` | 1247 | `timeZone` | var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), timeZone); |
| `backend/Controllers/LegalEntitiesController.cs` | 397 | `Timezone` | Timezone = entity.Timezone, |
| `backend/Controllers/LegalEntitiesController.cs` | 600 | `Timezone` | entity.Timezone = await ResolveDictionaryValueAsync(_db.Timezones, entity.Timezone, "Добавлено автоматически", "#8B5CF6", ct); |
| `backend/Controllers/LegalEntitiesController.cs` | 1109 | `Timezone` | Timezone = NormalizeOptional(request.Timezone), |
| `backend/Controllers/LegalEntitiesController.cs` | 2063 | `Timezone` | entity.Timezone = NormalizeOptional(request.Timezone); |
| `backend/Controllers/LegalEntitiesController.cs` | 2247 | `Timezone` | Timezone: null); |
| `backend/Controllers/MessengerDocumentDeliveriesController.cs` | 474 | `.ToUniversalTime()` | var occurredAt = request.OccurredAt?.ToUniversalTime() ?? receivedAt; |
| `backend/Controllers/MoneyOperationsController.cs` | 2164 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/MoneyOperationsController.cs` | 2165 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/MoneyOperationsController.cs` | 2164 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/MoneyOperationsController.cs` | 2165 | `timeZone` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/NotificationInboxController.cs` | 51 | `DateTime.SpecifyKind` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 485 | `DateTime.SpecifyKind` | var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified); |
| `backend/Controllers/NotificationInboxController.cs` | 100 | `TimeZoneInfo.` | : TimeZoneInfo.Utc; |
| `backend/Controllers/NotificationInboxController.cs` | 872 | `TimeZoneInfo.` | var effectiveTimeZone = timeZone ?? TimeZoneInfo.Utc; |
| `backend/Controllers/NotificationInboxController.cs` | 99 | `CalendarTimeZoneHelper` | ? await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_context, userId.Value, ct) |
| `backend/Controllers/NotificationInboxController.cs` | 98 | `timeZone` | var timeZone = quietHours.Enabled |
| `backend/Controllers/NotificationInboxController.cs` | 122 | `timeZone` | Items = items.Select(item => Map(item, preferences, quietHours, timeZone)).ToList(), |
| `backend/Controllers/NotificationInboxController.cs` | 867 | `timeZone` | TimeZoneInfo? timeZone = null) |
| `backend/Controllers/NotificationInboxController.cs` | 872 | `timeZone` | var effectiveTimeZone = timeZone ?? TimeZoneInfo.Utc; |
| `backend/Controllers/PaymentsController.cs` | 58 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 173 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 174 | `CalendarTimeZoneHelper` | return CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 179 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 181 | `CalendarTimeZoneHelper` | return (nowUtc, CalendarTimeZoneHelper.ToUserLocal(nowUtc, timeZone), timeZone); |
| `backend/Controllers/ReportsController.cs` | 196 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 197 | `CalendarTimeZoneHelper` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 575 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 576 | `CalendarTimeZoneHelper` | var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date); |
| `backend/Controllers/ReportsController.cs` | 609 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 610 | `CalendarTimeZoneHelper` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 645 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 646 | `CalendarTimeZoneHelper` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 700 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 1321 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 1322 | `CalendarTimeZoneHelper` | var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 1997 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2032 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2052 | `CalendarTimeZoneHelper` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2273 | `CalendarTimeZoneHelper` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2430 | `CalendarTimeZoneHelper` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 4281 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.FormatUserDateTime(generatedAtUtc, timeZone, culture: CultureInfo.GetCultureInfo("ru-RU")); |
| `backend/Controllers/ReportsController.cs` | 4287 | `CalendarTimeZoneHelper` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4300 | `CalendarTimeZoneHelper` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 9193 | `CalendarTimeZoneHelper` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9194 | `CalendarTimeZoneHelper` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 9426 | `CalendarTimeZoneHelper` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9427 | `CalendarTimeZoneHelper` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
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
| `backend/Controllers/ReportsController.cs` | 309 | `TimeZone` | return Content(BuildLegalWorkPrintHtml(report!, now.Utc, now.TimeZone), "text/html; charset=utf-8"); |
| `backend/Controllers/ReportsController.cs` | 367 | `TimeZone` | Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(BuildReportExportExcelXml(document!, now.Utc, now.TimeZone))).ToArray(), |
| `backend/Controllers/ReportsController.cs` | 427 | `TimeZone` | BuildReportExportCsv(document!, now.Utc, now.TimeZone), |
| `backend/Controllers/ReportsController.cs` | 491 | `TimeZone` | var pdf = await _pdfService.RenderAsync(BuildReportExportPrintHtml(document!, now.Utc, now.TimeZone), ct); |
| `backend/Controllers/ReportsController.cs` | 549 | `TimeZone` | return Content(BuildReportExportPrintHtml(document!, now.Utc, now.TimeZone), "text/html; charset=utf-8", Encoding.UTF8); |
| `backend/Controllers/ReportsController.cs` | 575 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 576 | `timeZone` | var (start, end) = ResolveReportPeriod(from, to, CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date); |
| `backend/Controllers/ReportsController.cs` | 581 | `timeZone` | cancellationToken => BuildCaseFinanceReportAsync(start, end, timeZone, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 609 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 610 | `timeZone` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 616 | `timeZone` | cancellationToken => BuildCaseSuccessReportAsync(start, end, reportToday, timeZone, filters, effectiveLimit, casesVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 645 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 646 | `timeZone` | var reportToday = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 650 | `timeZone` | cancellationToken => BuildLegalWorkReportAsync(start, end, reportToday, timeZone, filters, casesVisibility, tasksVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 700 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 719 | `timeZone` | cancellationToken => BuildCaseFinanceReportAsync(start, end, timeZone, filters, effectiveLimit, casesVisibility, accountsVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1112 | `timeZone` | cancellationToken => BuildBankStatementImportReportAsync(start, end, timeZone, normalizedAccountName, normalizedCounterpartyName, effectiveClientId, effectiveCaseId, effectiveContractId, effectiveIncomeTypeId, cancellati |
| `backend/Controllers/ReportsController.cs` | 1127 | `timeZone` | cancellationToken => BuildSpreadsheetImportReportAsync(start, end, timeZone, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1144 | `timeZone` | cancellationToken => BuildCaseSuccessReportAsync(start, end, reportToday, timeZone, filters, effectiveLimit, casesVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1161 | `timeZone` | cancellationToken => BuildCurrentCasesReportAsync(effectiveAsOf, timeZone, filters, effectiveLimit, casesVisibility, tasksVisibility, cancellationToken), |
| `backend/Controllers/ReportsController.cs` | 1185 | `timeZone` | return (BuildTaskExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1202 | `timeZone` | return (BuildLawyerWorkloadExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1217 | `timeZone` | return (BuildOverdueDeadlinesExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1237 | `timeZone` | return (BuildCaseActivityExportDocument(reportDto, timeZone), null); |
| `backend/Controllers/ReportsController.cs` | 1321 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct); |
| `backend/Controllers/ReportsController.cs` | 1322 | `timeZone` | var effectiveAsOf = asOf?.Date ?? CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone).Date; |
| `backend/Controllers/ReportsController.cs` | 1332 | `timeZone` | timeZone, |
| `backend/Controllers/ReportsController.cs` | 1997 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2001 | `timeZone` | timeZone, |
| `backend/Controllers/ReportsController.cs` | 2032 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2033 | `timeZone` | return await BuildSpreadsheetImportReportAsync(start, end, timeZone, cancellationToken); |
| `backend/Controllers/ReportsController.cs` | 2045 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 2052 | `timeZone` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2266 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 2273 | `timeZone` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2424 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 2430 | `timeZone` | var endExclusiveUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(end.Date.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 2473 | `timeZone` | .Select(item => BuildCaseSuccessItem(item, asOf, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 2564 | `timeZone` | private static CaseSuccessItemDto BuildCaseSuccessItem(CaseSuccessQueryRow clientCase, DateTime asOf, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 2569 | `timeZone` | : ResolveReportLocalDate(clientCase.CreatedAt, timeZone); |
| `backend/Controllers/ReportsController.cs` | 3553 | `timeZone` | private static ReportExportDocument BuildTaskExportDocument(TaskReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3576 | `timeZone` | DateTimeCell(item.DueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3597 | `timeZone` | DateTimeCell(item.NearestDueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3601 | `timeZone` | private static ReportExportDocument BuildLawyerWorkloadExportDocument(LawyerWorkloadReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3629 | `timeZone` | DateTimeCell(item.NearestTaskDueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3632 | `timeZone` | : DateTimeCell(item.NearestDeadlineAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3636 | `timeZone` | private static ReportExportDocument BuildOverdueDeadlinesExportDocument(OverdueProceduralDeadlinesReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3655 | `timeZone` | : DateTimeCell(item.DueAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3673 | `timeZone` | private static ReportExportDocument BuildCaseActivityExportDocument(CaseActivityReportDto report, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3695 | `timeZone` | DateTimeCell(item.LastActivityAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3734 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3748 | `timeZone` | AppendLegalWorkExcelRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]); |
| `backend/Controllers/ReportsController.cs` | 3777 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3782 | `timeZone` | AppendCsvRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]); |
| `backend/Controllers/ReportsController.cs` | 3809 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3843 | `timeZone` | .Append(Html(GeneratedAtCell(generatedAtUtc, timeZone))) |
| `backend/Controllers/ReportsController.cs` | 3894 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 3921 | `timeZone` | GeneratedAtCell(generatedAtUtc, timeZone), |
| `backend/Controllers/ReportsController.cs` | 3957 | `timeZone` | DateCell(item.CreatedAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 4014 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4028 | `timeZone` | AppendLegalWorkExcelRow(builder, ["Сформировано", GeneratedAtCell(generatedAtUtc, timeZone)]); |
| `backend/Controllers/ReportsController.cs` | 4067 | `timeZone` | DateCell(item.CreatedAt, timeZone), |
| `backend/Controllers/ReportsController.cs` | 4132 | `timeZone` | LegalWorkDeadlineDateCell(item, timeZone), |
| `backend/Controllers/ReportsController.cs` | 4148 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4179 | `timeZone` | .Append(Html(GeneratedAtCell(generatedAtUtc, timeZone))) |
| `backend/Controllers/ReportsController.cs` | 4230 | `timeZone` | .Append("</td><td>").Append(Html(LegalWorkDeadlineDateCell(item, timeZone))) |
| `backend/Controllers/ReportsController.cs` | 4280 | `timeZone` | private static string GeneratedAtCell(DateTime generatedAtUtc, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4281 | `timeZone` | => CalendarTimeZoneHelper.FormatUserDateTime(generatedAtUtc, timeZone, culture: CultureInfo.GetCultureInfo("ru-RU")); |
| `backend/Controllers/ReportsController.cs` | 4283 | `timeZone` | private static string DateCell(DateTime? value, TimeZoneInfo? timeZone = null) |
| `backend/Controllers/ReportsController.cs` | 4285 | `timeZone` | ? (timeZone is null |
| `backend/Controllers/ReportsController.cs` | 4287 | `timeZone` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4291 | `timeZone` | private static string LegalWorkDeadlineDateCell(LegalWorkDeadlineItemDto item, TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4293 | `timeZone` | ? DateCell(item.DueAt, timeZone) |
| `backend/Controllers/ReportsController.cs` | 4296 | `timeZone` | private static string DateTimeCell(DateTime? value, TimeZoneInfo? timeZone = null) |
| `backend/Controllers/ReportsController.cs` | 4298 | `timeZone` | ? (timeZone is null |
| `backend/Controllers/ReportsController.cs` | 4300 | `timeZone` | : CalendarTimeZoneHelper.ToUserLocal(value.Value, timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4778 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 4873 | `timeZone` | timeZone)) |
| `backend/Controllers/ReportsController.cs` | 4936 | `timeZone` | TimeZoneInfo timeZone) |
| `backend/Controllers/ReportsController.cs` | 4940 | `timeZone` | : ResolveReportLocalDate(clientCase.CreatedAt, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9182 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 9193 | `timeZone` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9194 | `timeZone` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/ReportsController.cs` | 9421 | `timeZone` | TimeZoneInfo timeZone, |
| `backend/Controllers/ReportsController.cs` | 9426 | `timeZone` | var startUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localStart, timeZone); |
| `backend/Controllers/ReportsController.cs` | 9427 | `timeZone` | var endUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(localEnd.AddDays(1), timeZone); |
| `backend/Controllers/StickyNotesController.cs` | 1314 | `.ToUniversalTime()` | DateTimeKind.Local => value.Value.ToUniversalTime(), |
| `backend/Controllers/StickyNotesController.cs` | 1315 | `DateTime.SpecifyKind` | _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc), |
| `backend/Controllers/SystemJobsController.cs` | 112 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 334 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 964 | `CalendarTimeZoneHelper` | var scheduleTimeZone = CalendarTimeZoneHelper.ResolveTimeZone( |
| `backend/Controllers/SystemJobsController.cs` | 966 | `CalendarTimeZoneHelper` | var scheduleTimeZoneId = CalendarTimeZoneHelper.ResolveIanaTimeZoneId(scheduleTimeZone); |
| `backend/Controllers/SystemJobsController.cs` | 1189 | `CalendarTimeZoneHelper` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 1329 | `CalendarTimeZoneHelper` | => CalendarTimeZoneHelper.FormatUserDateTime(value, timeZone, culture: System.Globalization.CultureInfo.GetCultureInfo("ru-RU")); |
| `backend/Controllers/SystemJobsController.cs` | 112 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| `backend/Controllers/SystemJobsController.cs` | 170 | `timeZone` | await BuildNotificationDeliveryRetentionJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 172 | `timeZone` | await BuildExternalCalendarSyncJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 173 | `timeZone` | await BuildCalendarSyncLogRetentionJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 177 | `timeZone` | await BuildUserActivityRetentionJobAsync(settings, timeZone, ct), |
| `backend/Controllers/SystemJobsController.cs` | 334 | `timeZone` | var timeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, GetCurrentUserId(), ct); |
| Еще 1488 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Ручные смещения времени

Жестко заданные часовые пояса и смещения, которые нельзя оставлять для пользователей из разных регионов.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 5 | `+07:00` | "releasedAt": "2026-09-24T12:35:00+07:00", |
| `backend/AppReleases/releases.json` | 20 | `+07:00` | "releasedAt": "2026-09-24T11:59:00+07:00", |
| `backend/AppReleases/releases.json` | 35 | `+07:00` | "releasedAt": "2026-09-24T06:59:00+07:00", |
| `backend/AppReleases/releases.json` | 50 | `+07:00` | "releasedAt": "2026-09-24T04:55:00+07:00", |
| `backend/AppReleases/releases.json` | 89 | `+07:00` | "releasedAt": "2026-09-24T04:33:00+07:00", |
| `backend/AppReleases/releases.json` | 104 | `+07:00` | "releasedAt": "2026-09-24T04:08:00+07:00", |
| `backend/AppReleases/releases.json` | 119 | `+07:00` | "releasedAt": "2026-09-24T03:53:00+07:00", |
| `backend/AppReleases/releases.json` | 134 | `+07:00` | "releasedAt": "2026-09-24T01:50:00+07:00", |
| `backend/AppReleases/releases.json` | 157 | `+07:00` | "releasedAt": "2026-09-24T00:52:00+07:00", |
| `backend/AppReleases/releases.json` | 172 | `+07:00` | "releasedAt": "2026-09-24T00:08:00+07:00", |
| `backend/AppReleases/releases.json` | 187 | `+07:00` | "releasedAt": "2026-09-23T23:40:00+07:00", |
| `backend/AppReleases/releases.json` | 202 | `+07:00` | "releasedAt": "2026-09-23T23:19:00+07:00", |
| `backend/AppReleases/releases.json` | 217 | `+07:00` | "releasedAt": "2026-09-23T22:34:00+07:00", |
| `backend/AppReleases/releases.json` | 236 | `+07:00` | "releasedAt": "2026-09-23T22:03:00+07:00", |
| `backend/AppReleases/releases.json` | 255 | `+07:00` | "releasedAt": "2026-09-23T20:46:00+07:00", |
| `backend/AppReleases/releases.json` | 270 | `+07:00` | "releasedAt": "2026-09-23T20:06:00+07:00", |
| `backend/AppReleases/releases.json` | 289 | `+07:00` | "releasedAt": "2026-09-23T19:27:00+07:00", |
| `backend/AppReleases/releases.json` | 304 | `+07:00` | "releasedAt": "2026-09-23T19:15:00+07:00", |
| `backend/AppReleases/releases.json` | 323 | `+07:00` | "releasedAt": "2026-09-23T18:58:00+07:00", |
| `backend/AppReleases/releases.json` | 342 | `+07:00` | "releasedAt": "2026-09-23T18:25:00+07:00", |
| `backend/AppReleases/releases.json` | 365 | `+07:00` | "releasedAt": "2026-09-23T17:20:00+07:00", |
| `backend/AppReleases/releases.json` | 384 | `+07:00` | "releasedAt": "2026-09-23T16:54:00+07:00", |
| `backend/AppReleases/releases.json` | 403 | `+07:00` | "releasedAt": "2026-09-23T16:17:00+07:00", |
| `backend/AppReleases/releases.json` | 426 | `+07:00` | "releasedAt": "2026-09-23T15:20:00+07:00", |
| `backend/AppReleases/releases.json` | 449 | `+07:00` | "releasedAt": "2026-09-23T14:06:00+07:00", |
| `backend/AppReleases/releases.json` | 476 | `+07:00` | "releasedAt": "2026-09-23T13:40:00+07:00", |
| `backend/AppReleases/releases.json` | 495 | `+07:00` | "releasedAt": "2026-09-23T13:26:00+07:00", |
| `backend/AppReleases/releases.json` | 514 | `+07:00` | "releasedAt": "2026-09-23T13:05:00+07:00", |
| `backend/AppReleases/releases.json` | 533 | `+07:00` | "releasedAt": "2026-09-23T12:26:00+07:00", |
| `backend/AppReleases/releases.json` | 552 | `+07:00` | "releasedAt": "2026-09-23T11:45:00+07:00", |
| `backend/AppReleases/releases.json` | 571 | `+07:00` | "releasedAt": "2026-09-23T11:06:00+07:00", |
| `backend/AppReleases/releases.json` | 602 | `+07:00` | "releasedAt": "2026-09-22T18:30:00+07:00", |
| `backend/AppReleases/releases.json` | 637 | `+07:00` | "releasedAt": "2026-09-22T18:00:00+07:00", |
| `backend/AppReleases/releases.json` | 668 | `+07:00` | "releasedAt": "2026-09-21T22:45:00+07:00", |
| `backend/AppReleases/releases.json` | 707 | `+07:00` | "releasedAt": "2026-09-21T21:11:06+07:00", |
| `backend/AppReleases/releases.json` | 726 | `+07:00` | "releasedAt": "2026-09-21T20:50:00+07:00", |
| `backend/AppReleases/releases.json` | 749 | `+07:00` | "releasedAt": "2026-09-21T20:45:00+07:00", |
| `backend/AppReleases/releases.json` | 768 | `+07:00` | "releasedAt": "2026-09-21T20:15:00+07:00", |
| `backend/AppReleases/releases.json` | 791 | `+07:00` | "releasedAt": "2026-09-21T19:55:00+07:00", |
| `backend/AppReleases/releases.json` | 814 | `+07:00` | "releasedAt": "2026-09-21T19:42:00+07:00", |
| `backend/AppReleases/releases.json` | 837 | `+07:00` | "releasedAt": "2026-09-21T19:20:00+07:00", |
| `backend/AppReleases/releases.json` | 860 | `+07:00` | "releasedAt": "2026-09-21T18:56:00+07:00", |
| `backend/AppReleases/releases.json` | 883 | `+07:00` | "releasedAt": "2026-09-21T18:45:00+07:00", |
| `backend/AppReleases/releases.json` | 906 | `+07:00` | "releasedAt": "2026-09-21T18:05:10+07:00", |
| `backend/AppReleases/releases.json` | 929 | `+07:00` | "releasedAt": "2026-09-21T17:30:00+07:00", |
| `backend/AppReleases/releases.json` | 960 | `+07:00` | "releasedAt": "2026-09-21T16:57:04+07:00", |
| `backend/AppReleases/releases.json` | 983 | `+07:00` | "releasedAt": "2026-09-20T12:17:43+07:00", |
| `backend/AppReleases/releases.json` | 1014 | `+07:00` | "releasedAt": "2026-09-17T12:43:00+07:00", |
| `backend/AppReleases/releases.json` | 1037 | `+07:00` | "releasedAt": "2026-09-17T10:11:50+07:00", |
| `backend/AppReleases/releases.json` | 1060 | `+07:00` | "releasedAt": "2026-09-17T09:26:13+07:00", |
| `backend/AppReleases/releases.json` | 1083 | `+07:00` | "releasedAt": "2026-09-17T09:03:13+07:00", |
| `backend/AppReleases/releases.json` | 1106 | `+07:00` | "releasedAt": "2026-09-17T08:29:58+07:00", |
| `backend/AppReleases/releases.json` | 1129 | `+07:00` | "releasedAt": "2026-09-17T08:14:20+07:00", |
| `backend/AppReleases/releases.json` | 1152 | `+07:00` | "releasedAt": "2026-09-17T07:46:55+07:00", |
| `backend/AppReleases/releases.json` | 1175 | `+07:00` | "releasedAt": "2026-09-17T06:55:34+07:00", |
| `backend/AppReleases/releases.json` | 1198 | `+07:00` | "releasedAt": "2026-09-17T06:09:00+07:00", |
| `backend/AppReleases/releases.json` | 1221 | `+07:00` | "releasedAt": "2026-09-17T05:47:00+07:00", |
| `backend/AppReleases/releases.json` | 1244 | `+07:00` | "releasedAt": "2026-09-17T05:26:00+07:00", |
| `backend/AppReleases/releases.json` | 1267 | `+07:00` | "releasedAt": "2026-09-17T04:50:59+07:00", |
| `backend/AppReleases/releases.json` | 1290 | `+07:00` | "releasedAt": "2026-09-17T04:30:32+07:00", |
| `backend/AppReleases/releases.json` | 1313 | `+07:00` | "releasedAt": "2026-09-17T04:15:07+07:00", |
| `backend/AppReleases/releases.json` | 1336 | `+07:00` | "releasedAt": "2026-09-17T04:04:00+07:00", |
| `backend/AppReleases/releases.json` | 1359 | `+07:00` | "releasedAt": "2026-09-17T03:46:59+07:00", |
| `backend/AppReleases/releases.json` | 1382 | `+07:00` | "releasedAt": "2026-09-17T03:29:46+07:00", |
| `backend/AppReleases/releases.json` | 1405 | `+07:00` | "releasedAt": "2026-09-17T02:54:46+07:00", |
| `backend/AppReleases/releases.json` | 1428 | `+07:00` | "releasedAt": "2026-09-17T02:39:03+07:00", |
| `backend/AppReleases/releases.json` | 1451 | `+07:00` | "releasedAt": "2026-09-17T02:24:50+07:00", |
| `backend/AppReleases/releases.json` | 1474 | `+07:00` | "releasedAt": "2026-09-17T02:07:52+07:00", |
| `backend/AppReleases/releases.json` | 1497 | `+07:00` | "releasedAt": "2026-09-17T01:48:02+07:00", |
| `backend/AppReleases/releases.json` | 1520 | `+07:00` | "releasedAt": "2026-09-17T01:31:21+07:00", |
| `backend/AppReleases/releases.json` | 1543 | `+07:00` | "releasedAt": "2026-09-17T01:12:21+07:00", |
| `backend/AppReleases/releases.json` | 1562 | `+07:00` | "releasedAt": "2026-09-17T00:58:39+07:00", |
| `backend/AppReleases/releases.json` | 1581 | `+07:00` | "releasedAt": "2026-09-17T00:39:50+07:00", |
| `backend/AppReleases/releases.json` | 1600 | `+07:00` | "releasedAt": "2026-09-17T00:27:14+07:00", |
| `backend/AppReleases/releases.json` | 1619 | `+07:00` | "releasedAt": "2026-09-17T00:09:58+07:00", |
| `backend/AppReleases/releases.json` | 1638 | `+07:00` | "releasedAt": "2026-09-16T23:46:49+07:00", |
| `backend/AppReleases/releases.json` | 1657 | `+07:00` | "releasedAt": "2026-09-16T23:25:16+07:00", |
| `backend/AppReleases/releases.json` | 1676 | `+07:00` | "releasedAt": "2026-09-16T23:00:35+07:00", |
| `backend/AppReleases/releases.json` | 1695 | `+07:00` | "releasedAt": "2026-09-16T22:46:36+07:00", |
| `backend/AppReleases/releases.json` | 1718 | `+07:00` | "releasedAt": "2026-09-16T22:30:16+07:00", |
| `backend/AppReleases/releases.json` | 1741 | `+07:00` | "releasedAt": "2026-09-16T22:06:17+07:00", |
| `backend/AppReleases/releases.json` | 1764 | `+07:00` | "releasedAt": "2026-09-16T21:47:32+07:00", |
| `backend/AppReleases/releases.json` | 1787 | `+07:00` | "releasedAt": "2026-09-16T21:35:53+07:00", |
| `backend/AppReleases/releases.json` | 1806 | `+07:00` | "releasedAt": "2026-09-16T21:22:06+07:00", |
| `backend/AppReleases/releases.json` | 1829 | `+07:00` | "releasedAt": "2026-09-16T20:56:44+07:00", |
| `backend/AppReleases/releases.json` | 1848 | `+07:00` | "releasedAt": "2026-09-16T20:33:57+07:00", |
| `backend/AppReleases/releases.json` | 1867 | `+07:00` | "releasedAt": "2026-09-16T16:32:00+07:00", |
| `backend/AppReleases/releases.json` | 1910 | `+07:00` | "releasedAt": "2026-09-16T10:45:00+07:00", |
| `backend/AppReleases/releases.json` | 1989 | `+07:00` | "releasedAt": "2026-09-15T12:01:00+07:00", |
| `backend/AppReleases/releases.json` | 2008 | `+07:00` | "releasedAt": "2026-09-15T11:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2043 | `+07:00` | "releasedAt": "2026-09-15T10:15:00+07:00", |
| `backend/AppReleases/releases.json` | 2070 | `+07:00` | "releasedAt": "2026-09-14T22:50:00+07:00", |
| `backend/AppReleases/releases.json` | 2097 | `+07:00` | "releasedAt": "2026-09-14T21:30:00+07:00", |
| `backend/AppReleases/releases.json` | 2120 | `+07:00` | "releasedAt": "2026-09-14T20:50:00+07:00", |
| `backend/AppReleases/releases.json` | 2139 | `+07:00` | "releasedAt": "2026-09-14T12:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2162 | `+07:00` | "releasedAt": "2026-09-11T16:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2181 | `+07:00` | "releasedAt": "2026-09-11T16:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2204 | `+07:00` | "releasedAt": "2026-09-11T12:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2227 | `+07:00` | "releasedAt": "2026-09-09T15:48:11+07:00", |
| `backend/AppReleases/releases.json` | 2250 | `+07:00` | "releasedAt": "2026-09-09T14:32:24+07:00", |
| `backend/AppReleases/releases.json` | 2269 | `+07:00` | "releasedAt": "2026-09-09T12:10:00+07:00", |
| `backend/AppReleases/releases.json` | 2288 | `+07:00` | "releasedAt": "2026-09-09T11:33:03+07:00", |
| `backend/AppReleases/releases.json` | 2311 | `+07:00` | "releasedAt": "2026-09-09T10:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2334 | `+07:00` | "releasedAt": "2026-09-09T07:15:00+07:00", |
| `backend/AppReleases/releases.json` | 2357 | `+07:00` | "releasedAt": "2026-09-09T02:55:00+07:00", |
| `backend/AppReleases/releases.json` | 2380 | `+07:00` | "releasedAt": "2026-09-09T02:10:00+07:00", |
| `backend/AppReleases/releases.json` | 2403 | `+07:00` | "releasedAt": "2026-09-09T01:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2430 | `+07:00` | "releasedAt": "2026-09-09T00:45:00+07:00", |
| `backend/AppReleases/releases.json` | 2453 | `+07:00` | "releasedAt": "2026-09-08T23:50:00+07:00", |
| `backend/AppReleases/releases.json` | 2476 | `+07:00` | "releasedAt": "2026-09-08T23:15:00+07:00", |
| `backend/AppReleases/releases.json` | 2495 | `+07:00` | "releasedAt": "2026-09-08T22:53:00+07:00", |
| `backend/AppReleases/releases.json` | 2514 | `+07:00` | "releasedAt": "2026-08-28T16:15:00+07:00", |
| `backend/AppReleases/releases.json` | 2533 | `+07:00` | "releasedAt": "2026-08-27T18:25:00+07:00", |
| `backend/AppReleases/releases.json` | 2552 | `+07:00` | "releasedAt": "2026-08-27T15:25:00+07:00", |
| `backend/AppReleases/releases.json` | 2571 | `+07:00` | "releasedAt": "2026-08-27T14:55:00+07:00", |
| `backend/AppReleases/releases.json` | 2590 | `+07:00` | "releasedAt": "2026-08-27T14:45:00+07:00", |
| `backend/AppReleases/releases.json` | 2609 | `+07:00` | "releasedAt": "2026-08-26T10:20:00+07:00", |
| `backend/AppReleases/releases.json` | 2648 | `+07:00` | "releasedAt": "2026-08-26T00:55:00+07:00", |
| `backend/AppReleases/releases.json` | 2663 | `+07:00` | "releasedAt": "2026-08-25T09:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2682 | `+07:00` | "releasedAt": "2026-08-24T10:15:00+07:00", |
| `backend/AppReleases/releases.json` | 2697 | `+07:00` | "releasedAt": "2026-08-23T21:00:00+07:00", |
| `backend/AppReleases/releases.json` | 2720 | `+07:00` | "releasedAt": "2026-08-23T20:42:00+07:00", |
| `backend/AppReleases/releases.json` | 2739 | `+07:00` | "releasedAt": "2026-08-23T20:37:00+07:00", |
| `backend/AppReleases/releases.json` | 2758 | `+07:00` | "releasedAt": "2026-08-23T20:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2781 | `+07:00` | "releasedAt": "2026-08-23T09:25:00+07:00", |
| `backend/AppReleases/releases.json` | 2800 | `+07:00` | "releasedAt": "2026-08-23T02:05:00+07:00", |
| `backend/AppReleases/releases.json` | 2819 | `+07:00` | "releasedAt": "2026-08-23T01:05:00+07:00", |
| `backend/AppReleases/releases.json` | 2854 | `+07:00` | "releasedAt": "2026-08-15T15:25:00+07:00", |
| `backend/AppReleases/releases.json` | 2869 | `+07:00` | "releasedAt": "2026-08-15T14:58:00+07:00", |
| `backend/AppReleases/releases.json` | 2892 | `+07:00` | "releasedAt": "2026-08-15T14:35:00+07:00", |
| `backend/AppReleases/releases.json` | 2907 | `+07:00` | "releasedAt": "2026-08-15T14:05:00+07:00", |
| `backend/AppReleases/releases.json` | 2926 | `+07:00` | "releasedAt": "2026-08-14T10:30:00+07:00", |
| `backend/AppReleases/releases.json` | 2953 | `+07:00` | "releasedAt": "2026-08-13T23:10:00+07:00", |
| `backend/AppReleases/releases.json` | 2984 | `+07:00` | "releasedAt": "2026-08-13T22:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3006 | `+07:00` | "releasedAt": "2026-08-13T15:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3028 | `+07:00` | "releasedAt": "2026-08-13T03:15:00+07:00", |
| `backend/AppReleases/releases.json` | 3050 | `+07:00` | "releasedAt": "2026-08-11T14:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3080 | `+07:00` | "releasedAt": "2026-08-11T01:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3102 | `+07:00` | "releasedAt": "2026-08-10T23:59:30+07:00", |
| `backend/AppReleases/releases.json` | 3116 | `+07:00` | "releasedAt": "2026-08-10T23:59:00+07:00", |
| `backend/AppReleases/releases.json` | 3130 | `+07:00` | "releasedAt": "2026-08-10T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 3144 | `+07:00` | "releasedAt": "2026-08-10T23:50:00+07:00", |
| `backend/AppReleases/releases.json` | 3158 | `+07:00` | "releasedAt": "2026-08-10T23:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3172 | `+07:00` | "releasedAt": "2026-08-10T23:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3186 | `+07:00` | "releasedAt": "2026-08-10T23:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3200 | `+07:00` | "releasedAt": "2026-08-10T22:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3214 | `+07:00` | "releasedAt": "2026-08-10T22:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3228 | `+07:00` | "releasedAt": "2026-08-10T21:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3246 | `+07:00` | "releasedAt": "2026-08-10T20:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3264 | `+07:00` | "releasedAt": "2026-08-10T19:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3277 | `+07:00` | "releasedAt": "2026-08-10T18:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3290 | `+07:00` | "releasedAt": "2026-08-10T17:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3307 | `+07:00` | "releasedAt": "2026-08-10T16:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3320 | `+07:00` | "releasedAt": "2026-08-10T15:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3335 | `+07:00` | "releasedAt": "2026-08-10T14:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3350 | `+07:00` | "releasedAt": "2026-08-10T13:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3369 | `+07:00` | "releasedAt": "2026-08-09T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 3384 | `+07:00` | "releasedAt": "2026-08-09T23:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3399 | `+07:00` | "releasedAt": "2026-08-09T23:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3414 | `+07:00` | "releasedAt": "2026-08-09T23:15:00+07:00", |
| `backend/AppReleases/releases.json` | 3433 | `+07:00` | "releasedAt": "2026-08-09T22:05:00+07:00", |
| `backend/AppReleases/releases.json` | 3448 | `+07:00` | "releasedAt": "2026-08-09T21:20:00+07:00", |
| `backend/AppReleases/releases.json` | 3463 | `+07:00` | "releasedAt": "2026-08-09T20:10:00+07:00", |
| `backend/AppReleases/releases.json` | 3478 | `+07:00` | "releasedAt": "2026-08-09T18:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3497 | `+07:00` | "releasedAt": "2026-08-09T18:10:00+07:00", |
| `backend/AppReleases/releases.json` | 3516 | `+07:00` | "releasedAt": "2026-08-09T17:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3535 | `+07:00` | "releasedAt": "2026-08-09T16:40:00+07:00", |
| `backend/AppReleases/releases.json` | 3558 | `+07:00` | "releasedAt": "2026-08-09T16:10:00+07:00", |
| `backend/AppReleases/releases.json` | 3585 | `+07:00` | "releasedAt": "2026-08-09T15:20:00+07:00", |
| `backend/AppReleases/releases.json` | 3608 | `+07:00` | "releasedAt": "2026-08-09T14:10:00+07:00", |
| `backend/AppReleases/releases.json` | 3627 | `+07:00` | "releasedAt": "2026-08-09T13:25:00+07:00", |
| `backend/AppReleases/releases.json` | 3642 | `+07:00` | "releasedAt": "2026-08-07T18:05:00+07:00", |
| `backend/AppReleases/releases.json` | 3657 | `+07:00` | "releasedAt": "2026-08-07T17:55:00+07:00", |
| `backend/AppReleases/releases.json` | 3672 | `+07:00` | "releasedAt": "2026-08-07T17:40:00+07:00", |
| `backend/AppReleases/releases.json` | 3687 | `+07:00` | "releasedAt": "2026-08-07T17:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3702 | `+07:00` | "releasedAt": "2026-08-07T15:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3721 | `+07:00` | "releasedAt": "2026-08-06T22:05:00+07:00", |
| `backend/AppReleases/releases.json` | 3740 | `+07:00` | "releasedAt": "2026-08-06T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3755 | `+07:00` | "releasedAt": "2026-08-06T17:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3778 | `+07:00` | "releasedAt": "2026-08-05T19:30:00+07:00", |
| `backend/AppReleases/releases.json` | 3801 | `+07:00` | "releasedAt": "2026-08-05T17:10:06+07:00", |
| `backend/AppReleases/releases.json` | 3824 | `+07:00` | "releasedAt": "2026-08-05T14:41:03+07:00", |
| `backend/AppReleases/releases.json` | 3847 | `+07:00` | "releasedAt": "2026-08-05T12:18:04+07:00", |
| `backend/AppReleases/releases.json` | 3866 | `+07:00` | "releasedAt": "2026-08-05T11:39:17+07:00", |
| `backend/AppReleases/releases.json` | 3885 | `+07:00` | "releasedAt": "2026-08-05T11:20:00+07:00", |
| `backend/AppReleases/releases.json` | 3904 | `+07:00` | "releasedAt": "2026-08-05T10:00:00+07:00", |
| `backend/AppReleases/releases.json` | 3923 | `+07:00` | "releasedAt": "2026-08-05T07:35:00+07:00", |
| `backend/AppReleases/releases.json` | 3942 | `+07:00` | "releasedAt": "2026-08-05T07:01:45+07:00", |
| `backend/AppReleases/releases.json` | 3965 | `+07:00` | "releasedAt": "2026-08-05T05:45:00+07:00", |
| `backend/AppReleases/releases.json` | 3988 | `+07:00` | "releasedAt": "2026-08-05T04:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4011 | `+07:00` | "releasedAt": "2026-08-05T03:40:00+07:00", |
| `backend/AppReleases/releases.json` | 4034 | `+07:00` | "releasedAt": "2026-08-05T02:36:00+07:00", |
| `backend/AppReleases/releases.json` | 4057 | `+07:00` | "releasedAt": "2026-08-05T01:54:00+07:00", |
| `backend/AppReleases/releases.json` | 4076 | `+07:00` | "releasedAt": "2026-08-05T01:06:00+07:00", |
| `backend/AppReleases/releases.json` | 4095 | `+07:00` | "releasedAt": "2026-08-05T00:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4110 | `+07:00` | "releasedAt": "2026-08-05T00:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4125 | `+07:00` | "releasedAt": "2026-08-04T17:30:00+07:00", |
| `backend/AppReleases/releases.json` | 4140 | `+07:00` | "releasedAt": "2026-08-04T15:35:00+07:00", |
| `backend/AppReleases/releases.json` | 4159 | `+07:00` | "releasedAt": "2026-08-04T15:12:00+07:00", |
| `backend/AppReleases/releases.json` | 4174 | `+07:00` | "releasedAt": "2026-08-04T14:55:00+07:00", |
| `backend/AppReleases/releases.json` | 4189 | `+07:00` | "releasedAt": "2026-08-04T13:20:00+07:00", |
| `backend/AppReleases/releases.json` | 4204 | `+07:00` | "releasedAt": "2026-08-04T12:56:00+07:00", |
| `backend/AppReleases/releases.json` | 4219 | `+07:00` | "releasedAt": "2026-08-04T12:30:00+07:00", |
| `backend/AppReleases/releases.json` | 4242 | `+07:00` | "releasedAt": "2026-08-04T11:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4261 | `+07:00` | "releasedAt": "2026-08-04T10:49:00+07:00", |
| `backend/AppReleases/releases.json` | 4280 | `+07:00` | "releasedAt": "2026-08-04T10:10:00+07:00", |
| `backend/AppReleases/releases.json` | 4299 | `+07:00` | "releasedAt": "2026-08-03T19:30:00+07:00", |
| `backend/AppReleases/releases.json` | 4314 | `+07:00` | "releasedAt": "2026-08-01T03:38:46+07:00", |
| `backend/AppReleases/releases.json` | 4337 | `+07:00` | "releasedAt": "2026-08-01T03:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4360 | `+07:00` | "releasedAt": "2026-08-01T02:40:28+07:00", |
| `backend/AppReleases/releases.json` | 4383 | `+07:00` | "releasedAt": "2026-08-01T00:11:00+07:00", |
| `backend/AppReleases/releases.json` | 4406 | `+07:00` | "releasedAt": "2026-08-01T00:10:00+07:00", |
| `backend/AppReleases/releases.json` | 4425 | `+07:00` | "releasedAt": "2026-08-01T00:09:00+07:00", |
| `backend/AppReleases/releases.json` | 4448 | `+07:00` | "releasedAt": "2026-08-01T00:08:00+07:00", |
| `backend/AppReleases/releases.json` | 4471 | `+07:00` | "releasedAt": "2026-08-01T00:07:00+07:00", |
| `backend/AppReleases/releases.json` | 4494 | `+07:00` | "releasedAt": "2026-08-01T00:06:00+07:00", |
| `backend/AppReleases/releases.json` | 4517 | `+07:00` | "releasedAt": "2026-08-01T00:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4540 | `+07:00` | "releasedAt": "2026-08-01T00:04:00+07:00", |
| `backend/AppReleases/releases.json` | 4559 | `+07:00` | "releasedAt": "2026-08-01T00:03:00+07:00", |
| `backend/AppReleases/releases.json` | 4582 | `+07:00` | "releasedAt": "2026-08-01T00:02:00+07:00", |
| `backend/AppReleases/releases.json` | 4605 | `+07:00` | "releasedAt": "2026-08-01T00:01:00+07:00", |
| `backend/AppReleases/releases.json` | 4628 | `+07:00` | "releasedAt": "2026-08-01T00:00:00+07:00", |
| `backend/AppReleases/releases.json` | 4651 | `+07:00` | "releasedAt": "2026-07-31T23:59:30+07:00", |
| `backend/AppReleases/releases.json` | 4674 | `+07:00` | "releasedAt": "2026-07-31T23:59:00+07:00", |
| `backend/AppReleases/releases.json` | 4697 | `+07:00` | "releasedAt": "2026-07-31T23:55:00+07:00", |
| `backend/AppReleases/releases.json` | 4720 | `+07:00` | "releasedAt": "2026-07-31T23:35:00+07:00", |
| `backend/AppReleases/releases.json` | 4743 | `+07:00` | "releasedAt": "2026-07-31T23:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4762 | `+07:00` | "releasedAt": "2026-07-31T22:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4789 | `+07:00` | "releasedAt": "2026-07-31T22:15:00+07:00", |
| `backend/AppReleases/releases.json` | 4812 | `+07:00` | "releasedAt": "2026-07-31T21:05:00+07:00", |
| `backend/AppReleases/releases.json` | 4835 | `+07:00` | "releasedAt": "2026-07-31T20:35:00+07:00", |
| `backend/AppReleases/releases.json` | 4858 | `+07:00` | "releasedAt": "2026-07-31T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 4881 | `+07:00` | "releasedAt": "2026-07-31T19:10:00+07:00", |
| `backend/AppReleases/releases.json` | 4904 | `+07:00` | "releasedAt": "2026-07-31T18:10:00+07:00", |
| `backend/AppReleases/releases.json` | 4927 | `+07:00` | "releasedAt": "2026-07-31T16:35:00+07:00", |
| `backend/AppReleases/releases.json` | 4950 | `+07:00` | "releasedAt": "2026-07-31T16:00:00+07:00", |
| `backend/AppReleases/releases.json` | 4965 | `+07:00` | "releasedAt": "2026-07-31T15:25:00+07:00", |
| `backend/AppReleases/releases.json` | 4992 | `+07:00` | "releasedAt": "2026-07-31T14:35:00+07:00", |
| `backend/AppReleases/releases.json` | 5019 | `+07:00` | "releasedAt": "2026-07-31T11:15:00+07:00", |
| `backend/AppReleases/releases.json` | 5046 | `+07:00` | "releasedAt": "2026-07-31T10:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5073 | `+07:00` | "releasedAt": "2026-07-31T09:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5100 | `+07:00` | "releasedAt": "2026-07-31T08:04:00+07:00", |
| `backend/AppReleases/releases.json` | 5123 | `+07:00` | "releasedAt": "2026-07-30T19:45:00+07:00", |
| `backend/AppReleases/releases.json` | 5146 | `+07:00` | "releasedAt": "2026-07-30T19:30:00+07:00", |
| `backend/AppReleases/releases.json` | 5165 | `+07:00` | "releasedAt": "2026-07-30T19:20:00+07:00", |
| `backend/AppReleases/releases.json` | 5188 | `+07:00` | "releasedAt": "2026-07-30T19:10:00+07:00", |
| `backend/AppReleases/releases.json` | 5207 | `+07:00` | "releasedAt": "2026-07-30T18:55:00+07:00", |
| `backend/AppReleases/releases.json` | 5226 | `+07:00` | "releasedAt": "2026-07-30T18:40:00+07:00", |
| `backend/AppReleases/releases.json` | 5245 | `+07:00` | "releasedAt": "2026-07-30T18:25:00+07:00", |
| `backend/AppReleases/releases.json` | 5268 | `+07:00` | "releasedAt": "2026-07-30T17:40:00+07:00", |
| Еще 842 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

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
| `src/App.tsx` | 137 | `new Date(` | const now = new Date(); |
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
| `src/components/Accounts/Installments.tsx` | 264 | `formatDate` | case 'date': return <span className="font-medium text-slate-900">{formatDate(invoice.date)}</span>; |
| `src/components/Accounts/Installments.tsx` | 286 | `formatDate` | case 'dueDate': return formatDate(invoice.dueDate); |
| `src/components/Accounts/Installments.tsx` | 290 | `formatDate` | case 'createdAt': return formatDate(invoice.createdAt); |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 364 | `new Date(` | vi.setSystemTime(new Date('2026-06-04T22:30:00.000Z')); |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 382 | `type="date"` | const dateInput = container.querySelector('input[type="date"]') as HTMLInputElement \| null; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1543 | `type="date"` | const dateInputs = document.querySelectorAll('input[type="date"]') as NodeListOf<HTMLInputElement>; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1587 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1637 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.integration.test.tsx` | 1728 | `type="date"` | const dateInputs = Array.from(document.querySelectorAll('input[type="date"]')) as HTMLInputElement[]; |
| `src/components/Accounts/InvoiceModal.tsx` | 1780 | `new Date(` | (left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime(), |
| `src/components/Accounts/InvoiceModal.tsx` | 2468 | `new Date(` | return next.sort((left, right) => new Date(right.createdAt).getTime() - new Date(left.createdAt).getTime()); |
| `src/components/Accounts/InvoiceModal.tsx` | 3513 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Accounts/InvoiceModal.tsx` | 525 | `Date.parse(` | const leftDate = Date.parse(left.date ?? ''); |
| `src/components/Accounts/InvoiceModal.tsx` | 526 | `Date.parse(` | const rightDate = Date.parse(right.date ?? ''); |
| `src/components/Accounts/InvoiceModal.tsx` | 781 | `Date.parse(` | const fallbackTimestamp = Date.parse(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt ?? '') \|\| 0; |
| `src/components/Accounts/InvoiceModal.tsx` | 3513 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Accounts/InvoiceModal.tsx` | 5096 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5107 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5123 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5705 | `type="date"` | type="date" |
| `src/components/Accounts/InvoiceModal.tsx` | 5884 | `type="date"` | <SystemDateInput type="date" value={draftAct.date} onChange={(e) => setDraftAct((prev) => ({ ...prev, date: e.target.value }))} className="w-full rounded-lg border border-slate-300 px-3 py-2 text-sm" /> |
| `src/components/Accounts/InvoiceModal.tsx` | 534 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Accounts/InvoiceModal.tsx` | 592 | `formatDate` | const dateLabel = formatDate(contract.date) \|\| 'без даты'; |
| `src/components/Accounts/InvoiceModal.tsx` | 787 | `formatDate` | timestampLabel: formatDate(payment.lastPaymentDate ?? payment.paidDate ?? payment.createdAt) \|\| 'Без даты', |
| `src/components/Accounts/InvoiceModal.tsx` | 2789 | `formatDate` | const currentDueDateLabel = formatDate(currentDueDate) \|\| 'не указан'; |
| `src/components/Accounts/InvoiceModal.tsx` | 2790 | `formatDate` | const expectedDueDateLabel = formatDate(expectedDueDate); |
| `src/components/Accounts/InvoiceModal.tsx` | 3821 | `formatDate` | {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full} |
| `src/components/Accounts/InvoiceModal.tsx` | 3883 | `formatDate` | {formatDate(act.date)} · {formatCurrencySmart(act.amount ?? 0).full} |
| `src/components/Accounts/InvoiceModal.tsx` | 3941 | `formatDate` | ? `Счёт №${form.number \|\| '—'} от ${formatDate(form.date) \|\| '—'}` |
| `src/components/Accounts/InvoiceModal.tsx` | 4157 | `formatDate` | <span>Ожидаемая дата: {formatDate(expectedPaymentDate) \|\| '—'}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4159 | `formatDate` | <span>Фактическая дата: {formatDate(actualPaymentDate) \|\| actualPaymentDate}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4251 | `formatDate` | <p><span className="text-slate-500">Дата выставления:</span> {formatDate(form.date) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4252 | `formatDate` | <p><span className="text-slate-500">Срок оплаты:</span> {formatDate(form.dueDate) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4255 | `formatDate` | <p><span className="text-slate-500">Дата оплаты:</span> {formatDate(form.paidDate) \|\| '—'}</p> |
| `src/components/Accounts/InvoiceModal.tsx` | 4780 | `formatDate` | <span>Ожидаемая дата: {formatDate(expectedPaymentDate) \|\| '—'}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4782 | `formatDate` | <span>Фактическая дата: {formatDate(actualPaymentDate) \|\| actualPaymentDate}</span> |
| `src/components/Accounts/InvoiceModal.tsx` | 4898 | `formatDate` | <div>{formatDate(operation.date) \|\| 'Без даты'}</div> |
| `src/components/Accounts/InvoiceModal.tsx` | 5227 | `formatDate` | {formatDate(selectedContractPreview.date) \|\| '—'} |
| `src/components/Accounts/InvoiceModal.tsx` | 5235 | `formatDate` | : (formatDate(selectedContractPreview.validUntil) \|\| '—')} |
| `src/components/Accounts/InvoiceModal.tsx` | 5361 | `formatDate` | {formatDate(selectedPaymentPreviewExpectedDate) \|\| '—'} |
| `src/components/Accounts/InvoiceModal.tsx` | 5368 | `formatDate` | {formatDate(selectedPaymentPreviewActualDate) \|\| selectedPaymentPreviewActualDate} |
| `src/components/Acts/ActDetailPage.tsx` | 85 | `formatDate` | function formatDate(value?: string \| null): string { |
| `src/components/Acts/ActDetailPage.tsx` | 400 | `formatDate` | ? `Акт №${act.number \|\| act.id} от ${formatDate(act.date)}` |
| `src/components/Acts/ActDetailPage.tsx` | 820 | `formatDate` | Акт №{act.number \|\| '—'} от {formatDate(act.date)} |
| `src/components/Acts/ActDetailPage.tsx` | 958 | `formatDate` | №{correction.number \|\| correction.id} от {formatDate(correction.date)} |
| `src/components/Acts/ActDetailPage.tsx` | 968 | `formatDate` | <p><span className="text-slate-500">Дата акта:</span> {formatDate(act.date)}</p> |
| `src/components/Acts/ActDetailPage.tsx` | 971 | `formatDate` | <p><span className="text-slate-500">Аннулирование:</span> {[formatDate(act.cancelledAt), act.cancelledReason?.trim()].filter(Boolean).join(' · ')}</p> |
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
| `src/components/Calendar/PaymentModal.integration.test.tsx` | 357 | `new Date(` | vi.setSystemTime(new Date('2026-06-04T20:30:00.000Z')); |
| `src/components/Calendar/PaymentModal.integration.test.tsx` | 739 | `new Date(` | vi.setSystemTime(new Date('2026-06-04T20:30:00.000Z')); |
| `src/components/Calendar/PaymentModal.tsx` | 3130 | `type="date"` | <div><label className={centeredLabelClass}>{t('expectedPaymentDate') ?? 'Ожидаемая дата платежа'}</label><SystemDateInput type="date" name="date" aria-label="Ожидаемая дата платежа" value={formData.date} onChange={handle |
| `src/components/Calendar/PaymentModal.tsx` | 3131 | `type="date"` | <div><label className={centeredLabelClass}>{t('plannedScheduleDate') ?? 'Плановая дата по графику'}</label><SystemDateInput type="date" name="plannedDate" aria-label="Плановая дата по графику" value={formData.plannedDate |
| `src/components/Calendar/PaymentModal.tsx` | 3174 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3332 | `type="date"` | <SystemDateInput type="date" name="lastPaymentDate" aria-label="Дата фактического платежа" value={formData.lastPaymentDate} onChange={handleChange} className={centeredFieldClass} /> |
| `src/components/Calendar/PaymentModal.tsx` | 3338 | `type="date"` | <SystemDateInput type="date" name="paidDate" aria-label="Дата оплаты" value={formData.paidDate} onChange={handleChange} className={centeredFieldClass} /> |
| `src/components/Calendar/PaymentModal.tsx` | 3657 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3688 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3867 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentModal.tsx` | 3899 | `type="date"` | type="date" |
| `src/components/Calendar/PaymentsTable.test.tsx` | 10 | `formatDate` | formatDate: (value: string) => value, |
| `src/components/Calendar/PaymentsTable.tsx` | 114 | `formatDate` | const { t, formatDate } = useTranslation(); |
| `src/components/Calendar/PaymentsTable.tsx` | 212 | `formatDate` | ? `Документ: ${payment.paymentOrderNumber}${payment.paymentDocumentDate ? ` от ${formatDate(payment.paymentDocumentDate)}` : ''}` |
| `src/components/Calendar/PaymentsTable.tsx` | 214 | `formatDate` | ? `Дата документа: ${formatDate(payment.paymentDocumentDate)}` |
| `src/components/Calendar/PaymentsTable.tsx` | 217 | `formatDate` | ? `Счет: ${payment.account}${payment.accountDate ? ` от ${formatDate(payment.accountDate)}` : ''}` |
| `src/components/Calendar/PaymentsTable.tsx` | 230 | `formatDate` | return payment.date ? formatDate(payment.date) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 232 | `formatDate` | return payment.plannedDate ? formatDate(payment.plannedDate) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 451 | `formatDate` | ? formatDate(payment.paidDate \|\| payment.lastPaymentDate \|\| '') |
| `src/components/Calendar/PaymentsTable.tsx` | 454 | `formatDate` | return payment.createdAt ? formatDate(payment.createdAt) : '—'; |
| `src/components/Calendar/PaymentsTable.tsx` | 459 | `formatDate` | [canEdit, canOpenCases, canOpenClients, canOpenUsers, formatDate, onAcceptAdvance, onAllocateAdvance, onIgnoreAdvance, onOpenClient, onOpenUser, onRefundAdvance, onStatusChange, statusLabels, t], |
| `src/components/Cases/AppealDeadlineDialog.tsx` | 23 | `new Date(` | const parts = getZonedParts(new Date(), timeZoneId); |
| `src/components/Cases/CaseAppealDecisionAction.tsx` | 48 | `new Date(` | const todayParts = getZonedParts(new Date(), timeZoneId); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 979 | `new Date(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 983 | `new Date(` | nextHearingConfirmedAt: new Date().toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1000 | `new Date(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1477 | `new Date(` | const latestActivityAt = new Date(); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1478 | `new Date(` | const nextControlAt = new Date(latestActivityAt.getTime() + 14 * 24 * 60 * 60 * 1000); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 979 | `.toISOString(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 983 | `.toISOString(` | nextHearingConfirmedAt: new Date().toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1000 | `.toISOString(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1480 | `.toISOString(` | lastTaskAt: latestActivityAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1481 | `.toISOString(` | lastMeaningfulActivityAt: latestActivityAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1482 | `.toISOString(` | nextInactivityControlAt: nextControlAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1486 | `.toISOString(` | lastViewedAt: latestActivityAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1489 | `.toISOString(` | lastViewedByResponsibleAt: latestActivityAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1496 | `.toISOString(` | lastMeaningfulActivityAt: latestActivityAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.test.tsx` | 1497 | `.toISOString(` | nextInactivityControlAt: nextControlAt.toISOString(), |
| `src/components/Cases/CaseDetailPage.tsx` | 649 | `new Date(` | const date = new Date(getPaymentCalendarDate(payment)); |
| `src/components/Cases/CaseDetailPage.tsx` | 663 | `new Date(` | const date = new Date(year, month - 1, 1); |
| `src/components/Cases/CaseDetailPage.tsx` | 928 | `new Date(` | now = new Date(), |
| `src/components/Cases/CaseDetailPage.tsx` | 933 | `new Date(` | const warningDateKey = toTimeZoneDateKey(new Date(nowTime + UPCOMING_DEADLINE_WARNING_MS), timeZoneId) |
| `src/components/Cases/CaseDetailPage.tsx` | 934 | `new Date(` | \|\| toDateInputValue(new Date(nowTime + UPCOMING_DEADLINE_WARNING_MS).toISOString()); |
| `src/components/Cases/CaseDetailPage.tsx` | 1012 | `new Date(` | now = new Date(), |
| `src/components/Cases/CaseDetailPage.tsx` | 1018 | `new Date(` | : `Последнее действие: ${formatRuDateTime(new Date(latestActivityTime).toISOString(), timeZoneId ?? undefined)}`; |
| `src/components/Cases/CaseDetailPage.tsx` | 1048 | `new Date(` | const nextControlLabel = formatRuDateTime(new Date(nextControlTime).toISOString(), timeZoneId ?? undefined); |
| `src/components/Cases/CaseDetailPage.tsx` | 3266 | `new Date(` | const startAt = caseItem.courtDecisionAt \|\| caseItem.claimFiledAt \|\| caseItem.nextHearingAt \|\| new Date().toISOString(); |
| `src/components/Cases/CaseDetailPage.tsx` | 3836 | `new Date(` | : getNearestCaseDateSummary(caseItem, proceduralDeadlines, new Date(), caseTimeZoneId); |
| `src/components/Cases/CaseDetailPage.tsx` | 3837 | `new Date(` | const inactivitySummary = getInactivitySummary(caseItem, new Date(), caseTimeZoneId); |
| `src/components/Cases/CaseDetailPage.tsx` | 6674 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 6679 | `new Date(` | const day = new Date(date); |
| `src/components/Cases/CaseDetailPage.tsx` | 6681 | `new Date(` | const today = new Date(); |
| `src/components/Cases/CaseDetailPage.tsx` | 7199 | `new Date(` | .sort((left, right) => new Date(left.scheduledAt).getTime() - new Date(right.scheduledAt).getTime()); |
| `src/components/Cases/CaseDetailPage.tsx` | 7203 | `new Date(` | .sort((left, right) => new Date(right.scheduledAt).getTime() - new Date(left.scheduledAt).getTime()) |
| `src/components/Cases/CaseDetailPage.tsx` | 988 | `Date.parse(` | const time = Date.parse(value); |
| `src/components/Cases/CaseDetailPage.tsx` | 1091 | `Date.parse(` | const parsed = value ? Date.parse(value) : Number.NaN; |
| `src/components/Cases/CaseDetailPage.tsx` | 1157 | `Date.parse(` | const dueAt = Date.parse(task.dueAt); |
| `src/components/Cases/CaseDetailPage.tsx` | 3145 | `Date.parse(` | setCourtEvents((current) => [...current, item].sort((left, right) => Date.parse(left.scheduledAt) - Date.parse(right.scheduledAt))); |
| `src/components/Cases/CaseDetailPage.tsx` | 3248 | `Date.parse(` | setProceduralDeadlines((current) => [...current, item].sort((left, right) => Date.parse(left.dueAt) - Date.parse(right.dueAt))); |
| `src/components/Cases/CaseDetailPage.tsx` | 5896 | `Date.parse(` | const leftTime = Date.parse(left.plannedDate \|\| left.date); |
| `src/components/Cases/CaseDetailPage.tsx` | 5897 | `Date.parse(` | const rightTime = Date.parse(right.plannedDate \|\| right.date); |
| `src/components/Cases/CaseDetailPage.tsx` | 6777 | `Date.parse(` | const leftTime = Date.parse(left.value ?? ''); |
| `src/components/Cases/CaseDetailPage.tsx` | 6778 | `Date.parse(` | const rightTime = Date.parse(right.value ?? ''); |
| `src/components/Cases/CaseDetailPage.tsx` | 7330 | `Date.parse(` | : caseItem.nextHearingConfirmedAt && caseItem.nextHearingConfirmedForAt && Date.parse(caseItem.nextHearingConfirmedForAt) === Date.parse(caseItem.nextHearingAt) |
| `src/components/Cases/CaseDetailPage.tsx` | 7333 | `Date.parse(` | {canEdit && caseItem.nextHearingAt && !['Closed', 'Lost'].includes(caseItem.status) && Date.parse(caseItem.nextHearingAt) > Date.now() && (!caseItem.nextHearingConfirmedAt \|\| !caseItem.nextHearingConfirmedForAt \|\| Date.p |
| `src/components/Cases/CaseDetailPage.tsx` | 7422 | `Date.parse(` | : caseItem.nextHearingConfirmedAt && caseItem.nextHearingConfirmedForAt && Date.parse(caseItem.nextHearingConfirmedForAt) === Date.parse(caseItem.nextHearingAt) |
| `src/components/Cases/CaseDetailPage.tsx` | 7425 | `Date.parse(` | {canEdit && caseItem.nextHearingAt && !['Closed', 'Lost'].includes(caseItem.status) && Date.parse(caseItem.nextHearingAt) > Date.now() && (!caseItem.nextHearingConfirmedAt \|\| !caseItem.nextHearingConfirmedForAt \|\| Date.p |
| `src/components/Cases/CaseDetailPage.tsx` | 932 | `.toISOString(` | const todayKey = toTimeZoneDateKey(now, timeZoneId) \|\| toDateInputValue(now.toISOString()); |
| `src/components/Cases/CaseDetailPage.tsx` | 934 | `.toISOString(` | \|\| toDateInputValue(new Date(nowTime + UPCOMING_DEADLINE_WARNING_MS).toISOString()); |
| `src/components/Cases/CaseDetailPage.tsx` | 1018 | `.toISOString(` | : `Последнее действие: ${formatRuDateTime(new Date(latestActivityTime).toISOString(), timeZoneId ?? undefined)}`; |
| `src/components/Cases/CaseDetailPage.tsx` | 1048 | `.toISOString(` | const nextControlLabel = formatRuDateTime(new Date(nextControlTime).toISOString(), timeZoneId ?? undefined); |
| `src/components/Cases/CaseDetailPage.tsx` | 3266 | `.toISOString(` | const startAt = caseItem.courtDecisionAt \|\| caseItem.claimFiledAt \|\| caseItem.nextHearingAt \|\| new Date().toISOString(); |
| `src/components/Cases/CaseDetailPage.tsx` | 664 | `Intl.DateTimeFormat` | return new Intl.DateTimeFormat('ru-RU', { month: 'long', year: 'numeric' }).format(date); |
| `src/components/Cases/CaseDetailPage.tsx` | 4860 | `type="date"` | <SystemDateInput type="date" value={draft.startDate} onChange={(event) => setField('startDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4864 | `type="date"` | <SystemDateInput type="date" value={draft.dueDate} onChange={(event) => setField('dueDate', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4982 | `type="date"` | <label className="text-sm font-medium text-slate-700">Иск подан<SystemDateInput type="date" value={draft.claimFiledAt} onChange={(event) => setField('claimFiledAt', event.target.value)} className="mt-1 h-10 w-full rounde |
| `src/components/Cases/CaseDetailPage.tsx` | 4983 | `type="date"` | <label className="text-sm font-medium text-slate-700">Решение суда<SystemDateInput type="date" value={draft.courtDecisionAt} onChange={(event) => setField('courtDecisionAt', event.target.value)} className="mt-1 h-10 w-fu |
| `src/components/Cases/CaseDetailPage.tsx` | 4984 | `type="date"` | <label className="text-sm font-medium text-slate-700">Решение вступило в силу<SystemDateInput aria-label="Дата вступления решения в силу" type="date" value={draft.courtDecisionEffectiveAt} onChange={(event) => setField(' |
| `src/components/Cases/CaseDetailPage.tsx` | 4985 | `type="date"` | <div><label className="text-sm font-medium text-slate-700">Исполнительный лист получен<SystemDateInput aria-label="Дата получения исполнительного листа" type="date" value={draft.executiveWritReceivedAt} onChange={(event) |
| `src/components/Cases/CaseDetailPage.tsx` | 4987 | `type="date"` | <label className="text-sm font-medium text-slate-700">Исполнительный лист подан<SystemDateInput aria-label="Дата подачи исполнительного листа" type="date" value={draft.executiveWritSubmittedAt} onChange={(event) => setFi |
| `src/components/Cases/CaseDetailPage.tsx` | 4991 | `type="date"` | <label className="text-sm font-medium text-slate-700">Апелляция<SystemDateInput type="date" value={draft.appealDeadlineAt} onChange={(event) => setField('appealDeadlineAt', event.target.value)} className="mt-1 h-10 w-ful |
| `src/components/Cases/CaseDetailPage.tsx` | 4992 | `type="date"` | <label className="text-sm font-medium text-slate-700">Кассация<SystemDateInput type="date" value={draft.cassationDeadlineAt} onChange={(event) => setField('cassationDeadlineAt', event.target.value)} className="mt-1 h-10  |
| `src/components/Cases/CaseDetailPage.tsx` | 4993 | `type="date"` | <label className="text-sm font-medium text-slate-700">Возражения<SystemDateInput type="date" value={draft.objectionDeadlineAt} onChange={(event) => setField('objectionDeadlineAt', event.target.value)} className="mt-1 h-1 |
| `src/components/Cases/CaseDetailPage.tsx` | 5752 | `type="date"` | <WorkOrderTextInput label="Срок" type="date" value={newItemDraft.dueDate} onChange={(value) => onNewItemDraftChange({ ...newItemDraft, dueDate: value })} disabled={isWorkOrderLocked} /> |
| `src/components/Cases/CaseDetailPage.tsx` | 5778 | `type="date"` | <WorkOrderTextInput label="Срок" type="date" value={itemDraft.dueDate} onChange={(value) => onItemDraftChange(item.id, { ...itemDraft, dueDate: value })} disabled={isWorkOrderLocked} /> |
| `src/components/Cases/CaseDetailPage.tsx` | 5841 | `type="date"` | type="date" |
| `src/components/Cases/CaseDetailPage.tsx` | 7690 | `type="date"` | <SystemDateInput type="date" value={courtComplaintDraft.filedAt} onChange={(event) => onCourtComplaintDraftChange({ ...courtComplaintDraft, filedAt: event.target.value })} className="w-full rounded-lg border border-slate |
| `src/components/Cases/CaseDetailPage.tsx` | 7987 | `type="date"` | <SystemDateInput type="date" value={deadlineDueAtDraft} onChange={(event) => onDeadlineDueAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 4914 | `type="datetime-local"` | <SystemDateInput type="datetime-local" value={draft.nextHearingAt} onChange={(event) => setField('nextHearingAt', event.target.value)} className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm" /> |
| `src/components/Cases/CaseDetailPage.tsx` | 7860 | `type="datetime-local"` | <SystemDateInput type="datetime-local" value={courtEventScheduledAtDraft} onChange={(event) => onCourtEventScheduledAtChange(event.target.value)} className="w-full rounded-lg border border-slate-200 px-3 py-2 text-sm" /> |
| `src/components/Cases/CasePreHearingActions.tsx` | 21 | `Date.parse(` | const hearingAt = Date.parse(caseItem.nextHearingAt ?? ''); |
| `src/components/Cases/CasesRegistry.test.tsx` | 338 | `new Date(` | const futureControlAt = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(); |
| `src/components/Cases/CasesRegistry.test.tsx` | 339 | `new Date(` | const dueControlAt = new Date(Date.now() - 60 * 60 * 1000).toISOString(); |
| `src/components/Cases/CasesRegistry.test.tsx` | 338 | `.toISOString(` | const futureControlAt = new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString(); |
| `src/components/Cases/CasesRegistry.test.tsx` | 339 | `.toISOString(` | const dueControlAt = new Date(Date.now() - 60 * 60 * 1000).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 246 | `new Date(` | const date = new Date(value); |
| `src/components/Cases/CasesRegistry.tsx` | 255 | `new Date(` | return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 294 | `new Date(` | const nextControlAt = new Date(item.nextInactivityControlAt); |
| `src/components/Cases/CasesRegistry.tsx` | 737 | `new Date(` | updatedAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1423 | `new Date(` | createdAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1246 | `Date.parse(` | const nextControlTime = item.nextInactivityControlAt ? Date.parse(item.nextInactivityControlAt) : Number.NaN; |
| `src/components/Cases/CasesRegistry.tsx` | 255 | `.toISOString(` | return new Date(Math.max(...values.map((value) => value.getTime()))).toISOString(); |
| `src/components/Cases/CasesRegistry.tsx` | 737 | `.toISOString(` | updatedAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1423 | `.toISOString(` | createdAt: new Date().toISOString(), |
| `src/components/Cases/CasesRegistry.tsx` | 1660 | `type="date"` | <SystemDateInput type="date" value={from} onChange={(event) => setFrom(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата с" /> |
| `src/components/Cases/CasesRegistry.tsx` | 1661 | `type="date"` | <SystemDateInput type="date" value={to} onChange={(event) => setTo(event.target.value)} className="h-10 rounded-lg border border-slate-200 bg-white px-3 text-sm" aria-label="Дата по" /> |
| `src/components/Cases/ClientPortalAccessPanel.tsx` | 132 | `new Date(` | {audit.length > 0 ? <details className="mt-3 rounded-lg border border-slate-200 px-3 py-2"><summary className="cursor-pointer text-sm font-medium text-slate-700">Последние действия ({audit.length})</summary><div classNam |
| `src/components/Cases/CourtCasesRegistry.tsx` | 72 | `new Date(` | const diffDays = Math.ceil((new Date(value).getTime() - Date.now()) / 86_400_000); |
| `src/components/Cases/CourtCasesRegistry.tsx` | 204 | `new Date(` | const key = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'long', timeZone: timeZoneId }).format(new Date(item.nextHearingAt!)); |
| `src/components/Cases/CourtCasesRegistry.tsx` | 241 | `new Date(` | title: trackingTitle.trim(), description: 'Судебное дело добавлено из рабочего раздела для автоматического контроля.', status: 'Open', caseNumber: '', caseType: 'Судебное дело', stage: INSTANCE_LABELS[trackingInstance],  |
| `src/components/Cases/CourtCasesRegistry.tsx` | 306 | `new Date(` | <main className="min-w-0"><div className="mb-2 flex flex-wrap items-center justify-between gap-2 px-1"><div className="text-sm font-medium text-slate-700">{scope === 'archive' ? 'Завершённые дела' : 'Активные дела'} · {f |
| `src/components/Cases/CourtCasesRegistry.tsx` | 241 | `.toISOString(` | title: trackingTitle.trim(), description: 'Судебное дело добавлено из рабочего раздела для автоматического контроля.', status: 'Open', caseNumber: '', caseType: 'Судебное дело', stage: INSTANCE_LABELS[trackingInstance],  |
| `src/components/Cases/CourtCasesRegistry.tsx` | 204 | `Intl.DateTimeFormat` | const key = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'long', timeZone: timeZoneId }).format(new Date(item.nextHearingAt!)); |
| `src/components/Cases/CourtCasesRegistry.tsx` | 306 | `Intl.DateTimeFormat` | <main className="min-w-0"><div className="mb-2 flex flex-wrap items-center justify-between gap-2 px-1"><div className="text-sm font-medium text-slate-700">{scope === 'archive' ? 'Завершённые дела' : 'Активные дела'} · {f |
| `src/components/Cases/CourtReportOverviewDialog.tsx` | 48 | `new Date(` | const nextDay = to ? new Date(Date.parse(`${to}T00:00:00Z`) + 86_400_000).toISOString().slice(0, 10) : ''; |
| `src/components/Cases/CourtReportOverviewDialog.tsx` | 48 | `Date.parse(` | const nextDay = to ? new Date(Date.parse(`${to}T00:00:00Z`) + 86_400_000).toISOString().slice(0, 10) : ''; |
| `src/components/Cases/CourtReportOverviewDialog.tsx` | 48 | `.toISOString(` | const nextDay = to ? new Date(Date.parse(`${to}T00:00:00Z`) + 86_400_000).toISOString().slice(0, 10) : ''; |
| `src/components/Cases/CourtReportOverviewDialog.tsx` | 63 | `type="date"` | <div className="mt-3 flex flex-wrap gap-2"><label className="text-xs font-medium text-slate-700">Созданы с<input type="date" aria-label="Дела созданы с" value={from} max={to \|\| undefined} onChange={(event) => setFrom(eve |
| `src/components/Cases/CourtSyncHealthDialog.tsx` | 102 | `formatDate` | const formatDate = (value: string \| null) => value ? formatDateTimeInTimeZone(value, timeZoneId) : 'Нет успешной проверки'; |
| `src/components/Cases/CourtSyncHealthDialog.tsx` | 115 | `formatDate` | <div className="mt-2 grid gap-1 text-xs text-slate-600 sm:grid-cols-2"><div>Последняя успешная проверка: {formatDate(item.lastSuccessAt)}</div><div>Последняя попытка: {item.lastAttemptAt ? formatDate(item.lastAttemptAt)  |
| `src/components/Cases/TomorrowCourtHearingsDialog.tsx` | 19 | `new Date(` | const local = getZonedParts(new Date(), timeZoneId); |
| `src/components/Cases/TomorrowCourtHearingsDialog.tsx` | 21 | `new Date(` | const tomorrow = new Date(Date.UTC(local.year, local.month - 1, local.day + 1)); |
| `src/components/Cases/TomorrowCourtHearingsDialog.tsx` | 22 | `.toISOString(` | return tomorrow.toISOString().slice(0, 10); |
| `src/components/Clients/CaseModal.test.tsx` | 194 | `new Date(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Clients/CaseModal.test.tsx` | 198 | `new Date(` | nextHearingConfirmedAt: new Date().toISOString(), |
| `src/components/Clients/CaseModal.test.tsx` | 217 | `new Date(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Clients/CaseModal.test.tsx` | 194 | `.toISOString(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Clients/CaseModal.test.tsx` | 198 | `.toISOString(` | nextHearingConfirmedAt: new Date().toISOString(), |
| `src/components/Clients/CaseModal.test.tsx` | 217 | `.toISOString(` | const nextHearingAt = new Date(Date.now() + 7 * 86400000).toISOString(); |
| `src/components/Clients/CaseModal.tsx` | 192 | `new Date(` | const daysInTargetMonth = new Date(targetYear, targetMonthIndex + 1, 0).getDate(); |
| `src/components/Clients/CaseModal.tsx` | 193 | `new Date(` | let targetDate = new Date(targetYear, targetMonthIndex, Math.min(day, daysInTargetMonth)); |
| `src/components/Clients/CaseModal.tsx` | 196 | `new Date(` | targetDate = new Date(targetDate.getFullYear(), targetDate.getMonth(), targetDate.getDate() + 1); |
| `src/components/Clients/CaseModal.tsx` | 232 | `new Date(` | const date = new Date(value); |
| `src/components/Clients/CaseModal.tsx` | 1691 | `Date.parse(` | : hearingConfirmation.nextHearingConfirmedAt && hearingConfirmation.nextHearingConfirmedForAt && Date.parse(hearingConfirmation.nextHearingConfirmedForAt) === Date.parse(caseData.nextHearingAt ?? '') |
| `src/components/Clients/CaseModal.tsx` | 1695 | `Date.parse(` | {caseData.id > 0 && allowEdit && caseData.status !== 'Closed' && caseData.status !== 'Lost' && caseData.nextHearingAt && Date.parse(caseData.nextHearingAt) > Date.now() && (!hearingConfirmation.nextHearingConfirmedAt \|\|  |
| `src/components/Clients/CaseModal.tsx` | 1280 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1292 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1751 | `type="date"` | type="date" |
| `src/components/Clients/CaseModal.tsx` | 1763 | `type="date"` | type="date" |
| Еще 662 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Фильтры и периоды

Параметры диапазонов дат в API и UI, где важны границы дня в timezone пользователя.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/Controllers/AccountsController.cs` | 34 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 35 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 508 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 509 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 34 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 35 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 508 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 509 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/AccountsController.cs` | 746 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/AccountsController.cs` | 747 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/ActsController.cs` | 195 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 196 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 767 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 768 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 195 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 196 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ActsController.cs` | 767 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ActsController.cs` | 768 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CalendarSyncController.cs` | 144 | `[FromQuery] DateTime?` | [FromQuery] DateTime? createdFrom = null, |
| `backend/Controllers/CalendarSyncController.cs` | 145 | `[FromQuery] DateTime?` | [FromQuery] DateTime? createdTo = null, |
| `backend/Controllers/CashOrdersController.cs` | 48 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 49 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 84 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 85 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 122 | `[FromQuery] DateTime?` | [FromQuery] DateTime? date, |
| `backend/Controllers/CashOrdersController.cs` | 143 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 144 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 189 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 190 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 259 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 260 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 324 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 325 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 48 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 49 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 84 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 85 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 143 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 144 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 189 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 190 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 259 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 260 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 324 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 325 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1143 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 1144 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 1340 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/CashOrdersController.cs` | 1341 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/CashOrdersController.cs` | 2427 | `DateTime? from` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/CashOrdersController.cs` | 2427 | `DateTime? to` | private static string BuildRegisterPrintHtml(CashOrderRegisterResponseDto register, DateTime? from, DateTime? to, string? accountName) |
| `backend/Controllers/ContractsController.cs` | 310 | `[FromQuery] DateTime?` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 1118 | `[FromQuery] DateTime` | public async Task<ActionResult<NextContractNumberDto>> GetNextNumber([FromQuery] DateTime date, [FromQuery] int? excludeId, CancellationToken ct) |
| `backend/Controllers/ContractsController.cs` | 310 | `DateTime? from` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 310 | `DateTime? to` | public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId, |
| `backend/Controllers/ContractsController.cs` | 2368 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/ContractsController.cs` | 2369 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InternalChatController.cs` | 262 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InternalChatController.cs` | 263 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InternalChatController.cs` | 262 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InternalChatController.cs` | 263 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 243 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 244 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 373 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 374 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 2037 | `[FromQuery] DateTime?` | public async Task<ActionResult<object>> GetNextNumber([FromQuery] DateTime? date, CancellationToken ct) |
| `backend/Controllers/InvoicesController.cs` | 243 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 244 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 373 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 374 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 3434 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 3435 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 4454 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/InvoicesController.cs` | 4455 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/InvoicesController.cs` | 6628 | `startDate` | var startDate = childPayments.Count == 0 |
| `backend/Controllers/InvoicesController.cs` | 6655 | `startDate` | StartDate = startDate, |
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
| `backend/Controllers/MoneyOperationsController.cs` | 3279 | `DateTime? from` | DateTime? from, |
| `backend/Controllers/MoneyOperationsController.cs` | 3280 | `DateTime? to` | DateTime? to, |
| `backend/Controllers/NotificationInboxController.cs` | 72 | `dateFrom` | [FromQuery] string? dateFrom = null, |
| `backend/Controllers/NotificationInboxController.cs` | 73 | `dateTo` | [FromQuery] string? dateTo = null, |
| `backend/Controllers/NotificationInboxController.cs` | 107 | `dateFrom` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 107 | `dateTo` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 134 | `dateFrom` | [FromQuery] string? dateFrom = null, |
| `backend/Controllers/NotificationInboxController.cs` | 135 | `dateTo` | [FromQuery] string? dateTo = null, |
| `backend/Controllers/NotificationInboxController.cs` | 159 | `dateFrom` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 159 | `dateTo` | query = ApplyDateRangeFilter(query, dateFrom, dateTo); |
| `backend/Controllers/NotificationInboxController.cs` | 526 | `dateFrom` | string? dateFrom, |
| `backend/Controllers/NotificationInboxController.cs` | 527 | `dateTo` | string? dateTo) |
| `backend/Controllers/NotificationInboxController.cs` | 529 | `dateFrom` | if (DateTime.TryParse(dateFrom, out var parsedFrom)) |
| `backend/Controllers/NotificationInboxController.cs` | 535 | `dateTo` | if (DateTime.TryParse(dateTo, out var parsedTo)) |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 44 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ObjectAuditController.cs` | 48 | `[FromQuery] DateTime` | [FromQuery] DateTimeOffset? fromUtc = null, |
| `backend/Controllers/ObjectAuditController.cs` | 49 | `[FromQuery] DateTime` | [FromQuery] DateTimeOffset? toUtcExclusive = null, |
| `backend/Controllers/ObjectAuditController.cs` | 43 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ObjectAuditController.cs` | 44 | `DateTime? to` | [FromQuery] DateTime? to, |
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
| `backend/Controllers/ReportsController.cs` | 295 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 296 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 315 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 316 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 324 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 375 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 376 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 384 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 435 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 436 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 444 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 498 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 499 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 507 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 554 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 555 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 590 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 591 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1302 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1346 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1383 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1422 | `[FromQuery] DateTime?` | [FromQuery] DateTime? asOf, |
| `backend/Controllers/ReportsController.cs` | 1459 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1460 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1500 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1501 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1539 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1540 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1560 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1561 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1581 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1582 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1602 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1603 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1623 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1624 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1644 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1645 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1665 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1666 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1686 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1687 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1707 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1708 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1728 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1729 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1749 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1750 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1770 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1771 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1791 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1792 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1812 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1813 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1833 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1834 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1854 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1855 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1875 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1876 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1896 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1897 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1917 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1918 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1938 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1939 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 1972 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 1973 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 2018 | `[FromQuery] DateTime?` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 2019 | `[FromQuery] DateTime?` | [FromQuery] DateTime? to, |
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
| `backend/Controllers/ReportsController.cs` | 295 | `DateTime? from` | [FromQuery] DateTime? from, |
| `backend/Controllers/ReportsController.cs` | 296 | `DateTime? to` | [FromQuery] DateTime? to, |
| `backend/Controllers/ReportsController.cs` | 315 | `DateTime? from` | [FromQuery] DateTime? from, |
| Еще 952 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Экспорты, импорты и файлы

Выгрузки и импорты, где дату надо отдавать в понятной timezone, а не в случайном серверном времени.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `_codex_reports/storage-audit/STORAGE_AUDIT.md` | 44 | `PDF` | \| E-signature originals/evidence \| e-sign service \| DB + absolute path \| CRITICAL / legal \| PDF ≤25MB \| default release tree \| SHA-256, archive years metadata \| unsafe persistence \| evidence package only, no DR \| EVID-02 |
| `_codex_reports/storage-audit/STORAGE_AUDIT.md` | 374 | `PDF` | \| E-signature \| PDF/hash/evidence package \| persistent provider path \| e-sign service/controller tests \| release path \| |
| `_codex_reports/storage-audit/STORAGE_AUDIT.md` | 48 | `Imports` | \| Imports \| admin/import APIs \| resulting DB rows \| HIGH / business \| current flow buffers bytes \| RAM \| source not retained generally \| DB RPO \| reimport if source exists \| EVID-024 \| |
| `_codex_reports/storage-audit/STORAGE_AUDIT.md` | 85 | `Download` | Upload business file: authorization → local stream → DB metadata; linked task/case may create two copies without replica identity. Download: DB authorization/path → local `PhysicalFile`. Delete: DB mutation then best-eff |
| `_codex_reports/storage-audit/STORAGE_AUDIT.md` | 148 | `Download` | ## 11. Upload/Download Architecture |
| `_codex_reports/storage-audit/STORAGE_EVIDENCE.md` | 39 | `PDF` | \| EVID-025 \| PDF/Word/CSV/Excel exports в основном transient и возвращаются из памяти \| `backend/Controllers/ActsController.cs:1240-1248`; `ContractsController.cs:396-449`; `CashOrdersController.cs:967-1006` \| code sampl |
| `_codex_reports/storage-audit/STORAGE_EVIDENCE.md` | 39 | `CSV` | \| EVID-025 \| PDF/Word/CSV/Excel exports в основном transient и возвращаются из памяти \| `backend/Controllers/ActsController.cs:1240-1248`; `ContractsController.cs:396-449`; `CashOrdersController.cs:967-1006` \| code sampl |
| `_codex_reports/storage-audit/STORAGE_EVIDENCE.md` | 83 | `ImportSqliteAsync` | \| EVID-088 \| SQLite import endpoint — merge/import данных, не disaster restore \| `backend/Controllers/DatabaseAdminController.cs:214-251`; `DatabaseImportService.ImportSqliteAsync()` \| code \| HIGH \| |
| `_codex_reports/storage-audit/STORAGE_EVIDENCE.md` | 49 | `Download` | \| EVID-044 \| Download целиком буферизуется в `MemoryStream`; presign/Range отсутствуют \| `backend/Services/S3StorageService.cs:995-1015` \| code \| HIGH \| |
| `_codex_reports/storage-audit/STORAGE_IMPLEMENTATION_TASK.md` | 88 | `DownloadFile` | - `TasksController.UploadFile/DownloadFile/DeleteFile` и обе legacy/new task card APIs: route bytes through orchestration, preserve responses/permissions. |
| `_codex_reports/storage-audit/STORAGE_IMPLEMENTATION_TASK.md` | 180 | `Download` | ### Download |
| `backend/AppReleases/releases.json` | 2625 | `PDF` | "text": "Заголовок, номер и дата РКО теперь располагаются на одной высоте, а длинные номера ПКО не переносятся на соседнюю строку. В PDF и Word используются пропорции шрифта, интервалы и подписи из печатного образца КУБа |
| `backend/AppReleases/releases.json` | 2808 | `PDF` | "text": "В Word- и PDF-версиях счёта блок «Покупатель (Заказчик)» получает сведения как из основных полей, так и из расширенной карточки клиента. ФИО, адрес, паспорт, СНИЛС, ИНН и телефон отображаются отдельными подписан |
| `backend/AppReleases/releases.json` | 3057 | `PDF` | "text": "В печати, PDF и Word-файлах счета и акта возвращены привычный шрифт, отступы и пропорции таблиц. Наименования, количество, единицы измерения, цены и суммы больше не наезжают друг на друга." |
| `backend/AppReleases/releases.json` | 3069 | `PDF` | "text": "Подпись, печать и расшифровка подписи в счете и акте теперь сохраняют аккуратные размеры и расположение при печати, скачивании PDF и открытии файла в Word." |
| `backend/AppReleases/releases.json` | 3091 | `PDF` | "text": "Счета, счета-договоры и заказы теперь можно печатать и скачивать в PDF или Word как без реквизитов, так и с подписью и печатью. Печать открывается из готового PDF, поэтому изображения успевают загрузиться до выз |
| `backend/AppReleases/releases.json` | 3161 | `PDF` | "summary": "Печатные формы актов точнее сохраняют положение подписей в PDF и Word.", |
| `backend/AppReleases/releases.json` | 3175 | `PDF` | "summary": "Подписанный и неподписанный варианты счета-договора приведены к единой форме в PDF и Word.", |
| `backend/AppReleases/releases.json` | 3189 | `PDF` | "summary": "Печатная форма счета приведена к полному виду КУБа в PDF и Word.", |
| `backend/AppReleases/releases.json` | 3217 | `PDF` | "summary": "Уточнено расположение реквизитов, таблицы, итогов и подписи в PDF и Word заказа.", |
| `backend/AppReleases/releases.json` | 3235 | `PDF` | "text": "PDF и Word счета-договора теперь используют те же четыре страницы и разрывы разделов, что и форма КУБа: счет расположен на первой странице, условия договора — на второй и третьей, реквизиты и подписи — на четвер |
| `backend/AppReleases/releases.json` | 3253 | `PDF` | "text": "PDF-счет теперь повторяет форму КУБа: банковские реквизиты, данные сторон, позиции, итоги и подпись расположены как в исходном документе." |
| `backend/AppReleases/releases.json` | 3266 | `PDF` | "summary": "ПКО и его квитанция теперь печатаются по привычной форме КО-1 и доступны в PDF и Word.", |
| `backend/AppReleases/releases.json` | 3270 | `PDF` | "text": "В карточке кассового ордера можно скачать ПКО в PDF или Word. Форма содержит ордер и отрывную квитанцию, официальный код КО-1, плательщика, основание, сумму цифрами и прописью, НДС, дату и строки подписей, как в |
| `backend/AppReleases/releases.json` | 3283 | `PDF` | "text": "Кнопка «Заказ» открывает печатную форму по образцу КУБа. В меню «Скачать» доступны PDF и настоящий Word-файл заказа; документ учитывает направление платежа, реквизиты сторон, НДС, позиции, итоговую сумму и подпи |
| `backend/AppReleases/releases.json` | 3296 | `PDF` | "text": "В PDF-акте номер строки, количество, единица, цена и сумма теперь начинаются от верхнего края строки, а длинное наименование услуги равномерно выравнивается по ширине. Это сохраняет расположение текста и перенос |
| `backend/AppReleases/releases.json` | 3352 | `PDF` | "summary": "PDF и Word-версия акта теперь повторяют компактную форму КУБа, включая таблицу, реквизиты и блок подписей.", |
| `backend/AppReleases/releases.json` | 3392 | `PDF` | "text": "В PDF и печатной форме отчета «Юридическая работа» заголовки таблиц повторяются на новых страницах, строки дел не разрываются между листами, а длинные названия переносятся внутри ячеек без обрезания правых колон |
| `backend/AppReleases/releases.json` | 3407 | `CSV` | "text": "В CSV, Excel, PDF и печатной форме отчета по банку больше не повторяется подпись «Источник: Банк». Заголовок периода стал короче и понятнее." |
| `backend/AppReleases/releases.json` | 3407 | `PDF` | "text": "В CSV, Excel, PDF и печатной форме отчета по банку больше не повторяется подпись «Источник: Банк». Заголовок периода стал короче и понятнее." |
| `backend/AppReleases/releases.json` | 3479 | `PDF` | "title": "Широкие отчеты полностью помещаются в PDF", |
| `backend/AppReleases/releases.json` | 3486 | `PDF` | "text": "В PDF отчета по задачам и других широких таблиц последние колонки теперь помещаются на лист A4 и остаются читаемыми." |
| `backend/AppReleases/releases.json` | 3505 | `CSV` | "text": "Повторяющаяся кнопка CSV удалена из всех разделов отчетов. CSV, Excel, PDF и печать теперь доступны в едином блоке." |
| `backend/AppReleases/releases.json` | 3505 | `PDF` | "text": "Повторяющаяся кнопка CSV удалена из всех разделов отчетов. CSV, Excel, PDF и печать теперь доступны в едином блоке." |
| `backend/AppReleases/releases.json` | 3524 | `CSV` | "text": "В отчете «Юридическая работа» появилась кнопка «Печать» рядом с CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 3524 | `PDF` | "text": "В отчете «Юридическая работа» появилась кнопка «Печать» рядом с CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 3528 | `PDF` | "text": "Печатная версия использует тот же макет, данные, фильтры, права доступа и локальные даты, что и PDF." |
| `backend/AppReleases/releases.json` | 3537 | `CSV` | "summary": "В отчете «Задачи по журналу» появились штатные форматы CSV, Excel, PDF и печатная версия.", |
| `backend/AppReleases/releases.json` | 3537 | `PDF` | "summary": "В отчете «Задачи по журналу» появились штатные форматы CSV, Excel, PDF и печатная версия.", |
| `backend/AppReleases/releases.json` | 3543 | `CSV` | "text": "Кнопки выгрузки отчета по задачам больше не скрыты: доступны CSV, Excel, PDF и печать." |
| `backend/AppReleases/releases.json` | 3543 | `PDF` | "text": "Кнопки выгрузки отчета по задачам больше не скрыты: доступны CSV, Excel, PDF и печать." |
| `backend/AppReleases/releases.json` | 3570 | `PDF` | "text": "Выбранный шаблон и режим подписи с печатью одинаково применяются при печати, скачивании Word/PDF, отправке по email и выгрузке на Яндекс.Диск." |
| `backend/AppReleases/releases.json` | 3610 | `PDF` | "summary": "Печатные формы и PDF кассовых документов теперь соответствуют унифицированным бланкам, используемым в КУБ24.", |
| `backend/AppReleases/releases.json` | 3897 | `CSV` | "text": "Печатная версия реестра использует те же фильтры, область видимости, локальное время и право экспорта, что CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 3897 | `PDF` | "text": "Печатная версия реестра использует те же фильтры, область видимости, локальное время и право экспорта, что CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 4362 | `PDF` | "summary": "Администратор может безопасно отменить ещё не отправленный PDF-снимок прямо в истории электронной подписи.", |
| `backend/AppReleases/releases.json` | 4372 | `PDF` | "text": "Отмена не удаляет PDF, контрольную сумму или историю и не применяется к уже отправленной версии без подтверждения внешнего провайдера." |
| `backend/AppReleases/releases.json` | 4376 | `PDF` | "text": "Повторный запрос отмены безопасен и не создаёт дублирующиеся события в истории; для каждой версии можно скачать внутренний ZIP-архив CRM с PDF, событиями и проверенными контрольными суммами." |
| `backend/AppReleases/releases.json` | 4395 | `PDF` | "text": "Для подписания сохраняется отдельный неизменяемый PDF-снимок с SHA-256; история показывает состояние envelope и каждого подписанта." |
| `backend/AppReleases/releases.json` | 4659 | `PDF` | "text": "В блоке внешнего согласования можно отметить до 10 PDF, изображений или офисных документов размером до 25 МБ каждый." |
| `backend/AppReleases/releases.json` | 4801 | `PDF` | "text": "Перед подписанием PDF сохраняется как отдельная неизменяемая версия с контрольной суммой; новая версия документа всегда получает отдельный процесс подписания." |
| `backend/AppReleases/releases.json` | 4814 | `PDF` | "summary": "Очередь безопасной доставки устойчиво обрабатывает временные задержки провайдера, крупные PDF и запоздалые статусы.", |
| `backend/AppReleases/releases.json` | 4824 | `PDF` | "text": "Очередь обрабатывает документы ограниченными пакетами, а PDF до 25 МБ копируются потоково с проверкой формата, размера и контрольной суммы." |
| `backend/AppReleases/releases.json` | 4843 | `PDF` | "text": "В карточках счета, акта и договора появилась кнопка «Мессенджер». Перед отправкой CRM формирует штатную PDF-версию и показывает получателя, канал и текст сообщения." |
| `backend/AppReleases/releases.json` | 4851 | `PDF` | "text": "Финансовые документы передаются только как PDF размером до 25 МБ через одноразовую защищённую ссылку. Внешняя отправка остаётся выключенной, пока администратор не настроит официальный канал." |
| `backend/AppReleases/releases.json` | 5284 | `CSV` | "text": "Состояние автоконтроля добавлено в CSV, Excel, PDF и печатную форму реестра дел." |
| `backend/AppReleases/releases.json` | 5284 | `PDF` | "text": "Состояние автоконтроля добавлено в CSV, Excel, PDF и печатную форму реестра дел." |
| `backend/AppReleases/releases.json` | 9419 | `PDF` | "title": "Печать счета больше не зависит от PDF-генератора", |
| `backend/AppReleases/releases.json` | 9420 | `PDF` | "summary": "Кнопки печати в карточке счета теперь открывают печатную HTML-форму напрямую. Если на сервере временно недоступно формирование PDF, пользователь все равно может распечатать счет или счет-оферту из браузера.", |
| `backend/AppReleases/releases.json` | 9426 | `PDF` | "text": "В разделе «Счета» кнопка «Печать» больше не обращается к серверному PDF-генератору. Печатная форма открывается как обычная страница документа и сразу вызывает печать в браузере." |
| `backend/AppReleases/releases.json` | 9430 | `PDF` | "text": "Печать счета-оферты работает по той же схеме: CRM формирует HTML-документ и печатает его без промежуточного PDF-файла, поэтому операция не должна падать с ошибкой временной недоступности сервиса PDF." |
| `backend/AppReleases/releases.json` | 9434 | `PDF` | "text": "Скачивание PDF и отправка PDF на Яндекс.Диск по-прежнему используют серверный PDF-генератор. Если он недоступен, эти операции могут показать отдельную ошибку, но обычная печать счета из браузера продолжит работа |
| `backend/AppReleases/releases.json` | 10555 | `PDF` | "summary": "В печатной форме счета и PDF улучшено отображение длинных названий организации, покупателя и услуги. Такие реквизиты теперь аккуратнее переносятся внутри страницы и не должны ломать внешний вид счета.", |
| `backend/AppReleases/releases.json` | 10561 | `PDF` | "text": "В разделе «Счета» печатная форма и PDF стали устойчивее к длинным названиям продавца, покупателя и строк услуг." |
| `backend/AppReleases/releases.json` | 10593 | `CSV` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась кнопка «Печать». Печатная форма открывается в отдельной вкладке и использует те же выбранные фильтры, что и отчет на экране, CSV, Excel и PDF.", |
| `backend/AppReleases/releases.json` | 10593 | `PDF` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась кнопка «Печать». Печатная форма открывается в отдельной вкладке и использует те же выбранные фильтры, что и отчет на экране, CSV, Excel и PDF.", |
| `backend/AppReleases/releases.json` | 10599 | `CSV` | "text": "Для управленческих отчетов добавлена печатная форма рядом с CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10599 | `PDF` | "text": "Для управленческих отчетов добавлена печатная форма рядом с CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 10615 | `CSV` | "title": "CSV-выгрузка управленческих отчетов", |
| `backend/AppReleases/releases.json` | 10616 | `CSV` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась выгрузка в CSV рядом с Excel и PDF. Файл формируется по тем же выбранным фильтрам, что и отчет на экране, поэтому его можно использовать для дальнейшей об |
| `backend/AppReleases/releases.json` | 10616 | `PDF` | "summary": "В разделе «Отчеты» у управленческих отчетов появилась выгрузка в CSV рядом с Excel и PDF. Файл формируется по тем же выбранным фильтрам, что и отчет на экране, поэтому его можно использовать для дальнейшей об |
| `backend/AppReleases/releases.json` | 10622 | `CSV` | "text": "Для управленческих отчетов добавлена кнопка CSV. Выгрузка доступна там же, где уже были Excel и PDF." |
| `backend/AppReleases/releases.json` | 10622 | `PDF` | "text": "Для управленческих отчетов добавлена кнопка CSV. Выгрузка доступна там же, где уже были Excel и PDF." |
| `backend/AppReleases/releases.json` | 10626 | `CSV` | "text": "CSV-файл учитывает выбранный период, источник денег, счет, клиента, дело, договор, статью дохода или расхода и другие фильтры конкретного отчета." |
| `backend/AppReleases/releases.json` | 10630 | `CSV` | "text": "Выгрузка CSV использует те же права доступа, что и остальные экспорты отчетов: скачать файл могут только пользователи с разрешением на экспорт." |
| `backend/AppReleases/releases.json` | 11151 | `PDF` | "title": "Отчет по задачам можно выгружать в Excel и PDF", |
| `backend/AppReleases/releases.json` | 11152 | `PDF` | "summary": "В разделе «Отчеты» серверная выгрузка управленческого отчета по задачам теперь доступна в Excel и PDF/печатном виде. В файл попадают список задач, сводка по статусам и распределение по исполнителям.", |
| `backend/AppReleases/releases.json` | 11482 | `PDF` | "summary": "В разделе «Отчеты» фильтр по статье дохода или расхода стал применяться к сводкам по статусам актов и договоров. Теперь эти отчеты, а также их Excel/PDF/печатная форма, показывают только документы, связанные  |
| `backend/AppReleases/releases.json` | 11577 | `PDF` | "summary": "В разделе «Отчеты» сводки по статусам счетов, актов и договоров стали лучше совпадать с общей панелью финансовых фильтров. Теперь при выборе источника денег, денежного счета или контрагента эти отчеты и их Ex |
| `backend/AppReleases/releases.json` | 11587 | `PDF` | "text": "Excel/PDF/печатная форма этих отчетов теперь формируется по тем же финансовым фильтрам, что выбраны на странице отчетов." |
| `backend/AppReleases/releases.json` | 11596 | `PDF` | "summary": "В разделе «Отчеты» сводка по импортам банковских выписок стала точнее работать с общей панелью фильтров. Теперь отчет и его Excel/PDF/печатная форма учитывают выбранный расчетный счет, контрагента, клиента, д |
| `backend/AppReleases/releases.json` | 11606 | `PDF` | "text": "Excel/PDF/печатная форма отчета по импортам банка теперь формируется по тем же фильтрам, что пользователь выбрал на странице отчетов." |
| `backend/AppReleases/releases.json` | 11615 | `PDF` | "summary": "В разделе «Отчеты» кассовая книга стала точнее совпадать с общей панелью фильтров. Теперь отчет по кассе и его Excel/PDF/печатная форма учитывают выбранного контрагента, клиента, дело, договор и статью дохода |
| `backend/AppReleases/releases.json` | 11625 | `PDF` | "text": "Excel/PDF/печатная форма отчета по кассе теперь формируется по тем же фильтрам, что пользователь выбрал на странице отчетов." |
| `backend/AppReleases/releases.json` | 11653 | `CSV` | "summary": "Платежные отчеты в разделе «Отчеты» стали точнее соблюдать матрицу прав. Пользователь может просматривать доступные ему финансовые сводки, но CSV, Excel и PDF/печатная форма теперь доступны только при наличии |
| `backend/AppReleases/releases.json` | 11653 | `PDF` | "summary": "Платежные отчеты в разделе «Отчеты» стали точнее соблюдать матрицу прав. Пользователь может просматривать доступные ему финансовые сводки, но CSV, Excel и PDF/печатная форма теперь доступны только при наличии |
| `backend/AppReleases/releases.json` | 11663 | `CSV` | "text": "Если у роли есть просмотр платежей, но нет права выгрузки, кнопки CSV, Excel и PDF в этих отчетах становятся недоступны, а прямой запрос на выгрузку отклоняется сервером." |
| `backend/AppReleases/releases.json` | 11663 | `PDF` | "text": "Если у роли есть просмотр платежей, но нет права выгрузки, кнопки CSV, Excel и PDF в этих отчетах становятся недоступны, а прямой запрос на выгрузку отклоняется сервером." |
| `backend/AppReleases/releases.json` | 11682 | `PDF` | "text": "Excel, PDF/печатная форма и CSV по этому отчету также закрыты для пользователей без административного доступа." |
| `backend/AppReleases/releases.json` | 11682 | `CSV` | "text": "Excel, PDF/печатная форма и CSV по этому отчету также закрыты для пользователей без административного доступа." |
| `backend/AppReleases/releases.json` | 11701 | `PDF` | "text": "Excel, PDF/печатная форма и CSV по этому отчету теперь доступны только при наличии права выгрузки платежей." |
| `backend/AppReleases/releases.json` | 11701 | `CSV` | "text": "Excel, PDF/печатная форма и CSV по этому отчету теперь доступны только при наличии права выгрузки платежей." |
| `backend/AppReleases/releases.json` | 11739 | `PDF` | "text": "В разделе «Отчеты» Excel и PDF/печатная форма отчета по банку теперь учитывают выбранный счет, контрагента, клиента, дело, договор и статью дохода или расхода." |
| `backend/AppReleases/releases.json` | 11758 | `PDF` | "text": "В разделе «Отчеты» Excel и PDF/печатная форма отчета по кассе теперь выгружают данные только по выбранной кассе, если она указана в фильтре." |
| `backend/AppReleases/releases.json` | 11781 | `PDF` | "text": "Excel и PDF/печатная форма отчета по кассовым ордерам выгружают ту же выборку, которую пользователь видит на экране." |
| `backend/AppReleases/releases.json` | 11796 | `PDF` | "text": "В разделе «Отчеты» отчеты по статусам счетов, актов и договоров применяют фильтры клиента, дела и договора на экране, в Excel и PDF/печатной форме." |
| `backend/AppReleases/releases.json` | 11847 | `PDF` | "summary": "В акте сверки клиента уточнено поведение кнопок выгрузки. Excel и PDF становятся доступны после того, как CRM загрузила данные операций и профиль организации, поэтому в документ попадает корректное название к |
| `backend/AppReleases/releases.json` | 11853 | `PDF` | "text": "В карточке клиента в акте сверки кнопки Excel и PDF больше нельзя нажать во время формирования акта. Это предотвращает выгрузку документа с временным названием организации вместо данных из профиля компании." |
| `backend/AppReleases/releases.json` | 13959 | `PDF` | "text": "Исправлен экранный отчет по банку: фильтры по счету, контрагенту, клиенту, делу, договору и статье доходов/расходов теперь применяются не только к Excel и PDF, но и к самой банковской сводке на странице." |
| `backend/AppReleases/releases.json` | 13968 | `PDF` | "summary": "Excel и PDF для отчета по банку теперь формируются по той же выборке, которую пользователь видит на экране.", |
| `backend/AppReleases/releases.json` | 13974 | `PDF` | "text": "В разделе «Отчеты» исправлена выгрузка отчета по банку: выбранные фильтры по счету, контрагенту, клиенту, делу, договору и статье доходов/расходов теперь учитываются при скачивании Excel и PDF." |
| `backend/AppReleases/releases.json` | 14016 | `PDF` | "text": "Фильтр по договору применяется к отчетам по денежным операциям, прогнозу денежных потоков, план-факт отчету и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 14039 | `PDF` | "text": "Фильтр по статье применяется к отчетам по денежным операциям, прогнозу денежных потоков, план-факт отчету и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 14081 | `PDF` | "text": "Фильтр по делу применяется к отчетам по денежным операциям и к Excel/PDF-выгрузкам этих отчетов. Для фактических операций система использует связь со счетом, а для прогноза платежей — связь платежа с делом." |
| `backend/AppReleases/releases.json` | 14104 | `PDF` | "text": "Фильтр по клиенту применяется к отчетам по денежным операциям и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 14173 | `PDF` | "text": "Фильтр применяется к финансовым отчетам по денежным операциям, прогнозу денежных потоков и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 14196 | `PDF` | "text": "Фильтр счета применяется к финансовым отчетам по денежным операциям, прогнозу денежных потоков и к Excel/PDF-выгрузкам этих отчетов." |
| `backend/AppReleases/releases.json` | 14219 | `PDF` | "text": "Фильтр применяется к финансовым отчетам по денежным операциям и к их выгрузкам Excel и PDF, чтобы данные на экране и в файлах совпадали." |
| `backend/AppReleases/releases.json` | 14238 | `CSV` | "text": "В разделе «Отчеты» у блоков «Отчет по счетам оплаты», «Отчет по оплаченным счетам», «Отчет по статусам счетов», «Отчет по статусам актов» и «Отчет по статусам договоров» теперь доступны выгрузки CSV, Excel и PDF |
| `backend/AppReleases/releases.json` | 14238 | `PDF` | "text": "В разделе «Отчеты» у блоков «Отчет по счетам оплаты», «Отчет по оплаченным счетам», «Отчет по статусам счетов», «Отчет по статусам актов» и «Отчет по статусам договоров» теперь доступны выгрузки CSV, Excel и PDF |
| `backend/AppReleases/releases.json` | 14250 | `PDF` | "text": "PDF-выгрузки можно использовать как печатные формы для сверки оплат и контроля статусов счетов, актов и договоров." |
| `backend/AppReleases/releases.json` | 14265 | `CSV` | "text": "В разделе «Отчеты» у блоков «План-факт отчет», «Отчет по авансам» и «Отчет по приоритетам платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14265 | `PDF` | "text": "В разделе «Отчеты» у блоков «План-факт отчет», «Отчет по авансам» и «Отчет по приоритетам платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14277 | `PDF` | "text": "PDF-выгрузки можно использовать как печатные формы для сверки план-факта, авансов и распределения операций по приоритетам." |
| `backend/AppReleases/releases.json` | 14292 | `CSV` | "text": "В разделе «Отчеты» у блоков «Отчет по кассе» и «Отчет по банку» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14292 | `PDF` | "text": "В разделе «Отчеты» у блоков «Отчет по кассе» и «Отчет по банку» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14304 | `PDF` | "text": "PDF-выгрузки можно использовать как печатные формы для сверки кассовых и банковских движений за выбранный период." |
| `backend/AppReleases/releases.json` | 14319 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по импортам из таблиц» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14319 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по импортам из таблиц» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14327 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля табличных загрузок, ошибок и откатов с конфликтами." |
| `backend/AppReleases/releases.json` | 14342 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по импортам банковских выписок» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14342 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по импортам банковских выписок» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14350 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля проблемных импортов и заблокированных откатов." |
| `backend/AppReleases/releases.json` | 14365 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по кассовым ордерам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14365 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по кассовым ордерам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14373 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки приходных и расходных кассовых ордеров за выбранный период." |
| `backend/AppReleases/releases.json` | 14388 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по валютам операций» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14388 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по валютам операций» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14396 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки оборотов по валютам операций." |
| `backend/AppReleases/releases.json` | 14411 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по журнальным статусам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14411 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по журнальным статусам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14419 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля операций, которые еще находятся в обработке или требуют исправления." |
| `backend/AppReleases/releases.json` | 14434 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по статусам сверки» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14434 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по статусам сверки» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14442 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для контроля несверенных операций и ручной проверки платежей." |
| `backend/AppReleases/releases.json` | 14457 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по назначениям платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14457 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по назначениям платежей» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14465 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки повторяющихся оснований оплат и списаний." |
| `backend/AppReleases/releases.json` | 14480 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по платежным поручениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14480 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по платежным поручениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14488 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки операций по номерам платежных поручений." |
| `backend/AppReleases/releases.json` | 14503 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по НДС» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14503 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по НДС» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14511 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки начисленного и входящего НДС за выбранный период." |
| `backend/AppReleases/releases.json` | 14526 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по точкам продаж» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14526 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по точкам продаж» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14534 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки финансового результата по точкам продаж и каналам." |
| `backend/AppReleases/releases.json` | 14549 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по направлениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14549 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по направлениям» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14557 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для обсуждения структуры доходов и расходов по направлениям бизнеса." |
| `backend/AppReleases/releases.json` | 14572 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по проектам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14572 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по проектам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14580 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для обсуждения финансового результата проектов." |
| `backend/AppReleases/releases.json` | 14595 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по контрагентам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14595 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по контрагентам» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14603 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для сверки оборотов по контрагентам за выбранный период." |
| `backend/AppReleases/releases.json` | 14618 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет по статьям доходов и расходов» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14618 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет по статьям доходов и расходов» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14626 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму для обсуждения структуры доходов и расходов за выбранный период." |
| `backend/AppReleases/releases.json` | 14641 | `CSV` | "text": "В разделе «Отчеты» у блока «Отчет движения денежных средств» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14641 | `PDF` | "text": "В разделе «Отчеты» у блока «Отчет движения денежных средств» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14649 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму фактического движения денег за выбранный период." |
| `backend/AppReleases/releases.json` | 14664 | `CSV` | "text": "В разделе «Отчеты» у блока «Прогноз денежных потоков» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14664 | `PDF` | "text": "В разделе «Отчеты» у блока «Прогноз денежных потоков» теперь доступны выгрузки CSV, Excel и PDF." |
| `backend/AppReleases/releases.json` | 14672 | `PDF` | "text": "PDF-выгрузку можно использовать как печатную форму прогноза для обсуждения плановых поступлений и списаний." |
| `backend/AppReleases/releases.json` | 14718 | `CSV` | "text": "CSV, Excel и PDF-выгрузки финансового отчета по делам также включают расходы, маржу и маржинальность." |
| `backend/AppReleases/releases.json` | 14718 | `PDF` | "text": "CSV, Excel и PDF-выгрузки финансового отчета по делам также включают расходы, маржу и маржинальность." |
| `backend/AppReleases/releases.json` | 14852 | `PDF` | "text": "На экране акта сверки, в Excel и в PDF авансовые строки подписываются как авансы, чтобы их было проще отличить от закрывающих оплат." |
| `backend/AppReleases/releases.json` | 15834 | `PDF` | "text": "В меню «Скачать» появились варианты «PDF счет-оферта» и «Word счет-оферта»." |
| `backend/AppReleases/releases.json` | 16218 | `ICS` | "text": "Для задач, календаря дел, ICS-календарей, заметок платежей, автоматической сверки баланса и проверки просрочки платежей убрана зависимость от часового пояса сервера, если профильный часовой пояс пользователя не  |
| `backend/AppReleases/releases.json` | 16330 | `CSV` | "summary": "В разделе \"Отчеты\" скачанные CSV, Excel и PDF-файлы теперь единообразно показывают, когда отчет был сформирован, с учетом часового пояса пользователя из профиля. Это помогает сотрудникам из разных регионов  |
| `backend/AppReleases/releases.json` | 16330 | `PDF` | "summary": "В разделе \"Отчеты\" скачанные CSV, Excel и PDF-файлы теперь единообразно показывают, когда отчет был сформирован, с учетом часового пояса пользователя из профиля. Это помогает сотрудникам из разных регионов  |
| `backend/AppReleases/releases.json` | 16340 | `CSV` | "text": "CSV и Excel-выгрузки отчета по юридической работе теперь показывают время формирования по часовому поясу профиля так же, как PDF." |
| `backend/AppReleases/releases.json` | 16340 | `PDF` | "text": "CSV и Excel-выгрузки отчета по юридической работе теперь показывают время формирования по часовому поясу профиля так же, как PDF." |
| `backend/AppReleases/releases.json` | 16429 | `CSV` | "summary": "В разделе \"Платежи\" появились выгрузки текущего списка в CSV и Excel. В файл попадают те же платежи, которые пользователь отобрал фильтрами на экране, а служебное время формирования и дата создания платежа  |
| `backend/AppReleases/releases.json` | 16435 | `CSV` | "text": "В журнале платежей добавлены кнопки выгрузки в CSV и Excel для пользователей с правом экспорта платежей." |
| `backend/AppReleases/releases.json` | 16627 | `CSV` | "summary": "В выгрузках отчета по юридической работе уточнены даты и имена файлов. CSV, Excel и PDF теперь показывают точные даты создания дел и судебных заседаний с учетом часового пояса профиля пользователя, а CSV-файл |
| `backend/AppReleases/releases.json` | 16627 | `PDF` | "summary": "В выгрузках отчета по юридической работе уточнены даты и имена файлов. CSV, Excel и PDF теперь показывают точные даты создания дел и судебных заседаний с учетом часового пояса профиля пользователя, а CSV-файл |
| `backend/AppReleases/releases.json` | 16637 | `CSV` | "text": "CSV-файл юридического отчета теперь скачивается с именем, в котором указаны дата и время формирования по часовому поясу профиля пользователя." |
| `backend/AppReleases/releases.json` | 16656 | `CSV` | "text": "CSV-выгрузки юридических отчетов теперь используют то же правило дат, что и экран отчета: дата без времени остается календарной датой, а точное время отображается по часовому поясу профиля." |
| `backend/AppReleases/releases.json` | 16709 | `PDF` | "text": "В HTML, Word и PDF-формах счетов дата счета, срок оплаты, дата договора-основания и дата выдачи паспорта больше не должны отображаться со сдвигом." |
| `backend/AppReleases/releases.json` | 16878 | `PDF` | "text": "Имена файлов PDF и Excel для отчетов теперь используют локальное время профиля, чтобы время формирования совпадало с тем, что пользователь видит внутри отчета." |
| `backend/AppReleases/releases.json` | 16887 | `CSV` | "summary": "В CSV, Excel, PDF и печатной версии реестра дел время заседаний и активности теперь отображается по часовому поясу пользователя из профиля.", |
| `backend/AppReleases/releases.json` | 16887 | `PDF` | "summary": "В CSV, Excel, PDF и печатной версии реестра дел время заседаний и активности теперь отображается по часовому поясу пользователя из профиля.", |
| `backend/AppReleases/releases.json` | 16897 | `CSV` | "text": "Поля последней активности и просмотра ответственным в CSV и Excel используют тот же часовой пояс профиля, что и интерфейс CRM." |
| `backend/AppReleases/releases.json` | 16901 | `PDF` | "text": "PDF и печатная версия реестра дел также показывают дату и время активности в локальном времени пользователя." |
| `backend/AppReleases/releases.json` | 17039 | `ICS` | "text": "ICS-выгрузка календаря дел сохраняет точные моменты времени без двойного сдвига, поэтому внешние календари получают корректное время события." |
| `backend/AppReleases/releases.json` | 17530 | `PDF` | "title": "PDF-отчеты учитывают часовой пояс профиля", |
| `backend/AppReleases/releases.json` | 17531 | `PDF` | "summary": "Уточнено отображение времени формирования PDF-отчетов и подготовлено единое правило для системных отметок времени.", |
| `backend/AppReleases/releases.json` | 17537 | `PDF` | "text": "В PDF-отчетах по юридической работе и управленческих отчетах строка «сформировано» теперь показывает время по часовому поясу пользователя из профиля, а не по локальному времени сервера." |
| `backend/AppReleases/releases.json` | 17622 | `CSV` | "text": "В отчете по загрузке юристов ближайшие задачи теперь показываются и выгружаются в CSV с учетом часового пояса профиля. В карточке дела время последнего просмотра, подтверждения активности и загрузки документов т |
| `backend/AppReleases/releases.json` | 17646 | `CSV` | "text": "Экспорт реестра дел и печатные формы заказ-наряда теперь показывают время формирования по часовому поясу пользователя. Имена файлов CSV, Excel и PDF также получают дату и время из профиля сотрудника." |
| `backend/AppReleases/releases.json` | 17646 | `PDF` | "text": "Экспорт реестра дел и печатные формы заказ-наряда теперь показывают время формирования по часовому поясу пользователя. Имена файлов CSV, Excel и PDF также получают дату и время из профиля сотрудника." |
| `backend/AppReleases/releases.json` | 17665 | `ICS` | "text": "В настройках внешних календарей добавлен блок «Выполненные задачи в календарях». Администратор может оставить выполненные задачи в календарях с отдельной отметкой или скрывать их после выполнения. Правило примен |
| `backend/AppReleases/releases.json` | 17673 | `ICS` | "text": "Исправлена выгрузка календаря дел и календаря задач в ICS: судебные заседания, задачи и другие события с точным временем больше не получают сдвиг при добавлении во внешний календарь." |
| `backend/AppReleases/releases.json` | 18648 | `PDF` | "text": "При выгрузке акта в Word или PDF CRM заменяет символы, которые могут мешать сохранению файла на разных устройствах и серверах. Документ по-прежнему содержит исходный номер акта внутри печатной формы." |
| `backend/AppReleases/releases.json` | 18714 | `PDF` | "summary": "В карточке акта появилась ручная отправка PDF акта на Яндекс.Диск по правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 18720 | `PDF` | "text": "В карточке акта рядом с действиями отправки, печати и скачивания появилась кнопка «Я.Диск». Она формирует PDF акта и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск». |
| `backend/AppReleases/releases.json` | 18733 | `PDF` | "summary": "В карточке счета появилась ручная отправка PDF счета на Яндекс.Диск по правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 18739 | `PDF` | "text": "В карточке счета рядом с действиями отправки, печати и скачивания появилась кнопка «Я.Диск». Она формирует PDF счета и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск |
| `backend/AppReleases/releases.json` | 18752 | `PDF` | "summary": "В карточке договора появилась ручная отправка PDF договора на Яндекс.Диск по настроенным правилам CRM-синхронизации.", |
| `backend/AppReleases/releases.json` | 18758 | `PDF` | "text": "В карточке договора рядом с действиями Word, PDF и Email появилась кнопка «Я.Диск». Она формирует PDF договора и отправляет его в подключенный Яндекс.Диск по шаблонам папки и имени файла из раздела «Я.Диск»." |
| `backend/AppReleases/releases.json` | 18911 | `PDF` | "text": "Акт сверки теперь печатается в фиксированной альбомной форме A4 с размерами колонок, полями, итогами и блоками подписей, приближенными к приложенному бухгалтерскому образцу. Для Excel сохранена структура эталонн |
| `backend/AppReleases/releases.json` | 18938 | `PDF` | "text": "Кнопка Excel выгружает акт сверки в формате .xls с той же бухгалтерской структурой, а кнопка PDF открывает чистую печатную форму без лишних элементов интерфейса." |
| `backend/AppReleases/releases.json` | 19674 | `ICS` | "text": "ICS-выгрузка отдельно отмечена как доступный безопасный контур, а Google Calendar и Яндекс Календарь остаются в подготовительном режиме до закрытия обязательных пунктов." |
| `backend/AppReleases/releases.json` | 19687 | `ICS` | "summary": "В настройках интеграций появился блок состояния календарных провайдеров: видно, что ICS-выгрузка уже доступна, а Google Calendar и Яндекс Календарь ждут отдельной настройки и приемки.", |
| `backend/AppReleases/releases.json` | 19693 | `ICS` | "text": "В разделе «Ключи и внешние сервисы» добавлен блок «Провайдеры календарной синхронизации». Он показывает доступность ICS, Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 19710 | `ICS` | "summary": "Выгрузки календаря задач и календаря дел в ICS теперь фиксируются в общем журнале действий, чтобы администраторы могли видеть, когда и кем был сформирован файл календаря.", |
| `backend/AppReleases/releases.json` | 19716 | `ICS` | "text": "В журнале всех действий появился тип «Синхронизация календаря». В нем отражаются выгрузки ICS-файлов из журнала задач и календаря дел." |
| `backend/AppReleases/releases.json` | 19732 | `ICS` | "title": "Настройка напоминаний в ICS-календарях", |
| `backend/AppReleases/releases.json` | 19733 | `ICS` | "summary": "В журнале задач можно выбрать, за сколько времени напоминать о событиях при выгрузке календаря задач и календаря дел в ICS.", |
| `backend/AppReleases/releases.json` | 19739 | `ICS` | "text": "Перед выгрузкой ICS в журнале задач теперь можно выбрать набор напоминаний: стандартный вариант за 1 час и 15 минут, только за 1 час, за 2 часа и 30 минут или за 1 день." |
| `backend/AppReleases/releases.json` | 19747 | `ICS` | "text": "Если напоминания не выбраны явно, ICS-файлы продолжают выгружаться со стандартными уведомлениями за 1 час и за 15 минут до события." |
| `backend/AppReleases/releases.json` | 19916 | `PDF` | "title": "Excel и PDF выгрузки юридических отчетов", |
| `backend/AppReleases/releases.json` | 19917 | `CSV` | "summary": "В разделе «Отчеты» расширены выгрузки управленческих юридических отчетов: теперь ключевые отчеты по делам можно сохранять не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 19917 | `PDF` | "summary": "В разделе «Отчеты» расширены выгрузки управленческих юридических отчетов: теперь ключевые отчеты по делам можно сохранять не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 19923 | `PDF` | "text": "Для финансового отчета по делам, статистики успешности, текущих дел, загрузки юристов, просроченных процессуальных сроков и диагностики активности добавлены кнопки Excel и PDF." |
| `backend/AppReleases/releases.json` | 19931 | `PDF` | "text": "PDF и Excel показывают сводные показатели и детальные строки отчета, чтобы руководитель мог передать файл дальше без ручной сборки данных из нескольких блоков CRM." |
| `backend/AppReleases/releases.json` | 19954 | `CSV` | "text": "Отчет учитывает доступ пользователя к делам, группирует статистику по ответственным, типам и статусам, а строки можно выгрузить в CSV." |
| `backend/AppReleases/releases.json` | 19973 | `CSV` | "text": "Отчет учитывает доступ пользователя к делам и платежам, группирует суммы по ответственным и статусам дел, а строки отчета можно выгрузить в CSV." |
| `backend/AppReleases/releases.json` | 20267 | `ICS` | "summary": "ICS-файл журнала задач теперь передает во внешние календари напоминания перед началом задачи.", |
| `backend/AppReleases/releases.json` | 20273 | `ICS` | "text": "При выгрузке календаря задач в ICS в каждую задачу добавляются напоминания за 1 час и за 15 минут до срока выполнения." |
| `backend/AppReleases/releases.json` | 20286 | `ICS` | "summary": "ICS-файл календаря дел теперь передает во внешние календари напоминания по событиям с точным временем.", |
| `backend/AppReleases/releases.json` | 20292 | `ICS` | "text": "При выгрузке календаря дел в ICS для судебных заседаний, задач и других событий с указанным временем добавляются напоминания за 1 час и за 15 минут до начала." |
| `backend/AppReleases/releases.json` | 20327 | `PDF` | "title": "Выгрузка реестра дел в Excel и PDF", |
| `backend/AppReleases/releases.json` | 20328 | `CSV` | "summary": "В разделе «Дела» расширены выгрузки реестра: теперь текущую выборку можно скачать не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 20328 | `PDF` | "summary": "В разделе «Дела» расширены выгрузки реестра: теперь текущую выборку можно скачать не только в CSV, но и в Excel или PDF.", |
| `backend/AppReleases/releases.json` | 20334 | `CSV` | "text": "В реестре дел рядом с кнопкой CSV появились кнопки Excel и PDF. Они формируют файл по текущим фильтрам, сортировке и выбранному режиму реестра." |
| `backend/AppReleases/releases.json` | 20334 | `PDF` | "text": "В реестре дел рядом с кнопкой CSV появились кнопки Excel и PDF. Они формируют файл по текущим фильтрам, сортировке и выбранному режиму реестра." |
| `backend/AppReleases/releases.json` | 20338 | `PDF` | "text": "Excel-файл содержит основные данные по делам, задачам, финансам, суду и активности, а PDF использует печатную форму реестра с итогами по выборке." |
| `backend/AppReleases/releases.json` | 20350 | `ICS` | "title": "Выгрузка календаря дел в ICS", |
| `backend/AppReleases/releases.json` | 20351 | `ICS` | "summary": "В календаре дел появилась выгрузка судебных событий и процессуальных сроков в файл ICS для внешних календарей.", |
| `backend/AppReleases/releases.json` | 20357 | `ICS` | "text": "В блоке «Календарь дел» добавлена кнопка ICS. Она формирует файл с судебными заседаниями, процессуальными сроками и другими событиями дел за текущий период календаря." |
| `backend/AppReleases/releases.json` | 20373 | `PDF` | "title": "Выгрузка юридического отчета в Excel и PDF", |
| `backend/AppReleases/releases.json` | 20380 | `CSV` | "text": "В отчете «Юридическая работа» появились серверные выгрузки CSV, Excel и PDF. Кнопки доступны в шапке отчета рядом с обновлением данных." |
| `backend/AppReleases/releases.json` | 20380 | `PDF` | "text": "В отчете «Юридическая работа» появились серверные выгрузки CSV, Excel и PDF. Кнопки доступны в шапке отчета рядом с обновлением данных." |
| `backend/AppReleases/releases.json` | 20462 | `PDF` | "summary": "В журнале задач появился отчет «Планинг по клиентам», который можно выгрузить в Word, PDF или HTML для передачи и печати.", |
| `backend/AppReleases/releases.json` | 20468 | `PDF` | "text": "В «Журнале задач» добавлены кнопки Word, PDF и HTML. Они формируют отчет «Планинг по клиентам» с группировкой задач по клиенту и делу." |
| `backend/AppReleases/releases.json` | 20472 | `PDF` | "text": "В отчете выводятся номер строки, вид задачи, описание, срок, статус, исполнитель и комментарий. Формат подходит для просмотра в браузере, печати, отправки в PDF и открытия в Word." |
| `backend/AppReleases/releases.json` | 20912 | `CSV` | "text": "Номер связанного акта также попадает в выгрузки журнала в CSV, Excel и печатную форму PDF." |
| `backend/AppReleases/releases.json` | 20912 | `PDF` | "text": "Номер связанного акта также попадает в выгрузки журнала в CSV, Excel и печатную форму PDF." |
| `backend/AppReleases/releases.json` | 21064 | `PDF` | "text": "В карточке расторгнутого договора появилась кнопка «Соглашение PDF». Она формирует соглашение о расторжении с данными сторон, датой расторжения, причиной, условиями и финансовыми условиями из карточки договора." |
| `backend/AppReleases/releases.json` | 21091 | `PDF` | "text": "Печать договора, скачивание в Word или PDF и отправка договора по email теперь используют активный шаблон из настроек, поэтому оформление можно поддерживать в одном месте." |
| `backend/AppReleases/releases.json` | 21148 | `PDF` | "text": "В карточке договора появились действия «Печать», «Word», «PDF» и «Email». Теперь договор можно открыть в печатном виде, скачать или отправить клиенту прямо из карточки." |
| `backend/AppReleases/releases.json` | 21179 | `CSV` | "text": "Информация о постановщике также добавлена в карточки доски, подсказки календаря и выгрузку журнала задач в CSV." |
| `backend/Controllers/ActsController.cs` | 1948 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/CashOrdersController.cs` | 1456 | `PDF` | "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения."); |
| `backend/Controllers/CashOrdersController.cs` | 938 | `DownloadPdf` | public async Task<IActionResult> DownloadPdf(int id, CancellationToken ct) |
| `backend/Controllers/CashOrdersController.cs` | 976 | `DownloadWord` | public async Task<IActionResult> DownloadWord(int id, CancellationToken ct) |
| `backend/Controllers/ClientPortalController.cs` | 133 | `DownloadDocument` | public async Task<IActionResult> DownloadDocument( |
| Еще 1482 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

### Интеграции календарей

Google/Yandex/ICS синхронизация, где надо хранить UTC и передавать timezone ID.

| Файл | Строка | Совпадение | Контекст |
| --- | ---: | --- | --- |
| `backend/AppReleases/releases.json` | 3780 | `Google Calendar` | "summary": "CRM больше не заставляет пользователя ждать ответы Google Calendar и Яндекс.Календаря при каждом изменении задачи.", |
| `backend/AppReleases/releases.json` | 3790 | `Google Calendar` | "text": "Несколько быстрых изменений одной задачи объединяются перед обращением к Google Calendar и Яндекс.Календарю, поэтому внешние сервисы получают актуальное состояние без повторной лишней нагрузки." |
| `backend/AppReleases/releases.json` | 3954 | `Google Calendar` | "text": "Webhook Google Calendar, Telegram и MAX, а также служебные публичные маршруты получили отдельные ограничения частоты и размера запроса. Это снижает лишнюю нагрузку от повторных уведомлений и некорректных запросо |
| `backend/AppReleases/releases.json` | 9031 | `Google Calendar` | "text": "Синхронизация с Google Calendar стала устойчивее к кратковременным сбоям внешнего сервиса. Если Google временно недоступен, CRM повторяет запрос и не показывает лишнее предупреждение пользователям из-за одиночно |
| `backend/AppReleases/releases.json` | 9287 | `Google Calendar` | "summary": "Исправлена ситуация после переноса VPS, когда ранее подключенный Google Calendar не мог синхронизироваться из-за серверных ключей защиты. Уже подключенные календари снова продолжают создавать и обновлять собы |
| `backend/AppReleases/releases.json` | 9293 | `Google Calendar` | "text": "Синхронизация Google Calendar снова работает для ранее подключенных пользователей после переноса сервера: задачи из CRM создаются и обновляются во внешнем календаре без ошибки служебного ключа." |
| `backend/AppReleases/releases.json` | 9934 | `Google Calendar` | "text": "Профиль пользователя стал точнее учитывать право на раздел «Календарь». Если сотруднику закрыт календарь, в личном профиле больше не показывается вкладка подключения Google Calendar и Яндекс.Календаря, а действи |
| `backend/AppReleases/releases.json` | 10791 | `Google Calendar` | "summary": "Настройки личной синхронизации Google Calendar и Яндекс.Календаря теперь учитывают актуальную роль пользователя в CRM. Если управление календарями ограничено администратором, старый открытый сеанс больше не п |
| `backend/AppReleases/releases.json` | 10801 | `Google Calendar` | "text": "Пользователь с актуальной ролью администратора в CRM по-прежнему может менять настройки синхронизации Google Calendar и Яндекс.Календаря." |
| `backend/AppReleases/releases.json` | 13297 | `Google Calendar` | "summary": "Google Calendar и Яндекс.Календарь теперь используют те же права видимости календаря, что и CRM: если роли доступны только выбранные сотрудники, во внешнем аккаунте синхронизируются только их календарные зада |
| `backend/AppReleases/releases.json` | 15626 | `Google Calendar` | "summary": "Улучшена синхронизация задач с Google Calendar и Яндекс.Календарём. Если во внешнем календаре появилось несколько событий одной и той же задачи CRM, PayPlanner оставит актуальное событие и удалит лишние дубли |
| `backend/AppReleases/releases.json` | 15632 | `Google Calendar` | "text": "В Google Calendar и Яндекс.Календаре добавлена дополнительная защита от повторного появления одинаковых событий одной CRM-задачи. При синхронизации лишние внешние события удаляются автоматически." |
| `backend/AppReleases/releases.json` | 15735 | `Google Calendar` | "text": "Синхронизация с SUDRF, Google Calendar, Яндекс.Календарём, Яндекс.Диском, Telegram и S3 получила более предсказуемую обработку отказов доступа, отсутствующих объектов, лимитов запросов и временных сбоев." |
| `backend/AppReleases/releases.json` | 15851 | `Google Calendar` | "summary": "В режиме отдельных календарей сотрудников Google Calendar и Яндекс.Календарь теперь создают календарь для активного одобренного пользователя даже тогда, когда у него еще нет задач в периоде синхронизации. Это |
| `backend/AppReleases/releases.json` | 15857 | `Google Calendar` | "text": "Исправлена ситуация, когда в Google Calendar или Яндекс.Календаре мог отсутствовать календарь активного пользователя, если у него еще не было задач для синхронизации." |
| `backend/AppReleases/releases.json` | 15945 | `Google Calendar` | "title": "Google Calendar не откатывает задачи из-за старых дублей", |
| `backend/AppReleases/releases.json` | 15946 | `Google Calendar` | "summary": "Улучшена синхронизация с Google Calendar после удаления или переноса дублей. Если во внешнем календаре осталось старое событие PayPlanner, которое уже не является актуальной связью задачи, CRM больше не приме |
| `backend/AppReleases/releases.json` | 15952 | `Google Calendar` | "text": "Старые дубли событий Google Calendar больше не могут вернуть задачу CRM на прежнее время или в прежний календарь." |
| `backend/AppReleases/releases.json` | 15964 | `Google Calendar` | "title": "Google Calendar больше не дублирует задачи при переносе между календарями", |
| `backend/AppReleases/releases.json` | 15965 | `Google Calendar` | "summary": "Исправлена синхронизация задач с Google Calendar в режиме отдельных календарей сотрудников. Если задача переходит в другой календарь или меняется ответственный, CRM обновляет существующую связь и убирает стар |
| `backend/AppReleases/releases.json` | 15971 | `Google Calendar` | "text": "При синхронизации с Google Calendar одна задача CRM больше не создает несколько одинаковых событий, если для нее меняется календарь сотрудника." |
| `backend/AppReleases/releases.json` | 16041 | `Google Calendar` | "summary": "При синхронизации задач с Google Calendar и Яндекс.Календарем система теперь проверяет часовой пояс внешнего календаря и приводит его к часовому поясу владельца из профиля. Это помогает избежать ситуации, ког |
| `backend/AppReleases/releases.json` | 16051 | `Google Calendar` | "text": "Ранее подключенные календари Google Calendar и Яндекс.Календаря при следующей синхронизации также обновляются до часового пояса владельца, чтобы события отображались без ручной правки timezone у провайдера." |
| `backend/AppReleases/releases.json` | 17048 | `Google Calendar` | "summary": "Синхронизация Google Calendar и Яндекс Календаря дополнительно проверяет переносы задач обратно в CRM и показывает время изменений в журнале по часовому поясу исполнителя.", |
| `backend/AppReleases/releases.json` | 17062 | `Google Calendar` | "text": "Обновление уже созданных событий из CRM в Google Calendar и Яндекс Календаре дополнительно проверено для задач около полуночи." |
| `backend/AppReleases/releases.json` | 17071 | `Google Calendar` | "summary": "Синхронизация задач с Google Calendar и Яндекс Календарем дополнительно проверена для сотрудников из других регионов и событий около полуночи.", |
| `backend/AppReleases/releases.json` | 17081 | `Google Calendar` | "text": "События Google Calendar и Яндекс Календаря получают служебную отметку часового пояса PayPlanner, чтобы последующие синхронизации устойчивее распознавали регион, для которого рассчитано время задачи." |
| `backend/AppReleases/releases.json` | 17192 | `Google Calendar` | "text": "Администраторам из разных регионов стало проще сверять журнал Google Calendar и Яндекс Календаря: один и тот же выбранный период больше не смещается при применении фильтра." |
| `backend/AppReleases/releases.json` | 17588 | `Google Calendar` | "summary": "Задачи, судебные заседания и события календарей теперь обрабатываются по единому правилу: система хранит точный момент времени и показывает его в часовом поясе пользователя из профиля. Это помогает избежать с |
| `backend/AppReleases/releases.json` | 17594 | `Google Calendar` | "text": "Исправлена обработка времени задач при синхронизации с Google Calendar и Яндекс Календарем. Перенос задачи во внешнем календаре теперь сохраняется в CRM как точный момент времени и не пересчитывается повторно пр |
| `backend/AppReleases/releases.json` | 17665 | `Google Calendar` | "text": "В настройках внешних календарей добавлен блок «Выполненные задачи в календарях». Администратор может оставить выполненные задачи в календарях с отдельной отметкой или скрывать их после выполнения. Правило примен |
| `backend/AppReleases/releases.json` | 17669 | `Google Calendar` | "text": "Исправлена выгрузка времени задач в Google Calendar и Яндекс Календарь: задачи теперь передаются по часовому поясу владельца календаря или исполнителя, а не по фиксированному времени CRM." |
| `backend/AppReleases/releases.json` | 17690 | `Google Calendar` | "summary": "Администратор может задать паузу перед повторной полной синхронизацией внешних календарей. Это помогает не создавать лишнюю нагрузку на Google Calendar и Яндекс.Календарь, когда пользователь несколько раз наж |
| `backend/AppReleases/releases.json` | 17700 | `Google Calendar` | "text": "В профиле пользователя кнопка ручной синхронизации Google Calendar и Яндекс.Календаря временно отключается, если полная синхронизация уже запускалась недавно. В это время обычная фоновая синхронизация продолжает |
| `backend/AppReleases/releases.json` | 17709 | `Google Calendar` | "summary": "Исправлена синхронизация даты и времени задач при переносе событий во внешних календарях. Изменения, сделанные в Google Calendar, теперь корректно сохраняются в CRM и не перетираются следующим циклом обмена." |
| `backend/AppReleases/releases.json` | 17715 | `Google Calendar` | "text": "При переносе задачи в Google Calendar новое время теперь корректно применяется к задаче в CRM и сохраняется при следующих циклах синхронизации." |
| `backend/AppReleases/releases.json` | 17719 | `Google Calendar` | "text": "Исправлена обработка времени задач, сохраненных в базе без признака часового пояса: Google Calendar и Яндекс.Календарь больше не получают такие задачи со сдвигом времени." |
| `backend/AppReleases/releases.json` | 17723 | `Google Calendar` | "text": "Если Google Calendar временно отвечает ограничением запросов, синхронизация делает повторные попытки и не показывает ошибку сразу после первого ответа Rate Limit Exceeded." |
| `backend/AppReleases/releases.json` | 17738 | `Google Calendar` | "text": "В разделе настроек внешних календарей появились отдельные интервалы фоновой синхронизации для Google Calendar и Яндекс.Календаря. По умолчанию обмен выполняется каждые 10 минут, а администратор может увеличить и |
| `backend/AppReleases/releases.json` | 17742 | `Google Calendar` | "text": "После сохранения интервала фоновые сервисы Google Calendar и Яндекс.Календаря применяют новые настройки без перезапуска приложения и начинают следующий цикл уже с обновленным расписанием." |
| `backend/AppReleases/releases.json` | 17750 | `Google Calendar` | "text": "Для изменений, пришедших из Google Calendar или Яндекс.Календаря, журнал показывает, что именно поменялось в задаче CRM: срок, длительность, статус или название." |
| `backend/AppReleases/releases.json` | 17769 | `Google Calendar` | "text": "В разделе «Аудит объектов» поля календарной синхронизации теперь отображаются по-русски: «Внешняя синхронизация календарей», «Синхронизация Google Calendar» и «Синхронизация Яндекс.Календаря»." |
| `backend/AppReleases/releases.json` | 17786 | `Google Calendar` | "summary": "В подсказках настройки Google Calendar и Яндекс.Календаря теперь сразу показаны готовые Redirect URI, которые нужно внести в кабинеты OAuth.", |
| `backend/AppReleases/releases.json` | 17792 | `Google Calendar` | "text": "В разделе «Настройки» → «Ключи интеграций» → блок внешних календарей вопросительные знаки рядом с Google Calendar и Яндекс.Календарем теперь показывают отдельный блок «Redirect URI для кабинета провайдера»." |
| `backend/AppReleases/releases.json` | 17796 | `Google Calendar` | "text": "Администратору больше не нужно искать callback URL в профиле пользователя: адрес для Google Calendar и Яндекс.Календаря можно скопировать прямо из окна подсказки." |
| `backend/AppReleases/releases.json` | 17847 | `Google Calendar` | "summary": "В настройках внешних календарей появились инструкции по подключению Google Calendar и Яндекс.Календаря, а профиль пользователя больше не показывает отключенные администратором провайдеры.", |
| `backend/AppReleases/releases.json` | 17853 | `Google Calendar` | "text": "В блоке «Модель синхронизации и конфликты» вопросительные знаки рядом с Google Calendar и Яндекс.Календарем теперь открывают инструкцию по настройке OAuth-приложения и ссылки на нужные кабинеты провайдеров." |
| `backend/AppReleases/releases.json` | 17857 | `Google Calendar` | "text": "Если администратор выключил Google Calendar или Яндекс.Календарь в настройках, соответствующая карточка больше не отображается в профиле пользователя." |
| `backend/AppReleases/releases.json` | 17876 | `Google Calendar` | "text": "В полном журнале синхронизации появилась постраничная навигация, чтобы администратор мог просматривать не только последние события Google Calendar и Яндекс.Календаря." |
| `backend/AppReleases/releases.json` | 17918 | `Google Calendar` | "text": "Исправлена ошибка загрузки статуса Google Calendar и Яндекс Календаря в профиле пользователя. Страница снова корректно показывает состояние подключения и предупреждение о настройке OAuth." |
| `backend/AppReleases/releases.json` | 17926 | `Google Calendar` | "text": "Пояснение помогает администратору понять, как работают отдельные включатели Google Calendar и Яндекс Календаря, главный источник событий, правила конфликтов, дата начала синхронизации, действия при потере доступ |
| `backend/AppReleases/releases.json` | 17934 | `Google Calendar` | "title": "Google Calendar и Яндекс Календарь включаются отдельно", |
| `backend/AppReleases/releases.json` | 17935 | `Google Calendar` | "summary": "В настройках внешних календарей администратор теперь может включать Google Calendar и Яндекс Календарь независимо друг от друга.", |
| `backend/AppReleases/releases.json` | 17941 | `Google Calendar` | "text": "В разделе внешних календарей вместо одного общего переключателя появились отдельные переключатели для Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 17945 | `Google Calendar` | "text": "Карточки провайдеров и чек-лист готовности теперь учитывают выбранный календарь отдельно. Если включен только Google Calendar, выключенный Яндекс Календарь не будет блокировать подготовку подключения." |
| `backend/AppReleases/releases.json` | 17949 | `Google Calendar` | "text": "Фоновые проверки запускаются только для включенных провайдеров: Google Calendar и Яндекс Календарь больше не зависят от одного общего включателя." |
| `backend/AppReleases/releases.json` | 17958 | `Google Calendar` | "summary": "В настройках внешних календарей появился понятный переключатель для включения Google Calendar и Яндекс Календаря.", |
| `backend/AppReleases/releases.json` | 17964 | `Google Calendar` | "text": "В разделе внешних календарей добавлен главный переключатель «Включить Google Calendar и Яндекс Календарь». Администратор может включить календарный контур прямо из настроек без изменения серверного конфига." |
| `backend/AppReleases/releases.json` | 17981 | `Google Calendar` | "summary": "В настройках внешних календарей администратор теперь может задавать общие правила синхронизации Google Calendar и Яндекс Календаря, а также смотреть полный журнал событий.", |
| `backend/AppReleases/releases.json` | 17991 | `Google Calendar` | "text": "Добавлена дата начала синхронизации по умолчанию. Если включен режим управления администратором, эта дата применяется к подключениям Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 17999 | `Google Calendar` | "text": "В настройках добавлен полный журнал синхронизации по Google Calendar и Яндекс Календарю. Администратор видит провайдера, сотрудника, действие, статус, время и сообщение по каждому событию." |
| `backend/AppReleases/releases.json` | 18008 | `Google Calendar` | "summary": "В настройках Google Calendar и Яндекс.Календаря можно указать, с какой даты задачи CRM должны попадать во внешний календарь.", |
| `backend/AppReleases/releases.json` | 18014 | `Google Calendar` | "text": "В профиле пользователя в настройках Google Calendar и Яндекс.Календаря добавлено поле «Синхронизировать задачи с даты». Если указать дату, во внешний календарь будут попадать только задачи со сроком выполнения н |
| `backend/AppReleases/releases.json` | 18031 | `Google Calendar` | "summary": "При отключении Google Calendar или Яндекс.Календаря CRM теперь сначала удаляет созданные PayPlanner календари и события во внешнем календаре.", |
| `backend/AppReleases/releases.json` | 18037 | `Google Calendar` | "text": "В профиле пользователя отключение Google Calendar и Яндекс.Календаря теперь работает с обязательной очисткой: CRM удаляет созданные PayPlanner календари и события во внешнем календаре, а только потом убирает под |
| `backend/AppReleases/releases.json` | 18050 | `Google Calendar` | "summary": "В профиле пользователя появился блок Яндекс.Календаря. Он работает по тем же правилам, что Google Calendar, и позволяет синхронизировать задачи CRM с календарем Яндекса.", |
| `backend/AppReleases/releases.json` | 18060 | `Google Calendar` | "text": "Если у пользователя подключены Google Calendar и Яндекс.Календарь, изменения проходят через CRM и передаются во второй календарь без зацикливания. Например, изменение события в Google обновит задачу CRM, а затем |
| `backend/AppReleases/releases.json` | 18114 | `Google Calendar` | "title": "Исправлена загрузка блока Google Calendar в профиле", |
| `backend/AppReleases/releases.json` | 18115 | `Google Calendar` | "summary": "Блок Google Calendar в профиле пользователя теперь стабильно открывается и показывает состояние подключения, настройки синхронизации, список календарей и последние события.", |
| `backend/AppReleases/releases.json` | 18121 | `Google Calendar` | "text": "Исправлена ошибка, из-за которой раздел Google Calendar в профиле мог не получить статус подключения и не запустить синхронизацию из-за внутренней ошибки сервиса." |
| `backend/AppReleases/releases.json` | 18125 | `Google Calendar` | "text": "Добавлена дополнительная проверка, которая контролирует, что сервис Google Calendar корректно создаётся при запуске приложения." |
| `backend/AppReleases/releases.json` | 18206 | `Google Calendar` | "title": "Улучшено удаление задач и календарей Google Calendar", |
| `backend/AppReleases/releases.json` | 18207 | `Google Calendar` | "summary": "Синхронизация Google Calendar теперь аккуратнее очищает внешние события и календари, чтобы после удаления задач или сотрудников в Google не оставались лишние записи PayPlanner.", |
| `backend/AppReleases/releases.json` | 18213 | `Google Calendar` | "text": "При удалении задачи CRM связанное событие PayPlanner удаляется из Google Calendar. Если событие не удалось удалить, задача не удаляется сразу, а пользователь видит ошибку и может проверить журнал синхронизации." |
| `backend/AppReleases/releases.json` | 18221 | `Google Calendar` | "text": "Журнал Google Calendar теперь фиксирует удаление событий и календарей, а также ошибки очистки, чтобы администратор видел, что именно произошло при удалении задачи или пользователя." |
| `backend/AppReleases/releases.json` | 18225 | `Google Calendar` | "text": "Права на удаление задач по-прежнему управляются ролями в разделе задач. Пользователь без права удаления не сможет удалить задачу и, соответственно, связанное событие в Google Calendar." |
| `backend/AppReleases/releases.json` | 18336 | `Google Calendar` | "title": "Добавлена синхронизация задач с Google Calendar", |
| `backend/AppReleases/releases.json` | 18337 | `Google Calendar` | "summary": "Пользователи могут подключить личный Google Calendar в профиле и синхронизировать с ним задачи CRM. Для администратора поддержан отдельный режим, при котором задачи раскладываются по календарям сотрудников.", |
| `backend/AppReleases/releases.json` | 18343 | `Google Calendar` | "text": "В профиле появился блок Google Calendar. Пользователь может подключить календарь через Google, включить или остановить синхронизацию, запустить обмен вручную и посмотреть последние события синхронизации." |
| `backend/AppReleases/releases.json` | 18347 | `Google Calendar` | "text": "Задачи, назначенные пользователю, создаются и обновляются в Google Calendar. Изменения времени, длительности, названия и статуса события могут возвращаться обратно в CRM по выбранному правилу конфликтов." |
| `backend/AppReleases/releases.json` | 18351 | `Google Calendar` | "text": "Для администратора доступен режим отдельных календарей по исполнителям: при синхронизации все задачи CRM попадают в Google Calendar аккуратно, с разделением по сотрудникам." |
| `backend/AppReleases/releases.json` | 19480 | `Google Calendar` | "summary": "Администратор теперь видит отдельный фоновый сервис для подготовки синхронизации Google Calendar и Яндекс Календаря. Сервис проверяет готовность подключения и события-кандидаты, не меняя внешние календари до  |
| `backend/AppReleases/releases.json` | 19494 | `Google Calendar` | "text": "Google Calendar и Яндекс Календарь по-прежнему не включаются автоматически. До настройки OAuth и приемки модели конфликтов сервис работает как диагностический и подготовительный контур." |
| `backend/AppReleases/releases.json` | 19618 | `Google Calendar` | "summary": "Изменения правил подключения Google Calendar и Яндекс Календаря теперь фиксируются в общем журнале действий. Администраторы могут увидеть, кто изменил модель синхронизации, правило конфликтов, ручное подтверж |
| `backend/AppReleases/releases.json` | 19651 | `Google Calendar` | "text": "Чек-лист готовности теперь учитывает сохраненные правила конфликтов и отметку о dry-run на копии базы. После сохранения этих пунктов видно, какие обязательные шаги еще остаются перед подключением Google Calendar |
| `backend/AppReleases/releases.json` | 19664 | `Google Calendar` | "summary": "В настройках интеграций появился чек-лист готовности к подключению Google Calendar и Яндекс Календаря. Он показывает, какие обязательные шаги уже закрыты, а что еще нужно согласовать перед запуском внешней си |
| `backend/AppReleases/releases.json` | 19674 | `Google Calendar` | "text": "ICS-выгрузка отдельно отмечена как доступный безопасный контур, а Google Calendar и Яндекс Календарь остаются в подготовительном режиме до закрытия обязательных пунктов." |
| `backend/AppReleases/releases.json` | 19687 | `Google Calendar` | "summary": "В настройках интеграций появился блок состояния календарных провайдеров: видно, что ICS-выгрузка уже доступна, а Google Calendar и Яндекс Календарь ждут отдельной настройки и приемки.", |
| `backend/AppReleases/releases.json` | 19693 | `Google Calendar` | "text": "В разделе «Ключи и внешние сервисы» добавлен блок «Провайдеры календарной синхронизации». Он показывает доступность ICS, Google Calendar и Яндекс Календаря." |
| `backend/AppReleases/releases.json` | 19701 | `Google Calendar` | "text": "Google Calendar и Яндекс Календарь не запускаются автоматически: до настройки OAuth и утверждения модели конфликтов они отображаются как недоступные или требующие настройки." |
| `backend/AppReleases/releases.json` | 21534 | `Google Calendar` | "text": "Исправлена синхронизация с Google Calendar: при повторном или параллельном запуске CRM больше не создает несколько одинаковых календарей PayPlanner для одного сотрудника. Если такие дубли уже появились, ближайша |
| `backend/Controllers/CalendarSyncController.cs` | 177 | `Google Calendar` | "Google Calendar", |
| `backend/Controllers/CalendarSyncController.cs` | 229 | `Google Calendar` | ProviderLabel = "Google Calendar", |
| `backend/Controllers/SystemJobsController.cs` | 799 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:Enabled", true)); |
| `backend/Controllers/SystemJobsController.cs` | 802 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:RetentionDays", CalendarSyncLogRetentionService.DefaultRetentionDays))); |
| `backend/Controllers/SystemJobsController.cs` | 805 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:IntervalHours", CalendarSyncLogRetentionService.DefaultIntervalHours))); |
| `backend/Controllers/SystemJobsController.cs` | 808 | `CalendarSync` | _configuration.GetValue("CalendarSync:LogRetention:BatchSize", CalendarSyncLogRetentionService.DefaultBatchSize))); |
| `backend/Controllers/SystemJobsController.cs` | 858 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:Enabled", false)); |
| `backend/Controllers/SystemJobsController.cs` | 861 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", CalendarSyncSchedulerService.DefaultIntervalMinutes))); |
| `backend/Controllers/SystemJobsController.cs` | 864 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:BatchSize", CalendarSyncSchedulerService.DefaultBatchSize))); |
| `backend/Controllers/SystemJobsController.cs` | 867 | `CalendarSync` | _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", CalendarSyncSchedulerService.DefaultLookAheadDays))); |
| `backend/Controllers/TasksController.cs` | 4345 | `ExternalCalendar` | Console.Error.WriteLine($"[ExternalCalendar] Task sync queue failed for task {taskId}: {ex.Message}"); |
| `backend/Controllers/TasksController.cs` | 4361 | `ExternalCalendar` | Console.Error.WriteLine($"[ExternalCalendar] Task sync failed for task {taskId}: {ex.Message}"); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 494 | `Google Calendar` | Assert.Equal("Google Calendar", item.ProviderLabel); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 497 | `Google Calendar` | Assert.Equal("CRM → Google Calendar", item.DirectionLabel); |
| `backend/PayPlanner.Api.Tests/AppFeatureFlagsTests.cs` | 557 | `Google Calendar` | Message = "Календарь «PayPlanner - Иван Иванов» удален из Google Calendar.", |
| `backend/PayPlanner.Api.Tests/ExternalCalendarSyncCoordinatorTests.cs` | 120 | `Google Calendar` | Assert.Contains("Google Calendar", notification.Title); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1476 | `GoogleCalendar` | var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.GoogleCalendar.Tokens.v1"); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 106 | `Google Calendar` | Assert.Contains("Google Calendar отключен администратором", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 783 | `Google Calendar` | Assert.Contains("Повторную полную синхронизацию Google Calendar можно запустить после", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1119 | `Google Calendar` | var task = await CreateTaskAsync(db, user.Id, "Отключить Google Calendar"); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1167 | `Google Calendar` | Assert.Contains("Не удалось отключить Google Calendar", error.Message); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1204 | `Google Calendar` | new TaskCanceledException("Google Calendar timeout.")))); |
| `backend/PayPlanner.Api.Tests/GoogleCalendarSyncServiceTests.cs` | 1209 | `Google Calendar` | Assert.Contains("Не удалось отключить Google Calendar", error.Message); |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 6926 | `Google Calendar` | "Google Calendar", |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 6931 | `Google Calendar` | "Google Calendar", |
| `backend/PayPlanner.Api.Tests/NotificationServiceTests.cs` | 6941 | `Google Calendar` | Assert.Contains("Google Calendar", notification.Title); |
| `backend/PayPlanner.Api.Tests/YandexCalendarSyncServiceTests.cs` | 1190 | `YandexCalendar` | var protector = TestDataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1"); |
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
| `backend/Services/GoogleCalendarSyncService.cs` | 1079 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "google-to-crm", "update-task", "success", "Изменения из Google Calendar применены к задаче.", string.Join("; ", changes), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1146 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "delete-event", "success", "Старое событие Google Calendar удалено после переноса задачи в другой календарь.", BuildTaskSnapshotDetails(task, timeZo |
| `backend/Services/GoogleCalendarSyncService.cs` | 1150 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "delete-event", "error", "Не удалось удалить старое событие Google Calendar после переноса задачи в другой календарь.", ex.Message, cancellationToke |
| `backend/Services/GoogleCalendarSyncService.cs` | 1153 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "create-event", "success", "Задача создана в Google Calendar.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1163 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, task.Id, "crm-to-google", "update-event", "success", "Задача обновлена в Google Calendar.", BuildTaskSnapshotDetails(task, timeZone), cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1331 | `Google Calendar` | await AddLogAsync(connection.Id, calendar.Id, null, "system", "create-calendar", "success", $"В Google Calendar создан календарь «{name}».", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1485 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1486 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при очистке задач раньше даты начала синхронизации.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1495 | `Google Calendar` | await AddLogAsync(connection.Id, link.CalendarSyncCalendarId, link.TaskJournalItemId, "crm-to-google", "delete-event", "error", "Не удалось удалить событие Google Calendar для задачи раньше даты начала синхронизации.", e |
| `backend/Services/GoogleCalendarSyncService.cs` | 1531 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что задача раньше даты начала синхронизации." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1532 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при очистке задачи раньше даты начала синхронизации.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1561 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что в настройках включено скрытие выполненных задач." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1562 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при скрытии выполненной задачи.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1591 | `Google Calendar` | ? "Событие удалено из Google Calendar, потому что доступ к календарю сотрудника отозван в правах роли." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1592 | `Google Calendar` | : "Событие уже отсутствовало в Google Calendar при отзыве доступа к календарю сотрудника.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1717 | `Google Calendar` | await AddLogAsync(connection.Id == 0 ? null : connection.Id, null, null, "system", "oauth-connected", "success", "Google Calendar подключён к профилю.", null, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1730 | `Google Calendar` | throw new InvalidOperationException("Нет refresh token для Google Calendar. Подключите календарь заново."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1875 | `Google Calendar` | ? "Лишний дубль события Google Calendar для задачи CRM удалён." |
| `backend/Services/GoogleCalendarSyncService.cs` | 1876 | `Google Calendar` | : "Лишний дубль события Google Calendar для задачи CRM уже отсутствовал.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1889 | `Google Calendar` | "Не удалось удалить лишний дубль события Google Calendar для задачи CRM.", |
| `backend/Services/GoogleCalendarSyncService.cs` | 2020 | `Google Calendar` | throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2036 | `Google Calendar` | throw new InvalidOperationException(ReadProviderError(response.Payload, response.ReasonPhrase) ?? "Google Calendar API вернул ошибку при удалении."); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2560 | `Google Calendar` | var title = success ? "Google Calendar подключён" : "Ошибка подключения Google Calendar"; |
| `backend/Services/GoogleCalendarSyncService.cs` | 2794 | `Google Calendar` | "googleWins" => "Google Calendar главный", |
| `backend/Services/GoogleCalendarSyncService.cs` | 1821 | `calendarId` | private async Task<bool> DeleteGoogleCalendarAsync(string accessToken, string calendarId, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1823 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}", accessToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1827 | `calendarId` | private async Task<GoogleEventWriteResult> CreateGoogleEventAsync(string accessToken, string calendarId, GoogleEventPayload payload, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1829 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Post, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", accessToken, JsonSerializer.Serialize(payload, JsonOptions)); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1836 | `calendarId` | private async Task<GoogleEventWriteResult> UpdateGoogleEventAsync(string accessToken, string calendarId, string eventId, GoogleEventPayload payload, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1838 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Put, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken, JsonSerializer.Serialize(payload, JsonOptions |
| `backend/Services/GoogleCalendarSyncService.cs` | 1845 | `calendarId` | private async Task<bool> DeleteGoogleEventAsync(string accessToken, string calendarId, string eventId, CancellationToken cancellationToken) |
| `backend/Services/GoogleCalendarSyncService.cs` | 1847 | `calendarId` | using var request = BuildGoogleRequest(HttpMethod.Delete, $"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}", accessToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 1898 | `calendarId` | string calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 1918 | `calendarId` | var url = QueryHelpers.AddQueryString($"{GoogleApiBaseUrl}/calendars/{Uri.EscapeDataString(calendarId)}/events", query!); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2228 | `calendarId` | int? calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 2237 | `calendarId` | var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken); |
| `backend/Services/GoogleCalendarSyncService.cs` | 2242 | `calendarId` | CalendarSyncCalendarId = calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 2257 | `calendarId` | int? calendarId, |
| `backend/Services/GoogleCalendarSyncService.cs` | 2284 | `calendarId` | if (calendarId.HasValue) |
| `backend/Services/GoogleCalendarSyncService.cs` | 2288 | `calendarId` | .Where(item => item.Id == calendarId.Value) |
| `backend/Services/YandexCalendarSyncService.cs` | 72 | `YandexCalendar` | _tokenProtector = dataProtectionProvider.CreateProtector("PayPlanner.YandexCalendar.Tokens.v1"); |
| `backend/Services/YandexCalendarSyncService.cs` | 2309 | `calendarId` | int? calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 2318 | `calendarId` | var detailsJson = await BuildLogDetailsJsonAsync(connectionId, calendarId, taskId, details, cancellationToken); |
| `backend/Services/YandexCalendarSyncService.cs` | 2323 | `calendarId` | CalendarSyncCalendarId = calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 2338 | `calendarId` | int? calendarId, |
| `backend/Services/YandexCalendarSyncService.cs` | 2365 | `calendarId` | if (calendarId.HasValue) |
| `backend/Services/YandexCalendarSyncService.cs` | 2369 | `calendarId` | .Where(item => item.Id == calendarId.Value) |
| `docs/anonymous-integration-security.md` | 7 | `Google Calendar` | - OAuth-callback Google Calendar, Яндекс.Календаря и Яндекс.Диска ограничены 30 запросами в минуту на внешний IP и конкретный маршрут. |
| `docs/anonymous-integration-security.md` | 8 | `Google Calendar` | - Webhook Google Calendar, Telegram и MAX ограничены 300 запросами в минуту на внешний IP и маршрут. Очередь rate limiter отключена, поэтому лишние запросы сразу получают `429` и не удерживают ресурсы приложения. |
| `docs/archive/roadmaps/audit-roadmap-2026-06-10.md` | 492 | `Google Calendar` | - Google Calendar; |
| `docs/archive/roadmaps/audit-roadmap-2026-06-10.md` | 497 | `Google Calendar` | - Выполнено: добавлены/расширены mocked tests для DaData, Sudrf, Yandex Disk, Google Calendar, Yandex Calendar, Telegram и S3; матрица покрытия зафиксирована в `docs/http-integration-error-coverage-2026-06-10.md`; target |
| `docs/archive/roadmaps/audit-roadmap-2026-06-10.md` | 504 | `Google Calendar` | - Google Calendar webhook; |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 63 | `Google Calendar` | - Синхронизация выбранных сотрудников во внешний Google Calendar секретаря или другой роли. |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 213 | `Google Calendar` | - Выполнено: внешний сценарий реализован для Google Calendar и Яндекс.Календаря на базе фактической видимости календаря роли. |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 239 | `Google Calendar` | - [x] Проверить Google Calendar. |
| `docs/archive/roadmaps/calendar-specific-employee-access-roadmap-2026-06-17.md` | 241 | `Google Calendar` | - Выполнено: покрыто unit-тестами Google Calendar sync; реальный аккаунт остается acceptance после деплоя. |
| `docs/archive/roadmaps/full-system-quality-roadmap-2026-08-04.md` | 115 | `Google Calendar` | Оставшиеся анонимные маршруты Google Calendar, Яндекс.Календаря, Яндекс.Диска, Telegram, MAX, feature flags и health получили централизованные ограничения по IP и маршруту; тела webhook ограничены до разбора JSON. OAuth- |
| `docs/archive/roadmaps/full-system-quality-roadmap-2026-08-04.md` | 135 | `Google Calendar` | Обе карточки задачи проверены в локальном браузере на одних и тех же данных: legacy и modern одинаково показывают основного и дополнительного исполнителя с полными ФИО и email, постановщика, согласующих, наблюдателей и р |
| `docs/archive/roadmaps/remaining-roadmap.md` | 251 | `Google Calendar` | - `[x]` 2026-06-23: пользовательские действия Google Calendar и Яндекс.Календаря в режиме `adminManaged` больше не доверяют старому `admin` claim и проверяют актуальную роль пользователя в БД перед изменением личной синх |
| `docs/batch-operations-audit-coverage-2026-06-10.md` | 22 | `Google Calendar` | \| `backend/Services/GoogleCalendarSyncService.cs:190` \| `ExecuteUpdateAsync` \| Один ожидающий OAuth-запрос подключения Google Calendar \| Персональный объектный аудит не создаётся: техническая строка хранит собственный ст |
| `docs/crm-legal-module-gap-report-2026-05-25.md` | 117 | `Google Calendar` | - диагностика календарных провайдеров в настройках интеграций: ICS активен, Google Calendar и Яндекс Календарь явно показывают статус feature flag/OAuth до запуска двусторонней синхронизации; |
| `docs/crm-legal-module-gap-report-2026-05-25.md` | 129 | `Google Calendar` | - Google Calendar и Яндекс Календарь; |
| `docs/full-project-audit-2026-06-10.md` | 1601 | `Google Calendar` | \| Google Calendar \| services + background sync \| Есть OAuth/calendar sync/manual sync/webhook paths. \| |
| `docs/google-calendar-sync-guide.md` | 1 | `Google Calendar` | # Google Calendar: подключение и синхронизация задач |
| `docs/google-calendar-sync-guide.md` | 5 | `Google Calendar` | Подключение Google Calendar добавлено в профиль пользователя, а не в общие настройки CRM. |
| `docs/google-calendar-sync-guide.md` | 7 | `Google Calendar` | Путь для пользователя: профиль пользователя -> блок "Google Calendar". |
| `docs/google-calendar-sync-guide.md` | 11 | `Google Calendar` | При отключении Google Calendar CRM сначала удаляет созданные PayPlanner календари и события во внешнем календаре. Если Google не подтвердил удаление хотя бы одного календаря или события, подключение не отключается, а при |
| `docs/google-calendar-sync-guide.md` | 13 | `Google Calendar` | В настройках подключения можно указать дату начала синхронизации. Если дата задана, CRM создаёт и обновляет в Google Calendar только задачи со сроком выполнения начиная с этой даты. Уже созданные события по задачам раньш |
| Еще 86 совпадений | - | - | Категория ограничена первыми 250 строками, полное количество указано в сводке. |

## Правила разбора

- Совпадение в отчете не всегда означает ошибку: это место, которое надо проверить при переводе на UTC + timezone ID.
- Жесткие смещения вроде `+07:00`, `UTC+7`, `Asia/Novosibirsk` и `AddHours(7)` считаются приоритетными для исправления.
- Для фильтров периода нужно отдельно проверить границы дня: начало и конец должны считаться в timezone пользователя, а храниться и сравниваться в UTC.
- Для календарей Google/Yandex/ICS нужно передавать timezone ID события или календаря, а не полагаться на timezone сервера.

_Корень проверки: `.`_
