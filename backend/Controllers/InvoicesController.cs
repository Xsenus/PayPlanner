using System.Linq.Expressions;
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
public class InvoicesController : ControllerBase
{
    private readonly PaymentContext _db;

    public InvoicesController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] PaymentType? type,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);

        var query = BuildInvoiceQuery();
        query = ApplyFilters(query, from, to, status, type, clientId, responsibleId, search);
        query = ApplySort(query, sortBy, sortDir);

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    private async Task<ActionResult?> ValidateLookupsAsync(UpsertInvoiceRequest request, CancellationToken ct)
    {
        if (request.IncomeTypeId.HasValue)
        {
            var incomeType = await _db.IncomeTypes.AsNoTracking()
                .Where(i => i.Id == request.IncomeTypeId.Value)
                .Select(i => new { i.PaymentType })
                .FirstOrDefaultAsync(ct);

            if (incomeType is null)
            {
                return BadRequest($"Unknown incomeTypeId {request.IncomeTypeId.Value}");
            }

            if (incomeType.PaymentType != request.Type)
            {
                return BadRequest("Selected income type does not match invoice type.");
            }
        }

        if (request.PaymentSourceId.HasValue)
        {
            var paymentSource = await _db.PaymentSources.AsNoTracking()
                .Where(s => s.Id == request.PaymentSourceId.Value)
                .Select(s => new { s.PaymentType })
                .FirstOrDefaultAsync(ct);

            if (paymentSource is null)
            {
                return BadRequest($"Unknown paymentSourceId {request.PaymentSourceId.Value}");
            }

            if (paymentSource.PaymentType.HasValue && paymentSource.PaymentType.Value != request.Type)
            {
                return BadRequest("Selected payment source does not match invoice type.");
            }
        }

        if (request.DealTypeId.HasValue)
        {
            var dealTypeExists = await _db.DealTypes.AsNoTracking()
                .AnyAsync(d => d.Id == request.DealTypeId.Value, ct);

            if (!dealTypeExists)
            {
                return BadRequest($"Unknown dealTypeId {request.DealTypeId.Value}");
            }
        }

        if (request.PaymentStatusEntityId.HasValue)
        {
            var statusExists = await _db.PaymentStatuses.AsNoTracking()
                .AnyAsync(s => s.Id == request.PaymentStatusEntityId.Value, ct);

            if (!statusExists)
            {
                return BadRequest($"Unknown paymentStatusEntityId {request.PaymentStatusEntityId.Value}");
            }
        }

        return null;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<InvoiceSummaryDto>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] PaymentType? type,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        CancellationToken ct = default)
    {
        var query = BuildInvoiceQuery();
        query = ApplyFilters(query, from, to, status, type, clientId, responsibleId, search);

        var grouped = await query
            .GroupBy(x => x.Payment.Status)
            .Select(g => new
            {
                Status = g.Key,
                Amount = g.Sum(x => x.Payment.Amount),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        decimal totalAmount = grouped.Sum(x => x.Amount);
        int totalCount = grouped.Sum(x => x.Count);

        var pending = grouped.FirstOrDefault(x => x.Status == PaymentStatus.Pending);
        var paid = grouped.FirstOrDefault(x => x.Status == PaymentStatus.Completed);
        var overdue = grouped.FirstOrDefault(x => x.Status == PaymentStatus.Overdue);

        var summary = new InvoiceSummaryDto
        {
            Total = new InvoiceSummaryBucketDto { Amount = totalAmount, Count = totalCount },
            Pending = new InvoiceSummaryBucketDto
            {
                Amount = pending?.Amount ?? 0m,
                Count = pending?.Count ?? 0,
            },
            Paid = new InvoiceSummaryBucketDto
            {
                Amount = paid?.Amount ?? 0m,
                Count = paid?.Count ?? 0,
            },
            Overdue = new InvoiceSummaryBucketDto
            {
                Amount = overdue?.Amount ?? 0m,
                Count = overdue?.Count ?? 0,
            },
        };

        return Ok(summary);
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var clientExists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ClientId, ct);
        if (!clientExists)
        {
            return BadRequest($"Unknown clientId {request.ClientId}");
        }

        var lookupError = await ValidateLookupsAsync(request, ct);
        if (lookupError is not null)
        {
            return lookupError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        var payment = new Payment
        {
            Account = number,
            AccountDate = request.Date.Date,
            Date = (request.DueDate ?? request.Date).Date,
            Amount = request.Amount,
            Status = request.Status,
            Type = request.Type,
            Description = Normalize(request.Description) ?? string.Empty,
            Notes = Normalize(request.ActReference) ?? string.Empty,
            ClientId = request.ClientId,
            ClientCaseId = request.ClientCaseId,
            PaymentSourceId = request.PaymentSourceId,
            IncomeTypeId = request.IncomeTypeId,
            DealTypeId = request.DealTypeId,
            PaymentStatusId = request.PaymentStatusEntityId,
            CreatedAt = DateTime.UtcNow,
        };

        ApplyPaidFlags(payment, request.PaidDate);

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        var dto = await BuildInvoiceQuery()
            .Where(x => x.Payment.Id == payment.Id)
            .Select(Projection)
            .FirstAsync(ct);

        return Created($"/api/invoices/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> Update(int id, [FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var payment = await _db.Payments.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        if (!await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ClientId, ct))
        {
            return BadRequest($"Unknown clientId {request.ClientId}");
        }

        var lookupError = await ValidateLookupsAsync(request, ct);
        if (lookupError is not null)
        {
            return lookupError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        payment.Account = number;
        payment.AccountDate = request.Date.Date;
        payment.Date = (request.DueDate ?? request.Date).Date;
        payment.Amount = request.Amount;
        payment.Status = request.Status;
        payment.Type = request.Type;
        payment.Description = Normalize(request.Description) ?? string.Empty;
        payment.Notes = Normalize(request.ActReference) ?? string.Empty;
        payment.ClientId = request.ClientId;
        payment.ClientCaseId = request.ClientCaseId;
        payment.PaymentSourceId = request.PaymentSourceId;
        payment.IncomeTypeId = request.IncomeTypeId;
        payment.DealTypeId = request.DealTypeId;
        payment.PaymentStatusId = request.PaymentStatusEntityId;

        ApplyPaidFlags(payment, request.PaidDate);

        await _db.SaveChangesAsync(ct);

        var dto = await BuildInvoiceQuery()
            .Where(x => x.Payment.Id == payment.Id)
            .Select(Projection)
            .FirstAsync(ct);

        return Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var payment = await _db.Payments.FindAsync(new object[] { id }, ct);
        if (payment is null)
        {
            return NotFound();
        }

        _db.Payments.Remove(payment);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<InvoiceQueryItem> BuildInvoiceQuery()
    {
        var payments = _db.Payments.AsNoTracking()
            .WithPaymentIncludes()
            .Where(p => p.Account != null && p.Account != "");

        var acts = _db.Acts.AsNoTracking()
            .Include(a => a.Client)
            .Include(a => a.Responsible);

        return from payment in payments
               join act in acts on payment.Account equals act.InvoiceNumber into actGroup
               from act in actGroup
                   .OrderByDescending(a => a.Date)
                   .ThenByDescending(a => a.Id)
                   .Take(1)
                   .DefaultIfEmpty()
               select new InvoiceQueryItem { Payment = payment, Act = act };
    }

    private static IQueryable<InvoiceQueryItem> ApplyFilters(
        IQueryable<InvoiceQueryItem> query,
        DateTime? from,
        DateTime? to,
        PaymentStatus? status,
        PaymentType? type,
        int? clientId,
        int? responsibleId,
        string? search)
    {
        if (from.HasValue)
        {
            var fromDate = from.Value.Date;
            query = query.Where(x => (x.Payment.AccountDate ?? x.Payment.Date) >= fromDate);
        }

        if (to.HasValue)
        {
            var toDate = to.Value.Date;
            query = query.Where(x => (x.Payment.AccountDate ?? x.Payment.Date) <= toDate);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Payment.Status == status.Value);
        }

        if (type.HasValue)
        {
            query = query.Where(x => x.Payment.Type == type.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(x => x.Payment.ClientId == clientId.Value);
        }

        if (responsibleId.HasValue)
        {
            query = query.Where(x => x.Act != null && x.Act.ResponsibleId == responsibleId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var like = $"%{term}%";
            query = query.Where(x =>
                (x.Payment.Account != null && EF.Functions.Like(x.Payment.Account, like)) ||
                (x.Payment.Description != null && EF.Functions.Like(x.Payment.Description, like)) ||
                (x.Payment.Notes != null && EF.Functions.Like(x.Payment.Notes, like)) ||
                (x.Payment.Client != null && x.Payment.Client.Name != null && EF.Functions.Like(x.Payment.Client.Name, like)) ||
                (x.Payment.Client != null && x.Payment.Client.Company != null && EF.Functions.Like(x.Payment.Client.Company, like)) ||
                (x.Payment.ClientCase != null && x.Payment.ClientCase.Title != null && EF.Functions.Like(x.Payment.ClientCase.Title, like)) ||
                (x.Act != null && x.Act.Number != null && EF.Functions.Like(x.Act.Number, like)) ||
                (x.Act != null && x.Act.Title != null && EF.Functions.Like(x.Act.Title, like)) ||
                (x.Act != null && x.Act.InvoiceNumber != null && EF.Functions.Like(x.Act.InvoiceNumber, like)) ||
                (x.Act != null && x.Act.CounterpartyInn != null && EF.Functions.Like(x.Act.CounterpartyInn, like)) ||
                (x.Act != null && x.Act.Comment != null && EF.Functions.Like(x.Act.Comment, like)) ||
                (x.Act != null && x.Act.Client != null && x.Act.Client.Name != null && EF.Functions.Like(x.Act.Client.Name, like)));
        }

        return query;
    }

    private static IQueryable<InvoiceQueryItem> ApplySort(
        IQueryable<InvoiceQueryItem> query,
        string? sortBy,
        string? sortDir)
    {
        var desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        return (sortBy?.ToLowerInvariant()) switch
        {
            "number" => desc
                ? query.OrderByDescending(x => x.Payment.Account)
                : query.OrderBy(x => x.Payment.Account),
            "amount" => desc
                ? query.OrderByDescending(x => x.Payment.Amount)
                : query.OrderBy(x => x.Payment.Amount),
            "status" => desc
                ? query.OrderByDescending(x => x.Payment.Status)
                : query.OrderBy(x => x.Payment.Status),
            "client" => desc
                ? query.OrderByDescending(x => x.Payment.Client != null ? x.Payment.Client.Name : string.Empty)
                : query.OrderBy(x => x.Payment.Client != null ? x.Payment.Client.Name : string.Empty),
            "responsible" => desc
                ? query.OrderByDescending(x => x.Act != null && x.Act.Responsible != null
                    ? (x.Act.Responsible.FullName ?? x.Act.Responsible.Email ?? string.Empty)
                    : string.Empty)
                : query.OrderBy(x => x.Act != null && x.Act.Responsible != null
                    ? (x.Act.Responsible.FullName ?? x.Act.Responsible.Email ?? string.Empty)
                    : string.Empty),
            "duedate" => desc
                ? query.OrderByDescending(x => x.Payment.Date)
                : query.OrderBy(x => x.Payment.Date),
            "createdat" => desc
                ? query.OrderByDescending(x => x.Payment.CreatedAt)
                : query.OrderBy(x => x.Payment.CreatedAt),
            _ => desc
                ? query.OrderByDescending(x => x.Payment.AccountDate ?? x.Payment.Date)
                : query.OrderBy(x => x.Payment.AccountDate ?? x.Payment.Date),
        };
    }

    private static void ApplyPaidFlags(Payment payment, DateTime? paidDate)
    {
        var now = DateTime.UtcNow;
        var effectiveDate = (paidDate ?? payment.LastPaymentDate ?? payment.PaidDate ?? payment.Date).Date;

        if (payment.Status == PaymentStatus.Completed || paidDate.HasValue)
        {
            payment.PaidAmount = payment.Amount;
            payment.LastPaymentDate = effectiveDate;
            payment.PaidDate = effectiveDate;
        }
        else
        {
            if (payment.Status is PaymentStatus.Pending or PaymentStatus.Overdue)
            {
                payment.PaidAmount = 0m;
                payment.LastPaymentDate = null;
                payment.PaidDate = null;
            }
            else
            {
                payment.PaidAmount = Math.Min(payment.PaidAmount, payment.Amount);
            }

            if (payment.PaidAmount <= 0m)
            {
                payment.LastPaymentDate = null;
                payment.PaidDate = null;
            }
        }

        PaymentDomainService.Normalize(payment);
        PaymentDomainService.ApplyStatusRules(payment, now);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly Expression<Func<InvoiceQueryItem, InvoiceDto>> Projection = x => new InvoiceDto
    {
        Id = x.Payment.Id,
        Number = x.Payment.Account ?? string.Empty,
        Date = x.Payment.AccountDate ?? x.Payment.Date,
        DueDate = x.Payment.Date,
        Amount = x.Payment.Amount,
        Status = x.Payment.Status,
        IsPaid = x.Payment.IsPaid,
        PaidDate = x.Payment.PaidDate,
        Type = x.Payment.Type,
        ClientId = x.Payment.ClientId,
        ClientName = x.Payment.Client != null ? x.Payment.Client.Name : null,
        ClientCompany = x.Payment.Client != null ? x.Payment.Client.Company : null,
        ClientStatusId = x.Payment.Client != null ? x.Payment.Client.ClientStatusId : null,
        ClientStatus = x.Payment.Client != null && x.Payment.Client.ClientStatus != null
            ? new ClientStatusDto
            {
                Id = x.Payment.Client.ClientStatus.Id,
                Name = x.Payment.Client.ClientStatus.Name,
                Description = x.Payment.Client.ClientStatus.Description,
                ColorHex = x.Payment.Client.ClientStatus.ColorHex,
                IsActive = x.Payment.Client.ClientStatus.IsActive,
                CreatedAt = x.Payment.Client.ClientStatus.CreatedAt,
            }
            : null,
        ClientCaseId = x.Payment.ClientCaseId,
        ClientCaseTitle = x.Payment.ClientCase != null ? x.Payment.ClientCase.Title : null,
        Description = x.Payment.Description,
        ActReference = x.Payment.Notes,
        ActId = x.Act != null ? x.Act.Id : (int?)null,
        ActNumber = x.Act != null ? x.Act.Number : null,
        ActTitle = x.Act != null ? x.Act.Title : null,
        ActStatus = x.Act != null ? x.Act.Status : null,
        ResponsibleId = x.Act != null ? x.Act.ResponsibleId : null,
        ResponsibleName = x.Act != null && x.Act.Responsible != null
            ? (string.IsNullOrWhiteSpace(x.Act.Responsible.FullName)
                ? x.Act.Responsible.Email
                : x.Act.Responsible.FullName)
            : null,
        CounterpartyInn = x.Act != null ? x.Act.CounterpartyInn : null,
        PaymentStatusName = x.Payment.PaymentStatusEntity != null ? x.Payment.PaymentStatusEntity.Name : null,
        PaymentSourceId = x.Payment.PaymentSourceId,
        PaymentSourceName = x.Payment.PaymentSource != null ? x.Payment.PaymentSource.Name : null,
        PaymentSourceColor = x.Payment.PaymentSource != null ? x.Payment.PaymentSource.ColorHex : null,
        PaymentSourceType = x.Payment.PaymentSource != null ? x.Payment.PaymentSource.PaymentType : null,
        IncomeTypeId = x.Payment.IncomeTypeId,
        IncomeTypeName = x.Payment.IncomeType != null ? x.Payment.IncomeType.Name : null,
        IncomeTypeColor = x.Payment.IncomeType != null ? x.Payment.IncomeType.ColorHex : null,
        CreatedAt = x.Payment.CreatedAt,
    };

    private sealed class InvoiceQueryItem
    {
        public Payment Payment { get; init; } = null!;
        public Act? Act { get; init; }
    }
}
