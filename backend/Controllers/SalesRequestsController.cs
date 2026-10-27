using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Requests;
using PayPlanner.Api.Services.Invoices;

namespace PayPlanner.Api.Controllers;

public sealed record SalesRequestDto(
    int Id, DateTime CreatedAt, string Source, int ClientId, string ClientName, string ClientPhone, int? ClientCaseId,
    bool LinkedCaseWasClosed, string Type, string Summary, int StatusId, DateTime StatusEnteredAt, int ResponsibleUserId,
    decimal? Amount, string? Comment, string FinalKind, DateTime? ClosedAt, int? PaidPaymentId, int? ResultClientCaseId, int Version, DateTime? SlaDueAt = null);

public sealed record SalesRequestStatusDto(int Id, string Key, string Title, int SortOrder, string FinalKind, int? AutoAdvanceToStatusId, int? SlaMinutes, IReadOnlyList<SalesRequestRuleDto> Rules);

public sealed record SalesRequestRuleDto(int Id, string Title, int? AssigneeUserId, int DueInMinutes, decimal? AmountGreaterThan, string? Source, string? RequestType);

public sealed record SalesRequestTaskDto(int Id, string Title, string Status, int? AssigneeUserId, string AssigneeName, DateTime DueAt);

public sealed record SalesRequestEventDto(int Id, string Kind, int? FromStatusId, int? ToStatusId, int? UserId, string? Comment, DateTime CreatedAt);

public sealed record SalesRequestDetailDto(SalesRequestDto Request, IReadOnlyList<SalesRequestTaskDto> Tasks, IReadOnlyList<SalesRequestEventDto> Events);

public sealed record SalesRequestClientCaseDto(int Id, string CaseNumber, string Title, string Status, DateTime? LastActivityAt, string? LawyerName, string ContractMode);

public sealed record SalesRequestClientCasePageDto(IReadOnlyList<SalesRequestClientCaseDto> Items, bool HasMore);

public sealed record SalesRequestClientDto(int Id, string Name, string Phone, string Email, int CasesCount);

public sealed record SalesRequestInvoiceCandidateDto(int InvoiceId, string Number, DateTime Date, int SellerCompanyId, int? SellerBranchId);
public sealed record SalesRequestInvoiceCandidatesDto(IReadOnlyList<SalesRequestInvoiceCandidateDto> Items, bool HasMore);
public sealed record SalesRequestServiceInvoiceDto(string State, SalesRequestInvoiceCandidateDto? Invoice, decimal? AllocationAmount = null);

public sealed class SalesRequestCreateRequest
{
    public string? Source { get; set; }
    public int? ClientId { get; set; }
    public string? NewClientName { get; set; }
    public string? NewClientPhone { get; set; }
    public string? NewClientEmail { get; set; }
    public int? ClientCaseId { get; set; }
    public string? Type { get; set; }
    public string? Summary { get; set; }
    public decimal? Amount { get; set; }
    public string? Comment { get; set; }
    public int? ResponsibleUserId { get; set; }
}

public sealed class SalesRequestUpdateRequest
{
    public string? Source { get; set; }
    public string? Summary { get; set; }
    public string? Type { get; set; }
    public decimal? Amount { get; set; }
    public string? Comment { get; set; }
    public int? ResponsibleUserId { get; set; }
    public int Version { get; set; }
}

public sealed class SalesRequestTransitionRequest
{
    public int ToStatusId { get; set; }
    public int Version { get; set; }
    public int? PaidPaymentId { get; set; }
    public bool ForceCloseTasks { get; set; }
    public string? Comment { get; set; }
}

public sealed class SalesRequestServiceInvoiceRequest
{
    public int Version { get; set; }
    public int? InvoiceId { get; set; }
    public decimal? AllocationAmount { get; set; }
    public bool Remove { get; set; }
}

public sealed class SalesRequestTaskRequest
{
    public int AssigneeUserId { get; set; }
    public string? Title { get; set; }
    public DateTime? DueAt { get; set; }
}

public sealed class SalesRequestStatusRequest
{
    public string? Title { get; set; }
    public string? FinalKind { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsActive { get; set; }
    public int? AutoAdvanceToStatusId { get; set; }
    public bool ClearAutoAdvance { get; set; }
    public int? SlaMinutes { get; set; }
    public bool ClearSla { get; set; }
}

public sealed class SalesRequestRulesRequest
{
    public List<SalesRequestRuleRequest> Rules { get; set; } = [];
}

public sealed class SalesRequestRuleRequest
{
    public string? Title { get; set; }
    public int? AssigneeUserId { get; set; }
    public int DueInMinutes { get; set; }
    public decimal? AmountGreaterThan { get; set; }
    public string? Source { get; set; }
    public string? RequestType { get; set; }
}

/// <summary>
/// Заявки воронки продаж. Доступ — по разделу прав «requests»: пока роли не получили права, раздел недоступен
/// (в том числе юристам); видимость «свои/все» определяет область просмотра роли. Заявка не раскрывается через дело.
/// </summary>
[ApiController]
[Route("api/requests")]
[Authorize]
public class SalesRequestsController : ControllerBase
{
    private const int MaxPageSize = 100;

    private readonly PaymentContext _db;
    private readonly ISalesRequestService _service;

    public SalesRequestsController(PaymentContext db, ISalesRequestService service)
    {
        _db = db;
        _service = service;
    }

    private async Task<SectionAccessContext?> AccessAsync(CancellationToken ct)
        => await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Requests, ct);

    private static bool Allowed(SectionAccessContext access, Func<RolePermissionValues, bool> check)
        => access.Visibility.IsAdmin || (access.Permission is { } permission && check(permission));

    private ActionResult Denied() => User.GetUserId().HasValue ? Forbid() : Unauthorized();

    [HttpGet("statuses")]
    public async Task<ActionResult<List<SalesRequestStatusDto>>> Statuses(CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanView))
        {
            return Denied();
        }

        await _service.EnsureDefaultsAsync(ct);
        var statuses = await _db.SalesRequestStatuses.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.SortOrder).ToListAsync(ct);
        var rules = await _db.SalesRequestStatusTaskRules.AsNoTracking().Where(item => item.IsActive).ToListAsync(ct);
        return Ok(statuses.Select(item => new SalesRequestStatusDto(item.Id, item.Key, item.Title, item.SortOrder, item.FinalKind, item.AutoAdvanceToStatusId, item.SlaMinutes,
            rules.Where(rule => rule.StatusId == item.Id).Select(rule => new SalesRequestRuleDto(rule.Id, rule.Title, rule.AssigneeUserId, rule.DueInMinutes, rule.ConditionAmountGreaterThan, rule.ConditionSource, rule.ConditionType)).ToList())).ToList());
    }

    [HttpPost("statuses")]
    public async Task<ActionResult<SalesRequestStatusDto>> CreateStatus([FromBody] SalesRequestStatusRequest request, CancellationToken ct)
        => await SaveStatusAsync(null, request, ct);

    [HttpPut("statuses/{id:int}")]
    public async Task<ActionResult<SalesRequestStatusDto>> UpdateStatus(int id, [FromBody] SalesRequestStatusRequest request, CancellationToken ct)
        => await SaveStatusAsync(id, request, ct);

    [HttpPut("statuses/{id:int}/rules")]
    public async Task<ActionResult<List<SalesRequestRuleDto>>> ReplaceRules(int id, [FromBody] SalesRequestRulesRequest request, CancellationToken ct)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(User, ct))
        {
            return Denied();
        }

        var result = await _service.ReplaceRulesAsync(id, request.Rules.Select(rule => new SalesRequestRuleInput(rule.Title, rule.AssigneeUserId, rule.DueInMinutes, rule.AmountGreaterThan, rule.Source, rule.RequestType)).ToList(), ct);
        return result.Ok
            ? Ok(result.Value!.Select(rule => new SalesRequestRuleDto(rule.Id, rule.Title, rule.AssigneeUserId, rule.DueInMinutes, rule.ConditionAmountGreaterThan, rule.ConditionSource, rule.ConditionType)).ToList())
            : StatusCode(result.Status, new { message = result.Error });
    }

    private async Task<ActionResult<SalesRequestStatusDto>> SaveStatusAsync(int? id, SalesRequestStatusRequest request, CancellationToken ct)
    {
        // Настройка воронки меняет правила для всех сотрудников, поэтому только администратор.
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(User, ct))
        {
            return Denied();
        }

        if (!request.ClearSla && request.SlaMinutes.HasValue)
        {
            var kind = id.HasValue ? await _db.SalesRequestStatuses.Where(item => item.Id == id.Value).Select(item => item.FinalKind).FirstOrDefaultAsync(ct)
                : request.FinalKind ?? SalesRequestFinalKinds.None;
            if (request.SlaMinutes is < 1 or > 60 * 24 * 365 || (kind is not null && kind != SalesRequestFinalKinds.None))
                return BadRequest(new { message = "Срок этапа задаётся только для рабочего статуса, от 1 минуты до года." });
        }
        var result = await _service.SaveStatusAsync(id, request.Title, request.FinalKind, request.SortOrder, request.IsActive, ct, request.AutoAdvanceToStatusId, request.ClearAutoAdvance);
        if (result.Ok && (request.SlaMinutes.HasValue || request.ClearSla))
        {
            result = await _service.SetSlaAsync(result.Value!.Id, request.ClearSla ? null : request.SlaMinutes, ct);
        }

        return result.Ok
            ? Ok(new SalesRequestStatusDto(result.Value!.Id, result.Value.Key, result.Value.Title, result.Value.SortOrder, result.Value.FinalKind, result.Value.AutoAdvanceToStatusId, result.Value.SlaMinutes, []))
            : StatusCode(result.Status, new { message = result.Error });
    }

    [HttpGet]
    public async Task<ActionResult<object>> List(
        [FromQuery] int? statusId, [FromQuery] string? source, [FromQuery] int? responsibleUserId, [FromQuery] string? q,
        [FromQuery] bool? closed, [FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanView))
        {
            return Denied();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if ((long)(page - 1) * pageSize > int.MaxValue)
            return BadRequest(new { message = "Укажите корректную страницу." });
        var query = _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(access.Visibility);

        if (statusId.HasValue) query = query.Where(item => item.StatusId == statusId.Value);
        if (!string.IsNullOrWhiteSpace(source)) query = query.Where(item => item.Source == source);
        if (responsibleUserId.HasValue) query = query.Where(item => item.ResponsibleUserId == responsibleUserId.Value);
        if (closed.HasValue) query = closed.Value ? query.Where(item => item.FinalKind != SalesRequestFinalKinds.None) : query.Where(item => item.FinalKind == SalesRequestFinalKinds.None);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim();
            query = query.Where(item => item.ClientName.Contains(text) || item.ClientPhone.Contains(text) || item.Summary.Contains(text));
        }

        var limits = await SlaLimitsAsync(ct);
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items = items.Select(item => Map(item, limits)).ToList() });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SalesRequestDetailDto>> Get(int id, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanView))
        {
            return Denied();
        }

        var request = await _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(access.Visibility).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (request is null)
        {
            return NotFound();
        }

        var tasks = await _db.TaskJournalItems.AsNoTracking().Where(task => task.SalesRequestId == id).OrderBy(task => task.DueAt)
            .Select(task => new SalesRequestTaskDto(task.Id, task.Title, task.Status, task.AssigneeUserId, task.AssigneeName, task.DueAt)).ToListAsync(ct);
        var events = await _db.SalesRequestEvents.AsNoTracking().Where(item => item.SalesRequestId == id).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .Select(item => new SalesRequestEventDto(item.Id, item.Kind, item.FromStatusId, item.ToStatusId, item.UserId, item.Comment, item.CreatedAt)).ToListAsync(ct);
        return Ok(new SalesRequestDetailDto(Map(request, await SlaLimitsAsync(ct)), tasks, events));
    }

    [HttpGet("{id:int}/invoice-candidates")]
    public async Task<ActionResult<SalesRequestInvoiceCandidatesDto>> InvoiceCandidates(
        int id, [FromQuery] string? search, [FromQuery] int afterInvoiceId = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (await GuardAsync(id, p => p.CanEdit, ct) is not { } requestAccess) return Denied();
        var queryText = search?.Trim();
        if (queryText?.Length > 100 || afterInvoiceId < 0 || take is < 1 or > 20)
            return BadRequest(new { message = "Уточните поиск и размер страницы счетов." });
        var request = await _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(requestAccess.Visibility)
            .Where(item => item.Id == id).Select(item => new { item.ClientId, item.FinalKind }).SingleOrDefaultAsync(ct);
        if (request is null) return Denied();
        if (request.FinalKind != SalesRequestFinalKinds.None) return Conflict(new { message = "Заявка уже закрыта." });

        var accounts = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        if (accounts?.Permission?.CanView != true) return Denied();
        IQueryable<Payment> Accessible(SectionAccessContext access) => InvoicePaymentAccess.ScopeHeaders(_db, access)
            .Where(payment => payment.Type == PaymentType.Income && payment.ClientId == request.ClientId
                && payment.InvoiceId > afterInvoiceId && payment.Invoice != null);
        var initial = Accessible(accounts);
        if (!string.IsNullOrEmpty(queryText)) initial = initial.Where(payment => payment.Invoice!.Number.Contains(queryText));
        var ids = await initial.Select(payment => payment.InvoiceId!.Value).Distinct().OrderBy(invoiceId => invoiceId).Take(take + 1).ToArrayAsync(ct);

        var freshRequestAccess = await GuardAsync(id, p => p.CanEdit, ct);
        var freshAccounts = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        if (freshRequestAccess is null || freshAccounts?.Permission?.CanView != true) return Denied();
        var currentRequest = await _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(freshRequestAccess.Visibility)
            .Where(item => item.Id == id).Select(item => new { item.ClientId, item.FinalKind }).SingleOrDefaultAsync(ct);
        if (currentRequest is null) return Denied();
        if (currentRequest.ClientId != request.ClientId || currentRequest.FinalKind != SalesRequestFinalKinds.None)
            return Conflict(new { message = "Заявка изменилась. Обновите карточку." });
        if (ids.Length == 0) return Ok(new SalesRequestInvoiceCandidatesDto([], false));
        var fresh = Accessible(freshAccounts).Where(payment => ids.Contains(payment.InvoiceId!.Value));
        if (!string.IsNullOrEmpty(queryText)) fresh = fresh.Where(payment => payment.Invoice!.Number.Contains(queryText));
        var rows = await fresh.Select(payment => new { InvoiceId = payment.InvoiceId!.Value, payment.Invoice!.Number, payment.Invoice.Date, SellerCompanyId = payment.SellerCompanyId!.Value, payment.SellerBranchId })
            .Distinct().OrderBy(item => item.InvoiceId).ToArrayAsync(ct);
        return Ok(new SalesRequestInvoiceCandidatesDto(rows.Take(take).Select(item => new SalesRequestInvoiceCandidateDto(item.InvoiceId, item.Number, item.Date, item.SellerCompanyId, item.SellerBranchId)).ToArray(), rows.Length > take));
    }

    [HttpGet("{id:int}/service-invoice")]
    public async Task<ActionResult<SalesRequestServiceInvoiceDto>> GetServiceInvoice(int id, CancellationToken ct)
    {
        if (await GuardAsync(id, p => p.CanView, ct) is not { } requestAccess) return Denied();
        var accounts = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        if (accounts?.Permission?.CanView != true) return Denied();
        var request = await _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(requestAccess.Visibility)
            .Where(item => item.Id == id).Select(item => new { item.ClientId, item.ServiceInvoiceId, item.ServiceInvoiceAllocationAmount, item.Version }).SingleOrDefaultAsync(ct);
        if (request is null) return Denied();
        var freshRequestAccess = await GuardAsync(id, p => p.CanView, ct);
        var freshAccounts = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Accounts, ct, requireApprovedUser: true);
        if (freshRequestAccess is null || freshAccounts?.Permission?.CanView != true) return Denied();
        var freshRequest = await _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(freshRequestAccess.Visibility)
            .Where(item => item.Id == id).Select(item => new { item.ClientId, item.ServiceInvoiceId, item.ServiceInvoiceAllocationAmount, item.Version }).SingleOrDefaultAsync(ct);
        if (freshRequest is null) return Denied();
        if (freshRequest.ClientId != request.ClientId || freshRequest.ServiceInvoiceId != request.ServiceInvoiceId
            || freshRequest.ServiceInvoiceAllocationAmount != request.ServiceInvoiceAllocationAmount || freshRequest.Version != request.Version)
            return Conflict(new { message = "Заявка изменилась. Обновите карточку." });
        if (!freshRequest.ServiceInvoiceId.HasValue) return Ok(new SalesRequestServiceInvoiceDto("none", null));
        var candidate = await InvoicePaymentAccess.ScopeHeaders(_db, freshAccounts)
            .Where(payment => payment.InvoiceId == freshRequest.ServiceInvoiceId && payment.ClientId == freshRequest.ClientId
                && payment.Type == PaymentType.Income && payment.Invoice != null && payment.InvoiceWorkflowStatus != InvoiceWorkflowStatus.Cancelled)
            .Select(payment => new { InvoiceId = payment.InvoiceId!.Value, payment.Invoice!.Number, payment.Invoice.Date, SellerCompanyId = payment.SellerCompanyId!.Value, payment.SellerBranchId })
            .Distinct().FirstOrDefaultAsync(ct);
        return candidate is null
            ? Ok(new SalesRequestServiceInvoiceDto("unavailable", null))
            : Ok(new SalesRequestServiceInvoiceDto("available", new SalesRequestInvoiceCandidateDto(candidate.InvoiceId, candidate.Number, candidate.Date, candidate.SellerCompanyId, candidate.SellerBranchId), freshRequest.ServiceInvoiceAllocationAmount));
    }

    [HttpGet("clients/search")]
    public async Task<ActionResult<List<SalesRequestClientDto>>> SearchClients([FromQuery] string? q, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanCreate))
        {
            return Denied();
        }

        var text = q?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length < 2)
        {
            return Ok(new List<SalesRequestClientDto>());
        }

        // Поиск ограничен десятью клиентами, дела догружаются отдельным запросом по выбранному клиенту.
        var clients = await _db.Clients.AsNoTracking()
            .Where(item => item.Name.Contains(text) || item.Phone.Contains(text) || item.Email.Contains(text))
            .OrderBy(item => item.Name).Take(10).Select(item => new { item.Id, item.Name, item.Phone, item.Email }).ToListAsync(ct);
        var ids = clients.Select(item => item.Id).ToList();
        var counts = await _db.ClientCases.AsNoTracking().Where(item => item.ClientId != null && ids.Contains(item.ClientId.Value))
            .GroupBy(item => item.ClientId!.Value).Select(group => new { ClientId = group.Key, Count = group.Count() }).ToDictionaryAsync(group => group.ClientId, group => group.Count, ct);
        return Ok(clients.Select(client => new SalesRequestClientDto(client.Id, client.Name, client.Phone, client.Email, counts.GetValueOrDefault(client.Id))).ToList());
    }

    [HttpGet("clients/{clientId:int}/cases")]
    public async Task<ActionResult<SalesRequestClientCasePageDto>> ClientCases(int clientId, [FromQuery] int skip = 0, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanCreate))
        {
            return Denied();
        }

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 50);
        var page = await _db.ClientCases.AsNoTracking().Where(item => item.ClientId == clientId)
            .OrderByDescending(item => item.Id).Skip(skip).Take(take + 1)
            .Select(item => new { item.Id, item.CaseNumber, item.Title, item.Status, item.CreatedAt, item.AuditUpdatedAt, item.ResponsibleUserId }).ToListAsync(ct);
        var hasMore = page.Count > take;
        var rows = page.Take(take).ToList();
        var caseIds = rows.Select(item => item.Id).ToList();
        var lawyerIds = rows.Where(item => item.ResponsibleUserId.HasValue).Select(item => item.ResponsibleUserId!.Value).Distinct().ToList();
        var lawyers = await _db.Users.AsNoTracking().Where(user => lawyerIds.Contains(user.Id)).ToDictionaryAsync(user => user.Id, user => user.FullName, ct);
        var modes = (await _db.ClientCaseContracts.AsNoTracking()
            .Where(link => caseIds.Contains(link.ClientCaseId) && link.Contract.Status == ContractStatus.Concluded)
            .Select(link => new { link.ClientCaseId, link.Contract.BillingMode }).ToListAsync(ct))
            .GroupBy(item => item.ClientCaseId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.BillingMode).Distinct().Count() > 1 ? "Mixed" : group.First().BillingMode);
        return Ok(new SalesRequestClientCasePageDto(rows.Select(item => new SalesRequestClientCaseDto(
            item.Id, item.CaseNumber, item.Title, item.Status.ToString(), item.AuditUpdatedAt ?? item.CreatedAt,
            item.ResponsibleUserId.HasValue ? lawyers.GetValueOrDefault(item.ResponsibleUserId.Value) : null,
            modes.GetValueOrDefault(item.Id, "None"))).ToList(), hasMore));
    }

    [HttpPost]
    public async Task<ActionResult<SalesRequestDto>> Create([FromBody] SalesRequestCreateRequest request, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanCreate))
        {
            return Denied();
        }

        var result = await _service.CreateAsync(new SalesRequestCreateInput(request.Source, request.ClientId, request.NewClientName,
            request.NewClientPhone, request.NewClientEmail, request.ClientCaseId, request.Type, request.Summary, request.Amount,
            request.Comment, request.ResponsibleUserId), access.Visibility.UserId, ct);
        return ToResult(result);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<SalesRequestDto>> Update(int id, [FromBody] SalesRequestUpdateRequest request, CancellationToken ct)
    {
        if (await GuardAsync(id, p => p.CanEdit, ct) is not { } access)
        {
            return Denied();
        }

        return ToResult(await _service.UpdateAsync(id, new SalesRequestUpdateInput(request.Summary, request.Type, request.Amount,
            request.Comment, request.ResponsibleUserId, request.Version, request.Source), access.Visibility.UserId, ct));
    }

    [HttpPut("{id:int}/service-invoice")]
    public async Task<ActionResult<SalesRequestDto>> SetServiceInvoice(int id, [FromBody] SalesRequestServiceInvoiceRequest request, CancellationToken ct)
    {
        if (await GuardAsync(id, p => p.CanEdit, ct) is not { } access) return Denied();
        if (request.Remove == request.InvoiceId.HasValue) return BadRequest(new { message = "Выберите счёт или явно удалите связь." });
        return ToResult(await _service.SetServiceInvoiceAsync(id, request.Version, request.InvoiceId, access.Visibility.UserId, ct, request.AllocationAmount));
    }

    [HttpPost("{id:int}/transition")]
    public async Task<ActionResult<SalesRequestDto>> Transition(int id, [FromBody] SalesRequestTransitionRequest request, CancellationToken ct)
    {
        if (await GuardAsync(id, p => p.CanEdit, ct) is not { } access)
        {
            return Denied();
        }

        return ToResult(await _service.TransitionAsync(id, new SalesRequestTransitionInput(request.ToStatusId, request.Version,
            request.PaidPaymentId, request.ForceCloseTasks, request.Comment), access.Visibility.UserId, ct));
    }

    [HttpPost("{id:int}/tasks")]
    public async Task<ActionResult<SalesRequestTaskDto>> CreateTask(int id, [FromBody] SalesRequestTaskRequest request, CancellationToken ct)
    {
        if (await GuardAsync(id, p => p.CanEdit, ct) is not { } access)
        {
            return Denied();
        }

        var result = await _service.CreateManualTaskAsync(id, request.AssigneeUserId, request.Title, request.DueAt, access.Visibility.UserId, ct);
        return result.Ok
            ? Ok(new SalesRequestTaskDto(result.Value!.Id, result.Value.Title, result.Value.Status, result.Value.AssigneeUserId, result.Value.AssigneeName, result.Value.DueAt))
            : StatusCode(result.Status, new { message = result.Error });
    }

    [HttpGet("journal")]
    public async Task<ActionResult<object>> Journal(
        [FromQuery] string? kind, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanView && p.CanViewAnalytics))
        {
            return Denied();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        if ((long)(page - 1) * pageSize > int.MaxValue || to?.Date == DateTime.MaxValue.Date || (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date))
            return BadRequest(new { message = "Укажите корректный период и страницу." });
        var visibleRequests = _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(access.Visibility).Select(item => item.Id);
        var query = _db.SalesRequestEvents.AsNoTracking().Where(item => visibleRequests.Contains(item.SalesRequestId));
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(item => item.Kind == kind);
        if (from.HasValue) query = query.Where(item => item.CreatedAt >= from.Value.Date);
        if (to.HasValue) query = query.Where(item => item.CreatedAt < to.Value.Date.AddDays(1));
        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new { item.Id, item.SalesRequestId, item.Kind, item.FromStatusId, item.ToStatusId, item.UserId, item.Comment, item.CreatedAt })
            .ToListAsync(ct);
        return Ok(new { total, page, pageSize, items });
    }

    [HttpGet("report")]
    public async Task<ActionResult<SalesRequestReport>> Report([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanView && (p.CanViewAnalytics)))
        {
            return Denied();
        }

        if (to.Date == DateTime.MaxValue.Date || to < from || (to - from).TotalDays > 732)
        {
            return BadRequest(new { message = "Период отчёта должен быть не длиннее двух лет." });
        }

        var visible = access.Visibility.SeesAll ? null : access.Visibility.VisibleUserIds.Append(access.Visibility.UserId).Distinct().ToList();
        return Ok(await _service.BuildReportAsync(from, to, visible, ct));
    }

    private async Task<Dictionary<int, int>> SlaLimitsAsync(CancellationToken ct)
        => await _db.SalesRequestStatuses.AsNoTracking().Where(item => item.SlaMinutes != null).ToDictionaryAsync(item => item.Id, item => item.SlaMinutes!.Value, ct);

    private async Task<SectionAccessContext?> GuardAsync(int id, Func<RolePermissionValues, bool> check, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, check))
        {
            return null;
        }

        // Чужая или несуществующая заявка выглядит одинаково: наличие заявки не раскрывается.
        return await _db.SalesRequests.AsNoTracking().ApplySalesRequestVisibility(access.Visibility).AnyAsync(item => item.Id == id, ct) ? access : null;
    }

    private ActionResult<SalesRequestDto> ToResult(SalesRequestResult<SalesRequest> result)
        => result.Ok ? Ok(Map(result.Value!)) : StatusCode(result.Status, new { message = result.Error, details = result.Details });

    private static SalesRequestDto Map(SalesRequest item, IReadOnlyDictionary<int, int>? slaLimits = null) => new(
        item.Id, item.CreatedAt, item.Source, item.ClientId, item.ClientName, item.ClientPhone, item.ClientCaseId,
        item.LinkedCaseWasClosed, item.Type, item.Summary, item.StatusId, item.StatusEnteredAt, item.ResponsibleUserId,
        item.Amount, item.Comment, item.FinalKind, item.ClosedAt, item.PaidPaymentId, item.ResultClientCaseId, item.Version,
        item.FinalKind == SalesRequestFinalKinds.None && slaLimits is not null && slaLimits.TryGetValue(item.StatusId, out var limit) ? item.StatusEnteredAt.AddMinutes(limit) : null);
}
