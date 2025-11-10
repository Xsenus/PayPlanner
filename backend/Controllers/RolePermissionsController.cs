using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/role-permissions")]
public class RolePermissionsController : ControllerBase
{
    private static readonly string[] Sections =
    {
        "calendar",
        "reports",
        "calculator",
        "clients",
        "accounts",
        "acts",
        "contracts",
        "dictionaries",
    };

    private readonly PaymentContext _db;

    public RolePermissionsController(PaymentContext db) => _db = db;

    [HttpGet("{roleId:int}")]
    [Authorize]
    public async Task<ActionResult<RolePermissionsDto>> Get(int roleId, CancellationToken ct)
    {
        if (!User.IsInRole("admin"))
        {
            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!int.TryParse(userIdClaim, out var userId))
            {
                return Forbid();
            }

            var userRoleId = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.RoleId)
                .FirstOrDefaultAsync(ct);

            if (userRoleId == 0 || userRoleId != roleId)
            {
                return Forbid();
            }
        }

        if (!await _db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct))
        {
            return NotFound();
        }

        var permissions = await _db.RolePermissions
            .AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        return Ok(MapToDto(permissions));
    }

    [HttpPut("{roleId:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Update(int roleId, [FromBody] RolePermissionsDto request, CancellationToken ct)
    {
        if (!await _db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct))
        {
            return NotFound();
        }

        var existing = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        var bySection = existing.ToDictionary(rp => rp.Section, StringComparer.OrdinalIgnoreCase);
        var incoming = Flatten(request);

        foreach (var section in Sections)
        {
            var values = incoming[section];
            if (bySection.TryGetValue(section, out var entity))
            {
                entity.CanView = values.CanView;
                entity.CanCreate = values.CanCreate;
                entity.CanEdit = values.CanEdit;
                entity.CanDelete = values.CanDelete;
                entity.CanExport = values.CanExport;
                entity.CanViewAnalytics = values.CanViewAnalytics;
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
                });
            }
        }

        var unused = existing.Where(rp => !incoming.ContainsKey(rp.Section)).ToList();
        if (unused.Count > 0)
        {
            _db.RolePermissions.RemoveRange(unused);
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{roleId:int}")]
    [Authorize(Policy = "Admin")]
    public async Task<IActionResult> Reset(int roleId, CancellationToken ct)
    {
        var entries = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        if (entries.Count == 0)
        {
            return NoContent();
        }

        _db.RolePermissions.RemoveRange(entries);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static RolePermissionsDto MapToDto(IReadOnlyCollection<RolePermission> permissions)
    {
        var dto = CreateDefault();
        foreach (var permission in permissions)
        {
            switch (permission.Section)
            {
                case "calendar":
                    dto.Calendar = new CalendarPermissionsDto
                    {
                        CanView = permission.CanView,
                        CanCreate = permission.CanCreate,
                        CanEdit = permission.CanEdit,
                        CanDelete = permission.CanDelete,
                        CanExport = permission.CanExport,
                        CanViewAnalytics = permission.CanViewAnalytics,
                    };
                    break;
                case "reports":
                    dto.Reports = ToSection(permission);
                    break;
                case "calculator":
                    dto.Calculator = ToSection(permission);
                    break;
                case "clients":
                    dto.Clients = ToSection(permission);
                    break;
                case "accounts":
                    dto.Accounts = ToSection(permission);
                    break;
                case "acts":
                    dto.Acts = ToSection(permission);
                    break;
                case "contracts":
                    dto.Contracts = ToSection(permission);
                    break;
                case "dictionaries":
                    dto.Dictionaries = ToSection(permission);
                    break;
            }
        }

        return dto;
    }

    private static Dictionary<string, SectionValues> Flatten(RolePermissionsDto dto)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["calendar"] = ToValues(dto.Calendar),
            ["reports"] = ToValues(dto.Reports),
            ["calculator"] = ToValues(dto.Calculator),
            ["clients"] = ToValues(dto.Clients),
            ["accounts"] = ToValues(dto.Accounts),
            ["acts"] = ToValues(dto.Acts),
            ["contracts"] = ToValues(dto.Contracts),
            ["dictionaries"] = ToValues(dto.Dictionaries),
        };

    private static RolePermissionsDto CreateDefault()
    {
        var allTrue = new MenuSectionPermissionsDto
        {
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
        };

        return new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                CanCreate = true,
                CanEdit = true,
                CanDelete = true,
                CanExport = true,
                CanViewAnalytics = true,
            },
            Reports = Clone(allTrue),
            Calculator = Clone(allTrue),
            Clients = Clone(allTrue),
            Accounts = Clone(allTrue),
            Acts = Clone(allTrue),
            Contracts = Clone(allTrue),
            Dictionaries = Clone(allTrue),
        };
    }

    private static MenuSectionPermissionsDto Clone(MenuSectionPermissionsDto source)
        => new()
        {
            CanView = source.CanView,
            CanCreate = source.CanCreate,
            CanEdit = source.CanEdit,
            CanDelete = source.CanDelete,
            CanExport = source.CanExport,
        };

    private static MenuSectionPermissionsDto ToSection(RolePermission permission)
        => new()
        {
            CanView = permission.CanView,
            CanCreate = permission.CanCreate,
            CanEdit = permission.CanEdit,
            CanDelete = permission.CanDelete,
            CanExport = permission.CanExport,
        };

    private static SectionValues ToValues(CalendarPermissionsDto section)
        => new(section.CanView, section.CanCreate, section.CanEdit, section.CanDelete, section.CanExport, section.CanViewAnalytics);

    private static SectionValues ToValues(MenuSectionPermissionsDto section)
        => new(section.CanView, section.CanCreate, section.CanEdit, section.CanDelete, section.CanExport, section is CalendarPermissionsDto calendar ? calendar.CanViewAnalytics : false);

    private sealed record SectionValues(bool CanView, bool CanCreate, bool CanEdit, bool CanDelete, bool CanExport, bool CanViewAnalytics);
}
