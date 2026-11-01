using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/clients/{clientId:int}/powers-of-attorney")]
[Authorize]
[RequireSectionPermission(RolePermissionCatalog.Clients)]
public class PowerOfAttorneyController : ControllerBase
{
    private readonly PaymentContext _db;

    public PowerOfAttorneyController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(int clientId, CancellationToken ct)
    {
        var clients = await GetVisibleClientsAsync(permission => permission.CanView, ct);
        if (clients is null) return Forbid();
        var result = await clients
            .AsNoTracking()
            .Where(client => client.Id == clientId)
            .Select(client => new
            {
                Items = client.PowersOfAttorney.OrderByDescending(item => item.IssuedAt).ToList(),
            })
            .SingleOrDefaultAsync(ct);
        return result is null ? NotFound() : Ok(result.Items);
    }

    [HttpPost]
    [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Create)]
    public async Task<IActionResult> Create(int clientId, [FromBody] UpsertPowerOfAttorneyRequest model, CancellationToken ct)
    {
        var clients = await GetVisibleClientsAsync(permission => permission.CanCreate, ct);
        if (clients is null) return Forbid();
        var clientExists = await clients.AsNoTracking().AnyAsync(c => c.Id == clientId, ct);
        if (!clientExists) return NotFound();

        var entity = new PowerOfAttorney
        {
            ClientId = clientId,
            Number = (model.Number ?? string.Empty).Trim(),
            IssuedAt = model.IssuedAt.GetValueOrDefault(),
            ValidUntil = model.ValidUntil,
            Issuer = Normalize(model.Issuer),
            Notes = Normalize(model.Notes),
            IsActive = model.IsActive,
        };

        _db.PowersOfAttorney.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/clients/{clientId}/powers-of-attorney/{entity.Id}", entity);
    }

    [HttpPut("{id:int}")]
    [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Edit)]
    public async Task<IActionResult> Update(int clientId, int id, [FromBody] UpsertPowerOfAttorneyRequest model, CancellationToken ct)
    {
        var clients = await GetVisibleClientsAsync(permission => permission.CanEdit, ct);
        if (clients is null) return Forbid();
        var entity = await _db.PowersOfAttorney.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Id == id
            && clients.Any(client => client.Id == x.ClientId), ct);
        if (entity is null) return NotFound();

        entity.Number = (model.Number ?? string.Empty).Trim();
        entity.IssuedAt = model.IssuedAt.GetValueOrDefault();
        entity.ValidUntil = model.ValidUntil;
        entity.Issuer = Normalize(model.Issuer);
        entity.Notes = Normalize(model.Notes);
        entity.IsActive = model.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }

    [HttpDelete("{id:int}")]
    [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Delete)]
    public async Task<IActionResult> Delete(int clientId, int id, CancellationToken ct)
    {
        var clients = await GetVisibleClientsAsync(permission => permission.CanDelete, ct);
        if (clients is null) return Forbid();
        var entity = await _db.PowersOfAttorney.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Id == id
            && clients.Any(client => client.Id == x.ClientId), ct);
        if (entity is null) return NotFound();

        _db.PowersOfAttorney.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<IQueryable<Client>?> GetVisibleClientsAsync(Func<RolePermissionValues, bool> isAllowed, CancellationToken ct)
    {
        var access = await _db.ResolveSectionAccessAsync(User, RolePermissionCatalog.Clients, ct, requireApprovedUser: true);
        return access is null || access.Permission is not null && !isAllowed(access.Permission)
            ? null : _db.Clients.ApplyClientVisibility(access.Visibility);
    }
}
