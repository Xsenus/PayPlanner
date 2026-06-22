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
public class CasesController : ControllerBase
{
    private readonly PaymentContext _db;
    public CasesController(PaymentContext db) => _db = db;

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private async Task<IActionResult?> RequireCasesPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Cases, isAllowed, ct)
            ? null
            : Forbid();

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int? clientId, [FromQuery] int? legalEntityId, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var q = _db.ClientCases
            .Include(c => c.Client)
            .Include(c => c.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .AsQueryable();
        if (clientId.HasValue) q = q.Where(c => c.ClientId == clientId.Value);
        if (legalEntityId.HasValue) q = q.Where(c => c.LegalEntityId == legalEntityId.Value);
        var items = await q.AsNoTracking()
            .OrderBy(c => c.ClientId ?? int.MaxValue)
            .ThenBy(c => c.LegalEntityId ?? int.MaxValue)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var entity = await _db.ClientCases
            .Include(c => c.Client)
            .Include(c => c.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        ApplyCaseDetails(model, model);
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, model.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Created($"/api/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;

        var shouldCloseLinkedTasks = !IsFinishedCaseStatus(e.Status) && IsFinishedCaseStatus(model.Status);
        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;
        ApplyCaseDetails(e, model);

        if (shouldCloseLinkedTasks)
        {
            await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        }

        await _db.SaveChangesAsync(ct);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
        foreach (var p in payments) p.ClientCaseId = null;

        await _db.SaveChangesAsync(ct);

        _db.ClientCases.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult?> ValidateCaseOwnerAsync(
        int? clientId,
        int? legalEntityId,
        DataVisibilityAccess visibility,
        CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        }

        if (hasClient && !await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(visibility)
                .AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }

    private static void ApplyCaseDetails(ClientCase target, ClientCase source)
    {
        target.CaseNumber = source.CaseNumber?.Trim() ?? string.Empty;
        target.CaseType = source.CaseType?.Trim() ?? string.Empty;
        target.Stage = source.Stage?.Trim() ?? string.Empty;
        target.ResponsibleUserId = source.ResponsibleUserId;
        target.StartDate = source.StartDate;
        target.DueDate = source.DueDate;
        target.FeeAmount = source.FeeAmount;
        target.Priority = string.IsNullOrWhiteSpace(source.Priority) ? "normal" : source.Priority.Trim();
        target.CourtName = source.CourtName?.Trim() ?? string.Empty;
        target.CourtCaseNumber = source.CourtCaseNumber?.Trim() ?? string.Empty;
        target.CourtUrl = source.CourtUrl?.Trim() ?? string.Empty;
        target.NextHearingAt = source.NextHearingAt;
        target.Claimant = source.Claimant?.Trim() ?? string.Empty;
        target.Defendant = source.Defendant?.Trim() ?? string.Empty;
        target.ThirdParties = source.ThirdParties?.Trim() ?? string.Empty;
        target.Representatives = source.Representatives?.Trim() ?? string.Empty;
        target.JudgeName = source.JudgeName?.Trim() ?? string.Empty;
        target.Courtroom = source.Courtroom?.Trim() ?? string.Empty;
        target.CourtSecretaryContacts = source.CourtSecretaryContacts?.Trim() ?? string.Empty;
        target.ClaimFiledAt = source.ClaimFiledAt;
        target.AppealDeadlineAt = source.AppealDeadlineAt;
        target.MotionDeadlineAt = source.MotionDeadlineAt;
        target.ObjectionDeadlineAt = source.ObjectionDeadlineAt;
        target.DocumentsSummary = source.DocumentsSummary?.Trim() ?? string.Empty;
        target.EventHistory = source.EventHistory?.Trim() ?? string.Empty;
        target.Strategy = source.Strategy?.Trim() ?? string.Empty;
    }

    private static bool IsFinishedCaseStatus(ClientCaseStatus status)
        => status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;
}
