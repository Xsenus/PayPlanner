using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize]
public class RolesController : ControllerBase
{
    private const string SystemAdministratorRoleName = "admin";

    private readonly PaymentContext _db;
    private readonly RoleManagementAccessService _roleAccess;

    public RolesController(PaymentContext db, RoleManagementAccessService roleAccess)
    {
        _db = db;
        _roleAccess = roleAccess;
    }

    [HttpGet]
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanView && !access.IsAdmin)
        {
            return Forbid();
        }

        return Ok(await BuildRoleQuery()
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(ct));
    }

    [HttpGet("paged")]
    public async Task<IActionResult> GetRolesPaged(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanView && !access.IsAdmin)
        {
            return Forbid();
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = BuildRoleQuery()
            .AsNoTracking()
            .OrderBy(r => r.Name);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<RoleDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetRole(int id, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanView && !access.IsAdmin)
        {
            return Forbid();
        }

        var role = await BuildRoleQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return role is not null ? Ok(role) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest req, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanCreate && !access.IsAdmin)
        {
            return Forbid();
        }

        var normalizedName = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BadRequest(new { message = "Название роли обязательно." });
        }

        var normalizedNameLower = normalizedName.ToLowerInvariant();
        if (await _db.Roles.AnyAsync(r => r.Name.ToLower() == normalizedNameLower, ct))
        {
            return Conflict(new { message = "Роль с таким именем уже существует." });
        }

        var now = DateTime.UtcNow;
        var allowRoleManagement = string.Equals(normalizedName, SystemAdministratorRoleName, StringComparison.Ordinal);

        var role = new Role
        {
            Name = normalizedName,
            Description = req.Description?.Trim() ?? string.Empty,
            CreatedAt = now,
            PermissionsUpdatedAt = now,
            PermissionsUpdatedByUserId = access.UserId,
            PermissionsUpdatedByUserName = User.GetDisplayName()
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);

        var defaultPermissions = RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement);
        _db.RolePermissions.AddRange(defaultPermissions);
        await _db.SaveChangesAsync(ct);

        var created = await BuildRoleQuery()
            .AsNoTracking()
            .FirstAsync(r => r.Id == role.Id, ct);

        return Created($"/api/roles/{role.Id}", created);
    }

    [HttpPost("{id:int}/copy")]
    public async Task<IActionResult> CopyRole(int id, [FromBody] CopyRoleRequest req, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanCreate && !access.IsAdmin)
        {
            return Forbid();
        }

        var normalizedName = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BadRequest(new { message = "Название новой роли обязательно." });
        }

        var normalizedNameLower = normalizedName.ToLowerInvariant();
        if (await _db.Roles.AnyAsync(r => r.Name.ToLower() == normalizedNameLower, ct))
        {
            return Conflict(new { message = "Роль с таким именем уже существует." });
        }

        var sourceRole = await _db.Roles
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        if (sourceRole is null)
        {
            return NotFound();
        }

        var now = DateTime.UtcNow;
        var role = new Role
        {
            Name = normalizedName,
            Description = req.Description?.Trim() ?? sourceRole.Description,
            CreatedAt = now,
            PermissionsUpdatedAt = now,
            PermissionsUpdatedByUserId = access.UserId,
            PermissionsUpdatedByUserName = User.GetDisplayName()
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);

        var copiedPermissions = BuildCopiedPermissions(role.Id, sourceRole);
        _db.RolePermissions.AddRange(copiedPermissions);
        await _db.SaveChangesAsync(ct);

        var created = await BuildRoleQuery()
            .AsNoTracking()
            .FirstAsync(r => r.Id == role.Id, ct);

        return Created($"/api/roles/{role.Id}", created);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateRole(int id, [FromBody] UpdateRoleRequest req, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanEdit && !access.IsAdmin)
        {
            return Forbid();
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return NotFound();
        }

        if (IsProtectedRole(role))
        {
            return BadRequest(new { message = "Стандартную роль администратора нельзя изменять." });
        }

        var normalizedName = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return BadRequest(new { message = "Название роли обязательно." });
        }

        var normalizedNameLower = normalizedName.ToLowerInvariant();
        if (await _db.Roles.AnyAsync(r => r.Name.ToLower() == normalizedNameLower && r.Id != id, ct))
        {
            return Conflict(new { message = "Роль с таким именем уже существует." });
        }

        role.Name = normalizedName;
        role.Description = req.Description?.Trim() ?? string.Empty;

        await _db.SaveChangesAsync(ct);

        var updated = await BuildRoleQuery()
            .AsNoTracking()
            .FirstAsync(r => r.Id == id, ct);

        return Ok(updated);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteRole(int id, CancellationToken ct)
    {
        var access = await _roleAccess.GetSnapshotAsync(User, ct);
        if (!access.CanDelete && !access.IsAdmin)
        {
            return Forbid();
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return NotFound();
        }

        if (IsProtectedRole(role))
        {
            return BadRequest(new { message = "Стандартную роль администратора нельзя удалять." });
        }

        if (await _db.Users.AnyAsync(u => u.RoleId == id, ct))
        {
            return BadRequest(new { message = "Невозможно удалить роль, так как она назначена пользователям." });
        }

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<RoleDto> BuildRoleQuery()
        => _db.Roles.Select(r => new RoleDto
        {
            Id = r.Id,
            Name = r.Name,
            Description = r.Description,
            CreatedAt = r.CreatedAt,
            IsSystemRole = r.Name == SystemAdministratorRoleName,
            AssignedUsersCount = r.Users.Count(),
            AccessibleSectionsCount = r.Permissions.Count(permission => permission.CanView),
            TotalSectionsCount = RolePermissionCatalog.AllSections.Length,
            PermissionsUpdatedAt = r.PermissionsUpdatedAt,
            PermissionsUpdatedByUserId = r.PermissionsUpdatedByUserId,
            PermissionsUpdatedByUserName = r.PermissionsUpdatedByUserName
        });

    private static bool IsProtectedRole(Role role)
        => string.Equals(role.Name, SystemAdministratorRoleName, StringComparison.Ordinal);

    private static List<RolePermission> BuildCopiedPermissions(int roleId, Role sourceRole)
    {
        var defaults = RolePermissionCatalog.BuildDefaults(IsProtectedRole(sourceRole));
        var sourceBySection = sourceRole.Permissions
            .GroupBy(permission => permission.Section, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<RolePermission>();
        foreach (var section in RolePermissionCatalog.AllSections)
        {
            if (sourceBySection.TryGetValue(section, out var sourcePermission))
            {
                result.Add(new RolePermission
                {
                    RoleId = roleId,
                    Section = section,
                    CanView = sourcePermission.CanView,
                    CanCreate = sourcePermission.CanCreate,
                    CanEdit = sourcePermission.CanEdit,
                    CanDelete = sourcePermission.CanDelete,
                    CanExport = sourcePermission.CanExport,
                    CanViewAnalytics = sourcePermission.CanViewAnalytics
                });
                continue;
            }

            var fallback = defaults[section];
            result.Add(new RolePermission
            {
                RoleId = roleId,
                Section = section,
                CanView = fallback.CanView,
                CanCreate = fallback.CanCreate,
                CanEdit = fallback.CanEdit,
                CanDelete = fallback.CanDelete,
                CanExport = fallback.CanExport,
                CanViewAnalytics = fallback.CanViewAnalytics
            });
        }

        return result;
    }
}
