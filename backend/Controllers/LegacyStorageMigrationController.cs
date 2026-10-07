using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/storage/legacy-replicas")]
[Authorize(Policy = "Admin")]
public sealed class LegacyStorageMigrationController(ILegacyStorageReplicaMigrationService migration) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<LegacyStorageReplicaMigrationReport>> Run(
        [FromBody] LegacyStorageReplicaMigrationRequest request, CancellationToken ct)
    {
        try { return Ok(await migration.RunAsync(request, ct)); }
        catch (ArgumentException exception) { return BadRequest(new { message = exception.Message }); }
    }
}
