using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/clients/{clientId:int}/passport")]
[Authorize]
[RequireSectionPermission(RolePermissionCatalog.Clients)]
public class ClientPassportController : ControllerBase
{
    private readonly PaymentContext _db;

    public ClientPassportController(PaymentContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Возвращает основной паспорт клиента одним SQL-запросом и проецирует
    /// только поля, разрешённые интерфейсу. Отсутствующий клиент даёт 404,
    /// а клиент без паспорта — 204.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get(int clientId, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);
        if (visibility is null) return Forbid();
        var lookup = await _db.Clients
            .AsNoTracking()
            .ApplyClientVisibility(visibility)
            .Where(client => client.Id == clientId)
            .Select(client => new
            {
                Passport = client.Passports
                    .OrderByDescending(passport => passport.IsPrimary)
                    .ThenByDescending(passport => passport.UpdatedAt)
                    .ThenByDescending(passport => passport.CreatedAt)
                    .Select(passport => new ClientPassportResponse(
                        passport.Id,
                        passport.ClientId,
                        passport.Series,
                        passport.Number,
                        passport.IssuedBy,
                        passport.IssuedAt,
                        passport.DepartmentCode,
                        passport.BirthPlacePassport,
                        passport.BirthPlaceFias,
                        passport.ValidFrom,
                        passport.ValidTo,
                        passport.IsPrimary,
                        passport.CreatedAt,
                        passport.UpdatedAt))
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        if (lookup is null) return NotFound();
        return lookup.Passport is null ? NoContent() : Ok(lookup.Passport);
    }

    /// <summary>
    /// Создаёт или изменяет основной паспорт. Клиент и его текущий основной
    /// паспорт загружаются одним запросом; входной контракт не принимает
    /// идентификаторы, признаки основного документа и поля шифрования.
    /// </summary>
    [HttpPut]
    [RequireSectionPermission(RolePermissionCatalog.Clients, RequireSectionPermissionOperations.Edit)]
    public async Task<IActionResult> Upsert(int clientId, [FromBody] UpsertClientPassportRequest model, CancellationToken ct)
    {
        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Clients, ct);
        if (visibility is null) return Forbid();
        var lookup = await _db.Clients
            .ApplyClientVisibility(visibility)
            .Where(client => client.Id == clientId)
            .Select(client => new
            {
                Passport = client.Passports
                    .OrderByDescending(passport => passport.IsPrimary)
                    .ThenByDescending(passport => passport.UpdatedAt)
                    .ThenByDescending(passport => passport.CreatedAt)
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (lookup is null) return NotFound();

        var entity = lookup.Passport;
        if (entity is null)
        {
            entity = new ClientPassport { ClientId = clientId, IsPrimary = true };
            _db.ClientPassports.Add(entity);
        }

        entity.Series = Normalize(model.Series);
        entity.Number = Normalize(model.Number);
        entity.IssuedBy = Normalize(model.IssuedBy);
        entity.IssuedAt = model.IssuedAt;
        entity.DepartmentCode = Normalize(model.DepartmentCode);
        entity.BirthPlacePassport = Normalize(model.BirthPlacePassport);
        entity.BirthPlaceFias = Normalize(model.BirthPlaceFias);
        entity.ValidFrom = model.ValidFrom;
        entity.ValidTo = model.ValidTo;
        entity.IsPrimary = true;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(ToResponse(entity));
    }

    /// <summary>Удаляет внешние пробелы и приводит пустое значение к null.</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Формирует безопасный ответ без криптографических служебных полей.</summary>
    private static ClientPassportResponse ToResponse(ClientPassport passport) => new(
        passport.Id,
        passport.ClientId,
        passport.Series,
        passport.Number,
        passport.IssuedBy,
        passport.IssuedAt,
        passport.DepartmentCode,
        passport.BirthPlacePassport,
        passport.BirthPlaceFias,
        passport.ValidFrom,
        passport.ValidTo,
        passport.IsPrimary,
        passport.CreatedAt,
        passport.UpdatedAt);
}
