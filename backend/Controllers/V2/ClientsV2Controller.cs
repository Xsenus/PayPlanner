using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/clients")]
[Authorize]
public class ClientsV2Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV2Controller(PaymentContext db) => _db = db;

    private static ClientResponse MapClient(Client client)
        => new()
        {
            Id = client.Id,
            Name = client.Name,
            Email = client.Email,
            Phone = client.Phone,
            Company = client.Company,
            Address = client.Address,
            Notes = client.Notes,
            CreatedAt = client.CreatedAt,
            IsActive = client.IsActive,
            Cases = client.Cases.OrderByDescending(c => c.CreatedAt).ToList(),
            Companies = client.CompanyMemberships
                .Where(link => link.Company is not null)
                .Select(link => new CompanySummaryResponse
                {
                    Id = link.CompanyId,
                    Name = link.Company!.Name,
                    Email = link.Company!.Email,
                    Phone = link.Company!.Phone,
                    Role = link.Role,
                })
                .OrderBy(c => c.Name)
                .ToList()
        };

    private async Task<ICollection<int>> GetValidCompanyIdsAsync(IEnumerable<int> requestedIds, CancellationToken ct)
    {
        var ids = requestedIds?.Distinct().ToList() ?? new List<int>();
        if (ids.Count == 0) return Array.Empty<int>();

        return await _db.Companies
            .Where(c => ids.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var query = _db.Clients.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            query = query.Where(c =>
                (c.Name != null && EF.Functions.Like(c.Name, $"%{search}%")) ||
                (c.Email != null && EF.Functions.Like(c.Email, $"%{search}%")) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, $"%{search}%")) ||
                (c.Company != null && EF.Functions.Like(c.Company, $"%{search}%")) ||
                (c.Address != null && EF.Functions.Like(c.Address, $"%{search}%")));
        }

        if (isActive.HasValue)
        {
            query = query.Where(c => c.IsActive == isActive.Value);
        }

        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        query = (sortBy?.ToLowerInvariant()) switch
        {
            "createdat" => desc ? query.OrderByDescending(c => c.CreatedAt) : query.OrderBy(c => c.CreatedAt),
            _ => desc ? query.OrderByDescending(c => c.Name) : query.OrderBy(c => c.Name),
        };

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(c => c.Cases)
            .Include(c => c.CompanyMemberships)
                .ThenInclude(link => link.Company)
            .AsNoTracking()
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Ok(new
        {
            items = items.Select(MapClient).ToList(),
            total,
            page,
            pageSize
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var client = await _db.Clients
            .Include(c => c.Cases)
            .Include(c => c.CompanyMemberships)
                .ThenInclude(link => link.Company)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        return client is not null ? Ok(MapClient(client)) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ClientRequest request, CancellationToken ct)
    {
        var entity = new Client
        {
            Name = request.Name,
            Email = request.Email,
            Phone = request.Phone,
            Company = request.Company,
            Address = request.Address,
            Notes = request.Notes,
            IsActive = request.IsActive,
        };

        var validCompanyIds = await GetValidCompanyIdsAsync(request.CompanyIds, ct);
        foreach (var companyId in validCompanyIds)
        {
            entity.CompanyMemberships.Add(new CompanyClient { CompanyId = companyId, Client = entity });
        }

        _db.Clients.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(c => c.Cases).LoadAsync(ct);
        await _db.Entry(entity).Collection(c => c.CompanyMemberships)
            .Query()
            .Include(link => link.Company)
            .LoadAsync(ct);

        return Created($"/api/v2/clients/{entity.Id}", MapClient(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ClientRequest request, CancellationToken ct)
    {
        var entity = await _db.Clients
            .Include(c => c.CompanyMemberships)
            .ThenInclude(link => link.Company)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (entity is null) return NotFound();

        entity.Name = request.Name;
        entity.Email = request.Email;
        entity.Phone = request.Phone;
        entity.Company = request.Company;
        entity.Address = request.Address;
        entity.Notes = request.Notes;
        entity.IsActive = request.IsActive;

        var requestedIds = await GetValidCompanyIdsAsync(request.CompanyIds, ct);
        var existingLinks = entity.CompanyMemberships.ToList();
        var existingIds = existingLinks.Select(link => link.CompanyId).ToHashSet();

        foreach (var link in existingLinks.Where(link => !requestedIds.Contains(link.CompanyId)))
        {
            _db.CompanyClients.Remove(link);
        }

        foreach (var companyId in requestedIds)
        {
            if (!existingIds.Contains(companyId))
            {
                entity.CompanyMemberships.Add(new CompanyClient { CompanyId = companyId, Client = entity });
            }
        }

        await _db.SaveChangesAsync(ct);

        await _db.Entry(entity).Collection(c => c.Cases).LoadAsync(ct);
        await _db.Entry(entity).Collection(c => c.CompanyMemberships)
            .Query()
            .Include(link => link.Company)
            .LoadAsync(ct);

        return Ok(MapClient(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await _db.Clients.FindAsync(new object[] { id }, ct);
        if (entity is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientId == id).ToListAsync(ct);
        foreach (var payment in payments)
        {
            payment.ClientId = null;
            payment.ClientCaseId = null;
        }

        await _db.SaveChangesAsync(ct);

        _db.Clients.Remove(entity);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
