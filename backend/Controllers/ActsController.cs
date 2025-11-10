using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ActsController : ControllerBase
{
    private readonly PaymentContext _db;

    public ActsController(PaymentContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ActStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = page <= 0 ? 1 : page;
        pageSize = pageSize <= 0 ? 20 : Math.Min(pageSize, 200);

        var query = _db.Acts.AsNoTracking()
            .ApplyActFilters(from, to, status, clientId, responsibleId, search)
            .ApplyActSort(sortBy, sortDir);

        var total = await query.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await query
            .Skip(skip)
            .Take(pageSize)
            .Select(a => new ActDto
            {
                Id = a.Id,
                Number = a.Number,
                Title = a.Title,
                Date = a.Date,
                Amount = a.Amount,
                InvoiceNumber = a.InvoiceNumber,
                CounterpartyInn = a.CounterpartyInn,
                Status = a.Status,
                ClientId = a.ClientId,
                ClientName = a.Client != null ? a.Client.Name : null,
                ResponsibleId = a.ResponsibleId,
                ResponsibleName = a.Responsible != null ? a.Responsible.FullName : null,
                Comment = a.Comment,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
            })
            .ToListAsync(ct);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ActDto>> GetById(int id, CancellationToken ct)
    {
        var dto = await _db.Acts.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new ActDto
            {
                Id = a.Id,
                Number = a.Number,
                Title = a.Title,
                Date = a.Date,
                Amount = a.Amount,
                InvoiceNumber = a.InvoiceNumber,
                CounterpartyInn = a.CounterpartyInn,
                Status = a.Status,
                ClientId = a.ClientId,
                ClientName = a.Client != null ? a.Client.Name : null,
                ResponsibleId = a.ResponsibleId,
                ResponsibleName = a.Responsible != null ? a.Responsible.FullName : null,
                Comment = a.Comment,
                CreatedAt = a.CreatedAt,
                UpdatedAt = a.UpdatedAt,
            })
            .FirstOrDefaultAsync(ct);

        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ActStatus? status,
        [FromQuery] int? clientId,
        [FromQuery] int? responsibleId,
        [FromQuery] string? search,
        CancellationToken ct = default)
    {
        var query = _db.Acts.AsNoTracking()
            .ApplyActFilters(from, to, status, clientId, responsibleId, search);

        var groups = await query
            .GroupBy(a => a.Status)
            .Select(g => new
            {
                Status = g.Key,
                Amount = g.Sum(x => x.Amount),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        decimal createdAmount = 0m, transferredAmount = 0m, signedAmount = 0m, terminatedAmount = 0m;
        int createdCount = 0, transferredCount = 0, signedCount = 0, terminatedCount = 0;

        foreach (var g in groups)
        {
            switch (g.Status)
            {
                case ActStatus.Created:
                    createdAmount = g.Amount;
                    createdCount = g.Count;
                    break;
                case ActStatus.Transferred:
                    transferredAmount = g.Amount;
                    transferredCount = g.Count;
                    break;
                case ActStatus.Signed:
                    signedAmount = g.Amount;
                    signedCount = g.Count;
                    break;
                case ActStatus.Terminated:
                    terminatedAmount = g.Amount;
                    terminatedCount = g.Count;
                    break;
            }
        }

        var response = new
        {
            created = new { amount = createdAmount, count = createdCount },
            transferred = new { amount = transferredAmount, count = transferredCount },
            signed = new { amount = signedAmount, count = signedCount },
            terminated = new { amount = terminatedAmount, count = terminatedCount },
            totalAmount = groups.Sum(g => g.Amount),
            totalCount = groups.Sum(g => g.Count),
        };

        return Ok(response);
    }

    [HttpGet("responsibles")]
    public async Task<ActionResult<IEnumerable<ActResponsibleDto>>> GetResponsibles(CancellationToken ct = default)
    {
        var items = await _db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.IsApproved && u.IsEmployee)
            .OrderBy(u => u.FullName)
            .Select(u => new ActResponsibleDto
            {
                Id = u.Id,
                FullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName,
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    [HttpPost]
    public async Task<ActionResult<ActDto>> Create([FromBody] UpsertActRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var act = new Act
        {
            Number = request.Number.Trim(),
            Title = Normalize(request.Title),
            Date = request.Date.Date,
            Amount = request.Amount,
            InvoiceNumber = Normalize(request.InvoiceNumber),
            CounterpartyInn = Normalize(request.CounterpartyInn),
            Status = request.Status,
            ClientId = request.ClientId,
            ResponsibleId = request.ResponsibleId,
            Comment = Normalize(request.Comment),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Acts.Add(act);
        await _db.SaveChangesAsync(ct);

        await _db.Entry(act).Reference(a => a.Client).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Responsible).LoadAsync(ct);

        return Created($"/api/acts/{act.Id}", Map(act));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ActDto>> Update(int id, [FromBody] UpsertActRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var act = await _db.Acts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (act is null)
        {
            return NotFound();
        }

        act.Number = request.Number.Trim();
        act.Title = Normalize(request.Title);
        act.Date = request.Date.Date;
        act.Amount = request.Amount;
        act.InvoiceNumber = Normalize(request.InvoiceNumber);
        act.CounterpartyInn = Normalize(request.CounterpartyInn);
        act.Status = request.Status;
        act.ClientId = request.ClientId;
        act.ResponsibleId = request.ResponsibleId;
        act.Comment = Normalize(request.Comment);
        act.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        await _db.Entry(act).Reference(a => a.Client).LoadAsync(ct);
        await _db.Entry(act).Reference(a => a.Responsible).LoadAsync(ct);

        return Ok(Map(act));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var act = await _db.Acts.FindAsync(new object[] { id }, ct);
        if (act is null)
        {
            return NotFound();
        }

        _db.Acts.Remove(act);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ActDto Map(Act act) => new()
    {
        Id = act.Id,
        Number = act.Number,
        Title = act.Title,
        Date = act.Date,
        Amount = act.Amount,
        InvoiceNumber = act.InvoiceNumber,
        CounterpartyInn = act.CounterpartyInn,
        Status = act.Status,
        ClientId = act.ClientId,
        ClientName = act.Client?.Name,
        ResponsibleId = act.ResponsibleId,
        ResponsibleName = act.Responsible?.FullName,
        Comment = act.Comment,
        CreatedAt = act.CreatedAt,
        UpdatedAt = act.UpdatedAt,
    };
}
