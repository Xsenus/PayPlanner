using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

internal static class CaseDocumentUploadAuthorization
{
    internal static async Task<bool> CanPublishAsync(PaymentContext db, int actorId, int caseId, CancellationToken ct)
    {
        if (actorId <= 0 || caseId <= 0
            || !await db.Users.AsNoTracking().AnyAsync(u => u.Id == actorId && u.IsActive && u.IsApproved, ct)) return false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actorId.ToString(CultureInfo.InvariantCulture))], "case-storage-publication"));
        var access = await db.ResolveSectionAccessAsync(principal, RolePermissionCatalog.Cases, ct);
        return access?.Permission?.CanEdit == true && await db.ClientCases.AsNoTracking()
            .ApplyClientCaseVisibility(access.Visibility).AnyAsync(c => c.Id == caseId, ct);
    }
}
