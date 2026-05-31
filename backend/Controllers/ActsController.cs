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
    private readonly IEmailService? _emailService;
    private readonly IYandexDiskService? _yandexDiskService;

    public ActsController(
        PaymentContext db,
        IFormRequirementsService formRequirements,
        IInvoiceDocumentPdfService? invoiceDocumentPdfService = null,
        IEmailService? emailService = null,
        IYandexDiskService? yandexDiskService = null)
    {
        _db = db;
        _formRequirements = formRequirements;
        _invoiceDocumentPdfService = invoiceDocumentPdfService;
        _emailService = emailService;
        _yandexDiskService = yandexDiskService;
    }

    private Task<DataVisibilityAccess?> ResolveActsVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Acts, ct);

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
        CancellationToken ct = default,
        [FromQuery] bool includeLineItems = false,
        [FromQuery] string? workflowStatus = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] int? caseId = null,
        [FromQuery] int? invoiceId = null,
        [FromQuery] int? paymentId = null)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);
        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var normalizedWorkflowStatus = NormalizeActWorkflowStatusOrNull(workflowStatus);
        if (!string.IsNullOrWhiteSpace(workflowStatus) && normalizedWorkflowStatus is null)
        {
            return ValidationError(nameof(workflowStatus), "Unknown act workflow status.");
        }

        var query = _db.Acts.AsNoTracking()
            .ApplyActVisibility(visibility)
            .ApplyActFilters(from, to, status, type, clientId, responsibleId, search)
            .ApplyActSort(sortBy, sortDir);

        if (legalEntityId.HasValue)
        {
            query = query.Where(act => act.LegalEntityId == legalEntityId.Value);
        }

        if (caseId.HasValue)
        {
            query = query.Where(act =>
                act.InvoiceId.HasValue
                && _db.Payments.Any(invoice =>
                    invoice.Id == act.InvoiceId.Value
                    && (invoice.ClientCaseId == caseId.Value
                        || invoice.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value))));
        }

        if (normalizedWorkflowStatus is not null)
        {
            query = query.Where(act => act.WorkflowStatus == normalizedWorkflowStatus);
        }

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

        if (invoiceId.HasValue)
        {
            query = query.Where(act =>
                act.InvoiceId == invoiceId.Value
                || (!act.InvoiceId.HasValue
                    && act.InvoiceNumber != null
                    && _db.Payments.Any(invoice =>
                        invoice.Id == invoiceId.Value
                        && invoice.Account == act.InvoiceNumber)));
        }

        if (paymentId.HasValue)
        {
            query = query.Where(act =>
                act.InvoiceId.HasValue
                && _db.Payments.Any(payment =>
                    payment.Id == paymentId.Value
                    && (
                        payment.Id == act.InvoiceId.Value
                        || (payment.InvoiceId.HasValue
                            && _db.Payments.Any(invoice =>
                                invoice.Id == act.InvoiceId.Value
                                && invoice.InvoiceId == payment.InvoiceId.Value)))));
        }

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(includeLineItems ? MapToDtoExpression : MapToListDtoExpression)
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

    private static CounterpartyOwner ResolveActOwner(UpsertActRequest request, Payment? invoice)
    {
        var requestClientId = request.ClientId is > 0 ? request.ClientId : null;
        var requestLegalEntityId = request.LegalEntityId is > 0 ? request.LegalEntityId : null;
        if (requestClientId.HasValue || requestLegalEntityId.HasValue)
        {
            return new CounterpartyOwner(requestClientId, requestLegalEntityId);
        }

        return new CounterpartyOwner(
            invoice?.ClientId is > 0 ? invoice.ClientId : null,
            invoice?.LegalEntityId is > 0 ? invoice.LegalEntityId : null);
    }

    private async Task<ActionResult?> ValidateActOwnerAsync(int? clientId, int? legalEntityId, CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("Act must reference either clientId or legalEntityId.");
        }

        if (hasClient && !await _db.Clients.AsNoTracking().AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities.AsNoTracking().AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }

    private sealed record CounterpartyOwner(int? ClientId, int? LegalEntityId);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ActDto>> GetById(int id, CancellationToken ct)
    {
        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var dto = await _db.Acts.AsNoTracking()
            .ApplyActVisibility(visibility)
            .Where(a => a.Id == id)
            .Select(MapToDtoExpression)
            .FirstOrDefaultAsync(ct);

        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost("{id:int}/send-to-signing")]
    public async Task<ActionResult<ActDto>> SendToSigning(int id, CancellationToken ct)
    {
        var act = await LoadActForWorkflowAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        act.Status = ActStatus.Transferred;
        act.WorkflowStatus = ActWorkflowStatus.OnSigning;
        act.SentForSigningAt ??= DateTime.UtcNow;
        act.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Map(act));
    }

    [HttpPost("{id:int}/mark-signed")]
    public async Task<ActionResult<ActDto>> MarkSigned(int id, CancellationToken ct)
    {
        var act = await LoadActForWorkflowAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        act.Status = ActStatus.Signed;
        act.WorkflowStatus = ActWorkflowStatus.Signed;
        act.SentForSigningAt ??= now;
        act.SignedAt ??= now;
        act.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return Ok(Map(act));
    }

    [HttpPost("{id:int}/mark-not-signed")]
    public async Task<ActionResult<ActDto>> MarkNotSigned(int id, CancellationToken ct)
    {
        var act = await LoadActForWorkflowAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        act.Status = ActStatus.Created;
        act.WorkflowStatus = ActWorkflowStatus.NotSigned;
        act.SentForSigningAt = null;
        act.SignedAt = null;
        act.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(Map(act));
    }

    [HttpPost("{id:int}/workflow-status")]
    public async Task<ActionResult<ActDto>> UpdateWorkflowStatus(int id, [FromBody] UpdateNamedStatusRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var workflowStatus = NormalizeActWorkflowStatusOrNull(request.Status);
        if (workflowStatus is null)
        {
            return ValidationError(nameof(request.Status), "Unknown act workflow status.");
        }

        var act = await LoadActForWorkflowAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        var reason = Normalize(request.Reason);
        var statusChanged = !string.Equals(act.WorkflowStatus, workflowStatus, StringComparison.OrdinalIgnoreCase);
        if (statusChanged)
        {
            var reasonError = await ValidateFinancialActionReasonAsync(reason, "изменения статуса акта", ct);
            if (reasonError is not null)
            {
                return reasonError;
            }

            ApplyAuditReason(reason);
        }

        var now = DateTime.UtcNow;
        act.WorkflowStatus = workflowStatus;
        act.UpdatedAt = now;

        switch (workflowStatus)
        {
            case ActWorkflowStatus.Draft:
            case ActWorkflowStatus.NotSigned:
            case ActWorkflowStatus.RequiresClientSignature:
            case ActWorkflowStatus.OnApproval:
            case ActWorkflowStatus.Approved:
            case ActWorkflowStatus.ApprovalRejected:
            case ActWorkflowStatus.NeedsCorrection:
                act.Status = ActStatus.Created;
                act.SentForSigningAt = null;
                act.SignedAt = null;
                act.CancelledAt = null;
                act.CancelledByUserId = null;
                act.CancelledReason = null;
                break;
            case ActWorkflowStatus.OnSigning:
                act.Status = ActStatus.Transferred;
                act.SentForSigningAt ??= now;
                act.SignedAt = null;
                act.CancelledAt = null;
                act.CancelledByUserId = null;
                act.CancelledReason = null;
                break;
            case ActWorkflowStatus.Signed:
                act.Status = ActStatus.Signed;
                act.SentForSigningAt ??= now;
                act.SignedAt ??= now;
                act.CancelledAt = null;
                act.CancelledByUserId = null;
                act.CancelledReason = null;
                break;
            case ActWorkflowStatus.Cancelled:
                act.Status = ActStatus.Terminated;
                act.CancelledAt ??= now;
                act.CancelledByUserId ??= User.GetUserId();
                act.CancelledReason ??= reason ?? "Быстрая смена статуса документа";
                break;
        }

        await _db.SaveChangesAsync(ct);
        return Ok(Map(act));
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<ActDto>> Cancel(int id, [FromBody] CancelActRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var reason = Normalize(request.Reason);
        if (string.IsNullOrWhiteSpace(reason))
        {
            return BadRequest("Cancellation reason is required.");
        }
        ApplyAuditReason(reason);

        var act = await LoadActForWorkflowAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        act.Status = ActStatus.Terminated;
        act.WorkflowStatus = ActWorkflowStatus.Cancelled;
        act.CancelledAt ??= now;
        act.CancelledByUserId ??= User.GetUserId();
        act.CancelledReason = reason;
        act.Comment = AppendCancellationComment(act.Comment, reason, now);
        act.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return Ok(Map(act));
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ActStatus? status,
        [FromQuery] PaymentType? type,
        [FromQuery] int? clientId,
        [FromQuery] int? contractId,
        [FromQuery] int? responsibleId,
        CancellationToken ct = default,
        [FromQuery] string? search = null,
        [FromQuery] string? workflowStatus = null,
        [FromQuery] int? legalEntityId = null,
        [FromQuery] int? caseId = null,
        [FromQuery] int? invoiceId = null,
        [FromQuery] int? paymentId = null)
    {
        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var normalizedWorkflowStatus = NormalizeActWorkflowStatusOrNull(workflowStatus);
        if (!string.IsNullOrWhiteSpace(workflowStatus) && normalizedWorkflowStatus is null)
        {
            return ValidationError(nameof(workflowStatus), "Unknown act workflow status.");
        }

        var query = _db.Acts.AsNoTracking()
            .ApplyActVisibility(visibility)
            .ApplyActFilters(from, to, status, type, clientId, responsibleId, search);

        if (legalEntityId.HasValue)
        {
            query = query.Where(act => act.LegalEntityId == legalEntityId.Value);
        }

        if (caseId.HasValue)
        {
            query = query.Where(act =>
                act.InvoiceId.HasValue
                && _db.Payments.Any(invoice =>
                    invoice.Id == act.InvoiceId.Value
                    && (invoice.ClientCaseId == caseId.Value
                        || invoice.InvoiceClientCases.Any(link => link.ClientCaseId == caseId.Value))));
        }

        if (normalizedWorkflowStatus is not null)
        {
            query = query.Where(act => act.WorkflowStatus == normalizedWorkflowStatus);
        }

        if (contractId.HasValue)
        {
            query = query.Where(act =>
                _db.ContractActs.Any(link => link.ContractId == contractId.Value && link.ActId == act.Id)
                || (act.InvoiceId.HasValue && (
                    _db.ContractInvoices.Any(link => link.ContractId == contractId.Value && link.InvoiceId == act.InvoiceId.Value)
                    || _db.Payments.Any(payment => payment.Id == act.InvoiceId.Value && payment.ContractId == contractId.Value))));
        }

        if (invoiceId.HasValue)
        {
            query = query.Where(act =>
                act.InvoiceId == invoiceId.Value
                || (!act.InvoiceId.HasValue
                    && act.InvoiceNumber != null
                    && _db.Payments.Any(invoice =>
                        invoice.Id == invoiceId.Value
                        && invoice.Account == act.InvoiceNumber)));
        }

        if (paymentId.HasValue)
        {
            query = query.Where(act =>
                act.InvoiceId.HasValue
                && _db.Payments.Any(payment =>
                    payment.Id == paymentId.Value
                    && (
                        payment.Id == act.InvoiceId.Value
                        || (payment.InvoiceId.HasValue
                            && _db.Payments.Any(invoice =>
                                invoice.Id == act.InvoiceId.Value
                                && invoice.InvoiceId == payment.InvoiceId.Value)))));
        }

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

        var workflowStatus = ResolveActWorkflowStatus(request);
        if (workflowStatus is null)
        {
            return ValidationError(nameof(request.WorkflowStatus), "Unknown act workflow status.");
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

        var owner = ResolveActOwner(request, invoice);
        var ownerValidationError = await ValidateActOwnerAsync(owner.ClientId, owner.LegalEntityId, ct);
        if (ownerValidationError is not null) return ownerValidationError;

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
            Status = ResolveActStatus(workflowStatus),
            WorkflowStatus = workflowStatus,
            ClientId = owner.ClientId,
            LegalEntityId = owner.LegalEntityId,
            ResponsibleId = request.ResponsibleId ?? User.GetUserId(),
            Comment = Normalize(request.Comment),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Acts.Add(act);
        SyncActLineItems(act, resolvedLineItems.LineItems);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(act).Reference(a => a.Client).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.LegalEntity).LoadAsync(ct);
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

        var workflowStatus = ResolveActWorkflowStatus(request);
        if (workflowStatus is null)
        {
            return ValidationError(nameof(request.WorkflowStatus), "Unknown act workflow status.");
        }

        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var act = await _db.Acts
            .ApplyActVisibility(visibility)
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

        var owner = ResolveActOwner(request, invoice);
        var ownerValidationError = await ValidateActOwnerAsync(owner.ClientId, owner.LegalEntityId, ct);
        if (ownerValidationError is not null) return ownerValidationError;

        act.Number = request.Number.Trim();
        act.Title = Normalize(request.Title);
        act.Date = request.Date.Date;
        act.Amount = resolvedLineItems.Amount;
        act.CreationMode = resolvedLineItems.CreationMode;
        act.InvoiceNumber = Normalize(request.InvoiceNumber) ?? invoice?.Account;
        act.InvoiceId = request.InvoiceId;
        act.CounterpartyInn = Normalize(request.CounterpartyInn);
        act.Status = ResolveActStatus(workflowStatus);
        act.WorkflowStatus = workflowStatus;
        act.ClientId = owner.ClientId;
        act.LegalEntityId = owner.LegalEntityId;
        act.ResponsibleId = request.ResponsibleId;
        act.Comment = Normalize(request.Comment);
        act.UpdatedAt = DateTime.UtcNow;
        SyncActLineItems(act, resolvedLineItems.LineItems);

        await _db.SaveChangesAsync(ct);

        await _db.Entry(act).Reference(a => a.Client).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.LegalEntity).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Responsible).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Invoice).LoadAsync(ct);
        await _db.Entry(act).Collection(a => a.LineItems).LoadAsync(ct);

        return Ok(Map(act));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var act = await _db.Acts
            .ApplyActVisibility(visibility)
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

    [HttpPost("{id:int}/yandex-disk")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> UploadDocumentToYandexDisk(
        int id,
        [FromQuery] string? format = "pdf",
        [FromQuery] bool? includeSignatureAndSeal = null,
        [FromQuery] bool? overwrite = null,
        CancellationToken ct = default)
    {
        if (_yandexDiskService is null)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "Сервис Яндекс.Диска не зарегистрирован на сервере.");
        }

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.YandexDisk, permission => permission.CanCreate, ct) is { } yandexForbidden)
        {
            return yandexForbidden;
        }

        if (await RequireSectionPermissionAsync(RolePermissionCatalog.Acts, permission => permission.CanExport, ct) is { } actForbidden)
        {
            return actForbidden;
        }

        var act = await LoadActDocumentAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        var normalizedFormat = NormalizeDocumentFormat(format);
        if (normalizedFormat == "html")
        {
            normalizedFormat = "word";
        }

        var mainCompany = await LoadMainCompanyAsync(ct);
        var documentHtml = ActDocumentTemplateEngine.RenderHtml(
            act,
            act.LineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToList(),
            mainCompany,
            includeSignatureAndSeal ?? false);

        byte[] bytes;
        string fileName;
        string contentType;
        if (normalizedFormat == "pdf")
        {
            try
            {
                bytes = await RenderDocumentPdfAsync(documentHtml, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }

            fileName = $"act-{SanitizeFilePart(act.Number)}.pdf";
            contentType = "application/pdf";
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(documentHtml);
            fileName = $"act-{SanitizeFilePart(act.Number)}.doc";
            contentType = "application/msword";
        }

        await using var stream = new MemoryStream(bytes);
        try
        {
            var result = await _yandexDiskService.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
                stream,
                contentType,
                fileName,
                "act",
                "Акт",
                act.Number,
                act.Date,
                string.IsNullOrWhiteSpace(act.Title) ? $"Акт {act.Number}" : act.Title,
                ResolveActClientName(act),
                ResolveActLegalEntityName(act),
                act.Invoice?.ClientCase?.CaseNumber ?? act.Invoice?.InvoiceClientCases.FirstOrDefault()?.ClientCase?.CaseNumber,
                act.Invoice?.ClientCase?.Title ?? act.Invoice?.InvoiceClientCases.FirstOrDefault()?.ClientCase?.Title,
                overwrite),
                ct);

            await YandexDiskCrmSyncStateRecorder.RecordSuccessAsync(
                _db,
                "act",
                "act",
                act.Id,
                fileName,
                result.Path,
                null,
                ct);

            _db.ObjectAuditEvents.Add(new ObjectAuditEvent
            {
                ObjectType = "act",
                ObjectId = act.Id,
                Action = "YandexDiskUploaded",
                Description = $"Акт «{act.Number}» отправлен на Яндекс.Диск.",
                ActorUserId = User.GetUserId(),
                ActorUserName = User.GetDisplayName(),
                MetadataJson = JsonSerializer.Serialize(new { act.Id, act.Number, FileName = fileName, result.Path }),
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            return result;
        }
        catch (YandexDiskOperationException ex)
        {
            return MapYandexDiskError(ex);
        }
    }

    [HttpPost("{id:int}/send-email")]
    public async Task<IActionResult> SendActEmail(int id, [FromBody] SendInvoiceEmailRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (_emailService is null)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "Email service is not registered on the server.");
        }

        var act = await LoadActDocumentAsync(id, ct);
        if (act is null)
        {
            return NotFound();
        }

        var email = string.IsNullOrWhiteSpace(request.Email)
            ? act.Client?.Email?.Trim()
            : request.Email.Trim();

        if (string.IsNullOrWhiteSpace(email))
        {
            return BadRequest("Recipient email is required.");
        }

        var normalizedFormat = NormalizeDocumentFormat(request.Format);
        if (normalizedFormat == "html")
        {
            normalizedFormat = "word";
        }

        var mainCompany = await LoadMainCompanyAsync(ct);
        var documentHtml = ActDocumentTemplateEngine.RenderHtml(
            act,
            act.LineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToList(),
            mainCompany,
            includeSignatureAndSeal: true);

        byte[] bytes;
        string fileName;
        string contentType;
        if (normalizedFormat == "pdf")
        {
            try
            {
                bytes = await RenderDocumentPdfAsync(documentHtml, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
            }

            fileName = $"act-{SanitizeFilePart(act.Number)}.pdf";
            contentType = "application/pdf";
        }
        else
        {
            bytes = Encoding.UTF8.GetBytes(documentHtml);
            fileName = $"act-{SanitizeFilePart(act.Number)}.doc";
            contentType = "application/msword";
        }

        var subject = string.IsNullOrWhiteSpace(request.Subject)
            ? $"Акт №{act.Number} от {act.Date.Date:dd.MM.yyyy}"
            : request.Subject.Trim();
        var body = string.IsNullOrWhiteSpace(request.Message)
            ? "Во вложении акт."
            : request.Message.Trim();

        await _emailService.SendEmailWithAttachmentAsync(
            email,
            subject,
            body,
            fileName,
            bytes,
            contentType);

        return Ok(new { sent = true, recipient = email, fileName, format = normalizedFormat });
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
        WorkflowStatus = act.WorkflowStatus,
        SentForSigningAt = act.SentForSigningAt,
        SignedAt = act.SignedAt,
        CancelledAt = act.CancelledAt,
        CancelledByUserId = act.CancelledByUserId,
        CancelledReason = act.CancelledReason,
        ClientId = act.ClientId,
        ClientName = act.Client != null ? act.Client.Name : null,
        LegalEntityId = act.LegalEntityId,
        LegalEntityName = act.LegalEntity != null ? act.LegalEntity.ShortName : null,
        LegalEntityInn = act.LegalEntity != null ? act.LegalEntity.Inn : null,
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

    private static readonly System.Linq.Expressions.Expression<Func<Act, ActDto>> MapToListDtoExpression = act => new ActDto
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
        WorkflowStatus = act.WorkflowStatus,
        SentForSigningAt = act.SentForSigningAt,
        SignedAt = act.SignedAt,
        CancelledAt = act.CancelledAt,
        CancelledByUserId = act.CancelledByUserId,
        CancelledReason = act.CancelledReason,
        ClientId = act.ClientId,
        ClientName = act.Client != null ? act.Client.Name : null,
        LegalEntityId = act.LegalEntityId,
        LegalEntityName = act.LegalEntity != null ? act.LegalEntity.ShortName : null,
        LegalEntityInn = act.LegalEntity != null ? act.LegalEntity.Inn : null,
        ResponsibleId = act.ResponsibleId,
        ResponsibleName = act.Responsible != null ? act.Responsible.FullName : null,
        Comment = act.Comment,
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
        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return null;
        }

        return await _db.Acts
            .AsNoTracking()
            .ApplyActVisibility(visibility)
            .Include(act => act.Client)
                .ThenInclude(client => client!.Addresses)
            .Include(act => act.Client)
                .ThenInclude(client => client!.Passports)
            .Include(act => act.LegalEntity)
                .ThenInclude(entity => entity!.Addresses)
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
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.LegalEntity)
                    .ThenInclude(entity => entity!.Addresses)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.ExpenseLegalEntity)
                    .ThenInclude(entity => entity!.Addresses)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.ClientCase)
            .Include(act => act.Invoice)
                .ThenInclude(invoice => invoice!.InvoiceClientCases)
                    .ThenInclude(link => link.ClientCase)
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
            var manualLineItems = ResolveManualActLineItems(request.LineItems);
            if (manualLineItems.Error is not null)
            {
                return (manualLineItems.Error, ActCreationMode.Manual, 0m, new List<ActLineItemDraft>());
            }

            var manualAmount = manualLineItems.LineItems.Count > 0
                ? manualLineItems.LineItems.Sum(item => item.Quantity * item.Price)
                : request.Amount;

            return (null, ActCreationMode.Manual, manualAmount, manualLineItems.LineItems);
        }

        var sourceItems = await LoadInvoiceSourceLineItemsAsync(invoice.Id, invoice.SystemNotes, ct);
        if (sourceItems.Count == 0)
        {
            var manualLineItems = ResolveManualActLineItems(request.LineItems);
            if (manualLineItems.Error is not null)
            {
                return (manualLineItems.Error, ActCreationMode.Manual, 0m, new List<ActLineItemDraft>());
            }

            var manualAmount = manualLineItems.LineItems.Count > 0
                ? manualLineItems.LineItems.Sum(item => item.Quantity * item.Price)
                : request.Amount;

            return (null, ActCreationMode.Manual, manualAmount, manualLineItems.LineItems);
        }

        var creationMode = ActCreationMode.Normalize(request.CreationMode);
        if (creationMode == ActCreationMode.Manual)
        {
            var manualLineItems = ResolveManualActLineItems(request.LineItems);
            if (manualLineItems.Error is not null)
            {
                return (manualLineItems.Error, ActCreationMode.Manual, 0m, new List<ActLineItemDraft>());
            }

            var manualAmount = manualLineItems.LineItems.Count > 0
                ? manualLineItems.LineItems.Sum(item => item.Quantity * item.Price)
                : request.Amount;

            return (null, ActCreationMode.Manual, manualAmount, manualLineItems.LineItems);
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

    private (ActionResult? Error, List<ActLineItemDraft> LineItems) ResolveManualActLineItems(
        IReadOnlyList<ActLineItemRequest>? requestLineItems)
    {
        if (requestLineItems is null || requestLineItems.Count == 0)
        {
            return (null, new List<ActLineItemDraft>());
        }

        var resolved = new List<ActLineItemDraft>(requestLineItems.Count);
        for (var index = 0; index < requestLineItems.Count; index++)
        {
            var item = requestLineItems[index];
            var sourceKey = Normalize(item.SourceKey);
            var name = Normalize(item.Name);
            var unit = Normalize(item.Unit) ?? "шт";

            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                return (ValidationError($"{nameof(UpsertActRequest.LineItems)}[{index}].{nameof(ActLineItemRequest.SourceKey)}", "Укажите внутренний ключ позиции акта."), new List<ActLineItemDraft>());
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return (ValidationError($"{nameof(UpsertActRequest.LineItems)}[{index}].{nameof(ActLineItemRequest.Name)}", "Укажите наименование позиции акта."), new List<ActLineItemDraft>());
            }

            if (item.Quantity <= 0)
            {
                return (ValidationError($"{nameof(UpsertActRequest.LineItems)}[{index}].{nameof(ActLineItemRequest.Quantity)}", "Количество позиции акта должно быть больше нуля."), new List<ActLineItemDraft>());
            }

            if (item.Price < 0)
            {
                return (ValidationError($"{nameof(UpsertActRequest.LineItems)}[{index}].{nameof(ActLineItemRequest.Price)}", "Цена позиции акта не может быть отрицательной."), new List<ActLineItemDraft>());
            }

            resolved.Add(new ActLineItemDraft(
                null,
                sourceKey,
                index,
                name,
                item.Quantity,
                unit,
                item.Price));
        }

        return (null, resolved);
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

    private async Task<ActionResult?> RequireSectionPermissionAsync(
        string section,
        Func<RolePermission, bool> isAllowed,
        CancellationToken ct)
    {
        if (User.IsInRole("admin"))
        {
            return null;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, section, ct);
        return permission is not null && permission.CanView && isAllowed(permission)
            ? null
            : Forbid();
    }

    private static ActionResult MapYandexDiskError(YandexDiskOperationException ex)
    {
        if (ex.StatusCode >= 400 && ex.StatusCode < 500)
        {
            return new BadRequestObjectResult(ex.Message);
        }

        return new ObjectResult(ex.Message)
        {
            StatusCode = StatusCodes.Status502BadGateway,
        };
    }

    private static string? ResolveActClientName(Act act)
    {
        if (act.Client is not null)
        {
            return act.Client.Name;
        }

        return act.Invoice?.Type == PaymentType.Expense
            ? act.Invoice.ExpenseClient?.Name ?? act.Invoice.CounterpartyName
            : act.Invoice?.Client?.Name ?? act.Invoice?.CounterpartyName;
    }

    private static string? ResolveActLegalEntityName(Act act)
    {
        if (act.LegalEntity is not null)
        {
            return act.LegalEntity.ShortName ?? act.LegalEntity.FullName;
        }

        var legalEntity = act.Invoice?.Type == PaymentType.Expense
            ? act.Invoice.ExpenseLegalEntity
            : act.Invoice?.LegalEntity;

        return legalEntity?.ShortName ?? legalEntity?.FullName;
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
            .Include(entity => entity.Addresses)
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

    private async Task<Act?> LoadActForWorkflowAsync(int id, CancellationToken ct)
    {
        var visibility = await ResolveActsVisibilityAsync(ct);
        if (visibility is null)
        {
            return null;
        }

        return await _db.Acts
            .ApplyActVisibility(visibility)
            .Include(act => act.Client)
            .Include(act => act.Responsible)
            .Include(act => act.Invoice)
            .Include(act => act.LineItems)
            .FirstOrDefaultAsync(act => act.Id == id, ct);
    }

    private static string? NormalizeActWorkflowStatusOrNull(string? value)
        => ActWorkflowStatus.Normalize(value);

    private static ActStatus ResolveActStatus(string workflowStatus)
        => workflowStatus switch
        {
            ActWorkflowStatus.OnSigning => ActStatus.Transferred,
            ActWorkflowStatus.Signed => ActStatus.Signed,
            ActWorkflowStatus.Cancelled => ActStatus.Terminated,
            _ => ActStatus.Created,
        };

    private static string? ResolveActWorkflowStatus(UpsertActRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorkflowStatus))
        {
            return ActWorkflowStatus.Normalize(request.WorkflowStatus);
        }

        return ActWorkflowStatus.FromActStatus(request.Status);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<ActionResult?> ValidateFinancialActionReasonAsync(string? reason, string actionName, CancellationToken ct)
    {
        if (!await User.RequiresFinancialActionReasonAsync(_db, ct))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(reason)
            ? BadRequest($"Укажите причину {actionName}. Она будет сохранена в журнале.")
            : null;
    }

    private void ApplyAuditReason(string? reason)
    {
        if (!string.IsNullOrWhiteSpace(reason))
        {
            _db.SetAuditReason(reason);
        }
    }

    private static string AppendCancellationComment(string? comment, string reason, DateTime now)
    {
        var note = $"Акт аннулирован {now:dd.MM.yyyy HH:mm}. Причина: {reason}";
        return string.IsNullOrWhiteSpace(comment)
            ? note
            : $"{comment.Trim()}{Environment.NewLine}{note}";
    }

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
            WorkflowStatus = act.WorkflowStatus,
            SentForSigningAt = act.SentForSigningAt,
            SignedAt = act.SignedAt,
            CancelledAt = act.CancelledAt,
            CancelledByUserId = act.CancelledByUserId,
            CancelledReason = act.CancelledReason,
            ClientId = act.ClientId,
            ClientName = act.Client?.Name,
            LegalEntityId = act.LegalEntityId,
            LegalEntityName = act.LegalEntity?.ShortName,
            LegalEntityInn = act.LegalEntity?.Inn,
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
