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
    public async Task<IActionResult> Get([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] int? clientId,
        [FromQuery] string? search, [FromQuery] string? sortBy, [FromQuery] string? sortDir, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);

        var query = ApplySort(ApplyFilters(BuildContractsQuery(), from, to, clientId, search), sortBy, sortDir);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(MapToDtoExpression).ToListAsync(ct);
        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ContractDto>> GetById(int id, CancellationToken ct)
    {
        var dto = await BuildContractsQuery().Where(c => c.Id == id).Select(MapToDtoExpression).FirstOrDefaultAsync(ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpPost]
    public async Task<ActionResult<ContractDto>> Create([FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var normalized = await ValidateAndNormalizePayload(request, ct);
        if (normalized.Error is not null) return normalized.Error;

        var contract = new Contract
        {
            Number = normalized.Number!,
            Title = Normalize(request.Title),
            Description = Normalize(request.Description),
            Amount = request.Amount,
            Date = request.Date.Date,
            ValidUntil = request.ValidUntil?.Date,
            CreatedAt = DateTime.UtcNow,
        };

        ApplyLinks(contract, normalized.Clients, normalized.ActIds, normalized.InvoiceIds, normalized.PaymentIds);

        _db.Contracts.Add(contract);
        await _db.SaveChangesAsync(ct);

        var dto = await BuildContractsQuery().Where(c => c.Id == contract.Id).Select(MapToDtoExpression).FirstAsync(ct);
        return Created($"/api/contracts/{dto.Id}", dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ContractDto>> Update(int id, [FromBody] UpsertContractRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var contract = await _db.Contracts
            .Include(c => c.ClientContracts)
            .Include(c => c.ContractActs)
            .Include(c => c.ContractInvoices)
            .Include(c => c.ContractPayments)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();

        var normalized = await ValidateAndNormalizePayload(request, ct);
        if (normalized.Error is not null) return normalized.Error;

        contract.Number = normalized.Number!;
        contract.Title = Normalize(request.Title);
        contract.Description = Normalize(request.Description);
        contract.Amount = request.Amount;
        contract.Date = request.Date.Date;
        contract.ValidUntil = request.ValidUntil?.Date;
        contract.UpdatedAt = DateTime.UtcNow;

        SyncClientLinks(contract, normalized.Clients);
        SyncLinks(contract.ContractActs, normalized.ActIds, id, actId => new ContractAct { ContractId = id, ActId = actId }, (l, v) => l.ActId == v);
        SyncLinks(contract.ContractInvoices, normalized.InvoiceIds, id, invoiceId => new ContractInvoice { ContractId = id, InvoiceId = invoiceId }, (l, v) => l.InvoiceId == v);
        SyncLinks(contract.ContractPayments, normalized.PaymentIds, id, paymentId => new ContractPayment { ContractId = id, PaymentId = paymentId }, (l, v) => l.PaymentId == v);

        await _db.SaveChangesAsync(ct);
        var dto = await BuildContractsQuery().Where(c => c.Id == contract.Id).Select(MapToDtoExpression).FirstAsync(ct);
        return Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var contract = await _db.Contracts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return NotFound();
        _db.Contracts.Remove(contract);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IQueryable<Contract> BuildContractsQuery() => _db.Contracts.AsNoTracking()
        .Include(c => c.ClientContracts).ThenInclude(cc => cc.Client)
        .Include(c => c.ContractActs)
        .Include(c => c.ContractInvoices)
        .Include(c => c.ContractPayments);

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
        Clients = contract.ClientContracts.Where(link => link.Client != null).Select(link => new ContractClientDto
        {
            Id = link.ClientId,
            Name = link.Client!.Name,
            Company = link.Client!.Company,
            Comment = link.Comment,
        }).OrderBy(c => c.Name).ToList(),
        ActIds = contract.ContractActs.Select(x => x.ActId).ToList(),
        InvoiceIds = contract.ContractInvoices.Select(x => x.InvoiceId).ToList(),
        PaymentIds = contract.ContractPayments.Select(x => x.PaymentId).ToList(),
    };

    private async Task<(ActionResult? Error, string? Number, List<(int ClientId, string? Comment)> Clients, List<int> ActIds, List<int> InvoiceIds, List<int> PaymentIds)> ValidateAndNormalizePayload(UpsertContractRequest request, CancellationToken ct)
    {
        if (request.Date == default) return (ValidationError(nameof(request.Date), "Дата договора обязательна."), null, [], [], [], []);
        var number = Normalize(request.Number);
        if (string.IsNullOrEmpty(number)) return (ValidationError(nameof(request.Number), "Номер договора обязателен."), null, [], [], [], []);

        var clients = request.Clients?.Where(x => x.ClientId > 0).GroupBy(x => x.ClientId)
            .Select(g => (ClientId: g.Key, Comment: Normalize(g.Last().Comment))).ToList() ?? [];
        if (clients.Count == 0) return (BadRequest("Необходимо выбрать хотя бы одного клиента."), null, [], [], [], []);

        var clientIds = clients.Select(x => x.ClientId).ToList();
        var existingClients = await _db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
        if (existingClients.Count != clientIds.Count) return (BadRequest($"Не найдены клиенты: {string.Join(", ", clientIds.Except(existingClients))}"), null, [], [], [], []);

        var actIds = NormalizeIds(request.ActIds);
        if (actIds.Count > 0)
        {
            var existing = await _db.Acts.AsNoTracking().Where(x => actIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != actIds.Count) return (BadRequest($"Не найдены акты: {string.Join(", ", actIds.Except(existing))}"), null, [], [], [], []);
        }

        var invoiceIds = NormalizeIds(request.InvoiceIds);
        if (invoiceIds.Count > 0)
        {
            var existing = await _db.Payments.AsNoTracking().Where(x => invoiceIds.Contains(x.Id) && x.Account != null && x.Account != "").Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != invoiceIds.Count) return (BadRequest($"Не найдены счета: {string.Join(", ", invoiceIds.Except(existing))}"), null, [], [], [], []);
        }

        var paymentIds = NormalizeIds(request.PaymentIds);
        if (paymentIds.Count > 0)
        {
            var existing = await _db.Payments.AsNoTracking().Where(x => paymentIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            if (existing.Count != paymentIds.Count) return (BadRequest($"Не найдены начисления: {string.Join(", ", paymentIds.Except(existing))}"), null, [], [], [], []);
        }

        return (null, number, clients, actIds, invoiceIds, paymentIds);
    }

    private static void ApplyLinks(Contract contract, List<(int ClientId, string? Comment)> clients, List<int> actIds, List<int> invoiceIds, List<int> paymentIds)
    {
        foreach (var client in clients) contract.ClientContracts.Add(new ClientContract { ClientId = client.ClientId, Comment = client.Comment });
        foreach (var actId in actIds) contract.ContractActs.Add(new ContractAct { ActId = actId });
        foreach (var invoiceId in invoiceIds) contract.ContractInvoices.Add(new ContractInvoice { InvoiceId = invoiceId });
        foreach (var paymentId in paymentIds) contract.ContractPayments.Add(new ContractPayment { PaymentId = paymentId });
    }

    private static void SyncClientLinks(Contract contract, List<(int ClientId, string? Comment)> clients)
    {
        var requested = clients.ToDictionary(x => x.ClientId, x => x.Comment);
        foreach (var link in contract.ClientContracts.Where(x => !requested.ContainsKey(x.ClientId)).ToList())
            contract.ClientContracts.Remove(link);
        foreach (var (clientId, comment) in requested)
        {
            var existing = contract.ClientContracts.FirstOrDefault(x => x.ClientId == clientId);
            if (existing is null) contract.ClientContracts.Add(new ClientContract { ContractId = contract.Id, ClientId = clientId, Comment = comment });
            else existing.Comment = comment;
        }
    }

    private static void SyncLinks<TLink>(ICollection<TLink> existing, List<int> targetIds, int contractId, Func<int, TLink> create, Func<TLink, int, bool> match)
    {
        foreach (var link in existing.Where(link => !targetIds.Any(id => match(link, id))).ToList()) existing.Remove(link);
        foreach (var id in targetIds)
            if (!existing.Any(link => match(link, id))) existing.Add(create(id));
    }

    private static IQueryable<Contract> ApplyFilters(IQueryable<Contract> query, DateTime? from, DateTime? to, int? clientId, string? search)
    {
        if (from.HasValue) query = query.Where(c => c.Date >= from.Value.Date);
        if (to.HasValue) query = query.Where(c => c.Date <= to.Value.Date);
        if (clientId.HasValue) query = query.Where(c => c.ClientContracts.Any(link => link.ClientId == clientId.Value));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim())}%";
            query = query.Where(c => EF.Functions.Like(c.Number, pattern, "\\") || (c.Title != null && EF.Functions.Like(c.Title, pattern, "\\")) || (c.Description != null && EF.Functions.Like(c.Description, pattern, "\\")));
        }
        return query;
    }

    private static IQueryable<Contract> ApplySort(IQueryable<Contract> query, string? sortBy, string? sortDir)
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

    private ActionResult ValidationError(string field, string message)
    {
        ModelState.AddModelError(field, message);
        return ValidationProblem(ModelState);
    }

    private static List<int> NormalizeIds(IEnumerable<int>? ids) => ids?.Where(x => x > 0).Distinct().ToList() ?? [];
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
