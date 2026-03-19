using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Controllers.V2;

[ApiController]
[Route("api/v2/clients")]
[Authorize]
public class ClientsV2Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV2Controller(PaymentContext db) => _db = db;

    private IQueryable<Client> BuildVisibleClientsQuery(int? currentUserId)
        => _db.Clients.ApplyClientVisibility(User.IsInRole("admin"), currentUserId);

    private static string? FormatResponsibleUserName(User? user)
    {
        if (user is null) return null;
        return string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName;
    }

    private static void PopulateResponsibleUserNames(IEnumerable<Client> clients)
    {
        foreach (var client in clients)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        }
    }

    private async Task<bool> ResponsibleUserExistsAsync(int? responsibleUserId, CancellationToken ct)
    {
        if (!responsibleUserId.HasValue) return true;
        return await _db.Users.AsNoTracking().AnyAsync(u => u.Id == responsibleUserId.Value && u.IsActive, ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int? id,
        [FromQuery] string? name,
        [FromQuery] string? email,
        [FromQuery] string? phone,
        [FromQuery] string? company,
        [FromQuery] string? address,
        [FromQuery] string? notes,
        [FromQuery] int? legalEntityId,
        [FromQuery] string? legalEntityShortName,
        [FromQuery] string? legalEntityFullName,
        [FromQuery] string? legalEntityInn,
        [FromQuery] string? legalEntityOgrn,
        [FromQuery] string? legalEntityKpp,
        [FromQuery] int? clientStatusId,
        [FromQuery] string? clientStatus,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var currentUserId = User.GetUserId();
        if (!User.IsInRole("admin") && !currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var q = BuildVisibleClientsQuery(currentUserId)
            .Include(c => c.Cases)
            .Include(c => c.Contracts)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            q = q.Where(c =>
                (c.Name != null && EF.Functions.Like(c.Name, $"%{search}%")) ||
                (c.Email != null && EF.Functions.Like(c.Email, $"%{search}%")) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, $"%{search}%")) ||
                (c.Company != null && EF.Functions.Like(c.Company, $"%{search}%")) ||
                (c.Address != null && EF.Functions.Like(c.Address, $"%{search}%")));
        }

        if (id.HasValue)
            q = q.Where(c => c.Id == id.Value);
        if (!string.IsNullOrWhiteSpace(name))
            q = q.Where(c => c.Name != null && EF.Functions.Like(c.Name, $"%{name.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(email))
            q = q.Where(c => c.Email != null && EF.Functions.Like(c.Email, $"%{email.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(phone))
            q = q.Where(c => c.Phone != null && EF.Functions.Like(c.Phone, $"%{phone.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(company))
            q = q.Where(c => c.Company != null && EF.Functions.Like(c.Company, $"%{company.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(address))
            q = q.Where(c => c.Address != null && EF.Functions.Like(c.Address, $"%{address.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(notes))
            q = q.Where(c => c.Notes != null && EF.Functions.Like(c.Notes, $"%{notes.Trim()}%"));
        if (legalEntityId.HasValue)
            q = q.Where(c => c.LegalEntityId == legalEntityId.Value);
        if (!string.IsNullOrWhiteSpace(legalEntityShortName))
            q = q.Where(c => c.LegalEntity != null && EF.Functions.Like(c.LegalEntity.ShortName, $"%{legalEntityShortName.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(legalEntityFullName))
            q = q.Where(c => c.LegalEntity != null && EF.Functions.Like(c.LegalEntity.FullName, $"%{legalEntityFullName.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(legalEntityInn))
            q = q.Where(c => c.LegalEntity != null && EF.Functions.Like(c.LegalEntity.Inn, $"%{legalEntityInn.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(legalEntityOgrn))
            q = q.Where(c => c.LegalEntity != null && EF.Functions.Like(c.LegalEntity.Ogrn, $"%{legalEntityOgrn.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(legalEntityKpp))
            q = q.Where(c => c.LegalEntity != null && EF.Functions.Like(c.LegalEntity.Kpp, $"%{legalEntityKpp.Trim()}%"));
        if (clientStatusId.HasValue)
            q = q.Where(c => c.ClientStatusId == clientStatusId.Value);
        if (!string.IsNullOrWhiteSpace(clientStatus))
        {
            var statusValue = clientStatus.Trim();
            if (string.Equals(statusValue, "__none__", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(c => c.ClientStatusId == null);
            }
            else
            {
                q = q.Where(c => c.ClientStatus != null && EF.Functions.Like(c.ClientStatus.Name, $"%{statusValue}%"));
            }
        }

        if (isActive.HasValue)
            q = q.Where(c => c.IsActive == isActive.Value);

        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        q = (sortBy?.ToLowerInvariant()) switch
        {
            "createdat" => (desc
                ? q.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.CreatedAt)
                : q.OrderByDescending(c => c.IsActive).ThenBy(c => c.CreatedAt)),
            _ => (desc
                ? q.OrderByDescending(c => c.IsActive).ThenByDescending(c => c.Name)
                : q.OrderByDescending(c => c.IsActive).ThenBy(c => c.Name)),
        };

        var total = await q.CountAsync(ct);
        var items = await q.AsNoTracking().Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        PopulateResponsibleUserNames(items);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (!User.IsInRole("admin") && !currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(currentUserId)
            .Include(c => c.Cases)
            .Include(c => c.LegalEntity)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is not null)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
        }
        return client is not null ? Ok(client) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Client model, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (!currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var responsibleUserId = User.IsInRole("admin")
            ? (model.ResponsibleUserId ?? currentUserId.Value)
            : currentUserId.Value;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        model.ResponsibleUserId = responsibleUserId;
        _db.Clients.Add(model);
        await _db.SaveChangesAsync(ct);
        await _db.Entry(model).Reference(c => c.ResponsibleUser).LoadAsync(ct);
        model.ResponsibleUserName = FormatResponsibleUserName(model.ResponsibleUser);
        return Created($"/api/v2/clients/{model.Id}", model);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] Client model, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (!User.IsInRole("admin") && !currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(currentUserId)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var responsibleUserId = User.IsInRole("admin")
            ? model.ResponsibleUserId
            : e.ResponsibleUserId;
        if (!await ResponsibleUserExistsAsync(responsibleUserId, ct))
        {
            return BadRequest($"Ответственный #{responsibleUserId} не найден");
        }

        e.Name = model.Name;
        e.Email = model.Email;
        e.Phone = model.Phone;
        e.Company = model.Company;
        e.Address = model.Address;
        e.Notes = model.Notes;
        e.IsActive = model.IsActive;
        e.LegalEntityId = model.LegalEntityId;
        e.ResponsibleUserId = responsibleUserId;
        e.ClientStatusId = model.ClientStatusId;

        await _db.SaveChangesAsync(ct);
        await _db.Entry(e).Reference(c => c.ResponsibleUser).LoadAsync(ct);
        e.ResponsibleUserName = FormatResponsibleUserName(e.ResponsibleUser);
        return Ok(e);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var currentUserId = User.GetUserId();
        if (!User.IsInRole("admin") && !currentUserId.HasValue)
        {
            return Unauthorized();
        }

        var e = await BuildVisibleClientsQuery(currentUserId)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (e is null) return NotFound();

        var payments = await _db.Payments.Where(p => p.ClientId == id).ToListAsync(ct);
        foreach (var p in payments) { p.ClientId = null; p.ClientCaseId = null; }
        await _db.SaveChangesAsync(ct);

        _db.Clients.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
