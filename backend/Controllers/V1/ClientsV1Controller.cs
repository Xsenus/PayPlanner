using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Clients;

namespace PayPlanner.Api.Controllers.V1;

[ApiController]
[Route("api/v1/clients")]
[Authorize]
public class ClientsV1Controller : ControllerBase
{
    private readonly PaymentContext _db;
    public ClientsV1Controller(PaymentContext db) => _db = db;

    private IQueryable<Client> BuildVisibleClientsQuery(DataVisibilityAccess? access)
        => _db.Clients.ApplyClientVisibility(access);

    private Task<DataVisibilityAccess?> GetCurrentAccessAsync(CancellationToken ct)
        => _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);

    private async Task<IActionResult?> RequireClientsPermissionAsync(
        Func<RolePermissionValues, bool> isAllowed,
        CancellationToken ct)
        => await _db.HasUserSectionPermissionAsync(User, RolePermissionCatalog.Clients, isAllowed, ct)
            ? null
            : Forbid();

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
    public Task<IActionResult> Get(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int? limit,
        CancellationToken ct)
        => ClientReadTransaction.RunAsync(_db, User, () => GetCoreAsync(search, isActive, sortBy, sortDir, limit, ct), ct);

    private async Task<IActionResult> GetCoreAsync(
        string? search,
        bool? isActive,
        string? sortBy,
        string? sortDir,
        int? limit,
        CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var q = BuildVisibleClientsQuery(access)
            .Include(c => c.ResponsibleUser)
            .AsQueryable()
            .ApplyClientFilters(search, isActive)
            .ApplyClientSort(sortBy, sortDir);

        if (limit.HasValue && limit.Value > 0)
        {
            var size = Math.Min(limit.Value, 200);
            q = q.Take(size);
        }

        var items = await q.AsNoTracking().ToListAsync(ct);
        PopulateResponsibleUserNames(items);
        return Ok(items);
    }

    [HttpGet("{id:int}")]
    public Task<IActionResult> GetById(int id, CancellationToken ct)
        => ClientReadTransaction.RunAsync(_db, User, () => GetByIdCoreAsync(id, ct), ct);

    private async Task<IActionResult> GetByIdCoreAsync(int id, CancellationToken ct)
    {
        if (await RequireClientsPermissionAsync(permission => permission.CanView, ct) is { } forbidden)
        {
            return forbidden;
        }

        var access = await GetCurrentAccessAsync(ct);
        if (access is null)
        {
            return Unauthorized();
        }

        var client = await BuildVisibleClientsQuery(access)
            .Include(c => c.ClientStatus)
            .Include(c => c.ResponsibleUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client is not null)
        {
            client.ResponsibleUserName = FormatResponsibleUserName(client.ResponsibleUser);
            var casesVisibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
            client.Cases = await _db.ClientCases
                .AsNoTracking()
                .ApplyClientCaseVisibility(casesVisibility)
                .Where(clientCase => clientCase.ClientId == client.Id)
                .OrderByDescending(clientCase => clientCase.CreatedAt)
                .ThenByDescending(clientCase => clientCase.Id)
                .ToListAsync(ct);
        }
        if (client is not null) await ClientLegalReferences.PopulateAsync(_db, User, [client], ct);
        return client is not null ? Ok(client) : NotFound();
    }

    [HttpPost]
    public Task<IActionResult> Create([FromBody] Client model, CancellationToken ct)
    {
        return ClientCreateTransaction.RunAsync(_db, User, model, async responsibleUserId =>
        {
            if (ClientCreationInput.Validate(_db, model) is { } graphError) return graphError;
            if (ClientBirthDateInput.Resolve(model, null, out var birthDate) is { } invalidDate) return invalidDate;
            if (await ClientsController.ValidateWriteRequirementsAsync(new FormRequirementsService(_db), model, birthDate, true, ct) is { } missingFields) return missingFields;
            model.BirthDate = birthDate;

            model.ResponsibleUserId = responsibleUserId;
            ClientCreationInput.InitializeServerStamps(model);
            _db.Clients.Add(model);
            await _db.SaveChangesAsync(ct);
            await _db.Entry(model).Reference(c => c.ResponsibleUser).LoadAsync(ct);
            model.ResponsibleUserName = FormatResponsibleUserName(model.ResponsibleUser);
            return Created($"/api/v1/clients/{model.Id}", model);
        }, ct);
    }

    [HttpPut("{id:int}")]
    public Task<IActionResult> Update(int id, [FromBody] Client model, CancellationToken ct)
    {
        return ClientUpdateTransaction.RunAsync(_db, User, id, model, async (e, responsibleUserId) =>
        {
            if (ClientBirthDateInput.Resolve(model, e.BirthDate, out var birthDate) is { } invalidDate) return invalidDate;
            if (await ClientsController.ValidateWriteRequirementsAsync(new FormRequirementsService(_db), model, birthDate, false, ct) is { } missingFields) return missingFields;

            e.Name = model.Name;
            e.Email = model.Email;
            e.Phone = model.Phone;
            e.Company = model.Company;
            e.Address = model.Address;
            e.Notes = model.Notes;
            e.BirthDate = birthDate;
            e.IsActive = model.IsActive;
            e.LegalEntityId = model.LegalEntityId;
            e.ResponsibleUserId = responsibleUserId;
            e.ClientStatusId = model.ClientStatusId;
            e.IsClientAndSupplier = model.IsClientAndSupplier;

            await _db.SaveChangesAsync(ct);
            await _db.Entry(e).Reference(c => c.ResponsibleUser).LoadAsync(ct);
            e.ResponsibleUserName = FormatResponsibleUserName(e.ResponsibleUser);
            return Ok(e);
        }, ct);
    }

    [HttpDelete("{id:int}")]
    public Task<IActionResult> Delete(int id, CancellationToken ct)
        => ClientBatchTransaction.DeleteAsync(_db, User, [id], true, ct);

}
