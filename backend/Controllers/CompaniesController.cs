using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CompaniesController : ControllerBase
{
    private readonly PaymentContext _db;
    public CompaniesController(PaymentContext db) => _db = db;

    private static CompanyResponse MapCompany(Company company)
        => new()
        {
            Id = company.Id,
            Name = company.Name,
            Email = company.Email,
            Phone = company.Phone,
            Address = company.Address,
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
            Name = request.Name,
            Email = request.Email,
            Phone = request.Phone,
            Address = request.Address,
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

        entity.Name = request.Name;
        entity.Email = request.Email;
        entity.Phone = request.Phone;
        entity.Address = request.Address;
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
