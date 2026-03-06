using System.Linq.Expressions;
using System.Text;
using System.Net;
using System.IO;
using System.Text.Json;
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
    private readonly IEmailService _emailService;
    private readonly IFormRequirementsService _formRequirements;
    private const string InvoiceItemsMarker = "[pp:invoice-items]";

    public InvoicesController(PaymentContext db, IEmailService emailService, IFormRequirementsService formRequirements)
    {
        _db = db;
        _emailService = emailService;
        _formRequirements = formRequirements;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        [FromQuery] PaymentType? type,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);

        var query = BuildInvoiceQuery();
        query = ApplyFilters(query, from, to, status, clientId, responsibleId, search, type);
        query = ApplySort(query, sortBy, sortDir);

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var rawItems = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(ProjectionRaw)
            .ToListAsync(ct);

        var items = rawItems.Select(MapFromRaw).ToList();
        var responsiblesMap = await LoadResponsiblesMapAsync(items.Select(i => i.Id), ct);
        foreach (var item in items)
        {
            item.Responsibles = responsiblesMap.GetValueOrDefault(item.Id, new List<InvoiceResponsibleDto>());
            if (item.Responsibles.Count > 0)
            {
                item.ResponsibleId = item.Responsibles[0].Id;
                item.ResponsibleName = item.Responsibles[0].FullName;
            }
        }

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("summary")]
    public async Task<ActionResult<InvoiceSummaryDto>> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] PaymentStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        [FromQuery] PaymentType? type,
        CancellationToken ct = default)
    {
        var query = BuildInvoiceQuery();
        query = ApplyFilters(query, from, to, status, clientId, responsibleId, search, type);

        var today = DateTime.UtcNow.Date;

        var overdueAgingRows = await query
            .Where(x => x.Payment.Status == PaymentStatus.Overdue)
            .Select(x => new
            {
                Amount = x.Payment.Amount,
                DueDate = x.Payment.Date,
            })
            .ToListAsync(ct);

        decimal overdue0To10 = 0m;
        decimal overdue11To30 = 0m;
        decimal overdue31To60 = 0m;
        decimal overdue61To90 = 0m;
        decimal overdue91Plus = 0m;

        foreach (var row in overdueAgingRows)
        {
            var daysOverdue = Math.Max(0, (today - row.DueDate.Date).Days);
            if (daysOverdue <= 10)
            {
                overdue0To10 += row.Amount;
                continue;
            }

            if (daysOverdue <= 30)
            {
                overdue11To30 += row.Amount;
                continue;
            }

            if (daysOverdue <= 60)
            {
                overdue31To60 += row.Amount;
                continue;
            }

            if (daysOverdue <= 90)
            {
                overdue61To90 += row.Amount;
                continue;
            }

            overdue91Plus += row.Amount;
        }

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
            OverdueAging = new InvoiceOverdueAgingDto
            {
                Day0To10 = overdue0To10,
                Day11To30 = overdue11To30,
                Day31To60 = overdue31To60,
                Day61To90 = overdue61To90,
                Day91Plus = overdue91Plus,
                Total = overdue0To10 + overdue11To30 + overdue31To60 + overdue61To90 + overdue91Plus,
            },
        };

        return Ok(summary);
    }

    [HttpPost]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("contract-invoices", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var clientExists = await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ClientId, ct);
        if (!clientExists)
        {
            return BadRequest($"Unknown clientId {request.ClientId}");
        }

        var contractValidationError = await ValidateContractClientLinkAsync(request.ContractId, request.ClientId, ct);
        if (contractValidationError is not null)
        {
            return contractValidationError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        if (request.ServiceId.HasValue)
        {
            var serviceExists = await _db.Services.AsNoTracking().AnyAsync(s => s.Id == request.ServiceId.Value, ct);
            if (!serviceExists)
            {
                return BadRequest($"Unknown serviceId {request.ServiceId.Value}");
            }
        }

        var payment = new Payment
        {
            Account = number,
            DocumentKind = DocumentKind.Invoice,
            AccountDate = request.Date.Date,
            Date = (request.DueDate ?? request.Date).Date,
            Amount = request.Amount,
            Status = request.Status,
            Type = request.Type,
            Description = Normalize(request.Description) ?? string.Empty,
            Notes = Normalize(request.ActReference) ?? string.Empty,
            ClientId = request.ClientId,
            ContractId = request.ContractId,
            ClientCaseId = request.ClientCaseId,
            PaymentSourceId = request.PaymentSourceId,
            ServiceId = request.ServiceId,
            IncomeTypeId = request.IncomeTypeId,
            DealTypeId = request.DealTypeId,
            PaymentStatusId = request.PaymentStatusEntityId,
            CounterpartyName = Normalize(request.CounterpartyName),
            CreatedAt = DateTime.UtcNow,
        };

        payment.SystemNotes = MergeInvoiceItemsIntoSystemNotes(payment.SystemNotes, request.LineItems);

        var responsibleIds = NormalizeResponsibleIds(request.ResponsibleIds);
        if (!await EnsureResponsibleIdsExistAsync(responsibleIds, ct))
        {
            return BadRequest("One or more responsible users were not found.");
        }

        foreach (var responsibleId in responsibleIds)
        {
            payment.Responsibles.Add(new PaymentResponsible
            {
                UserId = responsibleId,
            });
        }

        ApplyPaidFlags(payment, request.PaidDate);

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);

        var raw = await BuildInvoiceQuery()
            .Where(x => x.Payment.Id == payment.Id)
            .Select(ProjectionRaw)
            .FirstAsync(ct);

        var dto = MapFromRaw(raw);
        var responsiblesMap = await LoadResponsiblesMapAsync(new[] { payment.Id }, ct);
        dto.Responsibles = responsiblesMap.GetValueOrDefault(payment.Id, new List<InvoiceResponsibleDto>());
        if (dto.Responsibles.Count > 0)
        {
            dto.ResponsibleId = dto.Responsibles[0].Id;
            dto.ResponsibleName = dto.Responsibles[0].FullName;
        }

        return Created($"/api/invoices/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<InvoiceDto>> Update(int id, [FromBody] UpsertInvoiceRequest request, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("contract-invoices", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var payment = await _db.Payments
            .Include(p => p.Responsibles)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payment is null)
        {
            return NotFound();
        }

        if (!await _db.Clients.AsNoTracking().AnyAsync(c => c.Id == request.ClientId, ct))
        {
            return BadRequest($"Unknown clientId {request.ClientId}");
        }

        var contractValidationError = await ValidateContractClientLinkAsync(request.ContractId, request.ClientId, ct);
        if (contractValidationError is not null)
        {
            return contractValidationError;
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            return BadRequest("Invoice number is required.");
        }

        if (request.ServiceId.HasValue)
        {
            var serviceExists = await _db.Services.AsNoTracking().AnyAsync(s => s.Id == request.ServiceId.Value, ct);
            if (!serviceExists)
            {
                return BadRequest($"Unknown serviceId {request.ServiceId.Value}");
            }
        }

        payment.Account = number;
        payment.DocumentKind = DocumentKind.Invoice;
        payment.AccountDate = request.Date.Date;
        payment.Date = (request.DueDate ?? request.Date).Date;
        payment.Amount = request.Amount;
        payment.Status = request.Status;
        payment.Type = request.Type;
        payment.Description = Normalize(request.Description) ?? string.Empty;
        payment.Notes = Normalize(request.ActReference) ?? string.Empty;
        payment.ClientId = request.ClientId;
        payment.ContractId = request.ContractId;
        payment.ClientCaseId = request.ClientCaseId;
        payment.PaymentSourceId = request.PaymentSourceId;
        payment.ServiceId = request.ServiceId;
        payment.IncomeTypeId = request.IncomeTypeId;
        payment.DealTypeId = request.DealTypeId;
        payment.PaymentStatusId = request.PaymentStatusEntityId;
        payment.CounterpartyName = Normalize(request.CounterpartyName);
        payment.SystemNotes = MergeInvoiceItemsIntoSystemNotes(payment.SystemNotes, request.LineItems);

        var responsibleIds = NormalizeResponsibleIds(request.ResponsibleIds);
        if (!await EnsureResponsibleIdsExistAsync(responsibleIds, ct))
        {
            return BadRequest("One or more responsible users were not found.");
        }

        var forRemoval = payment.Responsibles.Where(r => !responsibleIds.Contains(r.UserId)).ToList();
        foreach (var row in forRemoval)
        {
            payment.Responsibles.Remove(row);
        }

        var existingResponsibleIds = payment.Responsibles.Select(r => r.UserId).ToHashSet();
        foreach (var responsibleId in responsibleIds)
        {
            if (existingResponsibleIds.Contains(responsibleId))
            {
                continue;
            }

            payment.Responsibles.Add(new PaymentResponsible
            {
                PaymentId = payment.Id,
                UserId = responsibleId,
            });
        }

        ApplyPaidFlags(payment, request.PaidDate);

        await _db.SaveChangesAsync(ct);

        var raw = await BuildInvoiceQuery()
            .Where(x => x.Payment.Id == payment.Id)
            .Select(ProjectionRaw)
            .FirstAsync(ct);

        var dto = MapFromRaw(raw);
        var responsiblesMap = await LoadResponsiblesMapAsync(new[] { payment.Id }, ct);
        dto.Responsibles = responsiblesMap.GetValueOrDefault(payment.Id, new List<InvoiceResponsibleDto>());
        if (dto.Responsibles.Count > 0)
        {
            dto.ResponsibleId = dto.Responsibles[0].Id;
            dto.ResponsibleName = dto.Responsibles[0].FullName;
        }

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


    [HttpGet("{id:int}/document")]
    public async Task<IActionResult> GetDocument(int id, [FromQuery] string? format = "word", CancellationToken ct = default)
    {
        var payment = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (payment is null)
        {
            return NotFound();
        }

        var normalizedFormat = NormalizeDocumentFormat(format);
        var lineItems = ExtractInvoiceItems(payment.SystemNotes);
        var documentHtml = BuildInvoiceHtml(payment, lineItems);
        var bytes = Encoding.UTF8.GetBytes(documentHtml);
        var ext = normalizedFormat == "pdf" ? "pdf" : "doc";
        var fileName = $"invoice-{SanitizeFilePart(payment.Account)}.{ext}";

        if (normalizedFormat == "pdf")
        {
            return BadRequest("PDF export is not configured on server yet. Use format=word.");
        }

        return File(bytes, "application/msword", fileName);
    }

    [HttpPost("{id:int}/send-email")]
    public async Task<IActionResult> SendInvoiceEmail(int id, [FromBody] SendInvoiceEmailRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var payment = await _db.Payments
            .AsNoTracking()
            .Include(p => p.Client)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (payment is null)
        {
            return NotFound();
        }

        var email = string.IsNullOrWhiteSpace(request.Email)
            ? payment.Client?.Email?.Trim()
            : request.Email.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Recipient email is required.");
        }

        var normalizedFormat = NormalizeDocumentFormat(request.Format);
        if (normalizedFormat == "pdf")
        {
            return BadRequest("PDF export is not configured on server yet. Use format=word.");
        }

        var lineItems = ExtractInvoiceItems(payment.SystemNotes);
        var documentHtml = BuildInvoiceHtml(payment, lineItems);
        var bytes = Encoding.UTF8.GetBytes(documentHtml);
        var fileName = $"invoice-{SanitizeFilePart(payment.Account)}.doc";
        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? $"Счёт №{payment.Account} от {payment.AccountDate?.Date:dd.MM.yyyy}"
            : request.Subject.Trim();
        var body = string.IsNullOrWhiteSpace(request.Message)
            ? "Во вложении счёт."
            : request.Message.Trim();

        await _emailService.SendEmailWithAttachmentAsync(
            email,
            subject,
            body,
            fileName,
            bytes,
            "application/msword");

        return Ok(new { sent = true, recipient = email, fileName, format = normalizedFormat });
    }

    private IQueryable<InvoiceQueryItem> BuildInvoiceQuery()
    {
        var payments = _db.Payments.AsNoTracking()
            .Where(p => p.Account != null && p.Account != "" && p.DocumentKind == DocumentKind.Invoice);

        return payments.Select(payment => new InvoiceQueryItem
        {
            Payment = payment,
            Act = _db.Acts.AsNoTracking()
                .Where(a => a.InvoiceId == payment.Id
                    || (a.InvoiceId == null && a.InvoiceNumber != null && a.InvoiceNumber == payment.Account))
                .OrderByDescending(a => a.Date)
                .ThenByDescending(a => a.Id)
                .FirstOrDefault(),
        });
    }

    private static IQueryable<InvoiceQueryItem> ApplyFilters(
        IQueryable<InvoiceQueryItem> query,
        DateTime? from,
        DateTime? to,
        PaymentStatus? status,
        int? clientId,
        int? responsibleId,
        string? search,
        PaymentType? type)
    {
        if (type.HasValue)
        {
            query = query.Where(x => x.Payment.Type == type.Value);
        }

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
                (x.Payment.CounterpartyName != null && EF.Functions.Like(x.Payment.CounterpartyName, like)) ||
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

    private async Task<ActionResult?> ValidateContractClientLinkAsync(int? contractId, int clientId, CancellationToken ct)
    {
        if (!contractId.HasValue) return null;

        var contractExists = await _db.Contracts.AsNoTracking().AnyAsync(c => c.Id == contractId.Value, ct);
        if (!contractExists) return BadRequest($"Unknown contractId {contractId.Value}");

        var linkExists = await _db.Set<ClientContract>().AsNoTracking()
            .AnyAsync(x => x.ContractId == contractId.Value && x.ClientId == clientId, ct);
        if (!linkExists) return BadRequest("Client is not linked to selected contract.");

        return null;
    }

    private static readonly Expression<Func<InvoiceQueryItem, InvoiceProjectionRaw>> ProjectionRaw = x => new InvoiceProjectionRaw
    {
        Id = x.Payment.Id,
        Number = x.Payment.Account ?? string.Empty,
        Date = x.Payment.AccountDate ?? x.Payment.Date,
        DueDate = x.Payment.Date,
        Amount = x.Payment.Amount,
        Type = x.Payment.Type,
        Status = x.Payment.Status,
        IsPaid = x.Payment.IsPaid,
        PaidDate = x.Payment.PaidDate,
        ClientId = x.Payment.ClientId,
        ContractId = x.Payment.ContractId,
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
        CounterpartyName = x.Payment.CounterpartyName,
        ServiceId = x.Payment.ServiceId,
        ServiceName = x.Payment.Service != null ? x.Payment.Service.Name : null,
        PaymentStatusName = x.Payment.PaymentStatusEntity != null ? x.Payment.PaymentStatusEntity.Name : null,
        CreatedAt = x.Payment.CreatedAt,
        DocumentKind = x.Payment.DocumentKind,
        SystemNotes = x.Payment.SystemNotes,
    };

    private static InvoiceDto MapFromRaw(InvoiceProjectionRaw raw)
    {
        return new InvoiceDto
        {
            Id = raw.Id,
            Number = raw.Number,
            Date = raw.Date,
            DueDate = raw.DueDate,
            Amount = raw.Amount,
            Type = raw.Type,
            Status = raw.Status,
            IsPaid = raw.IsPaid,
            PaidDate = raw.PaidDate,
            ClientId = raw.ClientId,
            ContractId = raw.ContractId,
            ClientName = raw.ClientName,
            ClientCompany = raw.ClientCompany,
            ClientStatusId = raw.ClientStatusId,
            ClientStatus = raw.ClientStatus,
            ClientCaseId = raw.ClientCaseId,
            ClientCaseTitle = raw.ClientCaseTitle,
            Description = raw.Description,
            ActReference = raw.ActReference,
            ActId = raw.ActId,
            ActNumber = raw.ActNumber,
            ActTitle = raw.ActTitle,
            ActStatus = raw.ActStatus,
            ResponsibleId = raw.ResponsibleId,
            ResponsibleName = raw.ResponsibleName,
            CounterpartyInn = raw.CounterpartyInn,
            CounterpartyName = raw.CounterpartyName,
            ServiceId = raw.ServiceId,
            ServiceName = raw.ServiceName,
            PaymentStatusName = raw.PaymentStatusName,
            CreatedAt = raw.CreatedAt,
            DocumentKind = raw.DocumentKind,
            LineItems = ExtractInvoiceItems(raw.SystemNotes),
        };
    }

    private static List<InvoiceLineItemDto> ExtractInvoiceItems(string? systemNotes)
    {
        if (string.IsNullOrWhiteSpace(systemNotes)) return new();

        var markerLine = systemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(InvoiceItemsMarker, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(markerLine)) return new();

        var json = markerLine[InvoiceItemsMarker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json)) return new();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<InvoiceLineItemDto>>(json);
            return parsed?
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new InvoiceLineItemDto
                {
                    Name = item.Name.Trim(),
                    Quantity = item.Quantity,
                    Unit = string.IsNullOrWhiteSpace(item.Unit) ? "шт" : item.Unit.Trim(),
                    Price = item.Price,
                })
                .ToList() ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static string MergeInvoiceItemsIntoSystemNotes(string? existingSystemNotes, List<InvoiceLineItemRequest>? lineItems)
    {
        if (lineItems is null)
        {
            return existingSystemNotes ?? string.Empty;
        }

        var baseLines = (existingSystemNotes ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith(InvoiceItemsMarker, StringComparison.Ordinal))
            .ToList();

        var normalizedItems = lineItems
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new InvoiceLineItemDto
            {
                Name = item.Name!.Trim(),
                Quantity = Math.Max(item.Quantity, 0),
                Unit = string.IsNullOrWhiteSpace(item.Unit) ? "шт" : item.Unit.Trim(),
                Price = Math.Max(item.Price, 0),
            })
            .ToList();

        if (normalizedItems.Count > 0)
        {
            var json = JsonSerializer.Serialize(normalizedItems);
            baseLines.Add($"{InvoiceItemsMarker} {json}");
        }

        return string.Join("\n", baseLines);
    }


    private async Task<Dictionary<int, List<InvoiceResponsibleDto>>> LoadResponsiblesMapAsync(IEnumerable<int> paymentIds, CancellationToken ct)
    {
        var ids = paymentIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, List<InvoiceResponsibleDto>>();
        }

        var rows = await _db.PaymentResponsibles
            .AsNoTracking()
            .Include(pr => pr.User)
            .Where(pr => ids.Contains(pr.PaymentId))
            .OrderBy(pr => pr.User!.LastName)
            .ThenBy(pr => pr.User!.FirstName)
            .ThenBy(pr => pr.User!.MiddleName)
            .ThenBy(pr => pr.User!.FullName)
            .Select(pr => new
            {
                pr.PaymentId,
                pr.UserId,
                FullName = string.IsNullOrWhiteSpace(pr.User!.LastName)
                    ? (string.IsNullOrWhiteSpace(pr.User.FullName) ? pr.User.Email : pr.User.FullName)
                    : ($"{pr.User.LastName} {((string.IsNullOrWhiteSpace(pr.User.FirstName) ? string.Empty : pr.User.FirstName[0] + "."))}{((string.IsNullOrWhiteSpace(pr.User.MiddleName) ? string.Empty : pr.User.MiddleName[0] + "."))}").Trim(),
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.PaymentId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new InvoiceResponsibleDto
                {
                    Id = x.UserId,
                    FullName = x.FullName,
                }).ToList());
    }

    private async Task<bool> EnsureResponsibleIdsExistAsync(HashSet<int> responsibleIds, CancellationToken ct)
    {
        if (responsibleIds.Count == 0)
        {
            return true;
        }

        var existingCount = await _db.Users
            .AsNoTracking()
            .CountAsync(u => responsibleIds.Contains(u.Id), ct);

        return existingCount == responsibleIds.Count;
    }

    private static HashSet<int> NormalizeResponsibleIds(List<int>? responsibleIds)
    {
        return responsibleIds is null
            ? new HashSet<int>()
            : responsibleIds.Where(x => x > 0).ToHashSet();
    }

    private static string NormalizeDocumentFormat(string? format)
    {
        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "pdf";
        }

        return "word";
    }

    private static string BuildInvoiceHtml(Payment payment, IReadOnlyCollection<InvoiceLineItemDto> lineItems)
    {
        var number = WebUtility.HtmlEncode(payment.Account ?? "—");
        var date = (payment.AccountDate ?? payment.Date).ToString("dd.MM.yyyy");
        var counterparty = WebUtility.HtmlEncode(payment.CounterpartyName ?? "—");
        var clientName = WebUtility.HtmlEncode(payment.Client?.Name ?? "—");
        var amount = payment.Amount.ToString("0.00");

        var rowsBuilder = new StringBuilder();
        var index = 1;
        foreach (var item in lineItems)
        {
            var total = item.Quantity * item.Price;
            rowsBuilder.Append("<tr>");
            rowsBuilder.Append($"<td>{index}</td>");
            rowsBuilder.Append($"<td>{WebUtility.HtmlEncode(item.Name)}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{item.Quantity:0.##}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{WebUtility.HtmlEncode(item.Unit)}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{item.Price:0.00}</td>");
            rowsBuilder.Append($"<td style='text-align:right'>{total:0.00}</td>");
            rowsBuilder.Append("</tr>");
            index++;
        }

        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"ru\">");
        html.AppendLine("<head>");
        html.AppendLine("  <meta charset=\"utf-8\" />");
        html.AppendLine($"  <title>Счёт № {number}</title>");
        html.AppendLine("  <style>");
        html.AppendLine("    body { font-family: Arial, sans-serif; color: #111827; margin: 24px; }");
        html.AppendLine("    table { border-collapse: collapse; width: 100%; margin-top: 12px; }");
        html.AppendLine("    th, td { border: 1px solid #1f2937; padding: 6px 8px; font-size: 12px; }");
        html.AppendLine("    .num { text-align: right; }");
        html.AppendLine("  </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine($"  <h1>Счёт № {number} от {date}</h1>");
        html.AppendLine($"  <p><b>От кого:</b> {counterparty}</p>");
        html.AppendLine($"  <p><b>Кому:</b> {clientName}</p>");
        html.AppendLine("  <table>");
        html.AppendLine("    <thead>");
        html.AppendLine("      <tr><th>№</th><th>Товары (работы, услуги)</th><th class=\"num\">Кол-во</th><th class=\"num\">Ед.</th><th class=\"num\">Цена</th><th class=\"num\">Сумма</th></tr>");
        html.AppendLine("    </thead>");
        html.AppendLine("    <tbody>");
        html.AppendLine(rowsBuilder.ToString());
        html.AppendLine("    </tbody>");
        html.AppendLine("  </table>");
        html.AppendLine($"  <p><b>Итого:</b> {amount}</p>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    private static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "export";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '-');
        }

        return value.Trim();
    }

    private sealed class InvoiceProjectionRaw
    {
        public int Id { get; init; }
        public string Number { get; init; } = string.Empty;
        public DateTime Date { get; init; }
        public DateTime? DueDate { get; init; }
        public decimal Amount { get; init; }
        public PaymentType Type { get; init; }
        public PaymentStatus Status { get; init; }
        public bool IsPaid { get; init; }
        public DateTime? PaidDate { get; init; }
        public int? ClientId { get; init; }
        public int? ContractId { get; init; }
        public string? ClientName { get; init; }
        public string? ClientCompany { get; init; }
        public int? ClientStatusId { get; init; }
        public ClientStatusDto? ClientStatus { get; init; }
        public int? ClientCaseId { get; init; }
        public string? ClientCaseTitle { get; init; }
        public string? Description { get; init; }
        public string? ActReference { get; init; }
        public int? ActId { get; init; }
        public string? ActNumber { get; init; }
        public string? ActTitle { get; init; }
        public ActStatus? ActStatus { get; init; }
        public int? ResponsibleId { get; init; }
        public string? ResponsibleName { get; init; }
        public string? CounterpartyInn { get; init; }
        public string? CounterpartyName { get; init; }
        public int? ServiceId { get; init; }
        public string? ServiceName { get; init; }
        public string? PaymentStatusName { get; init; }
        public DateTime CreatedAt { get; init; }
        public DocumentKind DocumentKind { get; init; }
        public string? SystemNotes { get; init; }
    }

    private sealed class InvoiceQueryItem
    {
        public Payment Payment { get; init; } = null!;
        public Act? Act { get; init; }
    }
}
