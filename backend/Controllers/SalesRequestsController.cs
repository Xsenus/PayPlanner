using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Requests;

namespace PayPlanner.Api.Controllers;

public sealed record SalesRequestDto(
    int Id, DateTime CreatedAt, string Source, int ClientId, string ClientName, string ClientPhone, int? ClientCaseId,
    bool LinkedCaseWasClosed, string Type, string Summary, int StatusId, DateTime StatusEnteredAt, int ResponsibleUserId,
    decimal? Amount, string? Comment, string FinalKind, DateTime? ClosedAt, int? PaidPaymentId, int? ResultClientCaseId, int Version);

public sealed record SalesRequestStatusDto(int Id, string Key, string Title, int SortOrder, string FinalKind, IReadOnlyList<SalesRequestRuleDto> Rules);

public sealed record SalesRequestRuleDto(int Id, string Title, int? AssigneeUserId, int DueInMinutes);

public sealed record SalesRequestTaskDto(int Id, string Title, string Status, int? AssigneeUserId, string AssigneeName, DateTime DueAt);

public sealed record SalesRequestEventDto(int Id, string Kind, int? FromStatusId, int? ToStatusId, int? UserId, string? Comment, DateTime CreatedAt);

public sealed record SalesRequestDetailDto(SalesRequestDto Request, IReadOnlyList<SalesRequestTaskDto> Tasks, IReadOnlyList<SalesRequestEventDto> Events);

public sealed record SalesRequestClientCaseDto(int Id, string CaseNumber, string Title, string Status, DateTime? LastActivityAt);

public sealed record SalesRequestClientDto(int Id, string Name, string Phone, string Email, IReadOnlyList<SalesRequestClientCaseDto> Cases);

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

public sealed class SalesRequestTaskRequest
{
    public int AssigneeUserId { get; set; }
    public string? Title { get; set; }
    public DateTime? DueAt { get; set; }
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
        return Ok(statuses.Select(item => new SalesRequestStatusDto(item.Id, item.Key, item.Title, item.SortOrder, item.FinalKind,
            rules.Where(rule => rule.StatusId == item.Id).Select(rule => new SalesRequestRuleDto(rule.Id, rule.Title, rule.AssigneeUserId, rule.DueInMinutes)).ToList())).ToList());
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

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { total, page, pageSize, items = items.Select(Map).ToList() });
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
        return Ok(new SalesRequestDetailDto(Map(request), tasks, events));
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

        var clients = await _db.Clients.AsNoTracking()
            .Where(item => item.Name.Contains(text) || item.Phone.Contains(text) || item.Email.Contains(text))
            .OrderBy(item => item.Name).Take(10).Select(item => new { item.Id, item.Name, item.Phone, item.Email }).ToListAsync(ct);
        var ids = clients.Select(item => item.Id).ToList();
        var cases = await _db.ClientCases.AsNoTracking().Where(item => item.ClientId != null && ids.Contains(item.ClientId.Value))
            .OrderByDescending(item => item.Id).Select(item => new { item.Id, ClientId = item.ClientId!.Value, item.CaseNumber, item.Title, item.Status }).ToListAsync(ct);
        return Ok(clients.Select(client => new SalesRequestClientDto(client.Id, client.Name, client.Phone, client.Email,
            cases.Where(item => item.ClientId == client.Id).Take(20)
                .Select(item => new SalesRequestClientCaseDto(item.Id, item.CaseNumber, item.Title, item.Status.ToString(), null)).ToList())).ToList());
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
            request.Comment, request.ResponsibleUserId, request.Version), access.Visibility.UserId, ct));
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

    [HttpGet("report")]
    public async Task<ActionResult<SalesRequestReport>> Report([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
    {
        if (await AccessAsync(ct) is not { } access || !Allowed(access, p => p.CanView && (p.CanViewAnalytics)))
        {
            return Denied();
        }

        if (to < from || (to - from).TotalDays > 732)
        {
            return BadRequest(new { message = "Период отчёта должен быть не длиннее двух лет." });
        }

        var visible = access.Visibility.SeesAll ? null : access.Visibility.VisibleUserIds.Append(access.Visibility.UserId).Distinct().ToList();
        return Ok(await _service.BuildReportAsync(from, to, visible, ct));
    }

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

    private static SalesRequestDto Map(SalesRequest item) => new(
        item.Id, item.CreatedAt, item.Source, item.ClientId, item.ClientName, item.ClientPhone, item.ClientCaseId,
        item.LinkedCaseWasClosed, item.Type, item.Summary, item.StatusId, item.StatusEnteredAt, item.ResponsibleUserId,
        item.Amount, item.Comment, item.FinalKind, item.ClosedAt, item.PaidPaymentId, item.ResultClientCaseId, item.Version);
}
