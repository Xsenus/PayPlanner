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
        var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Cases, ct);
        if (access?.Permission is not { CanView: true } permission) return Forbid();
        var visibility = access.Visibility;

        var q = _db.ClientCases.AsQueryable()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .ApplyCaseFilters(clientId, legalEntityId, status, search)
            .ApplyCaseSort(sortBy, sortDir);

        var items = await q.AsNoTracking().ToListAsync(ct);
        if (items.Count > 0) CaseResponsePrivacy.ApplyToRead(items, permission.CanEdit,
            await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static value => value.CanView, ct));
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Cases, ct);
        if (access?.Permission is not { CanView: true } permission) return Forbid();
        var visibility = access.Visibility;

        var entity = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is not null) CaseResponsePrivacy.ApplyToRead([entity], permission.CanEdit,
            await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static value => value.CanView, ct));
        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        var result = await CaseEditTransaction.RunCreateAsync(_db, User, model, async () =>
        {

            if (!await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static permission => permission.CanView, ct)) model.FeeAmount = 0m;
            _db.ClientCases.Add(model);
            await _db.SaveChangesAsync(ct);
            return Created($"/api/v1/cases/{model.Id}", CaseEditSnapshot.ForReply(model));
        }, ct);
        return result;
    }

    [HttpPut("{id:int}")]
    [HttpPut("{id:int}/checked")]
    public Task<IActionResult> UpdateWithSource(int id, [FromBody] CaseV1EditRequest model, CancellationToken ct)
        => model.ExpectedSource is not { HasObservedFields: true } source
            ? Task.FromResult<IActionResult>(BadRequest(new { message = "Для сохранения нужны исходные данные дела: название, описание, статус и владелец. Обновите карточку." }))
            : UpdateCoreAsync(id, model, source.ToCase(), ct);

    // Trusted in-process callers retain the existing business entry point.
    [NonAction]
    public Task<IActionResult> Update(int id, ClientCase model, CancellationToken ct)
        => UpdateCoreAsync(id, model, null, ct);

    private async Task<IActionResult> UpdateCoreAsync(int id, ClientCase model, ClientCase? expectedSource, CancellationToken ct)
    {
        return await CaseEditTransaction.RunAsync(_db, User, id, async (e, access) =>
        {
            if (expectedSource is not null && !CaseEditSnapshot.TryMergeV1(e, model, expectedSource))
                return Conflict(new { message = "Дело изменилось после открытия редактора. Обновите данные перед сохранением." });
            // The responsible user is not one of the fields edited by V1.
            var links = new ClientCase { ClientId = model.ClientId, LegalEntityId = model.LegalEntityId, ResponsibleUserId = e.ResponsibleUserId };
            var validation = await CaseEditTransaction.ValidateEditedLinksAsync(_db, e, links, access.Visibility, ct);
            if (validation is not null) return validation;

            var shouldCloseLinkedTasks = e.Status is not (ClientCaseStatus.Closed or ClientCaseStatus.Lost)
                && model.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost;
            var shouldCancelFinalReport = e.Status is ClientCaseStatus.Closed or ClientCaseStatus.Lost
                && model.Status is not (ClientCaseStatus.Closed or ClientCaseStatus.Lost);
            var isCourtCase = shouldCloseLinkedTasks
                && (await ClientCaseFinalReportTaskService.FilterCourtCaseIdsAsync(_db, [e], ct)).Contains(e.Id);
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
            var reply = CaseEditSnapshot.ForReply(e);
            if (!await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Payments, static permission => permission.CanView, ct)) CaseResponsePrivacy.HideFinanceData([reply]);
            return Ok(reply);
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

}
