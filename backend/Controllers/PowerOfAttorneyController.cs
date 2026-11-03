using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services.Clients;

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
        var (access, failure) = await ClientPowerOfAttorneyTransaction.ResolveAsync(_db, User, ClientPowerOperation.View, ct);
        if (failure is not null) return failure;
        var clients = _db.Clients.ApplyClientVisibility(access!.Visibility);
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
    public Task<IActionResult> Create(int clientId, [FromBody] UpsertPowerOfAttorneyRequest model, CancellationToken ct)
        => ClientPowerOfAttorneyTransaction.RunAsync(_db, User, clientId, null, ClientPowerOperation.Create, async _ =>
    {
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
    }, ct);

    [HttpPut("{id:int}")]
    [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Edit)]
    public Task<IActionResult> Update(int clientId, int id, [FromBody] UpsertPowerOfAttorneyRequest model, CancellationToken ct)
        => ClientPowerOfAttorneyTransaction.RunAsync(_db, User, clientId, id, ClientPowerOperation.Edit, async existing =>
    {
        var entity = existing!;
        entity.Number = (model.Number ?? string.Empty).Trim();
        entity.IssuedAt = model.IssuedAt.GetValueOrDefault();
        entity.ValidUntil = model.ValidUntil;
        entity.Issuer = Normalize(model.Issuer);
        entity.Notes = Normalize(model.Notes);
        entity.IsActive = model.IsActive;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(entity);
    }, ct);

    [HttpDelete("{id:int}")]
    [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Delete)]
    public Task<IActionResult> Delete(int clientId, int id, CancellationToken ct)
        => ClientPowerOfAttorneyTransaction.RunAsync(_db, User, clientId, id, ClientPowerOperation.Delete, async existing =>
    {
        var entity = existing!;
        _db.PowersOfAttorney.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }, ct);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
