using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Clients;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/clients")]
[Authorize]
public class ClientsV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV1Controller(PaymentContext db) => _db = db;

    private IQueryable<Client> BuildVisibleClientsQuery(DataVisibilityAccess? access)
        => _db.Clients.ApplyClientVisibility(access);

    private Task<DataVisibilityAccess?> GetCurrentAccessAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);

    private async Task<IActionResult?> RequireClientsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Clients, isAllowed, ct)
            ? null
            : Forbid();

    private static string? FormatResponsibleUserName(User? user)
    {
        if (user is null) return null;
        return string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
    }

    private static void PopulateResponsibleUserNames(IEnumerable<Client> clients)
    {
        foreach (var client in clients)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        }
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue) return true;
        return await _db.Users.AsNoTracking().AnyAsync(u => u.Id == responsibleUserId.Value && u.IsActive, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var q = BuildVisibleClientsQuery(access)
            .Include(c => c.ResponsibleUser)
            .AsQueryable()
            .ApplyClientFilters(search, isActive)
            .ApplyClientSort(sortBy, sortDir);

        if (limit.HasValue && limit.Value > 0)
        {
            var size = Math.Min(limit.Value, 200);
            q = q.Take(size);
        }

        var items = await q.AsNoTracking().ToListAsync(ct);
        PopulateResponsibleUserNames(items);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(access)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is not null)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
            var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
            client.Cases = await _db.ClientCases
                .AsNoTracking()
                .ApplyClientCaseVisibility(casesVisibility)
                .Where(clientCase => clientCase.ClientId == client.Id)
                .OrderByDescending(clientCase => clientCase.CreatedAt)
                .ThenByDescending(clientCase => clientCase.Id)
                .ToListAsync(ct);
        }
        return client is not null ? Ok(client) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model, CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        if (ClientCreationInput.Validate(_db, model) is { } graphError) return graphError;

        var responsibleUserId = access.IsAdmin
            ? (model.ResponsibleUserId ?? access.UserId)
            : access.UserId;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        model.ResponsibleUserId = responsibleUserId;
        _db.Clients.Add(model);
        await _db.SaveChangesAsync(ct);
        await _db.Entry(model).Reference(c => c.ResponsibleUser).LoadAsync(ct);
        model.ResponsibleUserName = FormatResponsibleUserName(model.ResponsibleUser);
        return Created($"/api/v1/clients/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model, CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(access)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var responsibleUserId = access.IsAdmin
            ? model.ResponsibleUserId
            : e.ResponsibleUserId;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        e.Name = model.Name;
        e.Email = model.Email;
        e.Phone = model.Phone;
        e.Company = model.Company;
        e.Address = model.Address;
        e.Notes = model.Notes;
        e.IsActive = model.IsActive;
        e.LegalEntityId = model.LegalEntityId;
        e.ResponsibleUserId = responsibleUserId;
        e.ClientStatusId = model.ClientStatusId;
        e.IsClientAndSupplier = model.IsClientAndSupplier;

        await _db.SaveChangesAsync(ct);
        await _db.Entry(e).Reference(c => c.ResponsibleUser).LoadAsync(ct);
        e.ResponsibleUserName = FormatResponsibleUserName(e.ResponsibleUser);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(access)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientId == id).ToListAsync(ct);
        foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
        await _db.SaveChangesAsync(ct);

        _db.Clients.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
