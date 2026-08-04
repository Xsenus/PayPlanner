using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Filters;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/client-reference-data")]
[Authorize]
[RequireSectionPermission(RolePermissionCatalog.Clients)]
public class ClientReferenceDataController : ControllerBase
{
    private readonly PaymentContext _db;

    public ClientReferenceDataController(PaymentContext db)
    {
        _db = db;
    }

    /// <summary>Ищет активные значения ОКИН и возвращает не более заданного лимита.</summary>
    [HttpGet("okin")]
    public async Task<IActionResult> GetOkin([FromQuery] ClientReferenceSearchQuery query, CancellationToken ct)
        => Ok(await SearchAsync(_db.OkinDictionaryItems
            .AsNoTracking(), query, ct));

    /// <summary>Ищет активные значения ОКСО и возвращает не более заданного лимита.</summary>
    [HttpGet("okso")]
    public async Task<IActionResult> GetOkso([FromQuery] ClientReferenceSearchQuery query, CancellationToken ct)
        => Ok(await SearchAsync(_db.OksoDictionaryItems
            .AsNoTracking(), query, ct));

    /// <summary>Ищет активные значения ОКПДТР и возвращает не более заданного лимита.</summary>
    [HttpGet("okpdtr")]
    public async Task<IActionResult> GetOkpdtr([FromQuery] ClientReferenceSearchQuery query, CancellationToken ct)
        => Ok(await SearchAsync(_db.OkpdtrDictionaryItems
            .AsNoTracking(), query, ct));

    /// <summary>
    /// Применяет единые правила поиска ко всем трём классификаторам.
    /// Проекция выполняется до materialization, поэтому база возвращает только
    /// идентификатор, код и название, а <c>Take</c> ограничивает объём ответа.
    /// </summary>
    private static Task<List<ClientReferenceItemResponse>> SearchAsync<TEntity>(
        IQueryable<TEntity> source,
        ClientReferenceSearchQuery query,
        CancellationToken ct)
        where TEntity : class
    {
        // EF.Property позволяет использовать один оптимизированный запрос для трёх
        // отдельных сущностей с одинаковым набором полей без клиентской обработки.
        source = source.Where(item => EF.Property<bool>(item, "IsActive"));

        var term = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var normalizedTerm = term.ToLower();
            source = source
                .Where(item => EF.Property<string>(item, "Code").StartsWith(term)
                    || EF.Property<string>(item, "Name").ToLower().Contains(normalizedTerm))
                .OrderByDescending(item => EF.Property<string>(item, "Code").StartsWith(term))
                .ThenBy(item => EF.Property<string>(item, "Code"))
                .ThenBy(item => EF.Property<string>(item, "Name"));
        }
        else
        {
            source = source
                .OrderBy(item => EF.Property<string>(item, "Name"))
                .ThenBy(item => EF.Property<string>(item, "Code"));
        }

        return source
            .Take(query.Limit)
            .Select(item => new ClientReferenceItemResponse(
                EF.Property<int>(item, "Id"),
                EF.Property<string>(item, "Code"),
                EF.Property<string>(item, "Name")))
            .ToListAsync(ct);
    }
}
