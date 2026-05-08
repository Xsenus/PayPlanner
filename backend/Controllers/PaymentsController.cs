using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private const decimal AmountTolerance = 0.009m;
    private const string LegacyPartialPaymentNoteText = "Частичная оплата";

    private static readonly Regex RemainderDateHintRegex = new(
        @"(?:Остаток[^\r\n]{0,120}(?:->|→|перенесен(?:о)?\s+на)|перенесен(?:о)?\s+на)\s*(\d{2}\.\d{2}\.\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LegacyPartialRemainderNoteRegex = new(
        @"Частичная\s+оплата:\s*(?<paid>[\d\s.,]+)\s*/\s*(?<original>[\d\s.,]+)[^\r\n]*?Остаток\s*(?<remainder>[\d\s.,]+)(?:\s*(?:->|→|перенес[^\d\r\n]{0,60}))?\s*(?<date>\d{2}\.\d{2}\.\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;
    private bool _skipRemainderTransferFormRequirements;
    public PaymentsController(PaymentContext db, IFormRequirementsService formRequirements)
    {
        _db = db;
        _formRequirements = formRequirements;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] string? clientRole = null,
        CancellationToken ct = default)
    {
        var q = _db.Payments
            .AsQueryable()
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents)
            .ApplyPaymentFilters(from, to, clientId, caseId, null, clientRole, legalEntityId);

        var list = await ProjectPaymentsForRead(q)
            .OrderBy(p => p.Date)
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await ProjectPaymentsForRead(_db.Payments.Where(p => p.Id == id))
            .FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return NotFound();
        }

        return Ok(item);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Payment model, CancellationToken ct)
    {
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var now = DateTime.UtcNow;
        if (model.DocumentKind == default)
        {
            model.DocumentKind = DocumentKind.Payment;
        }
        model.SystemNotes = model.SystemNotes ?? string.Empty;
        model.RescheduleCount = Math.Max(0, model.RescheduleCount);
        if (model.LastPaymentDate is null && model.PaidDate.HasValue)
        {
            model.LastPaymentDate = model.PaidDate.Value.Date;
        }

        await SyncInvoiceLinkAsync(model, ct);
        var createValidationError = await ValidatePaymentAttributionAsync(model, ct);
        if (createValidationError is not null) return createValidationError;
        var rawPaidAmount = model.PaidAmount;
        PaymentDomainService.Normalize(model);
        PaymentDomainService.ApplyStatusRules(model, now);

        _db.Payments.Add(model);
        await _db.SaveChangesAsync(ct);
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, model, rawPaidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, model, now, ct);
        await _db.SaveChangesAsync(ct);
        PopulateAuditMetadata(_db, model);
        return Created($"/api/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Payment model, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(e))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.UpdateBlockedMessage });
        }

        var transferProtectionError = await ValidatePartialRemainderUpdateAsync(e, model, ct);
        if (transferProtectionError is not null) return transferProtectionError;

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != model.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();

        var originalDueDate = e.Date.Date;
        var originalPlannedDate = (e.PlannedDate == default ? e.Date : e.PlannedDate).Date;
        var originalPaidAmount = e.PaidAmount;
        var originalStatus = e.Status;
        var originalRescheduleCount = Math.Max(0, e.RescheduleCount);
        var originalSystemNotes = e.SystemNotes ?? string.Empty;
        var originalInvoiceId = e.InvoiceId;

        if (e.PlannedDate == default)
        {
            e.PlannedDate = originalPlannedDate;
        }

        e.Date = model.Date;
        if (model.PlannedDate != default)
        {
            e.PlannedDate = model.PlannedDate;
        }
        e.Amount = model.Amount;
        e.Type = model.Type;
        e.Status = model.Status;
        e.Description = model.Description ?? string.Empty;
        e.Notes = model.Notes ?? string.Empty;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        e.ExpenseClientId = model.ExpenseClientId;
        e.ExpenseLegalEntityId = model.ExpenseLegalEntityId;
        e.ClientCaseId = model.ClientCaseId;
        e.ContractId = model.ContractId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.InvoiceId = model.InvoiceId;
        e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        e.DocumentKind = model.DocumentKind == default ? DocumentKind.Payment : model.DocumentKind;
        e.AccountDate = model.AccountDate;
        e.PaidDate = model.PaidDate;
        e.PaymentOrderNumber = model.PaymentOrderNumber;
        e.PaymentDocumentDate = model.PaymentDocumentDate;
        var rawPaidAmount = model.PaidAmount;
        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, e.Id, ct);
        if (rawPaidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }
        e.PaidAmount = rawPaidAmount;
        e.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? e.LastPaymentDate;
        e.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        await SyncInvoiceLinkAsync(e, ct);
        var updateValidationError = await ValidatePaymentAttributionAsync(e, ct);
        if (updateValidationError is not null) return updateValidationError;
        PaymentDomainService.Normalize(e);

        if (e.LastPaymentDate is null && e.PaidAmount > 0m)
        {
            e.LastPaymentDate = (model.LastPaymentDate ?? model.PaidDate)?.Date;
        }

        PaymentDomainService.ApplyStatusRules(e, now);

        var rescheduleCount = originalRescheduleCount;
        var plannedDateChanged = model.PlannedDate != default && originalPlannedDate != e.PlannedDate.Date;
        if (plannedDateChanged)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {e.PlannedDate:dd.MM.yyyy}",
                now,
                actor);
        }

        var userChangedDueDate = model.Date.Date != originalDueDate;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {e.Date:dd.MM.yyyy}",
                now,
                actor);
        }
        else if (originalDueDate != e.Date.Date)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа автоматически скорректирована на {e.Date:dd.MM.yyyy}",
                now,
                actor);
        }

        if (Math.Abs(originalPaidAmount - e.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {e.PaidAmount.ToString("N2", culture)}",
                now,
                actor);
        }

        if (originalStatus != e.Status)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {e.Status.ToDisplayString()}",
                now,
                actor);
        }

        e.RescheduleCount = rescheduleCount;

        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, e, rawPaidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, e, now, ct);
        if (originalInvoiceId.HasValue
            && originalInvoiceId.Value > 0
            && originalInvoiceId != e.InvoiceId)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, now, ct);
        }

        await _db.SaveChangesAsync(ct);
        PopulateAuditMetadata(_db, e);
        return Ok(e);
    }

    [HttpPut("{id:int}/invoice-managed")]
    public async Task<IActionResult> UpdateInvoiceManaged(int id, [FromBody] Payment model, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        if (!InvoiceManagedPaymentRules.IsInvoiceManagedPayment(e))
        {
            return BadRequest(new { message = "Payment is not invoice-managed." });
        }

        var transferProtectionError = await ValidatePartialRemainderUpdateAsync(e, model, ct);
        if (transferProtectionError is not null) return transferProtectionError;

        if (model.IncomeTypeId.HasValue)
        {
            var it = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value);
            if (it is null) return BadRequest("Unknown IncomeTypeId");
            if (it.PaymentType != e.Type) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();

        var originalDueDate = e.Date.Date;
        var originalPlannedDate = (e.PlannedDate == default ? e.Date : e.PlannedDate).Date;
        var originalPaidAmount = e.PaidAmount;
        var originalStatus = e.Status;
        var originalRescheduleCount = Math.Max(0, e.RescheduleCount);
        var originalSystemNotes = e.SystemNotes ?? string.Empty;

        if (e.PlannedDate == default)
        {
            e.PlannedDate = originalPlannedDate;
        }

        var immutableInvoiceId = e.InvoiceId;
        var immutableDocumentKind = e.DocumentKind;
        var immutableAccount = e.Account;
        var immutableAccountDate = e.AccountDate;
        var immutableLegacyAccountNumber = e.LegacyAccountNumber;
        var immutableLegacyAccountDate = e.LegacyAccountDate;
        var immutableType = e.Type;

        e.Date = model.Date;
        if (model.PlannedDate != default)
        {
            e.PlannedDate = model.PlannedDate;
        }
        e.Amount = model.Amount;
        e.Status = model.Status;
        e.Description = model.Description ?? string.Empty;
        e.Notes = model.Notes ?? string.Empty;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        e.ExpenseClientId = model.ExpenseClientId;
        e.ExpenseLegalEntityId = model.ExpenseLegalEntityId;
        e.ClientCaseId = model.ClientCaseId;
        e.ContractId = model.ContractId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.PaidDate = model.PaidDate;
        e.PaymentOrderNumber = model.PaymentOrderNumber;
        e.PaymentDocumentDate = model.PaymentDocumentDate;
        var rawPaidAmount = model.PaidAmount;
        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, e.Id, ct);
        if (rawPaidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }
        e.PaidAmount = rawPaidAmount;
        e.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? e.LastPaymentDate;
        e.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        e.InvoiceId = immutableInvoiceId;
        e.DocumentKind = immutableDocumentKind;
        e.Account = immutableAccount;
        e.AccountDate = immutableAccountDate;
        e.LegacyAccountNumber = immutableLegacyAccountNumber;
        e.LegacyAccountDate = immutableLegacyAccountDate;
        e.Type = immutableType;

        await SyncInvoiceManagedPaymentBindingsAsync(e, ct);
        var invoiceManagedValidationError = await ValidatePaymentAttributionAsync(e, ct);
        if (invoiceManagedValidationError is not null) return invoiceManagedValidationError;
        PaymentDomainService.Normalize(e);

        if (e.LastPaymentDate is null && e.PaidAmount > 0m)
        {
            e.LastPaymentDate = (model.LastPaymentDate ?? model.PaidDate)?.Date;
        }

        PaymentDomainService.ApplyStatusRules(e, now);

        var rescheduleCount = originalRescheduleCount;
        var plannedDateChanged = model.PlannedDate != default && originalPlannedDate != e.PlannedDate.Date;
        if (plannedDateChanged)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {e.PlannedDate:dd.MM.yyyy}",
                now,
                actor);
        }

        var userChangedDueDate = model.Date.Date != originalDueDate;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {e.Date:dd.MM.yyyy}",
                now,
                actor);
        }
        else if (originalDueDate != e.Date.Date)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Дата платежа автоматически скорректирована на {e.Date:dd.MM.yyyy}",
                now,
                actor);
        }

        if (Math.Abs(originalPaidAmount - e.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {e.PaidAmount.ToString("N2", culture)}",
                now,
                actor);
        }

        if (originalStatus != e.Status)
        {
            PaymentDomainService.AppendSystemNote(
                e,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {e.Status.ToDisplayString()}",
                now,
                actor);
        }

        e.RescheduleCount = rescheduleCount;

        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, e, rawPaidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, e, now, ct);
        await _db.SaveChangesAsync(ct);
        PopulateAuditMetadata(_db, e);
        return Ok(e);
    }

    [HttpGet("partial-remainder-candidates")]
    public async Task<ActionResult<PartialPaymentRemainderCandidatesResponse>> GetPartialRemainderCandidates(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] string? clientRole = null,
        [FromQuery] PaymentType? type = null,
        [FromQuery] bool includeFuture = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var now = DateTime.UtcNow;
        var today = now.Date;
        var sourceIdsWithRemainders = await GetPartialRemainderSourceIdsAsync(ct);

        var query = _db.Payments
            .AsQueryable()
            .WithPaymentIncludes()
            .ApplyPaymentFilters(from, to, clientId, caseId, null, clientRole, legalEntityId)
            .Where(payment =>
                payment.DocumentKind != DocumentKind.Invoice
                && payment.Status != PaymentStatus.Cancelled
                && payment.PaidAmount > 0m
                && (payment.Amount > payment.PaidAmount + AmountTolerance
                    || (payment.SystemNotes != null && payment.SystemNotes.Contains(LegacyPartialPaymentNoteText))
                    || (payment.Notes != null && payment.Notes.Contains(LegacyPartialPaymentNoteText))));

        if (!includeFuture)
        {
            query = query.Where(payment => payment.Date < today);
        }

        if (type.HasValue)
        {
            query = query.Where(payment => payment.Type == type.Value);
        }

        var rawCandidates = await query
            .OrderByDescending(payment => payment.Date)
            .ThenByDescending(payment => payment.Id)
            .AsNoTracking()
            .ToListAsync(ct);

        var candidates = new List<PartialPaymentRemainderCandidateDto>();
        foreach (var payment in rawCandidates)
        {
            if (sourceIdsWithRemainders.Contains(payment.Id)
                || PaymentRemainderTransferRules.IsPartialRemainderSourcePayment(payment)
                || !TryBuildPartialRemainderCandidate(payment, today, out var candidate))
            {
                continue;
            }

            candidates.Add(candidate);
        }

        var total = candidates.Count;
        var pageItems = candidates
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Ok(new PartialPaymentRemainderCandidatesResponse
        {
            Items = pageItems,
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalOriginalAmount = candidates.Sum(item => item.OriginalAmount),
            TotalPaidAmount = candidates.Sum(item => item.PaidAmount),
            TotalRemainderAmount = candidates.Sum(item => item.RemainderAmount),
            IncludeFuture = includeFuture,
            CheckedAt = now,
        });
    }

    private static bool TryBuildPartialRemainderCandidate(
        Payment payment,
        DateTime today,
        out PartialPaymentRemainderCandidateDto candidate)
    {
        candidate = default!;

        var paidAmount = Math.Round(payment.PaidAmount, 2, MidpointRounding.AwayFromZero);
        if (paidAmount <= 0m)
        {
            return false;
        }

        var originalAmount = ResolveStoredPartialRemainderOriginalAmount(payment);
        var remainderAmount = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);
        if (remainderAmount <= AmountTolerance)
        {
            return false;
        }

        var suggestedDate = TryInferSuggestedRemainderDate(payment, out var hintedDate)
            ? hintedDate
            : today;

        candidate = new PartialPaymentRemainderCandidateDto
        {
            Payment = payment,
            OriginalAmount = originalAmount,
            PaidAmount = paidAmount,
            RemainderAmount = remainderAmount,
            DaysPastDue = payment.Date.Date < today ? (today - payment.Date.Date).Days : 0,
            SuggestedRemainderDate = suggestedDate,
        };
        return true;
    }

    [HttpPost("{id:int}/partial-remainder/from-existing")]
    public async Task<IActionResult> TransferExistingPartialRemainder(
        int id,
        [FromBody] TransferExistingPaymentRemainderRequest request,
        CancellationToken ct)
    {
        if (request.RemainderDate == default)
        {
            return BadRequest(new { message = "Укажите дату переноса остатка." });
        }

        var source = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(payment => payment.Id == id, ct);
        if (source is null) return NotFound();

        var originalAmount = ResolveStoredPartialRemainderOriginalAmount(source);
        var paidAmount = Math.Round(source.PaidAmount, 2, MidpointRounding.AwayFromZero);
        var remainderAmount = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);
        if (paidAmount <= 0m || remainderAmount <= AmountTolerance)
        {
            return BadRequest(new { message = "У платежа нет частично оплаченного остатка для переноса." });
        }

        var transferModel = CreateStoredPaymentTransferModel(source);
        transferModel.Amount = originalAmount;
        _skipRemainderTransferFormRequirements = true;
        try
        {
            return await TransferPartialRemainder(
                id,
                new TransferPaymentRemainderRequest
                {
                    Payment = transferModel,
                    RemainderDate = request.RemainderDate.Date,
                },
                ct);
        }
        finally
        {
            _skipRemainderTransferFormRequirements = false;
        }
    }

    [HttpPut("{id:int}/partial-remainder")]
    public async Task<IActionResult> TransferPartialRemainder(
        int id,
        [FromBody] TransferPaymentRemainderRequest request,
        CancellationToken ct)
    {
        if (request.Payment is null)
        {
            return BadRequest(new { message = "Не переданы данные платежа." });
        }

        if (request.RemainderDate == default)
        {
            return BadRequest(new { message = "Укажите дату переноса остатка." });
        }

        if (!_skipRemainderTransferFormRequirements)
        {
            var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", request.Payment);
            if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });
        }

        var source = await LoadPaymentForRemainderTransferAsync(id, ct);
        if (source is null) return NotFound();
        if (source.DocumentKind == DocumentKind.Invoice)
        {
            return Conflict(new { message = "Остаток переносится по платежу, а не по самой записи счета." });
        }

        var existingRemainder = await FindPartialRemainderPaymentAsync(source.Id, ct);
        if (existingRemainder is not null)
        {
            PopulateAuditMetadata(_db, source);
            PopulateAuditMetadata(_db, existingRemainder);
            return Ok(new TransferPaymentRemainderResponse
            {
                Payment = source,
                RemainderPayment = existingRemainder,
                CreatedRemainder = false,
            });
        }

        if (source.Status == PaymentStatus.Cancelled)
        {
            return BadRequest(new { message = "Нельзя перенести остаток по отмененному платежу." });
        }

        var now = DateTime.UtcNow;
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var actor = User.GetDisplayName();
        var model = request.Payment;
        var sourceAmount = Math.Round(source.Amount, 2, MidpointRounding.AwayFromZero);
        var modelAmount = Math.Round(Math.Max(model.Amount, 0m), 2, MidpointRounding.AwayFromZero);
        var originalAmount = _skipRemainderTransferFormRequirements && modelAmount > sourceAmount + AmountTolerance
            ? modelAmount
            : sourceAmount;
        var paidAmount = Math.Round(Math.Max(model.PaidAmount, 0m), 2, MidpointRounding.AwayFromZero);
        var remainderAmount = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);

        if (paidAmount <= 0m)
        {
            return BadRequest(new { message = "Для переноса остатка укажите фактически оплаченную сумму." });
        }

        if (remainderAmount <= AmountTolerance)
        {
            return BadRequest(new { message = "Остаток к переносу не образуется." });
        }

        var invoiceManaged = InvoiceManagedPaymentRules.IsInvoiceManagedPayment(source);
        var targetType = invoiceManaged ? source.Type : model.Type;
        if (model.IncomeTypeId.HasValue)
        {
            var incomeType = await _db.IncomeTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == model.IncomeTypeId.Value, ct);
            if (incomeType is null) return BadRequest("Unknown IncomeTypeId");
            if (incomeType.PaymentType != targetType) return BadRequest("IncomeType.PaymentType mismatches payment.Type");
        }

        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, source.Id, ct);
        if (paidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var originalDueDate = source.Date.Date;
        var originalPlannedDate = (source.PlannedDate == default ? source.Date : source.PlannedDate).Date;
        var originalPaidAmount = source.PaidAmount;
        var originalStatus = source.Status;
        var originalRescheduleCount = Math.Max(0, source.RescheduleCount);
        var originalSystemNotes = source.SystemNotes ?? string.Empty;
        var originalInvoiceId = source.InvoiceId;

        if (source.PlannedDate == default)
        {
            source.PlannedDate = originalPlannedDate;
        }

        var immutableInvoiceId = source.InvoiceId;
        var immutableDocumentKind = source.DocumentKind;
        var immutableAccount = source.Account;
        var immutableAccountDate = source.AccountDate;
        var immutableLegacyAccountNumber = source.LegacyAccountNumber;
        var immutableLegacyAccountDate = source.LegacyAccountDate;
        var immutableType = source.Type;

        source.Date = model.Date == default ? source.Date : model.Date;
        source.PlannedDate = model.PlannedDate == default ? source.PlannedDate : model.PlannedDate;
        source.Amount = paidAmount;
        source.Type = targetType;
        source.Status = PaymentStatus.Completed;
        source.Description = model.Description ?? string.Empty;
        source.Notes = model.Notes ?? string.Empty;
        source.ClientId = model.ClientId;
        source.LegalEntityId = model.LegalEntityId;
        source.ExpenseClientId = model.ExpenseClientId;
        source.ExpenseLegalEntityId = model.ExpenseLegalEntityId;
        source.ClientCaseId = model.ClientCaseId;
        source.ContractId = model.ContractId;
        source.DealTypeId = model.DealTypeId;
        source.IncomeTypeId = model.IncomeTypeId;
        source.PaymentSourceId = model.PaymentSourceId;
        source.PaymentStatusId = model.PaymentStatusId;
        source.InvoiceId = model.InvoiceId;
        source.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        source.DocumentKind = model.DocumentKind == default ? source.DocumentKind : model.DocumentKind;
        source.AccountDate = model.AccountDate;
        source.PaidAmount = paidAmount;
        source.PaidDate = model.PaidDate ?? model.LastPaymentDate ?? source.PaidDate ?? source.Date;
        source.LastPaymentDate = model.LastPaymentDate ?? source.PaidDate ?? source.LastPaymentDate;
        source.PaymentOrderNumber = model.PaymentOrderNumber;
        source.PaymentDocumentDate = model.PaymentDocumentDate;
        source.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        if (invoiceManaged)
        {
            source.InvoiceId = immutableInvoiceId;
            source.DocumentKind = immutableDocumentKind;
            source.Account = immutableAccount;
            source.AccountDate = immutableAccountDate;
            source.LegacyAccountNumber = immutableLegacyAccountNumber;
            source.LegacyAccountDate = immutableLegacyAccountDate;
            source.Type = immutableType;
            await SyncInvoiceManagedPaymentBindingsAsync(source, ct);
        }
        else
        {
            await SyncInvoiceLinkAsync(source, ct);
        }

        var sourceValidationError = await ValidatePaymentAttributionAsync(source, ct);
        if (sourceValidationError is not null) return sourceValidationError;
        PaymentDomainService.Normalize(source);
        PaymentDomainService.ApplyStatusRules(source, now);

        AppendSourceRemainderAuditNotes(
            source,
            originalAmount,
            paidAmount,
            remainderAmount,
            request.RemainderDate.Date,
            now,
            actor);
        source.SystemNotes = ReplaceTechnicalMarkerLine(
            source.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderSourceMarker,
            PaymentRemainderTransferRules.BuildSourceMarker(
                source.Id,
                originalAmount,
                paidAmount,
                remainderAmount,
                request.RemainderDate.Date));

        if (model.PlannedDate != default && originalPlannedDate != source.PlannedDate.Date)
        {
            PaymentDomainService.AppendSystemNote(
                source,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {source.PlannedDate:dd.MM.yyyy}",
                now,
                actor);
        }

        var userChangedDueDate = model.Date != default && model.Date.Date != originalDueDate;
        var rescheduleCount = originalRescheduleCount;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                source,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {source.Date:dd.MM.yyyy}",
                now,
                actor);
        }

        if (Math.Abs(originalPaidAmount - source.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                source,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {source.PaidAmount.ToString("N2", culture)}",
                now,
                actor);
        }

        if (originalStatus != source.Status)
        {
            PaymentDomainService.AppendSystemNote(
                source,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {source.Status.ToDisplayString()}",
                now,
                actor);
        }

        source.RescheduleCount = rescheduleCount;

        var remainderPayment = CreateRemainderPaymentFromSource(
            source,
            originalAmount,
            paidAmount,
            remainderAmount,
            request.RemainderDate.Date,
            now);
        await CopyPaymentResponsiblesAsync(remainderPayment, source.Responsibles.Select(item => item.UserId), ct);
        CopyPaymentInvoiceClientCases(remainderPayment, source.InvoiceClientCases.Select(item => item.ClientCaseId));

        PaymentDomainService.Normalize(remainderPayment);
        PaymentDomainService.ApplyStatusRules(remainderPayment, now);

        var remainderValidationError = await ValidatePaymentAttributionAsync(remainderPayment, ct);
        if (remainderValidationError is not null) return remainderValidationError;

        _db.Payments.Add(remainderPayment);

        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, source, paidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, source, now, ct);
        if (originalInvoiceId.HasValue
            && originalInvoiceId.Value > 0
            && originalInvoiceId != source.InvoiceId)
        {
            await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(_db, originalInvoiceId.Value, now, ct);
        }

        await _db.SaveChangesAsync(ct);
        UpsertPartialRemainderTransfer(
            source.Id,
            remainderPayment.Id,
            originalAmount,
            paidAmount,
            remainderAmount,
            request.RemainderDate.Date,
            now);
        await _db.SaveChangesAsync(ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, remainderPayment, now, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        PopulateAuditMetadata(_db, source);
        PopulateAuditMetadata(_db, remainderPayment);
        return Ok(new TransferPaymentRemainderResponse
        {
            Payment = source,
            RemainderPayment = remainderPayment,
            CreatedRemainder = true,
        });
    }

    [HttpPost("{id:int}/partial-remainder/rollback")]
    public async Task<IActionResult> RollbackPartialRemainderTransfer(int id, CancellationToken ct)
    {
        var pair = await ResolvePartialRemainderTransferPairAsync(id, ct);
        if (pair is null)
        {
            return BadRequest(new { message = "Перенос остатка для платежа не найден." });
        }

        var validationError = await ValidateRemainderCanBeReworkedAsync(pair.Value, ct);
        if (validationError is not null) return validationError;

        var now = DateTime.UtcNow;
        var actor = User.GetDisplayName();
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var source = pair.Value.Source;
        var remainder = pair.Value.Remainder;
        var marker = pair.Value.Marker;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        source.Amount = marker.SourceOriginalAmount;
        source.PaidAmount = marker.PaidAmount;
        source.IsPaid = false;
        source.PaidDate = null;
        source.SystemNotes = RemoveTechnicalMarkerLines(
            source.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderSourceMarker);
        PaymentDomainService.AppendSystemNote(
            source,
            $"Перенос остатка отменен: платеж #{remainder.Id} на {marker.RemainderAmount.ToString("N2", culture)} удален, исходная сумма восстановлена до {marker.SourceOriginalAmount.ToString("N2", culture)}.",
            now,
            actor);
        PaymentDomainService.Normalize(source);
        PaymentDomainService.ApplyStatusRules(source, now);

        await RemovePartialRemainderTransferAsync(source.Id, remainder.Id, ct);
        _db.Payments.Remove(remainder);
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, source, source.PaidAmount, now, HttpContext, ct);
        await _db.SaveChangesAsync(ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, source, now, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        PopulateAuditMetadata(_db, source);
        return Ok(new RollbackPaymentRemainderTransferResponse
        {
            Payment = source,
            RemovedRemainderPaymentId = remainder.Id,
        });
    }

    [HttpPut("{id:int}/partial-remainder/adjust")]
    public async Task<IActionResult> AdjustPartialRemainderTransfer(
        int id,
        [FromBody] AdjustPaymentRemainderTransferRequest request,
        CancellationToken ct)
    {
        if (request.RemainderDate == default)
        {
            return BadRequest(new { message = "Укажите дату переноса остатка." });
        }

        var pair = await ResolvePartialRemainderTransferPairAsync(id, ct);
        if (pair is null)
        {
            return BadRequest(new { message = "Перенос остатка для платежа не найден." });
        }

        var validationError = await ValidateRemainderCanBeReworkedAsync(pair.Value, ct);
        if (validationError is not null) return validationError;

        var now = DateTime.UtcNow;
        var actor = User.GetDisplayName();
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var source = pair.Value.Source;
        var remainder = pair.Value.Remainder;
        var marker = pair.Value.Marker;
        var paidAmount = Math.Round(Math.Max(request.PaidAmount, 0m), 2, MidpointRounding.AwayFromZero);
        var sourceOriginalAmount = marker.SourceOriginalAmount;
        var remainderAmount = Math.Round(sourceOriginalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);

        if (paidAmount <= 0m)
        {
            return BadRequest(new { message = "Укажите фактически оплаченную сумму." });
        }

        if (remainderAmount <= 0.009m)
        {
            return BadRequest(new { message = "Остаток к переносу не образуется." });
        }

        var appliedCreditAmount = await ClientBalanceService.GetAppliedCreditAmountAsync(_db, source.Id, ct);
        if (paidAmount + 0.009m < appliedCreditAmount)
        {
            return BadRequest(new
            {
                message = $"Оплата не может быть меньше зачтенной суммы с баланса клиента: {appliedCreditAmount.ToString("N2", culture)}."
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var remainderDate = request.RemainderDate.Date;
        source.Amount = paidAmount;
        source.PaidAmount = paidAmount;
        source.IsPaid = true;
        source.Status = PaymentStatus.Completed;
        source.PaidDate ??= source.LastPaymentDate ?? source.Date;
        source.LastPaymentDate ??= source.PaidDate;
        source.SystemNotes = ReplaceTechnicalMarkerLine(
            source.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderSourceMarker,
            PaymentRemainderTransferRules.BuildSourceMarker(
                source.Id,
                sourceOriginalAmount,
                paidAmount,
                remainderAmount,
                remainderDate));
        PaymentDomainService.AppendSystemNote(
            source,
            $"Разделение платежа изменено: оплачено {paidAmount.ToString("N2", culture)} / {sourceOriginalAmount.ToString("N2", culture)}, остаток {remainderAmount.ToString("N2", culture)} перенесен на {remainderDate:dd.MM.yyyy}.",
            now,
            actor);

        remainder.Amount = remainderAmount;
        remainder.PaidAmount = 0m;
        remainder.IsPaid = false;
        remainder.PaidDate = null;
        remainder.LastPaymentDate = null;
        remainder.Date = remainderDate;
        remainder.PlannedDate = remainderDate;
        remainder.Status = remainderDate < now.Date ? PaymentStatus.Overdue : PaymentStatus.Pending;
        remainder.SystemNotes = ReplaceTechnicalMarkerLine(
            remainder.SystemNotes,
            PaymentRemainderTransferRules.PartialRemainderMarker,
            PaymentRemainderTransferRules.BuildMarker(
                source.Id,
                sourceOriginalAmount,
                paidAmount,
                remainderAmount,
                remainderDate));
        PaymentDomainService.AppendSystemNote(
            remainder,
            $"Параметры перенесенного остатка изменены: сумма {remainderAmount.ToString("N2", culture)}, дата {remainderDate:dd.MM.yyyy}.",
            now,
            actor);

        PaymentDomainService.Normalize(source);
        PaymentDomainService.ApplyStatusRules(source, now);
        PaymentDomainService.Normalize(remainder);
        PaymentDomainService.ApplyStatusRules(remainder, now);

        var sourceValidationError = await ValidatePaymentAttributionAsync(source, ct);
        if (sourceValidationError is not null) return sourceValidationError;
        var remainderValidationError = await ValidatePaymentAttributionAsync(remainder, ct);
        if (remainderValidationError is not null) return remainderValidationError;

        UpsertPartialRemainderTransfer(
            source.Id,
            remainder.Id,
            sourceOriginalAmount,
            paidAmount,
            remainderAmount,
            remainderDate,
            now);
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, source, paidAmount, now, HttpContext, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, source, now, ct);
        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(_db, remainder, now, ct);
        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        PopulateAuditMetadata(_db, source);
        PopulateAuditMetadata(_db, remainder);
        return Ok(new TransferPaymentRemainderResponse
        {
            Payment = source,
            RemainderPayment = remainder,
            CreatedRemainder = false,
        });
    }

    private async Task<HashSet<int>> GetPartialRemainderSourceIdsAsync(CancellationToken ct)
    {
        var sourceIds = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .Select(item => item.SourcePaymentId)
            .ToHashSetAsync(ct);

        var markerLines = await _db.Payments
            .AsNoTracking()
            .Where(payment => payment.SystemNotes != null
                              && payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker))
            .Select(payment => payment.SystemNotes)
            .ToListAsync(ct);

        foreach (var systemNotes in markerLines)
        {
            if (PaymentRemainderTransferRules.TryReadMarker(systemNotes, out var marker)
                && marker.SourcePaymentId > 0)
            {
                sourceIds.Add(marker.SourcePaymentId);
            }
        }

        return sourceIds;
    }

    private static decimal ResolveStoredPartialRemainderOriginalAmount(Payment payment)
    {
        var storedAmount = Math.Round(payment.Amount, 2, MidpointRounding.AwayFromZero);
        var paidAmount = Math.Round(payment.PaidAmount, 2, MidpointRounding.AwayFromZero);

        if (TryReadLegacyPartialRemainderHint(payment, out var hint)
            && storedAmount <= paidAmount + AmountTolerance
            && hint.OriginalAmount > storedAmount + AmountTolerance
            && hint.OriginalAmount > paidAmount + AmountTolerance)
        {
            return hint.OriginalAmount;
        }

        return storedAmount;
    }

    private static bool TryReadLegacyPartialRemainderHint(Payment payment, out LegacyPartialRemainderHint hint)
    {
        foreach (var text in new[] { payment.SystemNotes, payment.Notes })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var matches = LegacyPartialRemainderNoteRegex.Matches(text);
            for (var index = matches.Count - 1; index >= 0; index--)
            {
                var match = matches[index];
                if (!TryParseLegacyMoney(match.Groups["paid"].Value, out var paidAmount)
                    || !TryParseLegacyMoney(match.Groups["original"].Value, out var originalAmount)
                    || !TryParseLegacyMoney(match.Groups["remainder"].Value, out var remainderAmount)
                    || !DateTime.TryParseExact(
                        match.Groups["date"].Value,
                        "dd.MM.yyyy",
                        CultureInfo.GetCultureInfo("ru-RU"),
                        DateTimeStyles.None,
                        out var remainderDate))
                {
                    continue;
                }

                paidAmount = Math.Round(paidAmount, 2, MidpointRounding.AwayFromZero);
                originalAmount = Math.Round(originalAmount, 2, MidpointRounding.AwayFromZero);
                remainderAmount = Math.Round(remainderAmount, 2, MidpointRounding.AwayFromZero);
                var calculatedRemainder = Math.Round(originalAmount - paidAmount, 2, MidpointRounding.AwayFromZero);
                if (paidAmount <= 0m
                    || originalAmount <= paidAmount + AmountTolerance
                    || remainderAmount <= AmountTolerance
                    || Math.Abs(calculatedRemainder - remainderAmount) > 0.01m)
                {
                    continue;
                }

                hint = new LegacyPartialRemainderHint(
                    originalAmount,
                    paidAmount,
                    remainderAmount,
                    remainderDate.Date);
                return true;
            }
        }

        hint = default;
        return false;
    }

    private static bool TryParseLegacyMoney(string rawValue, out decimal amount)
    {
        var normalized = Regex.Replace(rawValue.Trim(), @"[\s\u00A0\u202F]+", string.Empty)
            .Replace(',', '.');

        return decimal.TryParse(
            normalized,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out amount);
    }

    private static bool TryInferSuggestedRemainderDate(Payment payment, out DateTime suggestedDate)
    {
        foreach (var text in new[] { payment.SystemNotes, payment.Notes })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var match = RemainderDateHintRegex.Match(text);
            if (!match.Success)
            {
                continue;
            }

            if (DateTime.TryParseExact(
                    match.Groups[1].Value,
                    "dd.MM.yyyy",
                    CultureInfo.GetCultureInfo("ru-RU"),
                    DateTimeStyles.None,
                    out suggestedDate))
            {
                suggestedDate = suggestedDate.Date;
                return true;
            }
        }

        suggestedDate = default;
        return false;
    }

    private readonly record struct LegacyPartialRemainderHint(
        decimal OriginalAmount,
        decimal PaidAmount,
        decimal RemainderAmount,
        DateTime RemainderDate);

    private static Payment CreateStoredPaymentTransferModel(Payment source)
    {
        var effectivePaidDate = source.LastPaymentDate ?? source.PaidDate ?? source.Date;
        return new Payment
        {
            Id = source.Id,
            Account = source.Account,
            AccountDate = source.AccountDate,
            Amount = source.Amount,
            ClientCaseId = source.ClientCaseId,
            ClientCaseReference = source.ClientCaseReference,
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            ContractId = source.ContractId,
            CounterpartyName = source.CounterpartyName,
            CreatedAt = source.CreatedAt,
            Date = source.Date,
            DealTypeId = source.DealTypeId,
            Description = source.Description,
            DocumentKind = source.DocumentKind,
            ExpenseClientId = source.ExpenseClientId,
            ExpenseLegalEntityId = source.ExpenseLegalEntityId,
            IncomeTypeId = source.IncomeTypeId,
            InvoiceId = source.InvoiceId,
            IsNotForAccounting = source.IsNotForAccounting,
            IsPaid = source.IsPaid,
            LastPaymentDate = effectivePaidDate,
            LegacyAccountDate = source.LegacyAccountDate,
            LegacyAccountNumber = source.LegacyAccountNumber,
            Notes = source.Notes,
            PaidAmount = source.PaidAmount,
            PaidDate = effectivePaidDate,
            PaymentDocumentDate = source.PaymentDocumentDate,
            PaymentOrderNumber = source.PaymentOrderNumber,
            PaymentSourceId = source.PaymentSourceId,
            PaymentStatusId = source.PaymentStatusId,
            PlannedDate = source.PlannedDate == default ? source.Date : source.PlannedDate,
            RescheduleCount = source.RescheduleCount,
            ServiceId = source.ServiceId,
            Status = source.Status,
            SystemNotes = source.SystemNotes,
            Type = source.Type,
        };
    }

    private async Task<Payment?> LoadPaymentForRemainderTransferAsync(int id, CancellationToken ct)
        => await _db.Payments
            .Include(payment => payment.Responsibles)
            .Include(payment => payment.InvoiceClientCases)
            .FirstOrDefaultAsync(payment => payment.Id == id, ct);

    private async Task<Payment?> FindPartialRemainderPaymentAsync(int sourcePaymentId, CancellationToken ct)
    {
        var structuredRemainderId = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .Where(link => link.SourcePaymentId == sourcePaymentId)
            .Select(link => (int?)link.RemainderPaymentId)
            .FirstOrDefaultAsync(ct);

        if (structuredRemainderId.HasValue)
        {
            return await LoadPaymentForRemainderTransferAsync(structuredRemainderId.Value, ct);
        }

        var legacyCandidates = await _db.Payments
            .Include(payment => payment.Responsibles)
            .Include(payment => payment.InvoiceClientCases)
            .Where(payment => payment.SystemNotes != null
                              && payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker))
            .OrderBy(payment => payment.Id)
            .ToListAsync(ct);

        return legacyCandidates.FirstOrDefault(payment =>
            PaymentRemainderTransferRules.TryReadMarker(payment.SystemNotes, out var marker)
            && marker.SourcePaymentId == sourcePaymentId);
    }

    private async Task<bool> IsPartialRemainderPaymentAsync(Payment payment, CancellationToken ct)
        => PaymentRemainderTransferRules.IsPartialRemainderPayment(payment)
           || await _db.PaymentRemainderTransfers
               .AsNoTracking()
               .AnyAsync(link => link.RemainderPaymentId == payment.Id, ct);

    private static PaymentRemainderTransferRules.PartialRemainderMarkerData ToMarkerData(PaymentRemainderTransfer link)
        => new(
            link.SourcePaymentId,
            Math.Round(link.SourceOriginalAmount, 2, MidpointRounding.AwayFromZero),
            Math.Round(link.PaidAmount, 2, MidpointRounding.AwayFromZero),
            Math.Round(link.RemainderAmount, 2, MidpointRounding.AwayFromZero),
            link.RemainderDate.Date);

    private void UpsertPartialRemainderTransfer(
        int sourcePaymentId,
        int remainderPaymentId,
        decimal sourceOriginalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate,
        DateTime now)
    {
        var link = _db.PaymentRemainderTransfers.Local.FirstOrDefault(item =>
                       item.SourcePaymentId == sourcePaymentId
                       || item.RemainderPaymentId == remainderPaymentId)
                   ?? _db.PaymentRemainderTransfers.FirstOrDefault(item =>
                       item.SourcePaymentId == sourcePaymentId
                       || item.RemainderPaymentId == remainderPaymentId);

        if (link is null)
        {
            _db.PaymentRemainderTransfers.Add(new PaymentRemainderTransfer
            {
                SourcePaymentId = sourcePaymentId,
                RemainderPaymentId = remainderPaymentId,
                SourceOriginalAmount = sourceOriginalAmount,
                PaidAmount = paidAmount,
                RemainderAmount = remainderAmount,
                RemainderDate = remainderDate.Date,
                CreatedAt = now,
            });
            return;
        }

        link.SourcePaymentId = sourcePaymentId;
        link.RemainderPaymentId = remainderPaymentId;
        link.SourceOriginalAmount = sourceOriginalAmount;
        link.PaidAmount = paidAmount;
        link.RemainderAmount = remainderAmount;
        link.RemainderDate = remainderDate.Date;
        link.UpdatedAt = now;
    }

    private async Task RemovePartialRemainderTransferAsync(int sourcePaymentId, int remainderPaymentId, CancellationToken ct)
    {
        var link = await _db.PaymentRemainderTransfers.FirstOrDefaultAsync(item =>
            item.SourcePaymentId == sourcePaymentId
            || item.RemainderPaymentId == remainderPaymentId, ct);
        if (link is not null)
        {
            _db.PaymentRemainderTransfers.Remove(link);
        }
    }

    private async Task<PartialRemainderTransferPair?> ResolvePartialRemainderTransferPairAsync(int paymentId, CancellationToken ct)
    {
        var current = await LoadPaymentForRemainderTransferAsync(paymentId, ct);
        if (current is null)
        {
            return null;
        }

        var structuredAsRemainder = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.RemainderPaymentId == paymentId, ct);
        if (structuredAsRemainder is not null)
        {
            var source = await LoadPaymentForRemainderTransferAsync(structuredAsRemainder.SourcePaymentId, ct);
            return source is null
                ? null
                : new PartialRemainderTransferPair(source, current, ToMarkerData(structuredAsRemainder), structuredAsRemainder.Id);
        }

        var structuredAsSource = await _db.PaymentRemainderTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.SourcePaymentId == paymentId, ct);
        if (structuredAsSource is not null)
        {
            var structuredRemainderPayment = await LoadPaymentForRemainderTransferAsync(structuredAsSource.RemainderPaymentId, ct);
            return structuredRemainderPayment is null
                ? null
                : new PartialRemainderTransferPair(current, structuredRemainderPayment, ToMarkerData(structuredAsSource), structuredAsSource.Id);
        }

        if (PaymentRemainderTransferRules.TryReadMarker(current.SystemNotes, out var currentMarker)
            && currentMarker.SourcePaymentId > 0)
        {
            var source = await LoadPaymentForRemainderTransferAsync(currentMarker.SourcePaymentId, ct);
            return source is null
                ? null
                : new PartialRemainderTransferPair(source, current, currentMarker, null);
        }

        var remainder = await FindPartialRemainderPaymentAsync(current.Id, ct);
        if (remainder is null
            || !PaymentRemainderTransferRules.TryReadMarker(remainder.SystemNotes, out var marker))
        {
            return null;
        }

        return new PartialRemainderTransferPair(current, remainder, marker, null);
    }

    private async Task<ActionResult?> ValidateRemainderCanBeReworkedAsync(
        PartialRemainderTransferPair pair,
        CancellationToken ct)
    {
        if (pair.Remainder.IsPaid
            || pair.Remainder.PaidAmount > 0.009m
            || pair.Remainder.Status == PaymentStatus.Completed)
        {
            return Conflict(new { message = PaymentRemainderTransferRules.RemainderAlreadyProcessedMessage });
        }

        var nestedRemainder = await FindPartialRemainderPaymentAsync(pair.Remainder.Id, ct);
        if (nestedRemainder is not null)
        {
            return Conflict(new { message = PaymentRemainderTransferRules.RemainderAlreadyProcessedMessage });
        }

        return null;
    }

    private async Task<ActionResult?> ValidatePartialRemainderUpdateAsync(Payment current, Payment model, CancellationToken ct)
    {
        if (await IsPartialRemainderPaymentAsync(current, ct))
        {
            if (!AreAmountsEqual(model.Amount, current.Amount))
            {
                return Conflict(new { message = PaymentRemainderTransferRules.RemainderAmountUpdateBlockedMessage });
            }

            if (model.Status == PaymentStatus.Cancelled && current.Status != PaymentStatus.Cancelled)
            {
                return Conflict(new { message = PaymentRemainderTransferRules.RemainderCancelBlockedMessage });
            }

            return null;
        }

        var transferredRemainder = await FindPartialRemainderPaymentAsync(current.Id, ct);
        if (transferredRemainder is null)
        {
            return null;
        }

        if (!AreAmountsEqual(model.Amount, current.Amount)
            || !AreAmountsEqual(model.PaidAmount, current.PaidAmount))
        {
            return Conflict(new { message = PaymentRemainderTransferRules.SourceAmountUpdateBlockedMessage });
        }

        return null;
    }

    private readonly record struct PartialRemainderTransferPair(
        Payment Source,
        Payment Remainder,
        PaymentRemainderTransferRules.PartialRemainderMarkerData Marker,
        int? TransferId);

    private static void AppendSourceRemainderAuditNotes(
        Payment source,
        decimal originalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate,
        DateTime now,
        string? actor)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        PaymentDomainService.AppendSystemNote(
            source,
            $"Частичная оплата: {paidAmount.ToString("N2", culture)} / {originalAmount.ToString("N2", culture)}. Остаток {remainderAmount.ToString("N2", culture)} перенесен на {remainderDate:dd.MM.yyyy}.",
            now,
            actor);
    }

    private static Payment CreateRemainderPaymentFromSource(
        Payment source,
        decimal originalAmount,
        decimal paidAmount,
        decimal remainderAmount,
        DateTime remainderDate,
        DateTime now)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var description = string.IsNullOrWhiteSpace(source.Description)
            ? "Остаток после частичной оплаты"
            : $"{source.Description.Trim()} · остаток";
        var marker = PaymentRemainderTransferRules.BuildMarker(
            source.Id,
            originalAmount,
            paidAmount,
            remainderAmount,
            remainderDate);

        return new Payment
        {
            Account = source.Account,
            AccountDate = source.AccountDate,
            InvoiceId = source.InvoiceId,
            LegacyAccountNumber = source.LegacyAccountNumber,
            LegacyAccountDate = source.LegacyAccountDate,
            Amount = remainderAmount,
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            ExpenseClientId = source.ExpenseClientId,
            ExpenseLegalEntityId = source.ExpenseLegalEntityId,
            ClientCaseId = source.ClientCaseId,
            ClientCaseReference = source.ClientCaseReference,
            ContractId = source.ContractId,
            DocumentKind = DocumentKind.Payment,
            CreatedAt = now,
            Date = remainderDate.Date,
            PlannedDate = remainderDate.Date,
            DealTypeId = source.DealTypeId,
            Description = description,
            CounterpartyName = source.CounterpartyName,
            IncomeTypeId = source.IncomeTypeId,
            IsPaid = false,
            PaidAmount = 0m,
            LastPaymentDate = null,
            Notes = source.Notes,
            IsNotForAccounting = source.IsNotForAccounting,
            SystemNotes = string.Join('\n', new[]
            {
                marker,
                $"Остаток после частичной оплаты платежа #{source.Id}: исходно {originalAmount.ToString("N2", culture)}, оплачено {paidAmount.ToString("N2", culture)}, остаток {remainderAmount.ToString("N2", culture)}.",
            }),
            RescheduleCount = Math.Max(0, source.RescheduleCount) + 1,
            PaidDate = null,
            PaymentSourceId = source.PaymentSourceId,
            ServiceId = source.ServiceId,
            PaymentStatusId = source.PaymentStatusId,
            Status = remainderDate.Date < now.Date ? PaymentStatus.Overdue : PaymentStatus.Pending,
            Type = source.Type,
        };
    }

    private async Task CopyPaymentResponsiblesAsync(Payment target, IEnumerable<int> responsibleIds, CancellationToken ct)
    {
        var ids = responsibleIds
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var existingUserIds = await _db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => user.Id)
            .ToListAsync(ct);

        foreach (var userId in existingUserIds)
        {
            target.Responsibles.Add(new PaymentResponsible
            {
                UserId = userId,
            });
        }
    }

    private static void CopyPaymentInvoiceClientCases(Payment target, IEnumerable<int> clientCaseIds)
    {
        foreach (var clientCaseId in clientCaseIds.Where(id => id > 0).Distinct())
        {
            target.InvoiceClientCases.Add(new InvoiceClientCase
            {
                ClientCaseId = clientCaseId,
            });
        }
    }

    private async Task SyncInvoiceManagedPaymentBindingsAsync(Payment payment, CancellationToken ct)
    {
        if (!(InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(payment)
              || InvoiceManagedPaymentRules.IsGeneratedInvoiceRemainderPayment(payment))
            || !payment.InvoiceId.HasValue
            || payment.InvoiceId.Value <= 0)
        {
            return;
        }

        var representative = await _db.Payments
            .Include(item => item.Responsibles)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.InvoiceId == payment.InvoiceId.Value
                    && item.DocumentKind == DocumentKind.Invoice,
                ct);
        if (representative is null)
        {
            return;
        }

        payment.Account = representative.Account;
        payment.AccountDate = representative.AccountDate;
        payment.Type = representative.Type;
        payment.ClientId = representative.ClientId;
        payment.LegalEntityId = representative.LegalEntityId;
        payment.ExpenseClientId = representative.ExpenseClientId;
        payment.ExpenseLegalEntityId = representative.ExpenseLegalEntityId;
        payment.ContractId = representative.ContractId;
        payment.ClientCaseId = representative.ClientCaseId;
        payment.ClientCaseReference = representative.ClientCaseReference;
        payment.PaymentSourceId = representative.PaymentSourceId;
        payment.ServiceId = representative.ServiceId;
        payment.IncomeTypeId = representative.IncomeTypeId;
        payment.DealTypeId = representative.DealTypeId;
        payment.PaymentStatusId = representative.PaymentStatusId;
        payment.CounterpartyName = null;

        await SyncPaymentResponsiblesAsync(payment, representative.Responsibles.Select(item => item.UserId), ct);
    }

    private async Task SyncPaymentResponsiblesAsync(Payment payment, IEnumerable<int> responsibleIds, CancellationToken ct)
    {
        var collection = _db.Entry(payment).Collection(item => item.Responsibles);
        if (!collection.IsLoaded)
        {
            await collection.LoadAsync(ct);
        }

        var targetIds = responsibleIds.Where(id => id > 0).Distinct().ToHashSet();
        foreach (var existing in payment.Responsibles.Where(item => !targetIds.Contains(item.UserId)).ToList())
        {
            payment.Responsibles.Remove(existing);
        }

        var existingIds = payment.Responsibles.Select(item => item.UserId).ToHashSet();
        foreach (var responsibleId in targetIds)
        {
            if (existingIds.Contains(responsibleId))
            {
                continue;
            }

            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }
    }

    [HttpPost("repair-invoice-links")]
    public async Task<IActionResult> RepairInvoiceLinks(CancellationToken ct)
    {
        var payments = await _db.Payments
            .Where(p => (p.Account != null && p.Account != "") || p.InvoiceId != null)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        var originalInvoiceIds = payments.ToDictionary(p => p.Id, p => p.InvoiceId);
        var originalKinds = payments.ToDictionary(p => p.Id, p => p.DocumentKind);
        var repairedLinks = 0;
        var clearedLinks = 0;
        var correctedKinds = 0;

        foreach (var payment in payments)
        {
            await SyncInvoiceLinkAsync(payment, ct);
        }

        var invoiceDocumentKeeperIds = payments
            .Where(p => p.DocumentKind == DocumentKind.Invoice && p.InvoiceId.HasValue)
            .GroupBy(p => p.InvoiceId!.Value)
            .Select(group => group.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).First().Id)
            .ToHashSet();

        foreach (var payment in payments)
        {
            if (payment.DocumentKind == DocumentKind.Invoice
                && payment.InvoiceId.HasValue
                && !invoiceDocumentKeeperIds.Contains(payment.Id))
            {
                payment.DocumentKind = DocumentKind.Payment;
            }

            if (payment.InvoiceId != originalInvoiceIds[payment.Id])
            {
                if (payment.InvoiceId.HasValue)
                {
                    repairedLinks++;
                }
                else
                {
                    clearedLinks++;
                }
            }

            if (payment.DocumentKind != originalKinds[payment.Id])
            {
                correctedKinds++;
            }
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            scanned = payments.Count,
            repairedLinks,
            clearedLinks,
            correctedKinds,
        });
    }

    private async Task SyncInvoiceLinkAsync(Payment payment, CancellationToken ct)
    {
        var normalizedAccount = NormalizeInvoiceNumber(payment.Account);
        var originalAccountDate = (payment.AccountDate ?? payment.Date).Date;
        var invoiceDocument = await ResolveInvoiceDocumentAsync(payment, ct);
        var invoice = invoiceDocument?.Invoice;
        if (invoice is null)
        {
            payment.InvoiceId = null;

            payment.Account = normalizedAccount;
            payment.AccountDate = normalizedAccount is null
                ? null
                : originalAccountDate;
            if (normalizedAccount is null)
            {
                payment.LegacyAccountNumber = null;
                payment.LegacyAccountDate = null;
            }
            await SyncPaymentInvoiceClientCasesAsync(payment, [], ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(payment.LegacyAccountNumber) && normalizedAccount is not null)
        {
            var normalizedInvoiceNumber = NormalizeInvoiceNumber(invoice.Number);
            if (!string.Equals(normalizedAccount, normalizedInvoiceNumber, StringComparison.OrdinalIgnoreCase)
                || originalAccountDate != invoice.Date.Date)
            {
                payment.LegacyAccountNumber = normalizedAccount;
                payment.LegacyAccountDate = originalAccountDate;
            }
        }

        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date.Date;
        if (invoiceDocument is not null)
        {
            payment.Type = invoiceDocument.Type;
            payment.ClientId = invoiceDocument.ClientId;
            payment.LegalEntityId = invoiceDocument.LegalEntityId;
            payment.ExpenseClientId = invoiceDocument.ExpenseClientId;
            payment.ExpenseLegalEntityId = invoiceDocument.ExpenseLegalEntityId;
            payment.ContractId = invoiceDocument.ContractId;

            var linkedCaseIds = invoiceDocument.InvoiceClientCases
                .Select(link => link.ClientCaseId)
                .Concat(invoiceDocument.ClientCaseId.HasValue ? [invoiceDocument.ClientCaseId.Value] : [])
                .Where(caseId => caseId > 0)
                .Distinct()
                .ToList();

            var firstCaseId = linkedCaseIds.FirstOrDefault();
            payment.ClientCaseId = firstCaseId > 0 ? firstCaseId : null;

            await SyncPaymentInvoiceClientCasesAsync(payment, linkedCaseIds, ct);
        }
        else
        {
            await SyncPaymentInvoiceClientCasesAsync(payment, [], ct);
        }
    }

    private async Task<Payment?> ResolveInvoiceDocumentAsync(Payment payment, CancellationToken ct)
    {
        if (payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
        {
            var linked = await _db.Payments
                .AsNoTracking()
                .Include(x => x.Invoice)
                .Include(x => x.InvoiceClientCases)
                .FirstOrDefaultAsync(x => x.InvoiceId == payment.InvoiceId.Value && x.DocumentKind == DocumentKind.Invoice, ct);
            if (linked is not null)
            {
                return linked;
            }

            var invoice = await _db.Invoices
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payment.InvoiceId.Value, ct);
            if (invoice is not null)
            {
                return new Payment
                {
                    InvoiceId = invoice.Id,
                    Invoice = invoice,
                    Type = payment.Type,
                    ClientId = payment.ClientId,
                    LegalEntityId = payment.LegalEntityId,
                    ExpenseClientId = payment.ExpenseClientId,
                    ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
                    ContractId = payment.ContractId,
                    ClientCaseId = payment.ClientCaseId,
                };
            }
        }

        var lookupCandidates = BuildLookupCandidates(payment);
        if (lookupCandidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in lookupCandidates)
        {
            var resolved = await ResolveInvoiceByPaymentHistoryAsync(payment, candidate.Number, candidate.Date, ct);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? NormalizeInvoiceNumber(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool AreAmountsEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= 0.009m;

    private async Task<Payment?> ResolveInvoiceByPaymentHistoryAsync(Payment payment, string lookupNumber, DateTime lookupDate, CancellationToken ct)
    {
        if (!payment.ClientId.HasValue || payment.ClientId.Value <= 0)
        {
            return null;
        }

        var normalizedLookup = lookupNumber.ToLowerInvariant();
        var caseId = payment.ClientCaseId is > 0 ? payment.ClientCaseId : null;
        var lookupYear = lookupDate.Year;

        var candidates = await _db.Payments
            .AsNoTracking()
            .Where(x => x.InvoiceId.HasValue && x.ClientId == payment.ClientId.Value && x.Type == payment.Type)
            .Where(x => caseId.HasValue
                ? x.ClientCaseId == caseId.Value || x.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value)
                : x.ClientCaseId == null && !x.InvoiceClientCases.Any())
            .Where(x =>
                (x.LegacyAccountNumber != null
                    && x.LegacyAccountNumber.ToLower() == normalizedLookup
                    && (x.LegacyAccountDate ?? x.AccountDate ?? x.Date).Year == lookupYear)
                || (x.Account != null
                    && x.Account.ToLower() == normalizedLookup
                    && (x.AccountDate ?? x.Date).Year == lookupYear))
            .Select(x => new
            {
                InvoiceId = x.InvoiceId!.Value,
                ExactLegacyDate = x.LegacyAccountNumber != null
                    && x.LegacyAccountNumber.ToLower() == normalizedLookup
                    && x.LegacyAccountDate.HasValue
                    && x.LegacyAccountDate.Value.Date == lookupDate.Date,
                ExactAccountDate = x.Account != null
                    && x.Account.ToLower() == normalizedLookup
                    && x.AccountDate.HasValue
                    && x.AccountDate.Value.Date == lookupDate.Date,
                IsInvoiceDocument = x.DocumentKind == DocumentKind.Invoice,
                x.CreatedAt,
                x.Id,
            })
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return null;
        }

        var invoiceId = candidates
            .GroupBy(x => x.InvoiceId)
            .OrderByDescending(group => group.Max(item => item.ExactLegacyDate ? 3 : item.ExactAccountDate ? 2 : item.IsInvoiceDocument ? 1 : 0))
            .ThenByDescending(group => group.Count())
            .ThenByDescending(group => group.Max(item => item.CreatedAt))
            .ThenByDescending(group => group.Max(item => item.Id))
            .Select(group => group.Key)
            .FirstOrDefault();

        if (invoiceId <= 0)
        {
            return null;
        }

        return await _db.Payments
            .AsNoTracking()
            .Include(x => x.Invoice)
            .Include(x => x.InvoiceClientCases)
            .FirstOrDefaultAsync(x => x.InvoiceId == invoiceId && x.DocumentKind == DocumentKind.Invoice, ct);
    }

    private async Task<ActionResult?> ValidatePaymentAttributionAsync(Payment payment, CancellationToken ct)
    {
        var hasClient = payment.ClientId.HasValue && payment.ClientId.Value > 0;
        var hasLegalEntity = payment.LegalEntityId.HasValue && payment.LegalEntityId.Value > 0;
        if (hasClient && hasLegalEntity)
        {
            return BadRequest("Payment must reference either clientId or legalEntityId, not both.");
        }

        if (hasClient)
        {
            var clientExists = await _db.Clients
                .AsNoTracking()
                .AnyAsync(client => client.Id == payment.ClientId!.Value, ct);
            if (!clientExists)
            {
                return BadRequest($"Unknown clientId {payment.ClientId!.Value}");
            }
        }

        if (hasLegalEntity)
        {
            var legalEntityExists = await _db.LegalEntities
                .AsNoTracking()
                .AnyAsync(entity => entity.Id == payment.LegalEntityId!.Value, ct);
            if (!legalEntityExists)
            {
                return BadRequest($"Unknown legalEntityId {payment.LegalEntityId!.Value}");
            }
        }

        var hasExpenseClient = payment.ExpenseClientId.HasValue && payment.ExpenseClientId.Value > 0;
        var hasExpenseLegalEntity = payment.ExpenseLegalEntityId.HasValue && payment.ExpenseLegalEntityId.Value > 0;
        if (hasExpenseClient && hasExpenseLegalEntity)
        {
            return BadRequest("Expense payment must reference either expenseClientId or expenseLegalEntityId, not both.");
        }

        if (payment.Type == PaymentType.Expense && hasExpenseClient)
        {
            var expenseClientExists = await _db.Clients
                .AsNoTracking()
                .AnyAsync(client => client.Id == payment.ExpenseClientId!.Value, ct);
            if (!expenseClientExists)
            {
                return BadRequest($"Unknown expenseClientId {payment.ExpenseClientId!.Value}");
            }
        }

        if (payment.Type == PaymentType.Expense && hasExpenseLegalEntity)
        {
            var expenseLegalEntityExists = await _db.LegalEntities
                .AsNoTracking()
                .AnyAsync(entity => entity.Id == payment.ExpenseLegalEntityId!.Value, ct);
            if (!expenseLegalEntityExists)
            {
                return BadRequest($"Unknown expenseLegalEntityId {payment.ExpenseLegalEntityId!.Value}");
            }
        }

        if (!payment.ClientCaseId.HasValue || payment.ClientCaseId.Value <= 0)
        {
            return null;
        }

        var caseOwnerClientId = payment.Type == PaymentType.Expense
            ? payment.ExpenseClientId
            : payment.ClientId;
        var caseOwnerLegalEntityId = payment.Type == PaymentType.Expense
            ? payment.ExpenseLegalEntityId
            : payment.LegalEntityId;
        var hasCaseOwnerClient = caseOwnerClientId.HasValue && caseOwnerClientId.Value > 0;
        var hasCaseOwnerLegalEntity = caseOwnerLegalEntityId.HasValue && caseOwnerLegalEntityId.Value > 0;
        if (hasCaseOwnerClient == hasCaseOwnerLegalEntity)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest("ExpenseClientId or expenseLegalEntityId is required when clientCaseId is specified for expense payments.")
                : BadRequest("ClientId or legalEntityId is required when clientCaseId is specified.");
        }

        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == payment.ClientCaseId.Value, ct);
        if (clientCase is null)
        {
            return BadRequest($"Unknown clientCaseId {payment.ClientCaseId.Value}");
        }

        if (hasCaseOwnerClient && clientCase.ClientId != caseOwnerClientId!.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseClientId {caseOwnerClientId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to clientId {caseOwnerClientId.Value}");
        }

        if (hasCaseOwnerLegalEntity && clientCase.LegalEntityId != caseOwnerLegalEntityId!.Value)
        {
            return payment.Type == PaymentType.Expense
                ? BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to expenseLegalEntityId {caseOwnerLegalEntityId.Value}")
                : BadRequest($"clientCaseId {payment.ClientCaseId.Value} does not belong to legalEntityId {caseOwnerLegalEntityId.Value}");
        }

        return null;
    }

    private async Task SyncPaymentInvoiceClientCasesAsync(Payment payment, IReadOnlyCollection<int> clientCaseIds, CancellationToken ct)
    {
        var targetIds = clientCaseIds
            .Where(caseId => caseId > 0)
            .Distinct()
            .ToHashSet();

        var entry = _db.Entry(payment);
        if (entry.State != EntityState.Detached)
        {
            var collection = entry.Collection(item => item.InvoiceClientCases);
            if (!collection.IsLoaded)
            {
                await collection.LoadAsync(ct);
            }
        }

        var toRemove = payment.InvoiceClientCases
            .Where(link => !targetIds.Contains(link.ClientCaseId))
            .ToList();
        foreach (var link in toRemove)
        {
            payment.InvoiceClientCases.Remove(link);
        }

        var existingIds = payment.InvoiceClientCases
            .Select(link => link.ClientCaseId)
            .ToHashSet();
        foreach (var clientCaseId in targetIds)
        {
            if (existingIds.Contains(clientCaseId))
            {
                continue;
            }

            payment.InvoiceClientCases.Add(new InvoiceClientCase
            {
                PaymentId = payment.Id,
                ClientCaseId = clientCaseId,
            });
        }
    }

    private static List<(string Number, DateTime Date)> BuildLookupCandidates(Payment payment)
    {
        var candidates = new List<(string Number, DateTime Date)>();

        void Add(string? number, DateTime? date)
        {
            var normalized = NormalizeInvoiceNumber(number);
            if (normalized is null)
            {
                return;
            }

            var effectiveDate = (date ?? payment.Date).Date;
            if (candidates.Any(item => string.Equals(item.Number, normalized, StringComparison.OrdinalIgnoreCase) && item.Date == effectiveDate))
            {
                return;
            }

            candidates.Add((normalized, effectiveDate));
        }

        Add(payment.LegacyAccountNumber, payment.LegacyAccountDate);
        Add(payment.Account, payment.AccountDate);

        return candidates;
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

    private static string ReplaceTechnicalMarkerLine(string? notes, string marker, string replacement)
    {
        var lines = SplitSystemNoteLines(notes)
            .Where(line => !line.StartsWith(marker, StringComparison.Ordinal))
            .ToList();

        if (!string.IsNullOrWhiteSpace(replacement))
        {
            lines.Insert(0, replacement.Trim());
        }

        return string.Join("\n", lines);
    }

    private static string RemoveTechnicalMarkerLines(string? notes, params string[] markers)
    {
        var markerSet = markers
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .ToArray();

        return string.Join(
            "\n",
            SplitSystemNoteLines(notes)
                .Where(line => !markerSet.Any(marker => line.StartsWith(marker, StringComparison.Ordinal))));
    }

    private static IEnumerable<string> SplitSystemNoteLines(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return Enumerable.Empty<string>();
        }

        return notes
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    private static void PopulateAuditMetadata(PaymentContext db, Payment payment)
    {
        var entry = db.Entry(payment);
        if (entry.Metadata.FindProperty("CreatedByUserId") is null)
        {
            return;
        }

        payment.AuditCreatedByUserId = entry.Property<int?>("CreatedByUserId").CurrentValue;
        payment.AuditCreatedByUserName = entry.Property<string?>("CreatedByUserName").CurrentValue;
        payment.AuditUpdatedAt = entry.Property<DateTime?>("UpdatedAt").CurrentValue;
        payment.AuditUpdatedByUserId = entry.Property<int?>("UpdatedByUserId").CurrentValue;
        payment.AuditUpdatedByUserName = entry.Property<string?>("UpdatedByUserName").CurrentValue;
        payment.AuditIsDeleted = entry.Property<bool>("IsDeleted").CurrentValue;
        payment.AuditDeletedAt = entry.Property<DateTime?>("DeletedAt").CurrentValue;
        payment.AuditDeletedByUserId = entry.Property<int?>("DeletedByUserId").CurrentValue;
        payment.AuditDeletedByUserName = entry.Property<string?>("DeletedByUserName").CurrentValue;
    }

    private static IQueryable<Payment> ProjectPaymentsForRead(IQueryable<Payment> query)
    {
        return query
            .AsNoTracking()
            .Select(payment => new Payment
            {
                Id = payment.Id,
                Account = payment.Account,
                AccountDate = payment.AccountDate,
                Amount = payment.Amount,
                ClientCaseId = payment.ClientCaseId,
                ClientCaseReference = payment.ClientCaseReference,
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                ContractId = payment.ContractId,
                CounterpartyName = payment.CounterpartyName,
                CreatedAt = payment.CreatedAt,
                Date = payment.Date,
                DealTypeId = payment.DealTypeId,
                Description = payment.Description,
                DocumentKind = payment.DocumentKind,
                ExpenseClientId = payment.ExpenseClientId,
                ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
                IncomeTypeId = payment.IncomeTypeId,
                InvoiceId = payment.InvoiceId,
                IsPaid = payment.IsPaid,
                LastPaymentDate = payment.LastPaymentDate,
                LegacyAccountDate = payment.LegacyAccountDate,
                LegacyAccountNumber = payment.LegacyAccountNumber,
                Notes = payment.Notes,
                PaidAmount = payment.PaidAmount,
                PaidDate = payment.PaidDate,
                PaymentDocumentDate = payment.PaymentDocumentDate,
                PaymentOrderNumber = payment.PaymentOrderNumber,
                PaymentSourceId = payment.PaymentSourceId,
                PaymentStatusId = payment.PaymentStatusId,
                PlannedDate = payment.PlannedDate,
                RescheduleCount = payment.RescheduleCount,
                ServiceId = payment.ServiceId,
                Status = payment.Status,
                SystemNotes = payment.SystemNotes,
                Type = payment.Type,
                AuditCreatedByUserId = EF.Property<int?>(payment, "CreatedByUserId"),
                AuditCreatedByUserName = EF.Property<string?>(payment, "CreatedByUserName"),
                AuditUpdatedAt = EF.Property<DateTime?>(payment, "UpdatedAt"),
                AuditUpdatedByUserId = EF.Property<int?>(payment, "UpdatedByUserId"),
                AuditUpdatedByUserName = EF.Property<string?>(payment, "UpdatedByUserName"),
                AuditIsDeleted = EF.Property<bool>(payment, "IsDeleted"),
                AuditDeletedAt = EF.Property<DateTime?>(payment, "DeletedAt"),
                AuditDeletedByUserId = EF.Property<int?>(payment, "DeletedByUserId"),
                AuditDeletedByUserName = EF.Property<string?>(payment, "DeletedByUserName"),
                Client = payment.Client == null
                    ? null
                    : new Client
                    {
                        Id = payment.Client.Id,
                        Name = payment.Client.Name,
                        Email = payment.Client.Email,
                        Phone = payment.Client.Phone,
                        Company = payment.Client.Company,
                        Address = payment.Client.Address,
                        Notes = payment.Client.Notes,
                        CreatedAt = payment.Client.CreatedAt,
                        IsActive = payment.Client.IsActive,
                        LegalEntityId = payment.Client.LegalEntityId,
                        ClientStatusId = payment.Client.ClientStatusId,
                        ResponsibleUserId = payment.Client.ResponsibleUserId,
                        ClientStatus = payment.Client.ClientStatus == null
                            ? null
                            : new ClientStatusEntity
                            {
                                Id = payment.Client.ClientStatus.Id,
                                Name = payment.Client.ClientStatus.Name,
                                Description = payment.Client.ClientStatus.Description,
                                ColorHex = payment.Client.ClientStatus.ColorHex,
                                IsActive = payment.Client.ClientStatus.IsActive,
                                CreatedAt = payment.Client.ClientStatus.CreatedAt,
                                SystemKey = payment.Client.ClientStatus.SystemKey,
                            },
                    },
                ClientCase = payment.ClientCase == null
                    ? null
                    : new ClientCase
                    {
                        Id = payment.ClientCase.Id,
                        ClientId = payment.ClientCase.ClientId,
                        LegalEntityId = payment.ClientCase.LegalEntityId,
                        Title = payment.ClientCase.Title,
                        Description = payment.ClientCase.Description,
                        Status = payment.ClientCase.Status,
                        CreatedAt = payment.ClientCase.CreatedAt,
                    },
                LegalEntity = payment.LegalEntity == null
                    ? null
                    : new LegalEntity
                    {
                        Id = payment.LegalEntity.Id,
                        ShortName = payment.LegalEntity.ShortName,
                        FullName = payment.LegalEntity.FullName,
                        Inn = payment.LegalEntity.Inn,
                        Kpp = payment.LegalEntity.Kpp,
                        Ogrn = payment.LegalEntity.Ogrn,
                        Address = payment.LegalEntity.Address,
                        Phone = payment.LegalEntity.Phone,
                        Email = payment.LegalEntity.Email,
                        CreatedAt = payment.LegalEntity.CreatedAt,
                    },
                ExpenseClient = payment.ExpenseClient == null
                    ? null
                    : new Client
                    {
                        Id = payment.ExpenseClient.Id,
                        Name = payment.ExpenseClient.Name,
                        Email = payment.ExpenseClient.Email,
                        Phone = payment.ExpenseClient.Phone,
                        Company = payment.ExpenseClient.Company,
                        Address = payment.ExpenseClient.Address,
                        Notes = payment.ExpenseClient.Notes,
                        CreatedAt = payment.ExpenseClient.CreatedAt,
                        IsActive = payment.ExpenseClient.IsActive,
                        LegalEntityId = payment.ExpenseClient.LegalEntityId,
                        ClientStatusId = payment.ExpenseClient.ClientStatusId,
                        ResponsibleUserId = payment.ExpenseClient.ResponsibleUserId,
                        ClientStatus = payment.ExpenseClient.ClientStatus == null
                            ? null
                            : new ClientStatusEntity
                            {
                                Id = payment.ExpenseClient.ClientStatus.Id,
                                Name = payment.ExpenseClient.ClientStatus.Name,
                                Description = payment.ExpenseClient.ClientStatus.Description,
                                ColorHex = payment.ExpenseClient.ClientStatus.ColorHex,
                                IsActive = payment.ExpenseClient.ClientStatus.IsActive,
                                CreatedAt = payment.ExpenseClient.ClientStatus.CreatedAt,
                                SystemKey = payment.ExpenseClient.ClientStatus.SystemKey,
                            },
                    },
                ExpenseLegalEntity = payment.ExpenseLegalEntity == null
                    ? null
                    : new LegalEntity
                    {
                        Id = payment.ExpenseLegalEntity.Id,
                        ShortName = payment.ExpenseLegalEntity.ShortName,
                        FullName = payment.ExpenseLegalEntity.FullName,
                        Inn = payment.ExpenseLegalEntity.Inn,
                        Kpp = payment.ExpenseLegalEntity.Kpp,
                        Ogrn = payment.ExpenseLegalEntity.Ogrn,
                        Address = payment.ExpenseLegalEntity.Address,
                        Phone = payment.ExpenseLegalEntity.Phone,
                        Email = payment.ExpenseLegalEntity.Email,
                        CreatedAt = payment.ExpenseLegalEntity.CreatedAt,
                    },
                DealType = payment.DealType == null
                    ? null
                    : new DealType
                    {
                        Id = payment.DealType.Id,
                        Name = payment.DealType.Name,
                        Description = payment.DealType.Description,
                        ColorHex = payment.DealType.ColorHex,
                        IsActive = payment.DealType.IsActive,
                        CreatedAt = payment.DealType.CreatedAt,
                    },
                IncomeType = payment.IncomeType == null
                    ? null
                    : new IncomeType
                    {
                        Id = payment.IncomeType.Id,
                        Name = payment.IncomeType.Name,
                        Description = payment.IncomeType.Description,
                        ColorHex = payment.IncomeType.ColorHex,
                        IsActive = payment.IncomeType.IsActive,
                        CreatedAt = payment.IncomeType.CreatedAt,
                        PaymentType = payment.IncomeType.PaymentType,
                    },
                PaymentSource = payment.PaymentSource == null
                    ? null
                    : new PaymentSource
                    {
                        Id = payment.PaymentSource.Id,
                        Name = payment.PaymentSource.Name,
                        Description = payment.PaymentSource.Description,
                        ColorHex = payment.PaymentSource.ColorHex,
                        IsActive = payment.PaymentSource.IsActive,
                        CreatedAt = payment.PaymentSource.CreatedAt,
                    },
                PaymentStatusEntity = payment.PaymentStatusEntity == null
                    ? null
                    : new PaymentStatusEntity
                    {
                        Id = payment.PaymentStatusEntity.Id,
                        Name = payment.PaymentStatusEntity.Name,
                        Description = payment.PaymentStatusEntity.Description,
                        ColorHex = payment.PaymentStatusEntity.ColorHex,
                        IsActive = payment.PaymentStatusEntity.IsActive,
                        CreatedAt = payment.PaymentStatusEntity.CreatedAt,
                    },
            });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var payment = await _db.Payments
            .Include(item => item.Invoice)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (payment is null) return NotFound();

        if (await IsPartialRemainderPaymentAsync(payment, ct))
        {
            return Conflict(new { message = PaymentRemainderTransferRules.RemainderDeleteBlockedMessage });
        }

        var transferredRemainder = await FindPartialRemainderPaymentAsync(payment.Id, ct);
        if (transferredRemainder is not null)
        {
            return Conflict(new { message = PaymentRemainderTransferRules.SourceDeleteBlockedMessage });
        }

        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(payment))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.DeleteBlockedMessage });
        }

        _db.Payments.Remove(payment);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
