using System.Globalization;
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
    private readonly PaymentContext _db;

    public PaymentsV2Controller(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<PaymentsPageResponse>> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] int? caseId,
        [FromQuery] int? contractId = null,
        [FromQuery] bool excludeInstallmentParents = true,
        [FromQuery] bool excludeInvoiceDocuments = false,
        [FromQuery] string? clientIds = null,
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

        var query = _db.Payments.AsQueryable()
            .WithPaymentIncludes()
            .ExcludeInstallmentParentInvoices(_db, excludeInstallmentParents)
            .ApplyPaymentFilters(from, to, clientId, caseId, search);

        if (contractId.HasValue)
        {
            query = query.Where(payment =>
                payment.ContractId == contractId.Value
                || _db.ContractPayments.Any(link => link.ContractId == contractId.Value && link.PaymentId == payment.Id));
        }

        if (excludeInvoiceDocuments)
        {
            query = query.Where(payment => payment.DocumentKind != DocumentKind.Invoice);
        }

        var clientIdList = ParseIntList(clientIds);
        if (clientIdList.Count > 0)
        {
            query = query.Where(payment =>
                (payment.ClientId.HasValue && clientIdList.Contains(payment.ClientId.Value))
                || (payment.ExpenseClientId.HasValue && clientIdList.Contains(payment.ExpenseClientId.Value)));
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

        query = ApplyCategoryFilters(query, categoryTokens);
        query = query.ApplyPaymentSort(sortBy, sortDir);

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

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var entity = await _db.Payments
            .WithPaymentIncludes()
            .AsNoTracking()
            .FirstOrDefaultAsync(payment => payment.Id == id, ct);

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Payment model)
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
        model.SystemNotes = model.SystemNotes ?? string.Empty;
        model.RescheduleCount = Math.Max(0, model.RescheduleCount);
        if (model.LastPaymentDate is null && model.PaidDate.HasValue)
        {
            model.LastPaymentDate = model.PaidDate.Value.Date;
        }

        PaymentDomainService.Normalize(model);
        PaymentDomainService.ApplyStatusRules(model, now);

        _db.Payments.Add(model);
        await _db.SaveChangesAsync();
        return Created($"/api/v2/payments/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Payment model)
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

        var originalDueDate = entity.Date.Date;
        var originalPlannedDate = (entity.PlannedDate == default ? entity.Date : entity.PlannedDate).Date;
        var originalPaidAmount = entity.PaidAmount;
        var originalStatus = entity.Status;
        var originalRescheduleCount = Math.Max(0, entity.RescheduleCount);
        var originalSystemNotes = entity.SystemNotes ?? string.Empty;

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
        entity.ClientCaseId = model.ClientCaseId;
        entity.ContractId = model.ContractId;
        entity.DealTypeId = model.DealTypeId;
        entity.IncomeTypeId = model.IncomeTypeId;
        entity.PaymentSourceId = model.PaymentSourceId;
        entity.PaymentStatusId = model.PaymentStatusId;
        entity.Account = string.IsNullOrWhiteSpace(model.Account) ? null : model.Account.Trim();
        entity.AccountDate = model.AccountDate;
        entity.PaidDate = model.PaidDate;
        entity.PaidAmount = model.PaidAmount;
        entity.LastPaymentDate = model.LastPaymentDate ?? model.PaidDate ?? entity.LastPaymentDate;
        entity.SystemNotes = MergeSystemNotes(originalSystemNotes, model.SystemNotes);

        PaymentDomainService.Normalize(entity);

        if (entity.LastPaymentDate is null && entity.PaidAmount > 0m)
        {
            entity.LastPaymentDate = (model.LastPaymentDate ?? model.PaidDate)?.Date;
        }

        PaymentDomainService.ApplyStatusRules(entity, now);

        var rescheduleCount = originalRescheduleCount;
        var plannedDateChanged = model.PlannedDate != default && originalPlannedDate != entity.PlannedDate.Date;
        if (plannedDateChanged)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Изменена плановая дата: {originalPlannedDate:dd.MM.yyyy} → {entity.PlannedDate:dd.MM.yyyy}",
                now,
                actor);
        }

        var userChangedDueDate = model.Date.Date != originalDueDate;
        if (userChangedDueDate)
        {
            rescheduleCount++;
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Дата платежа изменена: {originalDueDate:dd.MM.yyyy} → {entity.Date:dd.MM.yyyy}",
                now,
                actor);
        }
        else if (originalDueDate != entity.Date.Date)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Дата платежа автоматически скорректирована на {entity.Date:dd.MM.yyyy}",
                now,
                actor);
        }

        if (Math.Abs(originalPaidAmount - entity.PaidAmount) > 0.009m)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Изменена оплаченная сумма: было {originalPaidAmount.ToString("N2", culture)}, стало {entity.PaidAmount.ToString("N2", culture)}",
                now,
                actor);
        }

        if (originalStatus != entity.Status)
        {
            PaymentDomainService.AppendSystemNote(
                entity,
                $"Статус обновлён: {originalStatus.ToDisplayString()} → {entity.Status.ToDisplayString()}",
                now,
                actor);
        }

        entity.RescheduleCount = rescheduleCount;

        await _db.SaveChangesAsync();
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var entity = await _db.Payments.FindAsync(id);
        if (entity is null) return NotFound();
        if (InvoiceManagedPaymentRules.IsInvoiceManagedPayment(entity))
        {
            return Conflict(new { message = InvoiceManagedPaymentRules.DeleteBlockedMessage });
        }
        _db.Payments.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
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
}
