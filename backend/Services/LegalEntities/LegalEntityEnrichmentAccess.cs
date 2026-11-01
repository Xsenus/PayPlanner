using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.LegalEntities;

public static class LegalEntityEnrichmentAccess
{
    public static async Task<bool> CanUseLegalEntityEnrichmentAsync(this PaymentContext db, ClaimsPrincipal userPrincipal, CancellationToken ct)
    {
        var userId = userPrincipal.GetUserId();
        if (userPrincipal.Identity?.IsAuthenticated != true || !userId.HasValue
            || !AuthSessionClaims.TryGetTokenVersion(userPrincipal, out var tokenVersion)) return false;

        var access = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId.Value && user.IsActive && user.IsApproved
                && user.AuthTokenVersion == tokenVersion && user.Role != null)
            .Select(user => new
            {
                RoleName = user.Role!.Name,
                HasAnyPermissions = db.RolePermissions.Any(permission => permission.RoleId == user.RoleId),
                CanEnrich = db.RolePermissions.Any(permission => permission.RoleId == user.RoleId && permission.CanView
                    && ((permission.Section == RolePermissionCatalog.Clients || permission.Section == RolePermissionCatalog.LegalEntities)
                            && (permission.CanCreate || permission.CanEdit)
                        || permission.Section == RolePermissionCatalog.CompanyProfile && permission.CanEdit)),
            }).SingleOrDefaultAsync(ct);

        if (access is null) return false;
        if (string.Equals(access.RoleName, "admin", StringComparison.OrdinalIgnoreCase) || access.CanEnrich) return true;
        if (access.HasAnyPermissions) return false;

        // Preserve the existing defaults for an assigned legacy role without configured permissions.
        var defaults = RolePermissionCatalog.BuildDefaults(allowRoleManagement: false);
        return defaults[RolePermissionCatalog.Clients] is { CanView: true } clients && (clients.CanCreate || clients.CanEdit)
            || defaults[RolePermissionCatalog.LegalEntities] is { CanView: true } legal && (legal.CanCreate || legal.CanEdit)
            || defaults[RolePermissionCatalog.CompanyProfile] is { CanView: true, CanEdit: true };
    }
}
