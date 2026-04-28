using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;
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
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] string? clientRole = null,
        CancellationToken ct = default)
    {
        var q = _db.Payments
            .AsQueryable()
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents)
            .ApplyPaymentFilters(from, to, clientId, caseId, null, clientRole);

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
        await _db.SaveChangesAsync();
        await ClientBalanceService.UpsertPaymentOverpaymentAsync(_db, model, rawPaidAmount, now, HttpContext, ct);
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
        e.ExpenseClientId = model.ExpenseClientId;
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
        await _db.SaveChangesAsync();
        PopulateAuditMetadata(_db, e);
        return Ok(e);
    }

    [HttpPut("{id:int}/invoice-managed")]
    public async Task<IActionResult> UpdateInvoiceManaged(int id, [FromBody] Payment model, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "РќРµ Р·Р°РїРѕР»РЅРµРЅС‹ РѕР±СЏР·Р°С‚РµР»СЊРЅС‹Рµ РїРѕР»СЏ", missingFields = missingUpdate });

        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        if (!InvoiceManagedPaymentRules.IsInvoiceManagedPayment(e))
        {
            return BadRequest(new { message = "Payment is not invoice-managed." });
        }

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
        e.ExpenseClientId = model.ExpenseClientId;
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
        await _db.SaveChangesAsync();
        PopulateAuditMetadata(_db, e);
        return Ok(e);
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
            payment.ExpenseClientId = invoiceDocument.ExpenseClientId;
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
                    ExpenseClientId = payment.ExpenseClientId,
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
                ContractId = payment.ContractId,
                CounterpartyName = payment.CounterpartyName,
                CreatedAt = payment.CreatedAt,
                Date = payment.Date,
                DealTypeId = payment.DealTypeId,
                Description = payment.Description,
                DocumentKind = payment.DocumentKind,
                ExpenseClientId = payment.ExpenseClientId,
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
                        Title = payment.ClientCase.Title,
                        Description = payment.ClientCase.Description,
                        Status = payment.ClientCase.Status,
                        CreatedAt = payment.ClientCase.CreatedAt,
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

        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(payment))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.DeleteBlockedMessage });
        }

        _db.Payments.Remove(payment);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
