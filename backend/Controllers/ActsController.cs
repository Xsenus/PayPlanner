using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Acts;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ActsController : ControllerBase
{
    private const string LegacyMainCompanySettingKey = "legalEntities.mainCompanyId";
    private const string MainCompanyIdsSettingKey = "legalEntities.mainCompanyIds";
    private const string DefaultMainCompanyIdSettingKey = "legalEntities.defaultMainCompanyId";
    private readonly PaymentContext _db;
    private readonly IFormRequirementsService _formRequirements;
    private readonly IInvoiceDocumentPdfService? _invoiceDocumentPdfService;

    public ActsController(
        PaymentContext db,
        IFormRequirementsService formRequirements,
        IInvoiceDocumentPdfService? invoiceDocumentPdfService = null)
    {
        _db = db;
        _formRequirements = formRequirements;
        _invoiceDocumentPdfService = invoiceDocumentPdfService;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ActStatus? status,
        [FromQuery] PaymentType? type,
        [FromQuery] int? clientId,
        [FromQuery] int? contractId,
        [FromQuery] string? clientIds,
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

        var query = _db.Acts.AsNoTracking()
            .ApplyActFilters(from, to, status, type, clientId, responsibleId, search)
            .ApplyActSort(sortBy, sortDir);

        var clientIdList = ParseIntList(clientIds);
        if (clientIdList.Count > 0)
        {
            query = query.Where(act => act.ClientId.HasValue && clientIdList.Contains(act.ClientId.Value));
        }

        if (contractId.HasValue)
        {
            query = query.Where(act =>
                _db.ContractActs.Any(link => link.ContractId == contractId.Value && link.ActId == act.Id)
                || (act.InvoiceId.HasValue && (
                    _db.ContractInvoices.Any(link => link.ContractId == contractId.Value && link.InvoiceId == act.InvoiceId.Value)
                    || _db.Payments.Any(payment => payment.Id == act.InvoiceId.Value && payment.ContractId == contractId.Value))));
        }

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(MapToDtoExpression)
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
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

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ActDto>> GetById(int id, CancellationToken ct)
    {
        var dto = await _db.Acts.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(MapToDtoExpression)
            .FirstOrDefaultAsync(ct);

        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ActStatus? status,
        [FromQuery] PaymentType? type,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        CancellationToken ct = default)
    {
        var query = _db.Acts.AsNoTracking()
            .ApplyActFilters(from, to, status, type, clientId, responsibleId, search);

        var groups = await query
            .GroupBy(a => a.Status)
            .Select(g => new
            {
                Status = g.Key,
                Amount = g.Sum(x => x.Amount),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        decimal createdAmount = 0m, transferredAmount = 0m, signedAmount = 0m, terminatedAmount = 0m;
        int createdCount = 0, transferredCount = 0, signedCount = 0, terminatedCount = 0;

        foreach (var g in groups)
        {
            switch (g.Status)
            {
                case ActStatus.Created:
                    createdAmount = g.Amount;
                    createdCount = g.Count;
                    break;
                case ActStatus.Transferred:
                    transferredAmount = g.Amount;
                    transferredCount = g.Count;
                    break;
                case ActStatus.Signed:
                    signedAmount = g.Amount;
                    signedCount = g.Count;
                    break;
                case ActStatus.Terminated:
                    terminatedAmount = g.Amount;
                    terminatedCount = g.Count;
                    break;
            }
        }

        var response = new
        {
            created = new { amount = createdAmount, count = createdCount },
            transferred = new { amount = transferredAmount, count = transferredCount },
            signed = new { amount = signedAmount, count = signedCount },
            terminated = new { amount = terminatedAmount, count = terminatedCount },
            totalAmount = groups.Sum(g => g.Amount),
            totalCount = groups.Sum(g => g.Count),
        };

        return Ok(response);
    }

    [HttpGet("responsibles")]
    public async Task<ActionResult<IEnumerable<ActResponsibleDto>>> GetResponsibles(CancellationToken ct = default)
    {
        var items = await _db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.IsApproved)
            .OrderBy(u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName)
            .Select(u => new ActResponsibleDto
            {
                Id = u.Id,
                FullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<ActDto>> Create([FromBody] UpsertActRequest request, CancellationToken ct)
    {
        var missingCreate = await _formRequirements.GetMissingFieldsAsync("acts", request, ct);
        if (missingCreate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingCreate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        Payment? invoice = null;
        if (request.InvoiceId.HasValue)
        {
            invoice = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.InvoiceId.Value, ct);
            if (invoice is null) return BadRequest($"Unknown invoiceId {request.InvoiceId.Value}");
        }

        var resolvedLineItems = await ResolveActLineItemsAsync(invoice, request, null, ct);
        if (resolvedLineItems.Error is not null)
        {
            return resolvedLineItems.Error;
        }

        var act = new Act
        {
            Number = request.Number.Trim(),
            Title = Normalize(request.Title),
            Date = request.Date.Date,
            Amount = resolvedLineItems.Amount,
            CreationMode = resolvedLineItems.CreationMode,
            InvoiceNumber = Normalize(request.InvoiceNumber) ?? invoice?.Account,
            InvoiceId = request.InvoiceId,
            CounterpartyInn = Normalize(request.CounterpartyInn),
            Status = request.Status,
            ClientId = request.ClientId,
            ResponsibleId = request.ResponsibleId,
            Comment = Normalize(request.Comment),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Acts.Add(act);
        SyncActLineItems(act, resolvedLineItems.LineItems);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(act).Reference(a => a.Client).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Responsible).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Invoice).LoadAsync(ct);
        await _db.Entry(act).Collection(a => a.LineItems).LoadAsync(ct);

        return Created($"/api/acts/{act.Id}", Map(act));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ActDto>> Update(int id, [FromBody] UpsertActRequest request, CancellationToken ct)
    {
        var missingUpdate = await _formRequirements.GetMissingFieldsAsync("acts", request, ct);
        if (missingUpdate.Count > 0) return BadRequest(new { message = "Не заполнены обязательные поля", missingFields = missingUpdate });

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var act = await _db.Acts
            .Include(a => a.LineItems)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (act is null)
        {
            return NotFound();
        }

        Payment? invoice = null;
        if (request.InvoiceId.HasValue)
        {
            invoice = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.InvoiceId.Value, ct);
            if (invoice is null) return BadRequest($"Unknown invoiceId {request.InvoiceId.Value}");
        }

        var resolvedLineItems = await ResolveActLineItemsAsync(invoice, request, act, ct);
        if (resolvedLineItems.Error is not null)
        {
            return resolvedLineItems.Error;
        }

        act.Number = request.Number.Trim();
        act.Title = Normalize(request.Title);
        act.Date = request.Date.Date;
        act.Amount = resolvedLineItems.Amount;
        act.CreationMode = resolvedLineItems.CreationMode;
        act.InvoiceNumber = Normalize(request.InvoiceNumber) ?? invoice?.Account;
        act.InvoiceId = request.InvoiceId;
        act.CounterpartyInn = Normalize(request.CounterpartyInn);
        act.Status = request.Status;
        act.ClientId = request.ClientId;
        act.ResponsibleId = request.ResponsibleId;
        act.Comment = Normalize(request.Comment);
        act.UpdatedAt = DateTime.UtcNow;
        SyncActLineItems(act, resolvedLineItems.LineItems);

        await _db.SaveChangesAsync(ct);

        await _db.Entry(act).Reference(a => a.Client).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Responsible).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Invoice).LoadAsync(ct);
        await _db.Entry(act).Collection(a => a.LineItems).LoadAsync(ct);

        return Ok(Map(act));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var act = await _db.Acts
            .Include(item => item.LineItems)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (act is null)
        {
            return NotFound();
        }

        if (act.LineItems.Count > 0)
        {
            _db.RemoveRange(act.LineItems);
        }

        _db.Acts.Remove(act);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:int}/document")]
    public async Task<IActionResult> GetDocument(
        int id,
        [FromQuery] string? format = "word",
        [FromQuery] bool? includeSignatureAndSeal = null,
        CancellationToken ct = default)
    {
        var act = await LoadActDocumentAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        var normalizedFormat = NormalizeDocumentFormat(format);
        var mainCompany = await LoadMainCompanyAsync(ct);
        var documentHtml = ActDocumentTemplateEngine.RenderHtml(
            act,
            act.LineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToList(),
            mainCompany,
            includeSignatureAndSeal ?? true);

        if (normalizedFormat == "html")
        {
            return Content(documentHtml, "text/html; charset=utf-8");
        }

        var ext = normalizedFormat == "pdf" ? "pdf" : "doc";
        var fileName = $"act-{SanitizeFilePart(act.Number)}.{ext}";
        if (normalizedFormat == "pdf")
        {
            try
            {
                var pdfBytes = await RenderDocumentPdfAsync(documentHtml, ct);
                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }
        }

        return File(Encoding.UTF8.GetBytes(documentHtml), "application/msword", fileName);
    }

    private static readonly System.Linq.Expressions.Expression<Func<Act, ActDto>> MapToDtoExpression = act => new ActDto
    {
        Id = act.Id,
        Number = act.Number,
        Title = act.Title,
        Date = act.Date,
        Amount = act.Amount,
        CreationMode = act.CreationMode,
        InvoiceNumber = act.InvoiceNumber,
        InvoiceId = act.InvoiceId,
        Type = act.Invoice != null ? act.Invoice.Type : null,
        ContractId = act.Invoice != null ? act.Invoice.ContractId : null,
        CounterpartyInn = act.CounterpartyInn,
        Status = act.Status,
        ClientId = act.ClientId,
        ClientName = act.Client != null ? act.Client.Name : null,
        ResponsibleId = act.ResponsibleId,
        ResponsibleName = act.Responsible != null ? act.Responsible.FullName : null,
        Comment = act.Comment,
        LineItems = act.LineItems
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new ActLineItemDto
            {
                Id = item.Id,
                InvoiceLineItemId = item.InvoiceLineItemId,
                SourceKey = item.SourceKey,
                SourceSortOrder = item.SourceSortOrder,
                Name = item.Name,
                Quantity = item.Quantity,
                Unit = item.Unit,
                Price = item.Price,
            })
            .ToList(),
        CreatedAt = act.CreatedAt,
        UpdatedAt = act.UpdatedAt,
        AuditCreatedByUserId = EF.Property<int?>(act, "CreatedByUserId"),
        AuditCreatedByUserName = EF.Property<string?>(act, "CreatedByUserName"),
        AuditUpdatedAt = EF.Property<DateTime?>(act, "UpdatedAt"),
        AuditUpdatedByUserId = EF.Property<int?>(act, "UpdatedByUserId"),
        AuditUpdatedByUserName = EF.Property<string?>(act, "UpdatedByUserName"),
        AuditIsDeleted = EF.Property<bool>(act, "IsDeleted"),
        AuditDeletedAt = EF.Property<DateTime?>(act, "DeletedAt"),
        AuditDeletedByUserId = EF.Property<int?>(act, "DeletedByUserId"),
        AuditDeletedByUserName = EF.Property<string?>(act, "DeletedByUserName"),
    };

    private async Task<Act?> LoadActDocumentAsync(int id, CancellationToken ct)
    {
        return await _db.Acts
            .AsNoTracking()
            .Include(act => act.Client)
                .ThenInclude(client => client!.Addresses)
            .Include(act => act.Client)
                .ThenInclude(client => client!.Passports)
            .Include(act => act.Responsible)
            .Include(act => act.LineItems)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.Client)
                    .ThenInclude(client => client!.Addresses)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.Client)
                    .ThenInclude(client => client!.Passports)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.ExpenseClient)
                    .ThenInclude(client => client!.Addresses)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.ExpenseClient)
                    .ThenInclude(client => client!.Passports)
            .FirstOrDefaultAsync(act => act.Id == id, ct);
    }

    private async Task<(ActionResult? Error, string CreationMode, decimal Amount, List<ActLineItemDraft> LineItems)> ResolveActLineItemsAsync(
        Payment? invoice,
        UpsertActRequest request,
        Act? currentAct,
        CancellationToken ct)
    {
        if (!ActCreationMode.IsValid(request.CreationMode))
        {
            return (ValidationError(nameof(request.CreationMode), "Некорректный режим создания акта."), ActCreationMode.Manual, 0m, new List<ActLineItemDraft>());
        }

        if (invoice is null)
        {
            if (request.LineItems.Count > 0)
            {
                return (BadRequest("Строки акта можно выбрать только для акта, привязанного к счету."), ActCreationMode.Manual, 0m, new List<ActLineItemDraft>());
            }

            return (null, ActCreationMode.Manual, request.Amount, new List<ActLineItemDraft>());
        }

        var sourceItems = await LoadInvoiceSourceLineItemsAsync(invoice.Id, invoice.SystemNotes, ct);
        if (sourceItems.Count == 0)
        {
            return (null, ActCreationMode.Manual, request.Amount, new List<ActLineItemDraft>());
        }

        var creationMode = ActCreationMode.Normalize(request.CreationMode);
        if (currentAct is not null
            && creationMode == ActCreationMode.Manual
            && request.LineItems.Count == 0
            && currentAct.LineItems.Count > 0)
        {
            return (
                null,
                currentAct.CreationMode,
                currentAct.Amount,
                currentAct.LineItems
                    .OrderBy(item => item.SortOrder)
                    .ThenBy(item => item.Id)
                    .Select(item => new ActLineItemDraft(
                        item.InvoiceLineItemId,
                        item.SourceKey,
                        item.SourceSortOrder,
                        item.Name,
                        item.Quantity,
                        item.Unit,
                        item.Price))
                    .ToList());
        }

        if (creationMode == ActCreationMode.Manual)
        {
            creationMode = request.LineItems.Count > 0 ? ActCreationMode.Partial : ActCreationMode.Full;
        }

        var usedQuantities = await _db.Set<ActLineItem>()
            .AsNoTracking()
            .Where(item => item.Act.InvoiceId == invoice.Id
                && item.Act.Status != ActStatus.Terminated
                && (currentAct == null || item.ActId != currentAct.Id))
            .GroupBy(item => item.SourceKey)
            .Select(group => new
            {
                SourceKey = group.Key,
                Quantity = group.Sum(item => item.Quantity),
            })
            .ToDictionaryAsync(item => item.SourceKey, item => item.Quantity, ct);

        var sourceByKey = sourceItems.ToDictionary(item => item.SourceKey, StringComparer.OrdinalIgnoreCase);
        var remainingItems = sourceItems
            .Select(item =>
            {
                var used = usedQuantities.GetValueOrDefault(item.SourceKey);
                var remaining = Math.Max(0m, item.Quantity - used);
                return new
                {
                    Item = item,
                    Remaining = remaining,
                };
            })
            .Where(item => item.Remaining > 0)
            .ToDictionary(item => item.Item.SourceKey, item => item, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<ActLineItemDraft>();
        if (creationMode == ActCreationMode.Full)
        {
            foreach (var remaining in remainingItems.Values.OrderBy(item => item.Item.SortOrder ?? int.MaxValue))
            {
                resolved.Add(new ActLineItemDraft(
                    remaining.Item.Id,
                    remaining.Item.SourceKey,
                    remaining.Item.SortOrder,
                    remaining.Item.Name,
                    remaining.Remaining,
                    remaining.Item.Unit,
                    remaining.Item.Price));
            }
        }
        else
        {
            var requestedItems = request.LineItems
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.SourceKey) && item.Quantity > 0)
                .GroupBy(item => item.SourceKey.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    SourceKey = group.Key,
                    Quantity = group.Sum(item => item.Quantity),
                })
                .ToList();

            if (requestedItems.Count == 0)
            {
                return (BadRequest("Для частичного акта выберите хотя бы одну услугу из счета."), creationMode, 0m, new List<ActLineItemDraft>());
            }

            foreach (var requested in requestedItems)
            {
                if (!remainingItems.TryGetValue(requested.SourceKey, out var remaining))
                {
                    return (BadRequest($"Позиция счета недоступна для актирования: {requested.SourceKey}."), creationMode, 0m, new List<ActLineItemDraft>());
                }

                if (requested.Quantity > remaining.Remaining)
                {
                    return (BadRequest($"Нельзя актировать больше остатка по позиции \"{remaining.Item.Name}\"."), creationMode, 0m, new List<ActLineItemDraft>());
                }

                resolved.Add(new ActLineItemDraft(
                    remaining.Item.Id,
                    sourceByKey[requested.SourceKey].SourceKey,
                    remaining.Item.SortOrder,
                    remaining.Item.Name,
                    requested.Quantity,
                    remaining.Item.Unit,
                    remaining.Item.Price));
            }
        }

        if (resolved.Count == 0)
        {
            return (BadRequest("По счету не осталось услуг, доступных для выставления акта."), creationMode, 0m, new List<ActLineItemDraft>());
        }

        var amount = resolved.Sum(item => item.Quantity * item.Price);
        return (null, creationMode, amount, resolved);
    }

    private async Task<List<InvoiceLineItemDto>> LoadInvoiceSourceLineItemsAsync(int invoicePaymentId, string? invoiceSystemNotes, CancellationToken ct)
    {
        var storedItems = await _db.InvoiceLineItems
            .AsNoTracking()
            .Where(item => item.PaymentId == invoicePaymentId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new InvoiceLineItemDto
            {
                Id = item.Id,
                SortOrder = item.SortOrder,
                SourceKey = InvoiceLineItemStorage.BuildSourceKey(item.Id, item.SortOrder),
                ServiceId = item.ServiceId,
                Name = item.Name,
                Quantity = item.Quantity,
                Unit = item.Unit,
                Price = item.Price,
            })
            .ToListAsync(ct);

        return storedItems.Count > 0
            ? storedItems
            : InvoiceLineItemStorage.ExtractFromSystemNotes(invoiceSystemNotes);
    }

    private static void SyncActLineItems(Act act, IReadOnlyCollection<ActLineItemDraft> lineItems)
    {
        foreach (var existing in act.LineItems.ToList())
        {
            act.LineItems.Remove(existing);
        }

        var normalized = lineItems
            .Where(item => item.Quantity > 0)
            .OrderBy(item => item.SourceSortOrder ?? int.MaxValue)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var index = 0; index < normalized.Count; index++)
        {
            var item = normalized[index];
            act.LineItems.Add(new ActLineItem
            {
                SortOrder = index,
                InvoiceLineItemId = item.InvoiceLineItemId,
                SourceKey = item.SourceKey,
                SourceSortOrder = item.SourceSortOrder,
                Name = item.Name,
                Quantity = item.Quantity,
                Unit = item.Unit,
                Price = item.Price,
                CreatedAt = DateTime.UtcNow,
            });
        }
    }

    private Task<byte[]> RenderDocumentPdfAsync(string documentHtml, CancellationToken ct)
    {
        if (_invoiceDocumentPdfService is null)
        {
            throw new InvalidOperationException(
                "PDF-генератор не зарегистрирован на сервере. Проверьте настройки приложения.");
        }

        return _invoiceDocumentPdfService.RenderAsync(documentHtml, ct);
    }

    private async Task<LegalEntity?> LoadMainCompanyAsync(CancellationToken ct)
    {
        var settings = await _db.SystemSettings
            .AsNoTracking()
            .Where(setting =>
                setting.Key == LegacyMainCompanySettingKey
                || setting.Key == MainCompanyIdsSettingKey
                || setting.Key == DefaultMainCompanyIdSettingKey)
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, ct);

        int? companyId = null;
        if (settings.TryGetValue(DefaultMainCompanyIdSettingKey, out var rawDefaultId)
            && int.TryParse(rawDefaultId, out var parsedDefaultId)
            && parsedDefaultId > 0)
        {
            companyId = parsedDefaultId;
        }
        else if (settings.TryGetValue(LegacyMainCompanySettingKey, out var rawLegacyId)
            && int.TryParse(rawLegacyId, out var parsedLegacyId)
            && parsedLegacyId > 0)
        {
            companyId = parsedLegacyId;
        }
        else if (settings.TryGetValue(MainCompanyIdsSettingKey, out var rawIds)
            && !string.IsNullOrWhiteSpace(rawIds))
        {
            try
            {
                var parsedIds = JsonSerializer.Deserialize<List<int>>(rawIds);
                companyId = parsedIds?.FirstOrDefault(value => value > 0);
            }
            catch (JsonException)
            {
                companyId = null;
            }
        }

        if (!companyId.HasValue || companyId.Value <= 0)
        {
            return null;
        }

        return await _db.LegalEntities
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == companyId.Value, ct);
    }

    private static string NormalizeDocumentFormat(string? format)
    {
        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            return "html";
        }

        if (string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "pdf";
        }

        return "word";
    }

    private static string SanitizeFilePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "export";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return value.Trim();
    }

    private ActionResult ValidationError(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private ActDto Map(Act act)
    {
        var audit = _db.ReadAuditSnapshot(act);
        return new ActDto
        {
            Id = act.Id,
            Number = act.Number,
            Title = act.Title,
            Date = act.Date,
            Amount = act.Amount,
            CreationMode = act.CreationMode,
            InvoiceNumber = act.InvoiceNumber,
            InvoiceId = act.InvoiceId,
            Type = act.Invoice?.Type,
            ContractId = act.Invoice?.ContractId,
            CounterpartyInn = act.CounterpartyInn,
            Status = act.Status,
            ClientId = act.ClientId,
            ClientName = act.Client?.Name,
            ResponsibleId = act.ResponsibleId,
            ResponsibleName = act.Responsible?.FullName,
            Comment = act.Comment,
            LineItems = act.LineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item => new ActLineItemDto
                {
                    Id = item.Id,
                    InvoiceLineItemId = item.InvoiceLineItemId,
                    SourceKey = item.SourceKey,
                    SourceSortOrder = item.SourceSortOrder,
                    Name = item.Name,
                    Quantity = item.Quantity,
                    Unit = item.Unit,
                    Price = item.Price,
                })
                .ToList(),
            CreatedAt = act.CreatedAt,
            UpdatedAt = act.UpdatedAt,
            AuditCreatedByUserId = audit?.CreatedByUserId,
            AuditCreatedByUserName = audit?.CreatedByUserName,
            AuditUpdatedByUserId = audit?.UpdatedByUserId,
            AuditUpdatedByUserName = audit?.UpdatedByUserName,
            AuditIsDeleted = audit?.IsDeleted ?? false,
            AuditDeletedAt = audit?.DeletedAt,
            AuditDeletedByUserId = audit?.DeletedByUserId,
            AuditDeletedByUserName = audit?.DeletedByUserName,
        };
    }

    private sealed record ActLineItemDraft(
        int? InvoiceLineItemId,
        string SourceKey,
        int? SourceSortOrder,
        string Name,
        decimal Quantity,
        string Unit,
        decimal Price);
}
