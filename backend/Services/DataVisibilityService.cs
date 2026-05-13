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

        var rawScope = await db.RolePermissions
            .AsNoTracking()
            .Where(permission => permission.RoleId == user.RoleId && permission.Section == section)
            .Select(permission => permission.ViewScope)
            .FirstOrDefaultAsync(ct);

        var scope = NormalizeScope(section, rawScope, isAdmin);
        var visibleUserIds = new List<int> { user.Id };

        if (!isAdmin
            && string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(user.Department))
        {
            var department = user.Department.Trim();
            visibleUserIds = await db.Users
                .AsNoTracking()
                .Where(item => item.IsActive && item.Department != null && item.Department == department)
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

        return DataVisibilityScope.Normalize(rawScope, DataVisibilityScope.Own);
    }
}
