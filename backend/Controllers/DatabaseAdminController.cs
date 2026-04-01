using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/database")]
[Authorize(Policy = "Admin")]
public class DatabaseAdminController : ControllerBase
{
    private const long MaxImportFileSizeBytes = 1024L * 1024L * 1024L;

    private readonly DatabaseProviderInfo _databaseProvider;
    private readonly IDatabaseImportService _databaseImportService;

    public DatabaseAdminController(
        DatabaseProviderInfo databaseProvider,
        IDatabaseImportService databaseImportService)
    {
        _databaseProvider = databaseProvider;
        _databaseImportService = databaseImportService;
    }

    [HttpGet("status")]
    public ActionResult<DatabaseStatusDto> GetStatus()
    {
        return Ok(new DatabaseStatusDto
        {
            Provider = _databaseProvider.Key,
            DisplayName = _databaseProvider.DisplayName,
            SupportsSqliteMaintenance = _databaseProvider.IsSqlite,
            SupportsSqliteFileImport = true,
        });
    }

    [HttpPost("import/sqlite")]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportFileSizeBytes)]
    [RequestSizeLimit(MaxImportFileSizeBytes)]
    public async Task<ActionResult<DatabaseImportResultDto>> ImportSqliteAsync(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length <= 0)
        {
            return BadRequest(new { message = "Нужно выбрать непустой SQLite-файл для импорта." });
        }

        var tempPath = Path.Combine(Path.GetTempPath(), $"payplanner-import-{Guid.NewGuid():N}.sqlite");

        try
        {
            await using (var fileStream = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(fileStream, cancellationToken);
            }

            var result = await _databaseImportService.ImportSqliteAsync(tempPath, cancellationToken);
            return Ok(result);
        }
        finally
        {
            try
            {
                if (System.IO.File.Exists(tempPath))
                {
                    System.IO.File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }
}
