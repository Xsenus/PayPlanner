using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Extensions;

public static class UserAccessExtensions
{
    public static async Task<bool> IsApprovedUserAdminAsync(this PaymentContext db, ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;
        var userId = principal.GetUserId();
        if (!userId.HasValue) return false;
        var roleName = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId.Value && user.IsActive && user.IsApproved)
            .Select(user => user.Role == null ? null : user.Role.Name).SingleOrDefaultAsync(ct);
        return string.Equals(roleName, "admin", StringComparison.OrdinalIgnoreCase);
    }

    public static Task<bool> IsUserAdminAsync(this PaymentContext db, int userId, CancellationToken ct = default)
        => db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.Role != null && u.Role.Name == "admin")
            .FirstOrDefaultAsync(ct);

    public static async Task<bool> IsUserAdminOrLegacyFallbackAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        CancellationToken ct = default)
    {
        if (principal is null)
        {
            return false;
        }

        var userId = principal.GetUserId();
        if (!userId.HasValue)
        {
            return principal.IsInRole("admin");
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(item => item.Id == userId.Value && item.IsActive)
            .Select(item => new
            {
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
            })
            .FirstOrDefaultAsync(ct);

        if (user is not null)
        {
            return string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
        }

        return !await db.Users.AsNoTracking().AnyAsync(ct) && principal.IsInRole("admin");
    }

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

        return null;
    }

    /// <summary>
    /// Проверяет право текущего пользователя на операцию раздела по актуальной роли в БД.
    /// Для существующего пользователя роль, право раздела и признак legacy-роли без настроек
    /// читаются одной SQL-командой, чтобы последовательные проверки контроллеров не создавали
    /// отдельные запросы к пользователю и таблице прав.
    /// </summary>
    /// <remarks>
    /// Роль администратора подтверждается только базой данных. Если у legacy-роли ещё нет ни
    /// одной строки прав, сохраняются исторические значения по умолчанию. Отсутствующий в уже
    /// заполненной БД пользователь не получает доступ даже при устаревшем admin-claim в токене.
    /// </remarks>
    public static Task<bool> HasUserSectionPermissionAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        string section,
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct = default)
        => HasUserSectionPermissionCoreAsync(db, principal, section, isAllowed, requireApprovedUser: false, ct);

    public static Task<bool> HasApprovedUserSectionPermissionAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        string section,
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct = default)
        => HasUserSectionPermissionCoreAsync(db, principal, section, isAllowed, requireApprovedUser: true, ct);

    private static async Task<bool> HasUserSectionPermissionCoreAsync(PaymentContext db, ClaimsPrincipal? principal,
        string section, Func<RolePermissionValues, bool> isAllowed, bool requireApprovedUser, CancellationToken ct)
    {
        if (requireApprovedUser && (principal?.Identity?.IsAuthenticated != true || !principal.GetUserId().HasValue)) return false;
        if (principal is null)
        {
            return true;
        }

        var userId = principal.GetUserId();
        if (!userId.HasValue)
        {
            return principal.IsInRole("admin") || principal.Identity?.IsAuthenticated != true;
        }

        var user = await (
            from item in db.Users.AsNoTracking()
            join configuredPermission in db.RolePermissions
                    .AsNoTracking()
                    .Where(permission => permission.Section == section)
                on item.RoleId equals configuredPermission.RoleId into configuredPermissions
            from configuredPermission in configuredPermissions.DefaultIfEmpty()
            where item.Id == userId.Value && item.IsActive && (!requireApprovedUser || item.IsApproved)
            select new
            {
                item.RoleId,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
                HasAnyPermissions = db.RolePermissions.Any(permission => permission.RoleId == item.RoleId),
                CanView = configuredPermission == null ? (bool?)null : configuredPermission.CanView,
                CanCreate = configuredPermission == null ? (bool?)null : configuredPermission.CanCreate,
                CanEdit = configuredPermission == null ? (bool?)null : configuredPermission.CanEdit,
                CanDelete = configuredPermission == null ? (bool?)null : configuredPermission.CanDelete,
                CanExport = configuredPermission == null ? (bool?)null : configuredPermission.CanExport,
                CanViewAnalytics = configuredPermission == null ? (bool?)null : configuredPermission.CanViewAnalytics,
                ViewScope = configuredPermission == null ? null : configuredPermission.ViewScope,
            }).FirstOrDefaultAsync(ct);

        if (user is null)
        {
            if (requireApprovedUser) return false;
            if (await db.Users.AsNoTracking().AnyAsync(ct))
            {
                return false;
            }

            var fallbackValues = principal.IsInRole("admin")
                ? RolePermissionCatalog.BuildDefaults(allowRoleManagement: true)[section]
                : RolePermissionCatalog.BuildRestrictedDefaults(allowRoleManagement: false)[section];
            return fallbackValues.CanView && isAllowed(fallbackValues);
        }

        var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
        if (isAdmin)
        {
            return true;
        }

        if (user.CanView is null)
        {
            if (!user.HasAnyPermissions)
            {
                var fallbackValues = RolePermissionCatalog.BuildDefaults(allowRoleManagement: false)[section];
                return fallbackValues.CanView && isAllowed(fallbackValues);
            }

            return false;
        }

        var values = new RolePermissionValues(
            user.CanView.Value,
            user.CanCreate.GetValueOrDefault(),
            user.CanEdit.GetValueOrDefault(),
            user.CanDelete.GetValueOrDefault(),
            user.CanExport.GetValueOrDefault(),
            user.CanViewAnalytics.GetValueOrDefault(),
            user.ViewScope ?? DataVisibilityScope.Own);

        return values.CanView && isAllowed(values);
    }
}
