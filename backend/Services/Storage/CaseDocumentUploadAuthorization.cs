using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Cases;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services.Storage;

internal static class CaseDocumentUploadAuthorization
{
    internal static async Task<bool> CanPublishAsync(PaymentContext db, int actorId, int caseId, CancellationToken ct,
        bool pinForPublication = false)
    {
        if (actorId <= 0 || caseId <= 0) return false;
        if (pinForPublication)
        {
            await BalanceApplicationLease.PinActorAsync(db, actorId, ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {caseId} FOR UPDATE NOWAIT", ct);
        }
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == actorId && u.IsActive && u.IsApproved, ct)) return false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString(CultureInfo.InvariantCulture))], "case-storage-publication"));
        var access = await db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Cases, ct);
        if (access?.Permission?.CanEdit != true) return false;
        var visible = db.ClientCases.AsNoTracking().ApplyClientCaseVisibility(access.Visibility).Where(c => c.Id == caseId);
        if (!pinForPublication || !db.Database.IsNpgsql() || access.Visibility.SeesAll) return await visible.AnyAsync(ct);
        var owners = await visible.Select(c => new ClientCase
        { Id = c.Id, ClientId = c.ClientId, LegalEntityId = c.LegalEntityId, ResponsibleUserId = c.ResponsibleUserId }).SingleOrDefaultAsync(ct);
        if (owners is null) return false;
        await CaseEditTransaction.LockVisibleOwnersAsync(db, owners, ct);
        return true;
    }
}
