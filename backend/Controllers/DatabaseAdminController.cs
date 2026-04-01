using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PayPlanner.Api.Models.Requests;
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
    private readonly DatabaseBackupService _databaseBackupService;
    private readonly IDatabaseImportService _databaseImportService;

    public DatabaseAdminController(
        DatabaseProviderInfo databaseProvider,
        DatabaseBackupService databaseBackupService,
        IDatabaseImportService databaseImportService)
    {
        _databaseProvider = databaseProvider;
        _databaseBackupService = databaseBackupService;
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

    [HttpGet("backups")]
    public async Task<ActionResult<DatabaseBackupOverviewDto>> GetBackupsAsync(CancellationToken cancellationToken)
    {
        var overview = await _databaseBackupService.GetOverviewAsync(cancellationToken);
        return Ok(MapOverview(overview));
    }

    [HttpPut("backups/settings")]
    public async Task<ActionResult<DatabaseBackupOverviewDto>> UpdateBackupSettingsAsync(
        [FromBody] UpdateDatabaseBackupSettingsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var overview = await _databaseBackupService.UpdateSettingsAsync(
                request.AutomaticEnabled,
                request.ScheduleLocal,
                request.RetentionDays,
                cancellationToken);

            return Ok(MapOverview(overview));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("backups")]
    public async Task<ActionResult<DatabaseBackupCreateResultDto>> CreateBackupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _databaseBackupService.CreateManualBackupAsync(cancellationToken);
            return Ok(new DatabaseBackupCreateResultDto
            {
                Backup = MapFile(result.Backup),
                DownloadPath = result.DownloadPath,
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("backups/download")]
    public IActionResult DownloadBackupAsync([FromQuery] string? relativePath)
    {
        var resolvedPath = _databaseBackupService.ResolveBackupPath(relativePath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return NotFound(new { message = "Резервная копия не найдена." });
        }

        return PhysicalFile(
            resolvedPath,
            "application/octet-stream",
            Path.GetFileName(resolvedPath),
            enableRangeProcessing: true);
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

    private static DatabaseBackupOverviewDto MapOverview(DatabaseBackupOverview overview)
    {
        return new DatabaseBackupOverviewDto
        {
            Provider = overview.Provider,
            ProviderDisplayName = overview.ProviderDisplayName,
            BackupFormat = overview.BackupFormat,
            FileExtension = overview.FileExtension,
            AutomaticEnabled = overview.AutomaticEnabled,
            ScheduleLocal = overview.ScheduleLocal,
            RetentionDays = overview.RetentionDays,
            RootDirectory = overview.RootDirectory,
            TimeZone = overview.TimeZone,
            ToolCommand = overview.ToolCommand,
            Items = overview.Items.Select(MapFile).ToList(),
        };
    }

    private static DatabaseBackupFileDto MapFile(DatabaseBackupFileInfo file)
    {
        return new DatabaseBackupFileDto
        {
            FileName = file.FileName,
            RelativePath = file.RelativePath,
            SizeBytes = file.SizeBytes,
            CreatedAt = file.CreatedAt,
            Trigger = file.Trigger,
        };
    }
}
