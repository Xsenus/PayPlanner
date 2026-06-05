using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/payments")]
[Authorize]
public class PaymentsV2Controller : ControllerBase
{
    private const int MaxPaymentsExportRows = 5000;

    private readonly PaymentContext _db;

    public PaymentsV2Controller(PaymentContext db) => _db = db;

    private Task<DataVisibilityAccess?> ResolvePaymentsVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Payments, ct);

    private Task<TimeZoneInfo> ResolvePaymentTimeZoneAsync(CancellationToken ct)
        => CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(_db, User.GetUserId(), ct);

    private async Task<bool> CanExportPaymentsAsync(CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return User?.IsInRole("admin") == true || User?.Identity?.IsAuthenticated != true;
        }

        if (User.IsInRole("admin") || await _db.IsUserAdminAsync(userId.Value, ct))
        {
            return true;
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.Payments, ct);
        return permission?.CanExport == true;
    }

    [HttpGet]
    public async Task<ActionResult<PaymentsPageResponse>> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] int? contractId = null,
        [FromQuery] int? invoiceId = null,
        [FromQuery] int? actId = null,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] bool excludeInvoiceDocuments = false,
        [FromQuery] string? clientIds = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] string? categoryTokens = null,
        [FromQuery] PaymentType? type = null,
        [FromQuery] PaymentStatus? status = null,
        [FromQuery] decimal? minAmount = null,
        [FromQuery] decimal? maxAmount = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return Unauthorized();

        var query = BuildPaymentsQuery(
            visibility,
            from,
            to,
            clientId,
            caseId,
            legalEntityId,
            contractId,
            invoiceId,
            actId,
            excludeInstallmentParents,
            excludeInvoiceDocuments,
            clientIds,
            clientRole,
            categoryTokens,
            type,
            status,
            minAmount,
            maxAmount,
            search,
            sortBy,
            sortDir);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);

        var summary = await query
            .GroupBy(_ => 1)
            .Select(group => new PaymentsListSummaryDto
            {
                Income = group.Where(payment => payment.Type == PaymentType.Income).Sum(payment => payment.Amount),
                Expense = group.Where(payment => payment.Type == PaymentType.Expense).Sum(payment => payment.Amount),
                Balance =
                    group.Where(payment => payment.Type == PaymentType.Income).Sum(payment => payment.Amount) -
                    group.Where(payment => payment.Type == PaymentType.Expense).Sum(payment => payment.Amount),
                IncomeCount = group.Count(payment => payment.Type == PaymentType.Income),
                ExpenseCount = group.Count(payment => payment.Type == PaymentType.Expense),
                Total = group.Count(),
            })
            .FirstOrDefaultAsync(ct) ?? new PaymentsListSummaryDto();

        return Ok(new PaymentsPageResponse
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            Summary = summary,
        });
    }

    [HttpGet("export.csv")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] int? contractId = null,
        [FromQuery] int? invoiceId = null,
        [FromQuery] int? actId = null,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] bool excludeInvoiceDocuments = false,
        [FromQuery] string? clientIds = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] string? categoryTokens = null,
        [FromQuery] PaymentType? type = null,
        [FromQuery] PaymentStatus? status = null,
        [FromQuery] decimal? minAmount = null,
        [FromQuery] decimal? maxAmount = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return Unauthorized();
        if (!await CanExportPaymentsAsync(ct)) return Forbid();

        var timeZone = await ResolvePaymentTimeZoneAsync(ct);
        var nowLocal = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone);
        var items = await LoadPaymentExportItemsAsync(
            visibility,
            from,
            to,
            clientId,
            caseId,
            legalEntityId,
            contractId,
            invoiceId,
            actId,
            excludeInstallmentParents,
            excludeInvoiceDocuments,
            clientIds,
            clientRole,
            categoryTokens,
            type,
            status,
            minAmount,
            maxAmount,
            search,
            sortBy,
            sortDir,
            ct);

        return File(
            BuildPaymentsCsv(items, nowLocal, timeZone),
            "text/csv; charset=utf-8",
            $"payments-{FormatExportTimestamp(nowLocal)}.csv");
    }

    [HttpGet("export.xls")]
    public async Task<IActionResult> ExportExcel(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] int? contractId = null,
        [FromQuery] int? invoiceId = null,
        [FromQuery] int? actId = null,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] bool excludeInvoiceDocuments = false,
        [FromQuery] string? clientIds = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] string? categoryTokens = null,
        [FromQuery] PaymentType? type = null,
        [FromQuery] PaymentStatus? status = null,
        [FromQuery] decimal? minAmount = null,
        [FromQuery] decimal? maxAmount = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        CancellationToken ct = default)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return Unauthorized();
        if (!await CanExportPaymentsAsync(ct)) return Forbid();

        var timeZone = await ResolvePaymentTimeZoneAsync(ct);
        var nowLocal = CalendarTimeZoneHelper.ToUserLocal(DateTime.UtcNow, timeZone);
        var items = await LoadPaymentExportItemsAsync(
            visibility,
            from,
            to,
            clientId,
            caseId,
            legalEntityId,
            contractId,
            invoiceId,
            actId,
            excludeInstallmentParents,
            excludeInvoiceDocuments,
            clientIds,
            clientRole,
            categoryTokens,
            type,
            status,
            minAmount,
            maxAmount,
            search,
            sortBy,
            sortDir,
            ct);

        return File(
            BuildPaymentsExcel(items, nowLocal, timeZone),
            "application/vnd.ms-excel; charset=utf-8",
            $"payments-{FormatExportTimestamp(nowLocal)}.xls");
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var visibility = await ResolvePaymentsVisibilityAsync(ct);
        if (visibility is null) return Unauthorized();

        var entity = await _db.Payments
            .WithPaymentIncludes()
            .ApplyPaymentVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.Id == id, ct);

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Payment model, CancellationToken ct = default)
    {
        if (model.IncomeTypeId.HasValue)
        {
            var incomeType = await _db.IncomeTypes.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == model.IncomeTypeId.Value);
            if (incomeType is null) return BadRequest("Unknown IncomeTypeId");
            if (incomeType.PaymentType != model.Type)
            {
                return BadRequest("IncomeType.PaymentType mismatches payment.Type");
            }
        }

        var now = DateTime.UtcNow;
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);
        model.SystemNotes = model.SystemNotes ?? string.Empty;
        model.RescheduleCount = Math.Max(0, model.RescheduleCount);
        if (model.LastPaymentDate is null && model.PaidDate.HasValue)
        {
            model.LastPaymentDate = model.PaidDate.Value.Date;
        }

        var createValidationError = await ValidatePaymentAttributionAsync(model, ct);
        if (createValidationError is not null) return createValidationError;
        PaymentDomainService.Normalize(model);
        PaymentDomainService.ApplyStatusRules(model, now, paymentTimeZone);

        _db.Payments.Add(model);
        await _db.SaveChangesAsync(ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, model, now, ct);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/v2/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Payment model, CancellationToken ct = default)
    {
        var entity = await _db.Payments.FindAsync(id);
        if (entity is null) return NotFound();
        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(entity))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.UpdateBlockedMessage });
        }

        if (model.IncomeTypeId.HasValue)
        {
            var incomeType = await _db.IncomeTypes.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == model.IncomeTypeId.Value);
            if (incomeType is null) return BadRequest("Unknown IncomeTypeId");
            if (incomeType.PaymentType != model.Type)
            {
                return BadRequest("IncomeType.PaymentType mismatches payment.Type");
            }
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();
        var paymentTimeZone = await ResolvePaymentTimeZoneAsync(ct);

        var originalDueDate = entity.Date.Date;
        var originalPlannedDate = (entity.PlannedDate == default ? entity.Date : entity.PlannedDate).Date;
        var originalPaidAmount = entity.PaidAmount;
        var originalStatus = entity.Status;
        var originalRescheduleCount = Math.Max(0, entity.RescheduleCount);
        var originalSystemNotes = entity.SystemNotes ?? string.Empty;
        var originalInvoiceId = entity.InvoiceId;

        if (entity.PlannedDate == default)
        {
            entity.PlannedDate = originalPlannedDate;
        }

        entity.Date = model.Date;
        if (model.PlannedDate != default)
        {
            entity.PlannedDate = model.PlannedDate;
        }

        entity.Amount = model.Amount;
        entity.Type = model.Type;
        entity.Status = model.Status;
        entity.Description = model.Description ?? string.Empty;
        entity.Notes = model.Notes ?? string.Empty;
        entity.ClientId = model.ClientId;
        entity.ExpenseClientId = model.ExpenseClientId;
        entity.ClientCaseId = model.ClientCaseId;
        entity.ContractId = model.ContractId;
        entity.DealTypeId = model.DealTypeId;
        entity.IncomeTypeId = model.IncomeTypeId;
        entity.PaymentSourceId = model.PaymentSourceId;
        entity.PaymentStatusId = model.PaymentStatusId;
        entity.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        entity.AccountDate = model.AccountDate;
        entity.PaidDate = model.PaidDate;
        entity.PaymentOrderNumber = model.PaymentOrderNumber;
        entity.PaymentDocumentDate = model.PaymentDocumentDate;
        entity.PaidAmount = model.PaidAmount;
        entity.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? entity.LastPaymentDate;
        entity.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        var updateValidationError = await ValidatePaymentAttributionAsync(entity, ct);
        if (updateValidationError is not null) return updateValidationError;
        PaymentDomainService.Normalize(entity);

        if (entity.LastPaymentDate is null && entity.PaidAmount > 0m)
        {
            entity.LastPaymentDate = (model.LastPaymentDate ?? model.PaidDate)?.Date;
        }

        PaymentDomainService.ApplyStatusRules(entity, now, paymentTimeZone);

        var rescheduleCount = originalRescheduleCount;
        var plannedDateChanged = model.PlannedDate != default && originalPlannedDate != entity.PlannedDate.Date;
        if (plannedDateChanged)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {entity.PlannedDate:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        var userChangedDueDate = model.Date.Date != originalDueDate;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {entity.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }
        else if (originalDueDate != entity.Date.Date)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Дата платежа автоматически скорректирована на {entity.Date:dd.MM.yyyy}",
                now,
                actor,
                paymentTimeZone);
        }

        if (Math.Abs(originalPaidAmount - entity.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {entity.PaidAmount.ToString("N2", culture)}",
                now,
                actor,
                paymentTimeZone);
        }

        if (originalStatus != entity.Status)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {entity.Status.ToDisplayString()}",
                now,
                actor,
                paymentTimeZone);
        }

        entity.RescheduleCount = rescheduleCount;

        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, entity, now, ct);
        if (originalInvoiceId.HasValue
            && originalInvoiceId.Value > 0
            && originalInvoiceId != entity.InvoiceId)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, now, ct);
        }

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct = default)
    {
        var entity = await _db.Payments.FindAsync(id);
        if (entity is null) return NotFound();
        if (
            InvoiceManagedPaymentRules.IsInvoiceManagedPayment(entity)
            && (
                entity.DocumentKind == DocumentKind.Invoice
                || InvoiceManagedPaymentRules.IsGeneratedInvoiceRemainderPayment(entity)
            )
        )
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.DeleteBlockedMessage });
        }
        var invoiceId = entity.InvoiceId;
        _db.Payments.Remove(entity);
        await _db.SaveChangesAsync(ct);
        if (invoiceId.HasValue && invoiceId.Value > 0)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, invoiceId.Value, DateTime.UtcNow, ct);
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    private IQueryable<Payment> BuildPaymentsQuery(
        DataVisibilityAccess visibility,
        DateTime? from,
        DateTime? to,
        int? clientId,
        int? caseId,
        int? legalEntityId,
        int? contractId,
        int? invoiceId,
        int? actId,
        bool excludeInstallmentParents,
        bool excludeInvoiceDocuments,
        string? clientIds,
        string? clientRole,
        string? categoryTokens,
        PaymentType? type,
        PaymentStatus? status,
        decimal? minAmount,
        decimal? maxAmount,
        string? search,
        string? sortBy,
        string? sortDir)
    {
        var query = _db.Payments.AsQueryable()
            .WithPaymentIncludes()
            .ApplyPaymentVisibility(visibility)
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents)
            .ApplyPaymentFilters(from, to, clientId, caseId, search, clientRole, legalEntityId);

        if (contractId.HasValue)
        {
            query = query.Where(payment =>
                payment.ContractId == contractId.Value
                || _db.ContractPayments.Any(link => link.ContractId == contractId.Value && link.PaymentId == payment.Id)
                || _db.ContractInvoices.Any(link => link.ContractId == contractId.Value && link.InvoiceId == payment.Id)
                || (payment.InvoiceId.HasValue && _db.Payments.Any(invoicePayment =>
                    invoicePayment.InvoiceId == payment.InvoiceId.Value
                    && invoicePayment.DocumentKind == DocumentKind.Invoice
                    && (invoicePayment.ContractId == contractId.Value
                        || _db.ContractInvoices.Any(link => link.ContractId == contractId.Value && link.InvoiceId == invoicePayment.Id)))));
        }

        if (invoiceId.HasValue)
        {
            query = query.Where(payment =>
                payment.Id == invoiceId.Value
                || _db.Payments.Any(invoice =>
                    invoice.Id == invoiceId.Value
                    && invoice.InvoiceId.HasValue
                    && payment.InvoiceId == invoice.InvoiceId.Value));
        }

        if (actId.HasValue)
        {
            query = query.Where(payment =>
                _db.Acts.Any(act =>
                    act.Id == actId.Value
                    && (
                        (act.InvoiceId.HasValue
                            && (payment.Id == act.InvoiceId.Value
                                || _db.Payments.Any(invoice =>
                                    invoice.Id == act.InvoiceId.Value
                                    && invoice.InvoiceId.HasValue
                                    && payment.InvoiceId == invoice.InvoiceId.Value)))
                        || (!act.InvoiceId.HasValue
                            && act.InvoiceNumber != null
                            && payment.Account == act.InvoiceNumber))));
        }

        if (excludeInvoiceDocuments)
        {
            query = query.Where(payment => payment.DocumentKind != DocumentKind.Invoice);
        }

        var clientIdList = ParseIntList(clientIds);
        if (clientIdList.Count > 0)
        {
            var normalizedClientRole = QueryExtensions.NormalizeClientRole(clientRole);
            query = normalizedClientRole switch
            {
                "counterparty" => query.Where(payment => payment.ClientId.HasValue && clientIdList.Contains(payment.ClientId.Value)),
                "expenseClient" => query.Where(payment => payment.ExpenseClientId.HasValue && clientIdList.Contains(payment.ExpenseClientId.Value)),
                _ => query.Where(payment =>
                    (payment.ClientId.HasValue && clientIdList.Contains(payment.ClientId.Value))
                    || (payment.ExpenseClientId.HasValue && clientIdList.Contains(payment.ExpenseClientId.Value))),
            };
        }

        if (type.HasValue)
        {
            query = query.Where(payment => payment.Type == type.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(payment => payment.Status == status.Value);
        }

        if (minAmount.HasValue)
        {
            query = query.Where(payment => payment.Amount >= minAmount.Value);
        }

        if (maxAmount.HasValue)
        {
            query = query.Where(payment => payment.Amount <= maxAmount.Value);
        }

        return ApplyCategoryFilters(query, categoryTokens).ApplyPaymentSort(sortBy, sortDir);
    }

    private Task<List<Payment>> LoadPaymentExportItemsAsync(
        DataVisibilityAccess visibility,
        DateTime? from,
        DateTime? to,
        int? clientId,
        int? caseId,
        int? legalEntityId,
        int? contractId,
        int? invoiceId,
        int? actId,
        bool excludeInstallmentParents,
        bool excludeInvoiceDocuments,
        string? clientIds,
        string? clientRole,
        string? categoryTokens,
        PaymentType? type,
        PaymentStatus? status,
        decimal? minAmount,
        decimal? maxAmount,
        string? search,
        string? sortBy,
        string? sortDir,
        CancellationToken ct)
        => BuildPaymentsQuery(
                visibility,
                from,
                to,
                clientId,
                caseId,
                legalEntityId,
                contractId,
                invoiceId,
                actId,
                excludeInstallmentParents,
                excludeInvoiceDocuments,
                clientIds,
                clientRole,
                categoryTokens,
                type,
                status,
                minAmount,
                maxAmount,
                search,
                sortBy,
                sortDir)
            .Take(MaxPaymentsExportRows)
            .AsNoTracking()
            .ToListAsync(ct);

    private static byte[] BuildPaymentsCsv(IReadOnlyCollection<Payment> payments, DateTime generatedAtLocal, TimeZoneInfo timeZone)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(';', PaymentExportHeaders.Select(CsvCell)));
        foreach (var payment in payments)
        {
            builder.AppendLine(string.Join(';', GetPaymentExportCells(payment, timeZone).Select(CsvCell)));
        }

        builder.AppendLine();
        builder.AppendLine($"{CsvCell("Сформировано")};{CsvCell(FormatExportDateTime(generatedAtLocal))}");
        builder.AppendLine($"{CsvCell("Часовой пояс")};{CsvCell(timeZone.Id)}");
        if (payments.Count >= MaxPaymentsExportRows)
        {
            builder.AppendLine($"{CsvCell("Ограничение выгрузки")};{CsvCell($"Показаны первые {MaxPaymentsExportRows.ToString("N0", RuCulture)} записей")}");
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    private static byte[] BuildPaymentsExcel(IReadOnlyCollection<Payment> payments, DateTime generatedAtLocal, TimeZoneInfo timeZone)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
        builder.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");
        builder.AppendLine("  <Worksheet ss:Name=\"Платежи\">");
        builder.AppendLine("    <Table>");
        AppendExcelRow(builder, PaymentExportHeaders);
        foreach (var payment in payments)
        {
            AppendExcelRow(builder, GetPaymentExportCells(payment, timeZone));
        }

        AppendExcelRow(builder, Array.Empty<string>());
        AppendExcelRow(builder, ["Сформировано", FormatExportDateTime(generatedAtLocal)]);
        AppendExcelRow(builder, ["Часовой пояс", timeZone.Id]);
        if (payments.Count >= MaxPaymentsExportRows)
        {
            AppendExcelRow(builder, ["Ограничение выгрузки", $"Показаны первые {MaxPaymentsExportRows.ToString("N0", RuCulture)} записей"]);
        }

        builder.AppendLine("    </Table>");
        builder.AppendLine("  </Worksheet>");
        builder.AppendLine("</Workbook>");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] PaymentExportHeaders =
    [
        "ID",
        "Дата платежа",
        "Плановая дата",
        "Дата оплаты",
        "Тип",
        "Статус",
        "Контрагент",
        "Клиент расхода",
        "Дело",
        "Описание",
        "Категория",
        "Источник",
        "Сумма",
        "Оплачено",
        "Остаток",
        "Счет",
        "Дата счета",
        "Платежный документ",
        "Дата платежного документа",
        "Создано",
    ];

    private static string[] GetPaymentExportCells(Payment payment, TimeZoneInfo timeZone)
        =>
        [
            payment.Id.ToString(RuCulture),
            FormatExportDate(payment.Date),
            FormatExportDate(payment.PlannedDate),
            FormatExportDate(payment.PaidDate),
            payment.Type == PaymentType.Expense ? "Расход" : "Доход",
            payment.Status.ToDisplayString(),
            GetCounterpartyName(payment),
            GetExpenseClientName(payment),
            payment.ClientCase?.Title ?? string.Empty,
            payment.Description ?? string.Empty,
            payment.DealType?.Name ?? payment.IncomeType?.Name ?? payment.Service?.Name ?? string.Empty,
            payment.PaymentSource?.Name ?? string.Empty,
            FormatExportMoney(payment.Amount),
            FormatExportMoney(payment.PaidAmount),
            FormatExportMoney(payment.OutstandingAmount),
            payment.Account ?? string.Empty,
            FormatExportDate(payment.AccountDate),
            payment.PaymentOrderNumber ?? string.Empty,
            FormatExportDate(payment.PaymentDocumentDate),
            FormatExportDateTime(CalendarTimeZoneHelper.ToUserLocal(payment.CreatedAt, timeZone)),
        ];

    private static string GetCounterpartyName(Payment payment)
        => payment.Client?.Name
            ?? payment.LegalEntity?.ShortName
            ?? payment.LegalEntity?.FullName
            ?? payment.CounterpartyName
            ?? string.Empty;

    private static string GetExpenseClientName(Payment payment)
        => payment.ExpenseClient?.Name
            ?? payment.ExpenseLegalEntity?.ShortName
            ?? payment.ExpenseLegalEntity?.FullName
            ?? string.Empty;

    private static string FormatExportTimestamp(DateTime localDateTime)
        => localDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static string FormatExportDate(DateTime date)
        => date == default ? string.Empty : date.ToString("dd.MM.yyyy", RuCulture);

    private static string FormatExportDate(DateTime? date)
        => date.HasValue ? FormatExportDate(date.Value) : string.Empty;

    private static string FormatExportDateTime(DateTime localDateTime)
        => localDateTime.ToString("dd.MM.yyyy HH:mm", RuCulture);

    private static string FormatExportMoney(decimal amount)
        => amount.ToString("N2", RuCulture);

    private static string CsvCell(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        return $"\"{normalized.Replace("\"", "\"\"")}\"";
    }

    private static void AppendExcelRow(StringBuilder builder, IEnumerable<string?> cells)
    {
        builder.AppendLine("      <Row>");
        foreach (var cell in cells)
        {
            builder.Append("        <Cell><Data ss:Type=\"String\">");
            builder.Append(WebUtility.HtmlEncode(cell ?? string.Empty));
            builder.AppendLine("</Data></Cell>");
        }

        builder.AppendLine("      </Row>");
    }

    private static IQueryable<Payment> ApplyCategoryFilters(IQueryable<Payment> query, string? categoryTokens)
    {
        if (string.IsNullOrWhiteSpace(categoryTokens))
        {
            return query;
        }

        var dealIds = new HashSet<int>();
        var incomeIds = new HashSet<int>();
        var sourceIds = new HashSet<int>();

        foreach (var token in categoryTokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("deal-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token[5..], out var dealId))
            {
                dealIds.Add(dealId);
                continue;
            }

            if (token.StartsWith("income-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token[7..], out var incomeId))
            {
                incomeIds.Add(incomeId);
                continue;
            }

            if (token.StartsWith("source-", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(token[7..], out var sourceId))
            {
                sourceIds.Add(sourceId);
            }
        }

        if (dealIds.Count == 0 && incomeIds.Count == 0 && sourceIds.Count == 0)
        {
            return query.Where(_ => false);
        }

        return query.Where(payment =>
            (payment.DealTypeId.HasValue && dealIds.Contains(payment.DealTypeId.Value)) ||
            (payment.IncomeTypeId.HasValue && incomeIds.Contains(payment.IncomeTypeId.Value)) ||
            (payment.PaymentSourceId.HasValue && sourceIds.Contains(payment.PaymentSourceId.Value)));
    }

    private static HashSet<int> ParseIntList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    private static string MergeSystemNotes(string original, string? incoming)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AppendLines(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            foreach (var line in source.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (seen.Add(trimmed)) lines.Add(trimmed);
            }
        }

        AppendLines(original);
        AppendLines(incoming ?? string.Empty);

        return string.Join("\n", lines);
    }

    private async Task<ActionResult?> ValidatePaymentAttributionAsync(Payment payment, CancellationToken ct)
    {
        if (payment.Type == PaymentType.Expense && payment.ExpenseClientId.HasValue && payment.ExpenseClientId.Value > 0)
        {
            var expenseClientExists = await _db.Clients
                .AsNoTracking()
                .AnyAsync(client => client.Id == payment.ExpenseClientId.Value, ct);
            if (!expenseClientExists)
            {
                return BadRequest($"Unknown expenseClientId {payment.ExpenseClientId.Value}");
            }
        }

        if (!payment.ClientCaseId.HasValue || payment.ClientCaseId.Value <= 0)
        {
            return null;
        }

        var caseOwnerClientId = payment.Type == PaymentType.Expense
            ? payment.ExpenseClientId
            : payment.ClientId;
        if (!caseOwnerClientId.HasValue || caseOwnerClientId.Value <= 0)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest("ExpenseClientId is required when clientCaseId is specified for expense payments.")
                : BadRequest("ClientId is required when clientCaseId is specified.");
        }

        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == payment.ClientCaseId.Value, ct);
        if (clientCase is null)
        {
            return BadRequest($"Unknown clientCaseId {payment.ClientCaseId.Value}");
        }

        if (clientCase.ClientId != caseOwnerClientId.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseClientId {caseOwnerClientId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to clientId {caseOwnerClientId.Value}");
        }

        return null;
    }
}
