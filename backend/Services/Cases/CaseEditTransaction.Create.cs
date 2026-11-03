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
    public static async Task<IActionResult> RunCreateAsync(PaymentContext db, ClaimsPrincipal actor, ClientCase requested,
        Func<Task<IActionResult>> write, CancellationToken ct)
    {
        var actorId = actor.GetUserId();
        if (actor.Identity?.IsAuthenticated != true || actorId is not > 0) return new UnauthorizedResult();
        BalanceApplicationLease? lease = null;
        try
        {
            lease = await BalanceApplicationLease.BeginAsync(db, actorId.Value, ct);
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == actorId.Value && user.IsActive && user.IsApproved, ct))
                return new UnauthorizedResult();
            var access = await db.ResolveSectionAccessAsync(actor, RolePermissionCatalog.Cases, ct, requireApprovedUser: true);
            if (access?.Permission is not { CanView: true, CanCreate: true }) return new ForbidResult();
            if (requested.Id != 0) return new BadRequestObjectResult("Номер записи нового дела назначается автоматически. Откройте создание нового дела.");
            if (CaseCreationInput.Validate(db, requested) is { } graphError) return graphError;
            // A new case has no grandfathered owner or responsible user. Validate
            // every supplied link under the same locks as an ordinary edit.
            var validation = await ValidateEditedLinksAsync(db, new ClientCase(), requested, access.Visibility, ct);
            if (validation is not null) return validation;
            var result = await write();
            if (result is CreatedResult) await lease.CommitAsync(ct);
            return result;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && BalanceApplicationLease.IsConflict(error)
            && (lease?.CommitStarted != true || BalanceApplicationLease.IsDefiniteDatabaseAbort(error)))
        {
            return new ConflictObjectResult(new { message = "Права доступа или связанные записи изменены либо сейчас заняты. Обновите данные перед созданием дела." });
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }
}
