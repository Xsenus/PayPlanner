using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed record DataVisibilityAccess(
    int UserId,
    bool IsAdmin,
    string Section,
    string ViewScope,
    IReadOnlyCollection<int> VisibleUserIds)
{
    public bool SeesAll => IsAdmin || string.Equals(ViewScope, DataVisibilityScope.All, StringComparison.OrdinalIgnoreCase);

    public bool CanSeeUser(int userId)
        => SeesAll || VisibleUserIds.Contains(userId);
}

public static class DataVisibilityService
{
    public static async Task<DataVisibilityAccess?> ResolveDataVisibilityAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        string section,
        CancellationToken ct = default)
    {
        if (principal is null)
        {
            return new DataVisibilityAccess(
                UserId: 0,
                IsAdmin: true,
                Section: section,
                ViewScope: DataVisibilityScope.All,
                VisibleUserIds: Array.Empty<int>());
        }

        var currentUserId = principal.GetUserId();
        var claimIsAdmin = principal.IsInRole("admin");
        if (!currentUserId.HasValue)
        {
            if (claimIsAdmin)
            {
                return new DataVisibilityAccess(
                    UserId: 0,
                    IsAdmin: true,
                    Section: section,
                    ViewScope: DataVisibilityScope.All,
                    VisibleUserIds: Array.Empty<int>());
            }

            return null;
        }

        var user = await db.Users
            .AsNoTracking()
            .Where(item => item.Id == currentUserId.Value && item.IsActive)
            .Select(item => new
            {
                item.Id,
                item.RoleId,
                item.DepartmentId,
                item.Department,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
            })
            .FirstOrDefaultAsync(ct);

        if (user is null)
        {
            var fallbackScope = claimIsAdmin ? DataVisibilityScope.All : DataVisibilityScope.Own;
            return new DataVisibilityAccess(
                UserId: currentUserId.Value,
                IsAdmin: claimIsAdmin,
                Section: section,
                ViewScope: NormalizeScope(section, fallbackScope, claimIsAdmin),
                VisibleUserIds: new[] { currentUserId.Value });
        }

        var isAdmin = claimIsAdmin
            || string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);

        var permission = await db.RolePermissions
            .AsNoTracking()
            .Where(permission => permission.RoleId == user.RoleId && permission.Section == section)
            .Select(permission => new
            {
                permission.CanView,
                permission.RoleId,
                permission.ViewScope,
            })
            .FirstOrDefaultAsync(ct);
        if (permission is null)
        {
            var fallbackSection = RolePermissionCatalog.GetFallbackSection(section);
            if (!string.IsNullOrWhiteSpace(fallbackSection))
            {
                permission = await db.RolePermissions
                    .AsNoTracking()
                    .Where(permission => permission.RoleId == user.RoleId && permission.Section == fallbackSection)
                    .Select(permission => new
                    {
                        permission.CanView,
                        permission.RoleId,
                        permission.ViewScope,
                    })
                    .FirstOrDefaultAsync(ct);
            }
        }

        if (!isAdmin && permission is not null && !permission.CanView)
        {
            return null;
        }

        var scope = NormalizeScope(section, permission?.ViewScope, isAdmin);
        var visibleUserIds = new List<int> { user.Id };

        if (!isAdmin
            && string.Equals(scope, DataVisibilityScope.Specific, StringComparison.OrdinalIgnoreCase)
            && permission is not null
            && string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase))
        {
            var selectedUserIds = await db.RoleCalendarVisibleUsers
                .AsNoTracking()
                .Where(item => item.RoleId == permission.RoleId
                    && item.User != null
                    && item.User.IsActive
                    && item.User.IsApproved)
                .Select(item => item.UserId)
                .ToListAsync(ct);

            visibleUserIds.AddRange(selectedUserIds);
        }
        else if (!isAdmin
                 && string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase)
                 && (user.DepartmentId.HasValue || !string.IsNullOrWhiteSpace(user.Department)))
        {
            var department = user.Department?.Trim();
            var departmentId = user.DepartmentId;
            var hasDepartmentName = !string.IsNullOrWhiteSpace(department);
            visibleUserIds = await db.Users
                .AsNoTracking()
                .Where(item => item.IsActive
                    && ((departmentId.HasValue && item.DepartmentId == departmentId.Value)
                        || (hasDepartmentName && item.Department != null && item.Department == department)))
                .Select(item => item.Id)
                .ToListAsync(ct);

            if (!visibleUserIds.Contains(user.Id))
            {
                visibleUserIds.Add(user.Id);
            }
        }

        return new DataVisibilityAccess(
            user.Id,
            isAdmin,
            section,
            scope,
            visibleUserIds.Distinct().ToArray());
    }

    private static string NormalizeScope(string section, string? rawScope, bool isAdmin)
    {
        if (isAdmin || !RolePermissionCatalog.HasDataScope(section))
        {
            return DataVisibilityScope.All;
        }

        return DataVisibilityScope.Normalize(
            rawScope,
            DataVisibilityScope.Own,
            includeSpecific: string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase));
    }
}
