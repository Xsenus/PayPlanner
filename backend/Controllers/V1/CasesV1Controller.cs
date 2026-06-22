using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/cases")]
[Authorize]
public class CasesV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public CasesV1Controller(PaymentContext db) => _db = db;

    private Task<DataVisibilityAccess?> ResolveCasesVisibilityAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);

    private async Task<IActionResult?> RequireCasesPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Cases, isAllowed, ct)
            ? null
            : Forbid();

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int? clientId,
        [FromQuery] int? legalEntityId,
        [FromQuery] ClientCaseStatus? status,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var q = _db.ClientCases.AsQueryable()
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .ApplyCaseFilters(clientId, legalEntityId, status, search)
            .ApplyCaseSort(sortBy, sortDir);

        return Ok(await q.AsNoTracking().ToListAsync(ct));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var entity = await _db.ClientCases
            .WithCaseIncludes()
            .ApplyClientCaseVisibility(visibility)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return entity is not null ? Ok(entity) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanCreate, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;
        _db.ClientCases.Add(model);
        await _db.SaveChangesAsync(ct);
        return Created($"/api/v1/cases/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientCase model, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanEdit, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();
        var validation = await ValidateCaseOwnerAsync(model.ClientId, model.LegalEntityId, visibility, ct);
        if (validation is not null) return validation;

        e.Title = model.Title;
        e.Description = model.Description;
        e.Status = model.Status;
        e.ClientId = model.ClientId;
        e.LegalEntityId = model.LegalEntityId;

        await _db.SaveChangesAsync(ct);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        if (await RequireCasesPermissionAsync(permission => permission.CanDelete, ct) is { } forbidden)
        {
            return forbidden;
        }

        var visibility = await ResolveCasesVisibilityAsync(ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var e = await _db.ClientCases
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientCaseId == id).ToListAsync(ct);
        foreach (var p in payments) p.ClientCaseId = null;
        await _db.SaveChangesAsync(ct);

        _db.ClientCases.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<IActionResult?> ValidateCaseOwnerAsync(
        int? clientId,
        int? legalEntityId,
        DataVisibilityAccess visibility,
        CancellationToken ct)
    {
        var hasClient = clientId.HasValue && clientId.Value > 0;
        var hasLegalEntity = legalEntityId.HasValue && legalEntityId.Value > 0;
        if (hasClient == hasLegalEntity)
        {
            return BadRequest("У дела должен быть выбран ровно один владелец: клиент или юридическое лицо.");
        }

        if (hasClient && !await _db.Clients
                .AsNoTracking()
                .ApplyClientVisibility(visibility)
                .AnyAsync(client => client.Id == clientId!.Value, ct))
        {
            return BadRequest($"Unknown clientId {clientId!.Value}");
        }

        if (hasLegalEntity && !await _db.LegalEntities
                .AsNoTracking()
                .ApplyLegalEntityVisibility(visibility)
                .AnyAsync(entity => entity.Id == legalEntityId!.Value, ct))
        {
            return BadRequest($"Unknown legalEntityId {legalEntityId!.Value}");
        }

        return null;
    }
}
