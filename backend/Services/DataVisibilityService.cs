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

/// <summary>
/// Неизменяемый снимок прав операции и области видимости одного раздела.
/// Он позволяет контроллеру не читать пользователя, роль и право повторно
/// перед каждой последующей выборкой в рамках одного HTTP-запроса.
/// Отсутствующее право означает только legacy-видимость собственных данных
/// и не разрешает операции раздела.
/// </summary>
public sealed record SectionAccessContext(
    DataVisibilityAccess Visibility,
    RolePermissionValues? Permission);

public static class DataVisibilityService
{
    /// <summary>
    /// Одним SQL-запросом читает активного пользователя, его роль и право раздела,
    /// затем при необходимости отдельным ограниченным запросом получает сотрудников
    /// того же отдела или явно разрешённых пользователей календаря.
    /// </summary>
    /// <remarks>
    /// Метод предназначен для защищённых API. Идентификатор и роль из JWT не дают
    /// доступ сами по себе: пользователь, роль и право обязательно подтверждаются
    /// актуальными данными БД. Для администратора формируется полный набор операций.
    /// </remarks>
    /// <param name="db">Контекст базы данных.</param>
    /// <param name="principal">Текущий аутентифицированный пользователь.</param>
    /// <param name="section">Раздел из <see cref="RolePermissionCatalog"/>.</param>
    /// <param name="ct">Токен отмены запроса.</param>
    /// <returns>Снимок доступа или <see langword="null"/>, если доступ не подтверждён.</returns>
    public static async Task<SectionAccessContext?> ResolveSectionAccessAsync(
        this PaymentContext db,
        ClaimsPrincipal? principal,
        string section,
        CancellationToken ct = default)
    {
        var userId = principal?.GetUserId();
        if (!userId.HasValue)
        {
            return null;
        }

        var user = await (
            from item in db.Users.AsNoTracking()
            join configuredPermission in db.RolePermissions
                    .AsNoTracking()
                    .Where(permission => permission.Section == section)
                on item.RoleId equals configuredPermission.RoleId into configuredPermissions
            from configuredPermission in configuredPermissions.DefaultIfEmpty()
            where item.Id == userId.Value && item.IsActive
            select new
            {
                item.Id,
                item.RoleId,
                item.DepartmentId,
                item.Department,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
                HasAnyPermissions = db.RolePermissions.Any(permission => permission.RoleId == item.RoleId),
                CanView = configuredPermission == null ? (bool?)null : configuredPermission.CanView,
                CanCreate = configuredPermission == null ? (bool?)null : configuredPermission.CanCreate,
                CanEdit = configuredPermission == null ? (bool?)null : configuredPermission.CanEdit,
                CanDelete = configuredPermission == null ? (bool?)null : configuredPermission.CanDelete,
                CanExport = configuredPermission == null ? (bool?)null : configuredPermission.CanExport,
                CanViewAnalytics = configuredPermission == null ? (bool?)null : configuredPermission.CanViewAnalytics,
                ViewScope = configuredPermission == null ? null : configuredPermission.ViewScope,
                CanEditOthers = configuredPermission == null ? (bool?)null : configuredPermission.CanEditOthers,
                CanCompleteOthers = configuredPermission == null ? (bool?)null : configuredPermission.CanCompleteOthers,
                CanDeleteOthers = configuredPermission == null ? (bool?)null : configuredPermission.CanDeleteOthers,
            }).FirstOrDefaultAsync(ct);
        if (user is null)
        {
            return null;
        }

        var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
        if (!isAdmin
            && (user.CanView is false
                || user.CanView is null && user.HasAnyPermissions))
        {
            return null;
        }

        RolePermissionValues? permission = isAdmin
            ? new RolePermissionValues(true, true, true, true, true, true, DataVisibilityScope.All, true, true, true)
            : user.CanView is null
                ? null
                : new RolePermissionValues(
                    user.CanView.Value,
                    user.CanCreate.GetValueOrDefault(),
                    user.CanEdit.GetValueOrDefault(),
                    user.CanDelete.GetValueOrDefault(),
                    user.CanExport.GetValueOrDefault(),
                    user.CanViewAnalytics.GetValueOrDefault(),
                    user.ViewScope ?? DataVisibilityScope.Own,
                    user.CanEditOthers.GetValueOrDefault(),
                    user.CanCompleteOthers.GetValueOrDefault(),
                    user.CanDeleteOthers.GetValueOrDefault());
        var scope = NormalizeScope(section, permission?.ViewScope, isAdmin);
        IReadOnlyCollection<int> visibleUserIds = new[] { user.Id };

        if (!isAdmin
            && string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase)
            && (user.DepartmentId.HasValue || !string.IsNullOrWhiteSpace(user.Department)))
        {
            var departmentId = user.DepartmentId;
            var department = user.Department?.Trim();
            var hasDepartmentName = !string.IsNullOrWhiteSpace(department);
            visibleUserIds = (await db.Users
                    .AsNoTracking()
                    .Where(item => item.IsActive
                        && ((departmentId.HasValue && item.DepartmentId == departmentId.Value)
                            || (hasDepartmentName && item.Department != null && item.Department == department)))
                    .Select(item => item.Id)
                    .ToListAsync(ct))
                .Append(user.Id)
                .Distinct()
                .ToArray();
        }
        else if (!isAdmin
                 && string.Equals(scope, DataVisibilityScope.Specific, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase))
        {
            visibleUserIds = (await db.RoleCalendarVisibleUsers
                    .AsNoTracking()
                    .Where(item => item.RoleId == user.RoleId
                        && item.User != null
                        && item.User.IsActive
                        && item.User.IsApproved)
                    .Select(item => item.UserId)
                    .ToListAsync(ct))
                .Append(user.Id)
                .Distinct()
                .ToArray();
        }

        return new SectionAccessContext(
            new DataVisibilityAccess(user.Id, isAdmin, section, scope, visibleUserIds),
            permission);
    }

    /// <summary>
    /// Пакетно вычисляет видимость одного раздела для нескольких сотрудников.
    /// Метод загружает пользователей и права их ролей общими запросами, поэтому число
    /// обращений к базе не растёт вместе с количеством получателей уведомления.
    /// </summary>
    /// <param name="db">Контекст базы данных.</param>
    /// <param name="userIds">Идентификаторы сотрудников, для которых нужно проверить доступ.</param>
    /// <param name="section">Раздел из <see cref="RolePermissionCatalog"/>.</param>
    /// <param name="ct">Токен отмены операции.</param>
    /// <returns>
    /// Словарь по всем переданным идентификаторам. Значение <see langword="null"/>
    /// означает, что сотрудник отсутствует, отключён или не имеет права просмотра.
    /// </returns>
    public static async Task<IReadOnlyDictionary<int, DataVisibilityAccess?>> ResolveDataVisibilitiesForUsersAsync(
        this PaymentContext db,
        IEnumerable<int> userIds,
        string section,
        CancellationToken ct = default)
    {
        var normalizedUserIds = userIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        var result = normalizedUserIds.ToDictionary(userId => userId, _ => (DataVisibilityAccess?)null);
        if (normalizedUserIds.Length == 0)
        {
            return result;
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(user => normalizedUserIds.Contains(user.Id) && user.IsActive)
            .Select(user => new
            {
                user.Id,
                user.RoleId,
                user.DepartmentId,
                user.Department,
                RoleName = user.Role != null ? user.Role.Name : string.Empty,
                HasAnyPermissions = user.Role != null && user.Role.Permissions.Any(),
                SectionCanView = user.Role == null
                    ? null
                    : user.Role.Permissions
                        .Where(permission => permission.Section == section)
                        .Select(permission => (bool?)permission.CanView)
                        .FirstOrDefault(),
                SectionViewScope = user.Role == null
                    ? null
                    : user.Role.Permissions
                        .Where(permission => permission.Section == section)
                        .Select(permission => permission.ViewScope)
                        .FirstOrDefault(),
            })
            .ToListAsync(ct);
        if (users.Count == 0)
        {
            return result;
        }

        var departmentUsers = Array.Empty<(int Id, int? DepartmentId, string? Department)>();
        var needsDepartmentUsers = users.Any(user =>
        {
            var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
            return !isAdmin
                && user.SectionCanView is true
                && string.Equals(
                    NormalizeScope(section, user.SectionViewScope, isAdmin),
                    DataVisibilityScope.Department,
                    StringComparison.OrdinalIgnoreCase)
                && (user.DepartmentId.HasValue || !string.IsNullOrWhiteSpace(user.Department));
        });
        if (needsDepartmentUsers)
        {
            var departmentIds = users
                .Where(user => user.DepartmentId.HasValue)
                .Select(user => user.DepartmentId!.Value)
                .Distinct()
                .ToArray();
            var departmentNames = users
                .Select(user => user.Department?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            departmentUsers = await db.Users
                .AsNoTracking()
                .Where(user => user.IsActive
                    && ((user.DepartmentId.HasValue && departmentIds.Contains(user.DepartmentId.Value))
                        || (user.Department != null && departmentNames.Contains(user.Department))))
                .Select(user => new ValueTuple<int, int?, string?>(user.Id, user.DepartmentId, user.Department))
                .ToArrayAsync(ct);
        }

        foreach (var user in users)
        {
            var isAdmin = string.Equals(user.RoleName, "admin", StringComparison.OrdinalIgnoreCase);
            if (!isAdmin
                && (user.SectionCanView is null && user.HasAnyPermissions || user.SectionCanView is false))
            {
                continue;
            }

            var scope = NormalizeScope(section, user.SectionViewScope, isAdmin);
            IReadOnlyCollection<int> visibleUserIds = new[] { user.Id };
            if (!isAdmin
                && string.Equals(scope, DataVisibilityScope.Department, StringComparison.OrdinalIgnoreCase))
            {
                var department = user.Department?.Trim();
                visibleUserIds = departmentUsers
                    .Where(item =>
                        (user.DepartmentId.HasValue && item.DepartmentId == user.DepartmentId)
                        || (!string.IsNullOrWhiteSpace(department)
                            && string.Equals(item.Department, department, StringComparison.Ordinal)))
                    .Select(item => item.Id)
                    .Append(user.Id)
                    .Distinct()
                    .ToArray();
            }

            result[user.Id] = new DataVisibilityAccess(user.Id, isAdmin, section, scope, visibleUserIds);
        }

        return result;
    }

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
