using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/legal-entities")]
[Authorize]
public class LegalEntitiesController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly ILegalEntityEnrichmentService _enrichmentService;

    public LegalEntitiesController(PaymentContext db, ILegalEntityEnrichmentService enrichmentService)
    {
        _db = db;
        _enrichmentService = enrichmentService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<LegalEntityListItemDto>>> GetAll([FromQuery] string? search, CancellationToken ct)
    {
        var query = _db.LegalEntities
            .Include(le => le.Clients)
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(le =>
                EF.Functions.Like(le.ShortName, term) ||
                EF.Functions.Like(le.FullName ?? string.Empty, term) ||
                EF.Functions.Like(le.Inn ?? string.Empty, term) ||
                EF.Functions.Like(le.Kpp ?? string.Empty, term) ||
                EF.Functions.Like(le.Ogrn ?? string.Empty, term));
        }

        var items = await query
            .OrderBy(le => le.ShortName)
            .Select(le => new LegalEntityListItemDto
            {
                Id = le.Id,
                ShortName = le.ShortName,
                FullName = le.FullName,
                Inn = le.Inn,
                Kpp = le.Kpp,
                Ogrn = le.Ogrn,
                Address = le.Address,
                Phone = le.Phone,
                Email = le.Email,
                Director = le.Director,
                Notes = le.Notes,
                ClientsCount = le.Clients.Count,
                CreatedAt = le.CreatedAt,
                UpdatedAt = le.UpdatedAt,
                Clients = le.Clients
                    .OrderBy(c => c.Name)
                    .Select(c => new LegalEntityClientDto
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Phone = c.Phone,
                        Email = c.Email,
                        IsActive = c.IsActive,
                    })
                    .ToList(),
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<LegalEntityDetailDto>> GetById(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities
            .Include(le => le.Clients)
            .AsNoTracking()
            .FirstOrDefaultAsync(le => le.Id == id, ct);

        if (entity is null)
        {
            return NotFound();
        }

        return Ok(MapDetail(entity));
    }

    [HttpPost]
    public async Task<ActionResult<LegalEntityDetailDto>> Create([FromBody] LegalEntityRequest request, CancellationToken ct)
    {
        var (clientIds, errorResult) = await ValidateClientIdsAsync(request.ClientIds, ct);
        if (errorResult is not null)
        {
            return errorResult;
        }

        var entity = new LegalEntity
        {
            ShortName = request.ShortName,
            FullName = request.FullName,
            Inn = request.Inn,
            Kpp = request.Kpp,
            Ogrn = request.Ogrn,
            Address = request.Address,
            Phone = request.Phone,
            Email = request.Email,
            Director = request.Director,
            Notes = request.Notes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.LegalEntities.Add(entity);
        await _db.SaveChangesAsync(ct);

        if (clientIds.Count > 0)
        {
            await ApplyClientLinksAsync(entity.Id, clientIds, ct);
            await _db.SaveChangesAsync(ct);
        }

        await _db.Entry(entity).Collection(le => le.Clients).LoadAsync(ct);
        return Created($"/api/legal-entities/{entity.Id}", MapDetail(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<LegalEntityDetailDto>> Update(int id, [FromBody] LegalEntityRequest request, CancellationToken ct)
    {
        var entity = await _db.LegalEntities.Include(le => le.Clients).FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var (clientIds, errorResult) = await ValidateClientIdsAsync(request.ClientIds, ct);
        if (errorResult is not null)
        {
            return errorResult;
        }

        entity.ShortName = request.ShortName;
        entity.FullName = request.FullName;
        entity.Inn = request.Inn;
        entity.Kpp = request.Kpp;
        entity.Ogrn = request.Ogrn;
        entity.Address = request.Address;
        entity.Phone = request.Phone;
        entity.Email = request.Email;
        entity.Director = request.Director;
        entity.Notes = request.Notes;
        entity.UpdatedAt = DateTime.UtcNow;

        await ApplyClientLinksAsync(entity.Id, clientIds, ct);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(le => le.Clients).LoadAsync(ct);
        return Ok(MapDetail(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.LegalEntities.Include(le => le.Clients).FirstOrDefaultAsync(le => le.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        foreach (var client in entity.Clients)
        {
            client.LegalEntityId = null;
        }

        _db.LegalEntities.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("suggest")]
    public async Task<ActionResult<IEnumerable<LegalEntitySuggestionDto>>> Suggest(
        [FromBody] LegalEntitySuggestionRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query) && string.IsNullOrWhiteSpace(request.Inn))
        {
            return BadRequest("Необходимо указать запрос или ИНН");
        }

        var suggestions = await _enrichmentService.SuggestAsync(request.Query, request.Inn, request.Limit, ct);
        var response = suggestions.Select(s => new LegalEntitySuggestionDto
        {
            ShortName = s.ShortName,
            FullName = s.FullName,
            Inn = s.Inn,
            Kpp = s.Kpp,
            Ogrn = s.Ogrn,
            Address = s.Address,
            Phone = s.Phone,
            Email = s.Email,
            Director = s.Director,
        }).ToList();

        return Ok(response);
    }

    private static LegalEntityDetailDto MapDetail(LegalEntity entity)
        => new()
        {
            Id = entity.Id,
            ShortName = entity.ShortName,
            FullName = entity.FullName,
            Inn = entity.Inn,
            Kpp = entity.Kpp,
            Ogrn = entity.Ogrn,
            Address = entity.Address,
            Phone = entity.Phone,
            Email = entity.Email,
            Director = entity.Director,
            Notes = entity.Notes,
            ClientsCount = entity.Clients.Count,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt,
            Clients = entity.Clients
                .OrderBy(c => c.Name)
                .Select(c => new LegalEntityClientDto
                {
                    Id = c.Id,
                    Name = c.Name,
                    Phone = c.Phone,
                    Email = c.Email,
                    IsActive = c.IsActive,
                })
                .ToList(),
        };

    private async Task<(IReadOnlyCollection<int> ClientIds, ActionResult? Error)> ValidateClientIdsAsync(
        IEnumerable<int> clientIds,
        CancellationToken ct)
    {
        var uniqueIds = clientIds?.Distinct().ToList() ?? new List<int>();
        if (uniqueIds.Count == 0)
        {
            return (Array.Empty<int>(), null);
        }

        var existingIds = await _db.Clients
            .AsNoTracking()
            .Where(c => uniqueIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (existingIds.Count != uniqueIds.Count)
        {
            var missing = uniqueIds.Except(existingIds).ToArray();
            return (Array.Empty<int>(), BadRequest($"Некоторые клиенты не найдены: {string.Join(", ", missing)}"));
        }

        return (existingIds, null);
    }

    private async Task ApplyClientLinksAsync(int legalEntityId, IReadOnlyCollection<int> desiredClientIds, CancellationToken ct)
    {
        var desired = desiredClientIds.ToHashSet();

        var currentClients = await _db.Clients
            .Where(c => c.LegalEntityId == legalEntityId)
            .ToListAsync(ct);

        foreach (var client in currentClients)
        {
            if (!desired.Contains(client.Id))
            {
                client.LegalEntityId = null;
            }
        }

        var clientsToAssign = await _db.Clients
            .Where(c => desired.Contains(c.Id))
            .ToListAsync(ct);

        foreach (var client in clientsToAssign)
        {
            client.LegalEntityId = legalEntityId;
        }
    }
}
