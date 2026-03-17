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
        CancellationToken ct)
    {
        var q = _db.Payments.AsQueryable().WithPaymentIncludes();
        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            q = q.Where(p =>
                p.Date >= fromDate ||
                (p.AccountDate.HasValue && p.AccountDate.Value >= fromDate));
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            q = q.Where(p =>
                p.Date <= toDate ||
                (p.AccountDate.HasValue && p.AccountDate.Value <= toDate));
        }
        if (clientId.HasValue) q = q.Where(p => p.ClientId == clientId.Value);
        if (caseId.HasValue) q = q.Where(p => p.ClientCaseId == caseId.Value);
        var list = await q.AsNoTracking().OrderBy(p => p.Date).ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var item = await _db.Payments.WithPaymentIncludes().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return item is null ? NotFound() : Ok(item);
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
        PaymentDomainService.Normalize(model);
        PaymentDomainService.ApplyStatusRules(model, now);

        _db.Payments.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Payment model, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("payments", model);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();

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
        e.ClientCaseId = model.ClientCaseId;
        e.DealTypeId = model.DealTypeId;
        e.IncomeTypeId = model.IncomeTypeId;
        e.PaymentSourceId = model.PaymentSourceId;
        e.PaymentStatusId = model.PaymentStatusId;
        e.InvoiceId = model.InvoiceId;
        e.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        e.DocumentKind = model.DocumentKind == default ? DocumentKind.Payment : model.DocumentKind;
        e.AccountDate = model.AccountDate;
        e.PaidDate = model.PaidDate;
        e.PaidAmount = model.PaidAmount;
        e.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? e.LastPaymentDate;
        e.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        await SyncInvoiceLinkAsync(e, ct);
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

        await _db.SaveChangesAsync();
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
        var invoice = await ResolveInvoiceAsync(payment, ct);
        if (invoice is null)
        {
            payment.InvoiceId = null;

            var normalizedAccount = NormalizeInvoiceNumber(payment.Account);
            payment.Account = normalizedAccount;
            payment.AccountDate = normalizedAccount is null
                ? null
                : (payment.AccountDate ?? payment.Date).Date;
            return;
        }

        payment.InvoiceId = invoice.Id;
        payment.Account = invoice.Number;
        payment.AccountDate = invoice.Date.Date;
    }

    private async Task<Invoice?> ResolveInvoiceAsync(Payment payment, CancellationToken ct)
    {
        if (payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
        {
            var linked = await _db.Invoices.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == payment.InvoiceId.Value, ct);
            if (linked is not null)
            {
                return linked;
            }
        }

        var normalizedAccount = NormalizeInvoiceNumber(payment.Account);
        if (normalizedAccount is null)
        {
            return null;
        }

        var invoiceYear = (payment.AccountDate ?? payment.Date).Date.Year;
        var lookup = normalizedAccount.ToLowerInvariant();

        return await _db.Invoices.AsNoTracking()
            .Where(x => x.Number.ToLower() == lookup && x.Date.Year == invoiceYear)
            .OrderByDescending(x => x.Date)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static string? NormalizeInvoiceNumber(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.Payments.FindAsync(id);
        if (e is null) return NotFound();
        _db.Payments.Remove(e);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
