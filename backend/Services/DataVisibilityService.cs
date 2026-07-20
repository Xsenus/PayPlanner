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
    public static async Task<IReadOnlyDictionary<string, DataVisibilityAccess?>> ResolveDataVisibilitiesAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        IEnumerable<string> sections,
        CancellationToken ct = default)
    {
        var normalizedSections = sections
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new Dictionary<string, DataVisibilityAccess?>(StringComparer.OrdinalIgnoreCase);
        if (normalizedSections.Length == 0)
        {
            return result;
        }

        if (principal is null)
        {
            foreach (var section in normalizedSections)
            {
                result[section] = CreateUnrestrictedAccess(section);
            }

            return result;
        }

        var currentUserId = principal.GetUserId();
        var claimIsAdmin = principal.IsInRole("admin");
        if (!currentUserId.HasValue)
        {
            foreach (var section in normalizedSections)
            {
                result[section] = claimIsAdmin ? CreateUnrestrictedAccess(section) : null;
            }

            return result;
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
            var databaseHasUsers = await db.Users.AsNoTracking().AnyAsync(ct);
            foreach (var section in normalizedSections)
            {
                result[section] = databaseHasUsers
                    ? null
                    : new DataVisibilityAccess(
                        currentUserId.Value,
                        claimIsAdmin,
                        section,
                        NormalizeScope(section, claimIsAdmin ? DataVisibilityScope.All : DataVisibilityScope.Own, claimIsAdmin),
                        new[] { currentUserId.Value });
            }

            return result;
        }

        var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
        var permissions = await db.RolePermissions
            .AsNoTracking()
            .Where(permission => permission.RoleId == user.RoleId)
            .Select(permission => new
            {
                permission.Section,
                permission.CanView,
                permission.RoleId,
                permission.ViewScope,
            })
            .ToListAsync(ct);
        var permissionsBySection = permissions
            .Where(permission => normalizedSections.Contains(permission.Section, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(permission => permission.Section, StringComparer.OrdinalIgnoreCase);

        var departmentVisibleUserIds = Array.Empty<int>();
        var needsDepartmentUsers = !isAdmin && normalizedSections.Any(section =>
            permissionsBySection.TryGetValue(section, out var permission)
            && permission.CanView
            && string.Equals(
                NormalizeScope(section, permission.ViewScope, isAdmin),
                DataVisibilityScope.Department,
                StringComparison.OrdinalIgnoreCase));
        if (needsDepartmentUsers && (user.DepartmentId.HasValue || !string.IsNullOrWhiteSpace(user.Department)))
        {
            var department = user.Department?.Trim();
            var departmentId = user.DepartmentId;
            var hasDepartmentName = !string.IsNullOrWhiteSpace(department);
            departmentVisibleUserIds = await db.Users
                .AsNoTracking()
                .Where(item => item.IsActive
                    && ((departmentId.HasValue && item.DepartmentId == departmentId.Value)
                        || (hasDepartmentName && item.Department != null && item.Department == department)))
                .Select(item => item.Id)
                .ToArrayAsync(ct);
        }

        foreach (var section in normalizedSections)
        {
            permissionsBySection.TryGetValue(section, out var permission);
            if (!isAdmin && (permission is null && permissions.Count > 0 || permission is { CanView: false }))
            {
                result[section] = null;
                continue;
            }

            var scope = NormalizeScope(section, permission?.ViewScope, isAdmin);
            IReadOnlyCollection<int> visibleUserIds = new[] { user.Id };
            if (!isAdmin && string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
            {
                visibleUserIds = departmentVisibleUserIds
                    .Append(user.Id)
                    .Distinct()
                    .ToArray();
            }
            else if (!isAdmin
                     && string.Equals(scope, DataVisibilityScope.Specific, StringComparison.OrdinalIgnoreCase)
                     && permission is not null
                     && string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase))
            {
                visibleUserIds = (await db.RoleCalendarVisibleUsers
                        .AsNoTracking()
                        .Where(item => item.RoleId == permission.RoleId
                            && item.User != null
                            && item.User.IsActive
                            && item.User.IsApproved)
                        .Select(item => item.UserId)
                        .ToListAsync(ct))
                    .Append(user.Id)
                    .Distinct()
                    .ToArray();
            }

            result[section] = new DataVisibilityAccess(user.Id, isAdmin, section, scope, visibleUserIds);
        }

        return result;
    }

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
            if (await db.Users.AsNoTracking().AnyAsync(ct))
            {
                return null;
            }

            var fallbackScope = claimIsAdmin ? DataVisibilityScope.All : DataVisibilityScope.Own;
            return new DataVisibilityAccess(
                UserId: currentUserId.Value,
                IsAdmin: claimIsAdmin,
                Section: section,
                ViewScope: NormalizeScope(section, fallbackScope, claimIsAdmin),
                VisibleUserIds: new[] { currentUserId.Value });
        }

        var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);

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
        if (!isAdmin && permission is null)
        {
            if (await db.RolePermissions.AsNoTracking().AnyAsync(item => item.RoleId == user.RoleId, ct))
            {
                return null;
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

    private static DataVisibilityAccess CreateUnrestrictedAccess(string section)
        => new(
            UserId: 0,
            IsAdmin: true,
            Section: section,
            ViewScope: DataVisibilityScope.All,
            VisibleUserIds: Array.Empty<int>());
}
