using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Cases;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/cases")]
[Authorize]
public class CasesV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public CasesV1Controller(PaymentContext db) => _db = db;

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private async Task<IActionResult?> RequireCasesPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Cases, isAllowed, ct)
            ? null
            : Forbid();

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        CancellationToken ct)
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

        var q = _db.ClientCases.AsQueryable()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .ApplyCaseFilters(clientId, legalEntityId, status, search)
            .ApplyCaseSort(sortBy, sortDir);

        return Ok(await q.AsNoTracking().ToListAsync(ct));
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
            .WithCaseIncludes()
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

        if (CaseCreationInput.Validate(_db, model) is { } graphError) return graphError;
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;

        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/v1/cases/{model.Id}", CaseEditSnapshot.ForReply(model));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        return await CaseEditTransaction.RunAsync(_db, User, id, async (e, access) =>
        {
            // V1 changes only these owner fields; its other supplied case fields stay ignored.
            var links = new ClientCase { ClientId = model.ClientId, LegalEntityId = model.LegalEntityId, ResponsibleUserId = e.ResponsibleUserId };
            var validation = await CaseEditTransaction.ValidateEditedLinksAsync(_db, e, links, access.Visibility, ct);
            if (validation is not null) return validation;

            var shouldCloseLinkedTasks = e.Status is not (ClientCaseStatus.Closed or ClientCaseStatus.Lost)
                && model.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;
            var shouldCancelFinalReport = e.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost
                && model.Status is not (ClientCaseStatus.Closed or ClientCaseStatus.Lost);
            var isCourtCase = shouldCloseLinkedTasks && (ClientCaseFinalReportTaskService.HasCourtFields(model)
                || (await ClientCaseFinalReportTaskService.FilterCourtCaseIdsAsync(_db, [e], ct)).Contains(e.Id));
            var finalReportAssignee = isCourtCase
                ? await ClientCaseFinalReportTaskService.ResolveAssigneeAsync(_db, ct)
                : null;
            if (isCourtCase && finalReportAssignee is null)
            {
                return BadRequest("Перед закрытием дела выберите в настройках журнала задач ответственного за итоговые отчёты с доступом ко всем делам.");
            }
            if (finalReportAssignee is not null && finalReportAssignee.Id != User.GetUserId())
                await CaseEditTransaction.LockUserAccessAsync(_db, finalReportAssignee.Id, ct);

            e.Title = model.Title;
            e.Description = model.Description;
            e.Status = model.Status;
            e.ClientId = model.ClientId;
            e.LegalEntityId = model.LegalEntityId;

            if (shouldCloseLinkedTasks || shouldCancelFinalReport) await CaseEditTransaction.LockLinkedTasksAsync(_db, [e.Id], ct);
            if (shouldCloseLinkedTasks)
            {
                await ClientCaseTaskSyncService.CloseActiveTasksForClosedCaseAsync(_db, e.Id, User.GetUserId(), User.GetDisplayName(), ct);
                if (isCourtCase)
                {
                    await ClientCaseFinalReportTaskService.CreateForClosedCasesAsync(_db, [e.Id], finalReportAssignee!, User.GetUserId(), User.GetDisplayName(), ct);
                }
            }
            if (shouldCancelFinalReport)
            {
                await ClientCaseFinalReportTaskService.CancelForReopenedCasesAsync(_db, [e.Id], User.GetUserId(), User.GetDisplayName(), ct);
            }

            await _db.SaveChangesAsync(ct);
            return Ok(CaseEditSnapshot.ForReply(e));
        }, ct);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        return await CaseEditTransaction.RunDeleteAsync(_db, User, id, async (e, _) =>
        {
            var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
            foreach (var p in payments) p.ClientCaseId = null;
            await _db.SaveChangesAsync(ct);
            _db.ClientCases.Remove(e);
            await _db.SaveChangesAsync(ct);
            return NoContent();
        }, ct);
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
}
