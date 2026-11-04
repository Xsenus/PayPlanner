using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Cases;

internal static partial class CaseEditTransaction
{
    public static Task<IActionResult> RunAsync(PaymentContext db, ClaimsPrincipal actor, int id,
        Func<ClientCase, SectionAccessContext, Task<IActionResult>> write, CancellationToken ct, Action? beforeCommit = null)
        => RunSingleAsync(db, actor, id, write, ct, deleting: false, beforeCommit);

    public static Task<IActionResult> RunDeleteAsync(PaymentContext db, ClaimsPrincipal actor, int id,
        Func<ClientCase, SectionAccessContext, Task<IActionResult>> write, CancellationToken ct)
        => RunSingleAsync(db, actor, id, write, ct, deleting: true);

    private static async Task<IActionResult> RunSingleAsync(PaymentContext db, ClaimsPrincipal actor, int id,
        Func<ClientCase, SectionAccessContext, Task<IActionResult>> write, CancellationToken ct, bool deleting, Action? beforeCommit = null)
    {
        var actorId = actor.GetUserId();
        if (actor.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = deleting
                ? await BalanceApplicationLease.BeginForCaseDeletionAsync(db, actorId.Value, ct)
                : await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return new UnauthorizedResult();
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
            if (access?.Permission is not { CanView: true } permission
                || !(deleting ? permission.CanDelete : permission.CanEdit)) return new ForbidResult();
            var clientCase = await db.ClientCases.ApplyClientCaseVisibility(access.Visibility).FirstOrDefaultAsync(row => row.Id == id, ct);
            if (clientCase is null) return new NotFoundResult();
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {id} FOR UPDATE NOWAIT", ct);
                if (!access.Visibility.SeesAll) await LockVisibleOwnersAsync(db, clientCase, ct);
            }
            if (deleting)
            {
                if (await HasForeignCourtDeletionReferencesAsync(db, id, ct))
                    return new ConflictObjectResult(new
                    {
                        message = "Удаление дела остановлено из-за несогласованных связей судебных записей. Обратитесь к администратору для проверки связей."
                    });
                await LockLinkedTasksAsync(db, [id], ct);
                if (db.Database.IsNpgsql())
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"ClientCaseId\" = {id} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            }
            var result = await write(clientCase, access);
            if (deleting ? result is NoContentResult : result is OkObjectResult)
            {
                beforeCommit?.Invoke();
                await lease.CommitAsync(ct);
            }
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new
            {
                message = deleting
                ? "Дело, связанные записи или права доступа изменены либо сейчас заняты. Обновите карточку перед удалением."
                : "Дело или права доступа изменены либо сейчас заняты. Обновите карточку перед сохранением."
            });
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private static async Task<bool> HasForeignCourtDeletionReferencesAsync(PaymentContext db, int id, CancellationToken ct)
    {
        // Pin both parents and existing dependents. An instance lock excludes new
        // foreign references, while a complaint lock excludes changing its case.
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"ClientCaseId\" = {id} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtComplaints\" WHERE \"CourtInstanceId\" IN (SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"ClientCaseId\" = {id}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"ClientCaseId\" = {id} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseHearingPreparations\" WHERE \"Id\" IN (SELECT \"ClientCaseHearingPreparationId\" FROM \"ClientCaseHearingPreparationDocuments\" WHERE \"ClientCaseDocumentId\" IN (SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"ClientCaseId\" = {id})) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"ClientCaseHearingPreparationId\", \"ClientCaseDocumentId\" FROM \"ClientCaseHearingPreparationDocuments\" WHERE \"ClientCaseDocumentId\" IN (SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"ClientCaseId\" = {id}) ORDER BY \"ClientCaseHearingPreparationId\", \"ClientCaseDocumentId\" FOR UPDATE NOWAIT", ct);
            if (!db.UseObjectAuditModel)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtEvents\" WHERE \"ClientCaseId\" = {id} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtEventHistory\" WHERE \"ClientCaseCourtEventId\" IN (SELECT \"Id\" FROM \"ClientCaseCourtEvents\" WHERE \"ClientCaseId\" = {id}) ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseProceduralDeadlines\" WHERE \"ClientCaseId\" = {id} ORDER BY \"Id\" FOR UPDATE NOWAIT", ct);
            }
        }
        // Archived complaints and hidden historical parents retain their links.
        if (await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AnyAsync(complaint => complaint.ClientCaseId != id
            && db.ClientCaseCourtInstances.IgnoreQueryFilters().Any(instance => instance.Id == complaint.CourtInstanceId && instance.ClientCaseId == id), ct)) return true;
        if (!db.UseObjectAuditModel && await db.ClientCaseCourtEventHistory.IgnoreQueryFilters().AnyAsync(history => history.ClientCaseId != id
            && db.ClientCaseCourtEvents.IgnoreQueryFilters().Any(courtEvent => courtEvent.Id == history.ClientCaseCourtEventId && courtEvent.ClientCaseId == id), ct)) return true;
        if (!db.UseObjectAuditModel)
        {
            if (await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AnyAsync(complaint => complaint.ClientCaseId != id
                && db.ClientCaseDocuments.IgnoreQueryFilters().Any(document => document.Id == complaint.ClientCaseDocumentId && document.ClientCaseId == id), ct)) return true;
            if (await db.ClientCaseCourtEvents.IgnoreQueryFilters().AnyAsync(courtEvent => courtEvent.ClientCaseId != id
                && db.ClientCaseDocuments.IgnoreQueryFilters().Any(document => document.Id == courtEvent.ClientCaseDocumentId && document.ClientCaseId == id), ct)) return true;
            if (await db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().AnyAsync(deadline => deadline.ClientCaseId != id
                && db.ClientCaseDocuments.IgnoreQueryFilters().Any(document => document.Id == deadline.ClientCaseDocumentId && document.ClientCaseId == id), ct)) return true;
            if (await db.ClientCaseCourtEvents.IgnoreQueryFilters().AnyAsync(courtEvent => courtEvent.ClientCaseId != id
                && db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().Any(deadline => deadline.Id == courtEvent.ProceduralDeadlineId && deadline.ClientCaseId == id), ct)) return true;
        }
        return await db.ClientCaseHearingPreparationDocuments.IgnoreQueryFilters().AnyAsync(link =>
            db.ClientCaseDocuments.IgnoreQueryFilters().Any(document => document.Id == link.ClientCaseDocumentId && document.ClientCaseId == id)
            && db.ClientCaseHearingPreparations.IgnoreQueryFilters().Any(preparation => preparation.Id == link.ClientCaseHearingPreparationId && preparation.ClientCaseId != id), ct);
    }

    public static async Task LockVisibleOwnersAsync(PaymentContext db, ClientCase clientCase, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return;
        var userIds = new HashSet<int>();
        if (clientCase.ResponsibleUserId is int assignee) userIds.Add(assignee);
        if (clientCase.ClientId is int clientId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {clientId} FOR SHARE NOWAIT", ct);
            var owner = await db.Clients.AsNoTracking().Where(row => row.Id == clientId).Select(row => row.ResponsibleUserId).FirstOrDefaultAsync(ct);
            if (owner is int userId) userIds.Add(userId);
        }
        if (clientCase.LegalEntityId is int legalId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legalId} FOR SHARE NOWAIT", ct);
            var owner = await db.LegalEntities.AsNoTracking().Where(row => row.Id == legalId).Select(row => row.ResponsibleUserId).FirstOrDefaultAsync(ct);
            if (owner is int userId) userIds.Add(userId);
        }
        if (userIds.Count > 0)
        {
            var ids = userIds.Order().ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = ANY ({ids}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
        }
    }
}
