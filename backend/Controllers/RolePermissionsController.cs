using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
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

    public RolePermissionsController(PaymentContext db, RoleManagementAccessService roleAccess)
    {
        _db = db;
        _roleAccess = roleAccess;
    }

    [HttpGet("audit")]
    public async Task<ActionResult<RolePermissionAuditReportDto>> GetAudit(CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.IsAdmin && !access.CanView)
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
        if (!access.IsAdmin && !access.CanView)
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

        return Ok(MapToDto(permissions, IsProtectedRole(roleInfo.Name)));
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

        StampPermissionsAudit(role);
        await _db.SaveChangesAsync(ct);
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

        var defaults = RolePermissionCatalog.BuildDefaults(false);
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

        StampPermissionsAudit(role);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static RolePermissionsDto MapToDto(
        IReadOnlyCollection<RolePermission> permissions,
        bool allowRoleManagement)
    {
        var dto = RolePermissionCatalog.CreateDto(allowRoleManagement);
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
                    };
                    break;
                case RolePermissionCatalog.Reports:
                    dto.Reports = ToSection(permission);
                    break;
                case RolePermissionCatalog.Tasks:
                    dto.Tasks = ToSection(permission);
                    break;
                case RolePermissionCatalog.Calculator:
                    dto.Calculator = ToSection(permission);
                    break;
                case RolePermissionCatalog.LegalEntities:
                    dto.LegalEntities = ToSection(permission);
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
                case RolePermissionCatalog.Roles:
                    dto.Roles = ToSection(permission);
                    break;
                case RolePermissionCatalog.Audit:
                    dto.Audit = ToSection(permission);
                    break;
            }
        }

        if (!permissions.Any(permission => string.Equals(permission.Section, RolePermissionCatalog.Cases, StringComparison.OrdinalIgnoreCase))
            && permissions.FirstOrDefault(permission => string.Equals(permission.Section, RolePermissionCatalog.Clients, StringComparison.OrdinalIgnoreCase)) is { } clientsPermission)
        {
            dto.Cases = ToSection(clientsPermission);
        }

        return dto;
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
        if (!DataVisibilityScope.IsSupported(permission.ViewScope))
        {
            return true;
        }

        if (RolePermissionCatalog.HasDataScope(permission.Section))
        {
            return false;
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
            || permission.CanViewAnalytics;

    private static bool HasRestrictedCapability(RolePermission permission)
        => (!string.Equals(permission.Section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase)
                && permission.CanViewAnalytics)
            || (string.Equals(permission.Section, RolePermissionCatalog.Roles, StringComparison.OrdinalIgnoreCase)
                && permission.CanExport)
            || (string.Equals(permission.Section, RolePermissionCatalog.Audit, StringComparison.OrdinalIgnoreCase)
                && (permission.CanCreate || permission.CanDelete || permission.CanExport || permission.CanViewAnalytics));

    private static string FormatSectionScope(RolePermission permission)
        => $"{permission.Section}:{permission.ViewScope}";

    private static Dictionary<string, RolePermissionValues> Flatten(RolePermissionsDto dto)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [RolePermissionCatalog.Calendar] = ToValues(dto.Calendar),
            [RolePermissionCatalog.Tasks] = ToValues(dto.Tasks),
            [RolePermissionCatalog.Reports] = ToValues(dto.Reports),
            [RolePermissionCatalog.Calculator] = ToValues(dto.Calculator),
            [RolePermissionCatalog.LegalEntities] = ToValues(dto.LegalEntities),
            [RolePermissionCatalog.Clients] = ToValues(dto.Clients),
            [RolePermissionCatalog.Cases] = ToValues(dto.Cases),
            [RolePermissionCatalog.Accounts] = ToValues(dto.Accounts),
            [RolePermissionCatalog.Acts] = ToValues(dto.Acts),
            [RolePermissionCatalog.Payments] = ToValues(dto.Payments),
            [RolePermissionCatalog.Contracts] = ToValues(dto.Contracts),
            [RolePermissionCatalog.Dictionaries] = ToValues(dto.Dictionaries),
            [RolePermissionCatalog.Roles] = ToValues(dto.Roles),
            [RolePermissionCatalog.Audit] = ToValues(dto.Audit),
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
            ? false
            : values.CanExport;

        var canViewAnalytics = string.Equals(section, RolePermissionCatalog.Calendar, StringComparison.OrdinalIgnoreCase)
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

        return values with
        {
            CanExport = canExport,
            CanViewAnalytics = canViewAnalytics,
            ViewScope = viewScope
        };
    }

    private static string NormalizeViewScope(string section, string? value)
        => RolePermissionCatalog.HasDataScope(section)
            ? DataVisibilityScope.Normalize(value, DataVisibilityScope.Own)
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
}
