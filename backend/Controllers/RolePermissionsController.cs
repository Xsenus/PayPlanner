using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using System.Globalization;
using System.Text;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/role-permissions")]
[Authorize]
public class RolePermissionsController : ControllerBase
{
    private const string SystemAdministratorRoleName = "admin";

    private readonly PaymentContext _db;
    private readonly RoleManagementAccessService _roleAccess;
    private readonly INotificationService? _notificationService;

    public RolePermissionsController(
        PaymentContext db,
        RoleManagementAccessService roleAccess,
        INotificationService? notificationService = null)
    {
        _db = db;
        _roleAccess = roleAccess;
        _notificationService = notificationService;
    }

    [HttpGet("audit")]
    public async Task<ActionResult<RolePermissionAuditReportDto>> GetAudit(CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!await CanViewRoleAuditAsync(access, ct))
        {
            return Forbid();
        }

        var roles = await _db.Roles
            .AsNoTracking()
            .Include(role => role.Permissions)
            .Include(role => role.Users)
            .OrderBy(role => role.Name)
            .ToListAsync(ct);

        var roleAudits = roles
            .Select(BuildAuditRole)
            .ToList();

        return Ok(new RolePermissionAuditReportDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TotalRoles = roleAudits.Count,
            ReadyRoles = roleAudits.Count(role => !role.HasIssues),
            RolesWithIssues = roleAudits.Count(role => role.HasIssues),
            RolesWithBroadDataScope = roleAudits.Count(role => role.BroadDataScopeSections.Count > 0),
            CatalogSections = RolePermissionCatalog.AllSections,
            DataScopedSections = RolePermissionCatalog.DataScopedSections,
            Roles = roleAudits
        });
    }

    [HttpGet("audit.csv")]
    public async Task<IActionResult> ExportAuditCsv(CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!await CanViewRoleAuditAsync(access, ct))
        {
            return Forbid();
        }

        var roles = await _db.Roles
            .AsNoTracking()
            .Include(role => role.Permissions)
            .Include(role => role.Users)
            .OrderBy(role => role.Name)
            .ToListAsync(ct);

        var roleAudits = roles
            .Select(BuildAuditRole)
            .ToList();

        var bytes = BuildAuditCsv(roleAudits);
        var fileName = $"role-permissions-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    private async Task<bool> CanViewRoleAuditAsync(RoleManagementAccessSnapshot access, CancellationToken ct)
    {
        if (access.IsAdmin)
        {
            return true;
        }

        if (!access.CanView)
        {
            return false;
        }

        return await _db.HasUserSectionPermissionAsync(
            User,
            RolePermissionCatalog.Audit,
            static permission => permission.CanView,
            ct);
    }

    [HttpGet("{roleId:int}")]
    public async Task<ActionResult<RolePermissionsDto>> Get(int roleId, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin && !access.CanView && access.RoleId != roleId)
        {
            return Forbid();
        }

        var roleInfo = await _db.Roles
            .AsNoTracking()
            .Where(role => role.Id == roleId)
            .Select(role => new
            {
                role.Id,
                role.Name
            })
            .FirstOrDefaultAsync(ct);

        if (roleInfo is null)
        {
            return NotFound();
        }

        var permissions = await _db.RolePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        var calendarVisibleUserIds = await _db.RoleCalendarVisibleUsers
            .AsNoTracking()
            .Where(item => item.RoleId == roleId && item.User != null && item.User.IsActive && item.User.IsApproved)
            .OrderBy(item => item.UserId)
            .Select(item => item.UserId)
            .ToListAsync(ct);

        return Ok(MapToDto(permissions, IsProtectedRole(roleInfo.Name), calendarVisibleUserIds));
    }

    [HttpGet("calendar-users")]
    public async Task<ActionResult<IReadOnlyCollection<RoleCalendarVisibleUserOptionDto>>> GetCalendarUsers(CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin && !access.CanView && !access.CanEdit)
        {
            return Forbid();
        }

        var users = await _db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved)
            .OrderBy(user => user.FullName == "" ? user.Email : user.FullName)
            .ThenBy(user => user.Email)
            .Select(user => new RoleCalendarVisibleUserOptionDto
            {
                Id = user.Id,
                Name = user.FullName == "" ? user.Email : user.FullName,
                Email = user.Email,
                DepartmentId = user.DepartmentId,
                Department = user.Department,
                RoleName = user.Role == null ? string.Empty : user.Role.Name,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPut("{roleId:int}")]
    public async Task<IActionResult> Update(int roleId, [FromBody] RolePermissionsDto request, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin && !access.CanEdit)
        {
            return Forbid();
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            return NotFound();
        }

        if (IsProtectedRole(role.Name))
        {
            return BadRequest(new { message = "Стандартную роль администратора нельзя изменять." });
        }

        var existing = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        var bySection = existing.ToDictionary(rp => rp.Section, StringComparer.OrdinalIgnoreCase);
        var incoming = Flatten(request);

        foreach (var section in RolePermissionCatalog.AllSections)
        {
            var values = Normalize(section, incoming[section]);
            if (bySection.TryGetValue(section, out var entity))
            {
                Apply(entity, values);
            }
            else
            {
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    Section = section,
                    CanView = values.CanView,
                    CanCreate = values.CanCreate,
                    CanEdit = values.CanEdit,
                    CanDelete = values.CanDelete,
                    CanExport = values.CanExport,
                    CanViewAnalytics = values.CanViewAnalytics,
                    CanEditOthers = values.CanEditOthers,
                    CanCompleteOthers = values.CanCompleteOthers,
                    CanDeleteOthers = values.CanDeleteOthers,
                    ViewScope = values.ViewScope,
                });
            }
        }

        var unused = existing
            .Where(rp => !RolePermissionCatalog.AllSections.Contains(rp.Section, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unused.Count > 0)
        {
            _db.RolePermissions.RemoveRange(unused);
        }

        int[]? previousCalendarVisibleUserIds = null;
        if (request.Calendar.VisibleUserIds is not null)
        {
            previousCalendarVisibleUserIds = await _db.RoleCalendarVisibleUsers
                .AsNoTracking()
                .Where(item => item.RoleId == roleId)
                .OrderBy(item => item.UserId)
                .Select(item => item.UserId)
                .ToArrayAsync(ct);

            var validation = await SyncCalendarVisibleUsersAsync(roleId, request.Calendar.VisibleUserIds, ct);
            if (validation is not null)
            {
                return validation;
            }
        }

        StampPermissionsAudit(role);
        await _db.SaveChangesAsync(ct);
        if (_notificationService is not null && previousCalendarVisibleUserIds is not null)
        {
            var currentCalendarVisibleUserIds = request.Calendar.VisibleUserIds!
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            var addedVisibleUserIds = currentCalendarVisibleUserIds.Except(previousCalendarVisibleUserIds).ToArray();
            var removedVisibleUserIds = previousCalendarVisibleUserIds.Except(currentCalendarVisibleUserIds).ToArray();
            await _notificationService.NotifyCalendarAccessChangedAsync(
                roleId,
                addedVisibleUserIds,
                removedVisibleUserIds,
                User.GetDisplayName(),
                User.GetUserId() ?? 0,
                ct);
        }

        return NoContent();
    }

    [HttpDelete("{roleId:int}")]
    public async Task<IActionResult> Reset(int roleId, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin && !access.CanEdit)
        {
            return Forbid();
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null)
        {
            return NotFound();
        }

        if (IsProtectedRole(role.Name))
        {
            return BadRequest(new { message = "Стандартную роль администратора нельзя изменять." });
        }

        var defaults = RolePermissionCatalog.BuildDefaultsForRole(role.Name, allowRoleManagement: false);
        var existing = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        var bySection = existing.ToDictionary(rp => rp.Section, StringComparer.OrdinalIgnoreCase);
        foreach (var section in RolePermissionCatalog.AllSections)
        {
            var values = defaults[section];
            if (bySection.TryGetValue(section, out var entity))
            {
                Apply(entity, values);
            }
            else
            {
                _db.RolePermissions.Add(new RolePermission
                {
                    RoleId = roleId,
                    Section = section,
                    CanView = values.CanView,
                    CanCreate = values.CanCreate,
                    CanEdit = values.CanEdit,
                    CanDelete = values.CanDelete,
                    CanExport = values.CanExport,
                    CanViewAnalytics = values.CanViewAnalytics,
                    CanEditOthers = values.CanEditOthers,
                    CanCompleteOthers = values.CanCompleteOthers,
                    CanDeleteOthers = values.CanDeleteOthers,
                    ViewScope = values.ViewScope,
                });
            }
        }

        var unused = existing
            .Where(rp => !RolePermissionCatalog.AllSections.Contains(rp.Section, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unused.Count > 0)
        {
            _db.RolePermissions.RemoveRange(unused);
        }

        var calendarVisibleUsers = await _db.RoleCalendarVisibleUsers
            .Where(item => item.RoleId == roleId)
            .ToListAsync(ct);
        if (calendarVisibleUsers.Count > 0)
        {
            _db.RoleCalendarVisibleUsers.RemoveRange(calendarVisibleUsers);
        }

        StampPermissionsAudit(role);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static RolePermissionsDto MapToDto(
        IReadOnlyCollection<RolePermission> permissions,
        bool allowRoleManagement,
        IReadOnlyCollection<int>? calendarVisibleUserIds = null)
    {
        var dto = allowRoleManagement
            ? RolePermissionCatalog.CreateDto(allowRoleManagement: true)
            : CreateRestrictedDto();
        foreach (var permission in permissions)
        {
            switch (permission.Section)
            {
                case RolePermissionCatalog.Calendar:
                    dto.Calendar = new CalendarPermissionsDto
                    {
                        CanView = permission.CanView,
                        CanCreate = permission.CanCreate,
                        CanEdit = permission.CanEdit,
                        CanDelete = permission.CanDelete,
                        CanExport = permission.CanExport,
                        CanViewAnalytics = permission.CanViewAnalytics,
                        ViewScope = NormalizeViewScope(RolePermissionCatalog.Calendar, permission.ViewScope),
                        VisibleUserIds = calendarVisibleUserIds?.Distinct().OrderBy(id => id).ToList() ?? [],
                    };
                    break;
                case RolePermissionCatalog.Reports:
                    dto.Reports = ToSection(permission);
                    break;
                case RolePermissionCatalog.Tasks:
                    dto.Tasks = ToTasks(permission);
                    break;
                case RolePermissionCatalog.Notes:
                    dto.Notes = ToSection(permission);
                    break;
                case RolePermissionCatalog.Calculator:
                    dto.Calculator = ToSection(permission);
                    break;
                case RolePermissionCatalog.LegalEntities:
                    dto.LegalEntities = ToSection(permission);
                    break;
                case RolePermissionCatalog.CompanyProfile:
                    dto.CompanyProfile = ToSection(permission);
                    break;
                case RolePermissionCatalog.Clients:
                    dto.Clients = ToSection(permission);
                    break;
                case RolePermissionCatalog.Cases:
                    dto.Cases = ToSection(permission);
                    break;
                case RolePermissionCatalog.Accounts:
                    dto.Accounts = ToSection(permission);
                    break;
                case RolePermissionCatalog.Acts:
                    dto.Acts = ToSection(permission);
                    break;
                case RolePermissionCatalog.Payments:
                    dto.Payments = ToSection(permission);
                    break;
                case RolePermissionCatalog.Contracts:
                    dto.Contracts = ToSection(permission);
                    break;
                case RolePermissionCatalog.Dictionaries:
                    dto.Dictionaries = ToSection(permission);
                    break;
                case RolePermissionCatalog.Notifications:
                    dto.Notifications = ToSection(permission);
                    break;
                case RolePermissionCatalog.Roles:
                    dto.Roles = ToSection(permission);
                    break;
                case RolePermissionCatalog.Audit:
                    dto.Audit = ToSection(permission);
                    break;
                case RolePermissionCatalog.Integrations:
                    dto.Integrations = ToSection(permission);
                    break;
                case RolePermissionCatalog.Requests:
                    dto.Requests = RolePermissionCatalog.ToRequests(new RolePermissionValues(
                        permission.CanView, permission.CanCreate, permission.CanEdit, permission.CanDelete,
                        permission.CanExport, permission.CanViewAnalytics,
                        NormalizeViewScope(permission.Section, permission.ViewScope)));
                    break;
                case RolePermissionCatalog.YandexDisk:
                    dto.YandexDisk = ToSection(permission);
                    break;
                case RolePermissionCatalog.S3Storage:
                    dto.S3Storage = ToSection(permission);
                    break;
            }
        }

        if (allowRoleManagement)
        {
            dto.Tasks.CanEditOthers = true;
            dto.Tasks.CanCompleteOthers = true;
            dto.Tasks.CanDeleteOthers = true;
        }

        return dto;
    }

    private static RolePermissionsDto CreateRestrictedDto()
    {
        var defaults = RolePermissionCatalog.BuildRestrictedDefaults(allowRoleManagement: false);
        return new RolePermissionsDto
        {
            Calendar = RolePermissionCatalog.ToCalendar(defaults[RolePermissionCatalog.Calendar]),
            Tasks = RolePermissionCatalog.ToTasks(defaults[RolePermissionCatalog.Tasks]),
            Notes = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Notes]),
            Reports = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Reports]),
            Calculator = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Calculator]),
            LegalEntities = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.LegalEntities]),
            CompanyProfile = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.CompanyProfile]),
            Clients = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Clients]),
            Cases = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Cases]),
            Accounts = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Accounts]),
            Acts = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Acts]),
            Payments = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Payments]),
            Contracts = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Contracts]),
            Dictionaries = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Dictionaries]),
            Notifications = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Notifications]),
            Roles = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Roles]),
            Audit = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Audit]),
            Integrations = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.Integrations]),
            Requests = RolePermissionCatalog.ToRequests(defaults[RolePermissionCatalog.Requests]),
            YandexDisk = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.YandexDisk]),
            S3Storage = RolePermissionCatalog.ToSection(defaults[RolePermissionCatalog.S3Storage])
        };
    }

    private async Task<IActionResult?> SyncCalendarVisibleUsersAsync(
        int roleId,
        IReadOnlyCollection<int> requestedUserIds,
        CancellationToken ct)
    {
        var requested = requestedUserIds
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (requested.Length == 0)
        {
            var allExisting = await _db.RoleCalendarVisibleUsers
                .Where(item => item.RoleId == roleId)
                .ToListAsync(ct);
            if (allExisting.Count > 0)
            {
                _db.RoleCalendarVisibleUsers.RemoveRange(allExisting);
            }

            return null;
        }

        var validUserIds = await _db.Users
            .AsNoTracking()
            .Where(user => requested.Contains(user.Id) && user.IsActive && user.IsApproved)
            .Select(user => user.Id)
            .ToListAsync(ct);

        var invalidUserIds = requested.Except(validUserIds).ToArray();
        if (invalidUserIds.Length > 0)
        {
            return BadRequest(new
            {
                message = "Для видимости календарей выбраны несуществующие или неактивные пользователи.",
                userIds = invalidUserIds,
            });
        }

        var existing = await _db.RoleCalendarVisibleUsers
            .Where(item => item.RoleId == roleId)
            .ToListAsync(ct);
        var existingUserIds = existing.Select(item => item.UserId).ToHashSet();
        var requestedSet = requested.ToHashSet();

        var toRemove = existing
            .Where(item => !requestedSet.Contains(item.UserId))
            .ToList();
        if (toRemove.Count > 0)
        {
            _db.RoleCalendarVisibleUsers.RemoveRange(toRemove);
        }

        foreach (var userId in requested.Where(userId => !existingUserIds.Contains(userId)))
        {
            _db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser
            {
                RoleId = roleId,
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                CreatedByUserId = User.GetUserId(),
                CreatedByUserName = User.GetDisplayName(),
            });
        }

        return null;
    }

    private static RolePermissionAuditRoleDto BuildAuditRole(Role role)
    {
        var catalog = new HashSet<string>(RolePermissionCatalog.AllSections, StringComparer.OrdinalIgnoreCase);
        var permissions = role.Permissions.ToList();
        var permissionGroups = permissions
            .GroupBy(permission => permission.Section, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var configuredSections = permissionGroups
            .Where(group => catalog.Contains(group.Key))
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingSections = RolePermissionCatalog.AllSections
            .Where(section => !configuredSections.Contains(section))
            .ToArray();

        var unknownSections = permissionGroups
            .Where(group => !catalog.Contains(group.Key))
            .Select(group => group.Key)
            .OrderBy(section => section)
            .ToArray();

        var duplicateSections = permissionGroups
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(section => section)
            .ToArray();

        var invalidViewScopeSections = permissions
            .Where(permission => catalog.Contains(permission.Section))
            .Where(HasInvalidViewScope)
            .Select(FormatSectionScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section)
            .ToArray();

        var capabilityWithoutViewSections = permissions
            .Where(permission => catalog.Contains(permission.Section))
            .Where(permission => !permission.CanView && HasAnyCapability(permission))
            .Select(permission => permission.Section)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section)
            .ToArray();

        var restrictedCapabilitySections = permissions
            .Where(permission => catalog.Contains(permission.Section))
            .Where(HasRestrictedCapability)
            .Select(permission => permission.Section)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section)
            .ToArray();

        var broadDataScopeSections = permissions
            .Where(permission => catalog.Contains(permission.Section))
            .Where(permission => permission.CanView)
            .Where(permission => RolePermissionCatalog.HasDataScope(permission.Section))
            .Where(permission => string.Equals(DataVisibilityScope.Normalize(permission.ViewScope), DataVisibilityScope.All, StringComparison.Ordinal))
            .Select(permission => permission.Section)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(section => section)
            .ToArray();

        var hasIssues = missingSections.Length > 0
            || unknownSections.Length > 0
            || duplicateSections.Length > 0
            || invalidViewScopeSections.Length > 0
            || capabilityWithoutViewSections.Length > 0
            || restrictedCapabilitySections.Length > 0;

        return new RolePermissionAuditRoleDto
        {
            RoleId = role.Id,
            RoleName = role.Name,
            IsSystemRole = IsProtectedRole(role.Name),
            AssignedUsersCount = role.Users.Count,
            ConfiguredSectionsCount = configuredSections.Count,
            TotalSectionsCount = RolePermissionCatalog.AllSections.Length,
            HasIssues = hasIssues,
            MissingSections = missingSections,
            UnknownSections = unknownSections,
            DuplicateSections = duplicateSections,
            InvalidViewScopeSections = invalidViewScopeSections,
            CapabilityWithoutViewSections = capabilityWithoutViewSections,
            RestrictedCapabilitySections = restrictedCapabilitySections,
            BroadDataScopeSections = broadDataScopeSections
        };
    }

    private static byte[] BuildAuditCsv(IReadOnlyCollection<RolePermissionAuditRoleDto> roles)
    {
        var rows = new List<string>
        {
            CsvRow([
                "RoleId",
                "RoleName",
                "IsSystemRole",
                "AssignedUsersCount",
                "ConfiguredSectionsCount",
                "TotalSectionsCount",
                "HasIssues",
                "MissingSections",
                "UnknownSections",
                "DuplicateSections",
                "InvalidViewScopeSections",
                "CapabilityWithoutViewSections",
                "RestrictedCapabilitySections",
                "BroadDataScopeSections"
            ])
        };

        rows.AddRange(roles.Select(role => CsvRow([
            role.RoleId.ToString(CultureInfo.InvariantCulture),
            role.RoleName,
            role.IsSystemRole ? "true" : "false",
            role.AssignedUsersCount.ToString(CultureInfo.InvariantCulture),
            role.ConfiguredSectionsCount.ToString(CultureInfo.InvariantCulture),
            role.TotalSectionsCount.ToString(CultureInfo.InvariantCulture),
            role.HasIssues ? "true" : "false",
            string.Join(", ", role.MissingSections),
            string.Join(", ", role.UnknownSections),
            string.Join(", ", role.DuplicateSections),
            string.Join(", ", role.InvalidViewScopeSections),
            string.Join(", ", role.CapabilityWithoutViewSections),
            string.Join(", ", role.RestrictedCapabilitySections),
            string.Join(", ", role.BroadDataScopeSections),
        ])));

        var csv = string.Join('\n', rows);
        var body = Encoding.UTF8.GetBytes(csv);
        return Encoding.UTF8.GetPreamble().Concat(body).ToArray();
    }

    private static string CsvRow(IReadOnlyCollection<string?> values)
        => string.Join(';', values.Select(CsvCell));

    private static string CsvCell(string? value)
    {
        var normalized = (value ?? string.Empty).Replace("\"", "\"\"");
        return $"\"{normalized}\"";
    }

    private static bool HasInvalidViewScope(RolePermission permission)
    {
        var isCalendar = string.Equals(permission.Section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase);
        if (!DataVisibilityScope.IsSupported(permission.ViewScope, includeSpecific: isCalendar))
        {
            return true;
        }

        if (RolePermissionCatalog.HasDataScope(permission.Section))
        {
            return isCalendar
                ? false
                : string.Equals(permission.ViewScope, DataVisibilityScope.Specific, StringComparison.OrdinalIgnoreCase);
        }

        return !string.Equals(
            DataVisibilityScope.Normalize(permission.ViewScope, DataVisibilityScope.All),
            DataVisibilityScope.All,
            StringComparison.Ordinal);
    }

    private static bool HasAnyCapability(RolePermission permission)
        => permission.CanCreate
            || permission.CanEdit
            || permission.CanDelete
            || permission.CanExport
            || permission.CanViewAnalytics
            || permission.CanEditOthers
            || permission.CanCompleteOthers
            || permission.CanDeleteOthers;

    private static bool HasRestrictedCapability(RolePermission permission)
        => (!string.Equals(permission.Section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(permission.Section, RolePermissionCatalog.Requests, StringComparison.OrdinalIgnoreCase)
                && permission.CanViewAnalytics)
            || (string.Equals(permission.Section, RolePermissionCatalog.Roles, StringComparison.OrdinalIgnoreCase)
                && permission.CanExport)
            || (string.Equals(permission.Section, RolePermissionCatalog.Audit, StringComparison.OrdinalIgnoreCase)
                && (permission.CanCreate || permission.CanDelete || permission.CanExport || permission.CanViewAnalytics))
            || (string.Equals(permission.Section, RolePermissionCatalog.Notifications, StringComparison.OrdinalIgnoreCase)
                && (permission.CanCreate || permission.CanDelete || permission.CanExport || permission.CanViewAnalytics))
            || (string.Equals(permission.Section, RolePermissionCatalog.Integrations, StringComparison.OrdinalIgnoreCase)
                && (permission.CanCreate || permission.CanDelete || permission.CanExport || permission.CanViewAnalytics))
            || (string.Equals(permission.Section, RolePermissionCatalog.Notes, StringComparison.OrdinalIgnoreCase)
                && (permission.CanExport || permission.CanViewAnalytics))
            || (string.Equals(permission.Section, RolePermissionCatalog.YandexDisk, StringComparison.OrdinalIgnoreCase)
                && permission.CanViewAnalytics)
            || (string.Equals(permission.Section, RolePermissionCatalog.S3Storage, StringComparison.OrdinalIgnoreCase)
                && permission.CanViewAnalytics);

    private static string FormatSectionScope(RolePermission permission)
        => $"{permission.Section}:{permission.ViewScope}";

    private static Dictionary<string, RolePermissionValues> Flatten(RolePermissionsDto dto)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [RolePermissionCatalog.Calendar] = ToValues(dto.Calendar),
            [RolePermissionCatalog.Tasks] = ToValues(dto.Tasks),
            [RolePermissionCatalog.Notes] = ToValues(dto.Notes),
            [RolePermissionCatalog.Reports] = ToValues(dto.Reports),
            [RolePermissionCatalog.Calculator] = ToValues(dto.Calculator),
            [RolePermissionCatalog.LegalEntities] = ToValues(dto.LegalEntities),
            [RolePermissionCatalog.CompanyProfile] = ToValues(dto.CompanyProfile),
            [RolePermissionCatalog.Clients] = ToValues(dto.Clients),
            [RolePermissionCatalog.Cases] = ToValues(dto.Cases),
            [RolePermissionCatalog.Accounts] = ToValues(dto.Accounts),
            [RolePermissionCatalog.Acts] = ToValues(dto.Acts),
            [RolePermissionCatalog.Payments] = ToValues(dto.Payments),
            [RolePermissionCatalog.Contracts] = ToValues(dto.Contracts),
            [RolePermissionCatalog.Dictionaries] = ToValues(dto.Dictionaries),
            [RolePermissionCatalog.Notifications] = ToValues(dto.Notifications),
            [RolePermissionCatalog.Roles] = ToValues(dto.Roles),
            [RolePermissionCatalog.Audit] = ToValues(dto.Audit),
            [RolePermissionCatalog.Integrations] = ToValues(dto.Integrations),
            [RolePermissionCatalog.Requests] = ToValues(dto.Requests),
            [RolePermissionCatalog.YandexDisk] = ToValues(dto.YandexDisk),
            [RolePermissionCatalog.S3Storage] = ToValues(dto.S3Storage),
        };

    private void StampPermissionsAudit(Role role)
    {
        role.PermissionsUpdatedAt = DateTime.UtcNow;
        role.PermissionsUpdatedByUserId = User.GetUserId();
        role.PermissionsUpdatedByUserName = User.GetDisplayName();
    }

    private static void Apply(RolePermission entity, RolePermissionValues values)
    {
        entity.CanView = values.CanView;
        entity.CanCreate = values.CanCreate;
        entity.CanEdit = values.CanEdit;
        entity.CanDelete = values.CanDelete;
        entity.CanExport = values.CanExport;
        entity.CanViewAnalytics = values.CanViewAnalytics;
        entity.CanEditOthers = values.CanEditOthers;
        entity.CanCompleteOthers = values.CanCompleteOthers;
        entity.CanDeleteOthers = values.CanDeleteOthers;
        entity.ViewScope = values.ViewScope;
    }

    private static RolePermissionValues Normalize(string section, RolePermissionValues values)
    {
        var viewScope = NormalizeViewScope(section, values.ViewScope);

        if (!values.CanView)
        {
            return new RolePermissionValues(false, false, false, false, false, false, viewScope);
        }

        var canExport = string.Equals(section, RolePermissionCatalog.Roles, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, RolePermissionCatalog.Audit, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, RolePermissionCatalog.Notes, StringComparison.OrdinalIgnoreCase)
            ? false
            : values.CanExport;

        var canViewAnalytics = (string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, RolePermissionCatalog.Requests, StringComparison.OrdinalIgnoreCase))
            ? values.CanViewAnalytics
            : false;

        if (string.Equals(section, RolePermissionCatalog.Audit, StringComparison.OrdinalIgnoreCase))
        {
            return values with
            {
                CanCreate = false,
                CanDelete = false,
                CanExport = false,
                CanViewAnalytics = false,
                ViewScope = viewScope,
            };
        }

        if (string.Equals(section, RolePermissionCatalog.Notifications, StringComparison.OrdinalIgnoreCase)
            || string.Equals(section, RolePermissionCatalog.CompanyProfile, StringComparison.OrdinalIgnoreCase))
        {
            return values with
            {
                CanCreate = false,
                CanDelete = false,
                CanExport = false,
                CanViewAnalytics = false,
                ViewScope = viewScope,
            };
        }

        return values with
        {
            CanExport = canExport,
            CanViewAnalytics = canViewAnalytics,
            ViewScope = viewScope
        };
    }

    private static string NormalizeViewScope(string section, string? value)
        => RolePermissionCatalog.HasDataScope(section)
            ? DataVisibilityScope.Normalize(
                value,
                DataVisibilityScope.Own,
                includeSpecific: string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase))
            : DataVisibilityScope.All;

    private static bool IsProtectedRole(string roleName)
        => string.Equals(roleName, SystemAdministratorRoleName, StringComparison.Ordinal);

    private static MenuSectionPermissionsDto ToSection(RolePermission permission)
        => new()
        {
            CanView = permission.CanView,
            CanCreate = permission.CanCreate,
            CanEdit = permission.CanEdit,
            CanDelete = permission.CanDelete,
            CanExport = permission.CanExport,
            ViewScope = NormalizeViewScope(permission.Section, permission.ViewScope),
        };

    private static TaskSectionPermissionsDto ToTasks(RolePermission permission)
        => new()
        {
            CanView = permission.CanView,
            CanCreate = permission.CanCreate,
            CanEdit = permission.CanEdit,
            CanDelete = permission.CanDelete,
            CanExport = permission.CanExport,
            ViewScope = NormalizeViewScope(permission.Section, permission.ViewScope),
            CanEditOthers = permission.CanEditOthers,
            CanCompleteOthers = permission.CanCompleteOthers,
            CanDeleteOthers = permission.CanDeleteOthers,
        };

    private static RolePermissionValues ToValues(CalendarPermissionsDto section)
        => new(
            section.CanView,
            section.CanCreate,
            section.CanEdit,
            section.CanDelete,
            section.CanExport,
            section.CanViewAnalytics,
            NormalizeViewScope(RolePermissionCatalog.Calendar, section.ViewScope));

    private static RolePermissionValues ToValues(MenuSectionPermissionsDto section)
        => new(
            section.CanView,
            section.CanCreate,
            section.CanEdit,
            section.CanDelete,
            section.CanExport,
            false,
            DataVisibilityScope.Normalize(section.ViewScope, DataVisibilityScope.Own));

    private static RolePermissionValues ToValues(RequestSectionPermissionsDto section)
        => ToValues((MenuSectionPermissionsDto)section) with { CanViewAnalytics = section.CanViewAnalytics };

    private static RolePermissionValues ToValues(TaskSectionPermissionsDto section)
        => new(
            section.CanView,
            section.CanCreate,
            section.CanEdit,
            section.CanDelete,
            section.CanExport,
            false,
            DataVisibilityScope.Normalize(section.ViewScope, DataVisibilityScope.Own),
            section.CanEditOthers,
            section.CanCompleteOthers,
            section.CanDeleteOthers);
}

public sealed class RoleCalendarVisibleUserOptionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public string RoleName { get; set; } = string.Empty;
}
