using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/client-reference-data")]
[Authorize]
public class ClientReferenceDataController : ControllerBase
{
    private readonly PaymentContext _db;

    public ClientReferenceDataController(PaymentContext db)
    {
        _db = db;
    }

    [HttpGet("okin")]
    public async Task<IActionResult> GetOkin(CancellationToken ct)
        => Ok(await _db.OkinDictionaryItems
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Code, x.Name })
            .ToListAsync(ct));

    [HttpGet("okso")]
    public async Task<IActionResult> GetOkso(CancellationToken ct)
        => Ok(await _db.OksoDictionaryItems
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Code, x.Name })
            .ToListAsync(ct));

    [HttpGet("okpdtr")]
    public async Task<IActionResult> GetOkpdtr(CancellationToken ct)
        => Ok(await _db.OkpdtrDictionaryItems
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Code, x.Name })
            .ToListAsync(ct));
}
