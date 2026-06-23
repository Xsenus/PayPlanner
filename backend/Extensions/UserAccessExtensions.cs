using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

public static class UserAccessExtensions
{
    public static Task<bool> IsUserAdminAsync(this PaymentContext db, int userId, CancellationToken ct = default)
        => db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.Role != null && u.Role.Name == "admin")
            .FirstOrDefaultAsync(ct);

    public static async Task<RolePermission?> GetUserSectionPermissionAsync(
        this PaymentContext db,
        int userId,
        string section,
        CancellationToken ct = default)
    {
        var permission = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(
                u => db.RolePermissions
                    .Where(permission => permission.RoleId == u.RoleId && permission.Section == section))
            .FirstOrDefaultAsync(ct);

        if (permission is not null)
        {
            return permission;
        }

        var fallbackSection = RolePermissionCatalog.GetFallbackSection(section);
        if (string.IsNullOrWhiteSpace(fallbackSection))
        {
            return null;
        }

        return await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .SelectMany(
                u => db.RolePermissions
                    .Where(item => item.RoleId == u.RoleId && item.Section == fallbackSection))
            .FirstOrDefaultAsync(ct);
    }

    public static async Task<bool> HasUserSectionPermissionAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        string section,
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct = default)
    {
        if (principal is null)
        {
            return true;
        }

        var userId = principal.GetUserId();
        if (!userId.HasValue)
        {
            return principal.IsInRole("admin") || principal.Identity?.IsAuthenticated != true;
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(item => item.Id == userId.Value && item.IsActive)
            .Select(item => new
            {
                item.RoleId,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
            })
            .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            if (await db.Users.AsNoTracking().AnyAsync(ct))
            {
                return false;
            }

            var fallbackValues = RolePermissionCatalog.BuildDefaults(allowRoleManagement: principal.IsInRole("admin"))[section];
            return fallbackValues.CanView && isAllowed(fallbackValues);
        }

        var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
        if (isAdmin)
        {
            return true;
        }

        var permission = await db.RolePermissions
            .AsNoTracking()
            .Where(item => item.RoleId == user.RoleId && item.Section == section)
            .FirstOrDefaultAsync(ct);
        if (permission is null)
        {
            var fallbackSection = RolePermissionCatalog.GetFallbackSection(section);
            if (!string.IsNullOrWhiteSpace(fallbackSection))
            {
                permission = await db.RolePermissions
                    .AsNoTracking()
                    .Where(item => item.RoleId == user.RoleId && item.Section == fallbackSection)
                    .FirstOrDefaultAsync(ct);
            }
        }

        var values = permission is null
            ? RolePermissionCatalog.BuildDefaults(allowRoleManagement: false)[section]
            : new RolePermissionValues(
                permission.CanView,
                permission.CanCreate,
                permission.CanEdit,
                permission.CanDelete,
                permission.CanExport,
                permission.CanViewAnalytics,
                permission.ViewScope);

        return values.CanView && isAllowed(values);
    }
}
