using System;
using System.Linq;
using System.Linq.Expressions;
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
public class ContractsController : ControllerBase
{
    private readonly PaymentContext _db;

    public ContractsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? clientId,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);

        var query = BuildContractsQuery();
        query = ApplyFilters(query, from, to, clientId, search);
        query = ApplySort(query, sortBy, sortDir);

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(MapToDtoExpression)
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ContractDto>> GetById(int id, CancellationToken ct)
    {
        var dto = await BuildContractsQuery()
            .Where(c => c.Id == id)
            .Select(MapToDtoExpression)
            .FirstOrDefaultAsync(ct);

        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    public async Task<ActionResult<ContractDto>> Create([FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (request.Date == default)
        {
            ModelState.AddModelError(nameof(request.Date), "Дата договора обязательна.");
            return ValidationProblem(ModelState);
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            ModelState.AddModelError(nameof(request.Number), "Номер договора обязателен.");
            return ValidationProblem(ModelState);
        }

        var (clientIds, error) = await ValidateClientIdsAsync(request.ClientIds, ct);
        if (error is not null)
        {
            return error;
        }

        var contract = new Contract
        {
            Number = number,
            Title = Normalize(request.Title),
            Description = Normalize(request.Description),
            Amount = request.Amount,
            Date = request.Date.Date,
            ValidUntil = request.ValidUntil?.Date,
            CreatedAt = DateTime.UtcNow,
        };

        foreach (var clientId in clientIds)
        {
            contract.ClientContracts.Add(new ClientContract { ClientId = clientId });
        }

        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);

        var dto = await BuildContractsQuery()
            .Where(c => c.Id == contract.Id)
            .Select(MapToDtoExpression)
            .FirstAsync(ct);

        return Created($"/api/contracts/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ContractDto>> Update(int id, [FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var contract = await _db.Contracts
            .Include(c => c.ClientContracts)
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (contract is null)
        {
            return NotFound();
        }

        if (request.Date == default)
        {
            ModelState.AddModelError(nameof(request.Date), "Дата договора обязательна.");
            return ValidationProblem(ModelState);
        }

        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number))
        {
            ModelState.AddModelError(nameof(request.Number), "Номер договора обязателен.");
            return ValidationProblem(ModelState);
        }

        var (clientIds, error) = await ValidateClientIdsAsync(request.ClientIds, ct);
        if (error is not null)
        {
            return error;
        }

        contract.Number = number;
        contract.Title = Normalize(request.Title);
        contract.Description = Normalize(request.Description);
        contract.Amount = request.Amount;
        contract.Date = request.Date.Date;
        contract.ValidUntil = request.ValidUntil?.Date;
        contract.UpdatedAt = DateTime.UtcNow;

        var requested = clientIds.ToHashSet();
        var existing = contract.ClientContracts.ToDictionary(cc => cc.ClientId);

        foreach (var link in existing.Values.Where(link => !requested.Contains(link.ClientId)).ToList())
        {
            _db.ClientContracts.Remove(link);
        }

        foreach (var clientId in requested)
        {
            if (!existing.ContainsKey(clientId))
            {
                contract.ClientContracts.Add(new ClientContract
                {
                    ContractId = contract.Id,
                    ClientId = clientId,
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        var dto = await BuildContractsQuery()
            .Where(c => c.Id == contract.Id)
            .Select(MapToDtoExpression)
            .FirstAsync(ct);

        return Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null)
        {
            return NotFound();
        }

        _db.Contracts.Remove(contract);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    private IQueryable<Contract> BuildContractsQuery()
        => _db.Contracts
            .AsNoTracking()
            .Include(c => c.ClientContracts)
            .ThenInclude(cc => cc.Client)
            .ThenInclude(client => client!.ClientStatus);

    private static IQueryable<Contract> ApplyFilters(
        IQueryable<Contract> query,
        DateTime? from,
        DateTime? to,
        int? clientId,
        string? search)
    {
        if (from.HasValue)
        {
            var start = from.Value.Date;
            query = query.Where(c => c.Date >= start);
        }

        if (to.HasValue)
        {
            var end = to.Value.Date;
            query = query.Where(c => c.Date <= end);
        }

        if (clientId.HasValue)
        {
            query = query.Where(c => c.ClientContracts.Any(link => link.ClientId == clientId.Value));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Number, pattern, "\\") ||
                (c.Title != null && EF.Functions.Like(c.Title, pattern, "\\")) ||
                (c.Description != null && EF.Functions.Like(c.Description, pattern, "\\")));
        }

        return query;
    }

    private static IQueryable<Contract> ApplySort(
        IQueryable<Contract> query,
        string? sortBy,
        string? sortDir)
    {
        var descending = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

        return sortBy?.ToLowerInvariant() switch
        {
            "number" => descending ? query.OrderByDescending(c => c.Number) : query.OrderBy(c => c.Number),
            "amount" => descending ? query.OrderByDescending(c => c.Amount) : query.OrderBy(c => c.Amount),
            "createdat" => descending ? query.OrderByDescending(c => c.CreatedAt) : query.OrderBy(c => c.CreatedAt),
            "date" => descending ? query.OrderByDescending(c => c.Date) : query.OrderBy(c => c.Date),
            _ => query.OrderByDescending(c => c.Date).ThenByDescending(c => c.Id),
        };
    }

    private static readonly Expression<Func<Contract, ContractDto>> MapToDtoExpression = contract => new ContractDto
    {
        Id = contract.Id,
        Number = contract.Number,
        Title = contract.Title,
        Description = contract.Description,
        Amount = contract.Amount,
        Date = contract.Date,
        ValidUntil = contract.ValidUntil,
        CreatedAt = contract.CreatedAt,
        UpdatedAt = contract.UpdatedAt,
        Clients = contract.ClientContracts
            .Where(link => link.Client != null)
            .Select(link => new ContractClientDto
            {
                Id = link.ClientId,
                Name = link.Client!.Name,
                Company = link.Client!.Company,
                ClientStatusId = link.Client!.ClientStatusId,
                ClientStatusName = link.Client!.ClientStatus != null ? link.Client!.ClientStatus.Name : null,
                ClientStatusColorHex = link.Client!.ClientStatus != null ? link.Client!.ClientStatus.ColorHex : null,
            })
            .OrderBy(c => c.Name)
            .ToList()
    };

    private async Task<(List<int> ClientIds, ActionResult? Error)> ValidateClientIdsAsync(
        IEnumerable<int> clientIds,
        CancellationToken ct)
    {
        var normalized = clientIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
        {
            return (normalized, BadRequest("Необходимо выбрать хотя бы одного клиента."));
        }

        var existing = await _db.Clients
            .AsNoTracking()
            .Where(c => normalized.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (existing.Count != normalized.Count)
        {
            var missing = normalized.Except(existing).ToArray();
            return (existing, BadRequest($"Не найдены клиенты: {string.Join(", ", missing)}"));
        }

        return (existing, null);
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
