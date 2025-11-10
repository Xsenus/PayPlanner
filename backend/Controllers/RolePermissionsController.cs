using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/role-permissions")]
[Authorize(Policy = "Admin")]
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
    public async Task<ActionResult<RolePermissionsDto>> Get(int roleId, CancellationToken ct)
    {
        if (!await _db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct))
        {
            return NotFound();
        }

        if (!await _db.TableExistsAsync("RolePermissions", ct))
        {
            return Ok(RolePermissionsDto.CreateDefault());
        }

        var permissions = await _db.RolePermissions.AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        return Ok(RolePermissionsDto.FromEntities(permissions));
    }

    [HttpPut("{roleId:int}")]
    public async Task<ActionResult<RolePermissionsDto>> Update(int roleId, [FromBody] RolePermissionsDto? payload, CancellationToken ct)
    {
        if (!await _db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct))
        {
            return NotFound();
        }

        if (!await _db.TableExistsAsync("RolePermissions", ct))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Хранилище прав ролей не настроено. Выполните миграции базы данных и повторите попытку.",
            });
        }

        var normalized = RolePermissionsDto.CreateDefault();
        normalized.ApplyFrom(payload);

        var existing = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        var bySection = existing.ToDictionary(rp => rp.Section, StringComparer.OrdinalIgnoreCase);

        foreach (var (section, permissions) in normalized.EnumerateSections())
        {
            if (!bySection.TryGetValue(section, out var entity))
            {
                entity = CreateEntity(roleId, section);
                _db.RolePermissions.Add(entity);
                bySection[section] = entity;
            }

            entity.CanView = permissions.CanView;
            entity.CanCreate = permissions.CanCreate;
            entity.CanEdit = permissions.CanEdit;
            entity.CanDelete = permissions.CanDelete;
            entity.CanExport = permissions.CanExport;
            entity.CanViewAnalytics = permissions is CalendarPermissionsDto calendar
                ? calendar.CanViewAnalytics
                : null;
        }

        foreach (var extra in existing)
        {
            if (!Sections.Contains(extra.Section, StringComparer.OrdinalIgnoreCase))
            {
                _db.RolePermissions.Remove(extra);
            }
        }

        await _db.SaveChangesAsync(ct);

        var updated = await _db.RolePermissions.AsNoTracking()
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        return Ok(RolePermissionsDto.FromEntities(updated));
    }

    [HttpDelete("{roleId:int}")]
    public async Task<ActionResult<RolePermissionsDto>> Reset(int roleId, CancellationToken ct)
    {
        if (!await _db.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId, ct))
        {
            return NotFound();
        }

        if (!await _db.TableExistsAsync("RolePermissions", ct))
        {
            return Ok(RolePermissionsDto.CreateDefault());
        }

        var entries = await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .ToListAsync(ct);

        if (entries.Count > 0)
        {
            _db.RolePermissions.RemoveRange(entries);
            await _db.SaveChangesAsync(ct);
        }

        return Ok(RolePermissionsDto.CreateDefault());
    }

    private static RolePermission CreateEntity(int roleId, string section)
    {
        return new RolePermission
        {
            RoleId = roleId,
            Section = section,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            CanViewAnalytics = string.Equals(section, "calendar", StringComparison.OrdinalIgnoreCase) ? true : null,
        };
    }
}
