using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CompaniesController : ControllerBase
{
    private readonly PaymentContext _db;
    private readonly IDadataService _dadataService;

    public CompaniesController(PaymentContext db, IDadataService dadataService)
    {
        _db = db;
        _dadataService = dadataService;
    }

    private static CompanyResponse MapCompany(Company company)
        => new()
        {
            Id = company.Id,
            Name = string.IsNullOrWhiteSpace(company.Name) ? company.ShortName : company.Name,
            FullName = company.FullName,
            ShortName = company.ShortName,
            Inn = company.Inn,
            Kpp = company.Kpp,
            ActualAddress = company.ActualAddress,
            LegalAddress = company.LegalAddress,
            Email = company.Email,
            Phone = company.Phone,
            Notes = company.Notes,
            CreatedAt = company.CreatedAt,
            IsActive = company.IsActive,
            Members = company.Members
                .Where(link => link.Client is not null)
                .Select(link => new ClientSummaryResponse
                {
                    Id = link.ClientId,
                    Name = link.Client!.Name,
                    Email = link.Client!.Email,
                    Phone = link.Client!.Phone,
                    Role = link.Role,
                })
                .OrderBy(c => c.Name)
                .ToList()
        };

    private async Task<ICollection<int>> GetValidClientIdsAsync(IEnumerable<int> requestedIds, CancellationToken ct)
    {
        var ids = requestedIds?.Distinct().ToList() ?? new List<int>();
        if (ids.Count == 0) return Array.Empty<int>();

        return await _db.Clients
            .Where(c => ids.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var companies = await _db.Companies
            .Include(c => c.Members)
                .ThenInclude(link => link.Client)
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        return Ok(companies.Select(MapCompany));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var company = await _db.Companies
            .Include(c => c.Members)
                .ThenInclude(link => link.Client)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return company is null ? NotFound() : Ok(MapCompany(company));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CompanyRequest request, CancellationToken ct)
    {
        var entity = new Company
        {
            Name = GetShortName(request),
            FullName = GetFullName(request),
            ShortName = GetShortName(request),
            Inn = request.Inn?.Trim() ?? string.Empty,
            Kpp = request.Kpp?.Trim() ?? string.Empty,
            Email = request.Email?.Trim() ?? string.Empty,
            Phone = request.Phone?.Trim() ?? string.Empty,
            ActualAddress = request.ActualAddress?.Trim() ?? string.Empty,
            LegalAddress = request.LegalAddress?.Trim() ?? string.Empty,
            Notes = request.Notes,
            IsActive = request.IsActive,
        };

        var validClientIds = await GetValidClientIdsAsync(request.ClientIds, ct);
        foreach (var clientId in validClientIds)
        {
            entity.Members.Add(new CompanyClient { ClientId = clientId, Company = entity });
        }

        _db.Companies.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(c => c.Members)
            .Query()
            .Include(link => link.Client)
            .LoadAsync(ct);

        return Created($"/api/companies/{entity.Id}", MapCompany(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] CompanyRequest request, CancellationToken ct)
    {
        var entity = await _db.Companies
            .Include(c => c.Members)
                .ThenInclude(link => link.Client)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity is null) return NotFound();

        entity.Name = GetShortName(request);
        entity.FullName = GetFullName(request);
        entity.ShortName = GetShortName(request);
        entity.Inn = request.Inn?.Trim() ?? string.Empty;
        entity.Kpp = request.Kpp?.Trim() ?? string.Empty;
        entity.Email = request.Email?.Trim() ?? string.Empty;
        entity.Phone = request.Phone?.Trim() ?? string.Empty;
        entity.ActualAddress = request.ActualAddress?.Trim() ?? string.Empty;
        entity.LegalAddress = request.LegalAddress?.Trim() ?? string.Empty;
        entity.Notes = request.Notes;
        entity.IsActive = request.IsActive;

        var requestedIds = await GetValidClientIdsAsync(request.ClientIds, ct);
        var existingLinks = entity.Members.ToList();
        var existingIds = existingLinks.Select(link => link.ClientId).ToHashSet();

        foreach (var link in existingLinks.Where(link => !requestedIds.Contains(link.ClientId)))
        {
            _db.CompanyClients.Remove(link);
        }

        foreach (var clientId in requestedIds)
        {
            if (!existingIds.Contains(clientId))
            {
                entity.Members.Add(new CompanyClient { ClientId = clientId, Company = entity });
}

    private static string GetShortName(CompanyRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ShortName))
        {
            return request.ShortName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            return request.Name.Trim();
        }

        return request.FullName?.Trim() ?? string.Empty;
    }

    private static string GetFullName(CompanyRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.FullName))
        {
            return request.FullName.Trim();
        }

        return request.Name?.Trim() ?? string.Empty;
    }

    [HttpGet("lookup/inn/{inn}")]
    public async Task<IActionResult> LookupByInn(string inn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inn))
        {
            return BadRequest(new { message = "ИНН не должен быть пустым" });
        }

        try
        {
            var suggestion = await _dadataService.FindCompanyByInnAsync(inn, ct);
            if (suggestion is null)
            {
                return NotFound();
            }

            return Ok(suggestion);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = ex.Message });
        }
    }
}

        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(c => c.Members)
            .Query()
            .Include(link => link.Client)
            .LoadAsync(ct);

        return Ok(MapCompany(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.Companies.FindAsync(new object[] { id }, ct);
        if (entity is null) return NotFound();

        _db.Companies.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
