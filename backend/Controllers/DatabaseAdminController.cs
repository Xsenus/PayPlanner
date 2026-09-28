using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using System.Text.Json;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/database")]
[Authorize(Policy = "Admin")]
public class DatabaseAdminController : ControllerBase, IAsyncActionFilter
{
    private const long MaxImportFileSizeBytes = 1024L * 1024L * 1024L;
    private const int MaxImportFormValueCount = 50000;
    private static readonly JsonSerializerOptions SpreadsheetImportRequestJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _db;
    private readonly DatabaseProviderInfo _databaseProvider;
    private readonly DatabaseBackupService _databaseBackupService;
    private readonly DatabaseRestoreDrillService? _databaseRestoreDrillService;
    private readonly IDatabaseImportService _databaseImportService;
    private readonly ISpreadsheetImportService _spreadsheetImportService;

    public DatabaseAdminController(
        PaymentContext db,
        DatabaseProviderInfo databaseProvider,
        DatabaseBackupService databaseBackupService,
        IDatabaseImportService databaseImportService,
        ISpreadsheetImportService spreadsheetImportService,
        DatabaseRestoreDrillService? databaseRestoreDrillService = null)
    {
        _db = db;
        _databaseProvider = databaseProvider;
        _databaseBackupService = databaseBackupService;
        _databaseRestoreDrillService = databaseRestoreDrillService;
        _databaseImportService = databaseImportService;
        _spreadsheetImportService = spreadsheetImportService;
    }

    [NonAction]
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _db.IsUserAdminOrLegacyFallbackAsync(context.HttpContext.User, context.HttpContext.RequestAborted))
        {
            context.Result = Forbid();
            return;
        }

        await next();
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
                request.BackupFormat,
                request.S3UploadEnabled,
                request.S3ConnectionIds,
                request.S3PrefixTemplate,
                request.YandexDiskUploadEnabled,
                request.YandexDiskPrefixTemplate,
                cancellationToken);

            return Ok(MapOverview(overview));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("backups/catalog")]
    public async Task<ActionResult<DatabaseBackupCatalogPageDto>> GetBackupCatalogAsync(
        CancellationToken cancellationToken, [FromQuery] string? after = null, [FromQuery] int pageSize = 20)
    {
        try
        {
            var page = await _databaseBackupService.GetCatalogAsync(after, pageSize, cancellationToken);
            return Ok(new DatabaseBackupCatalogPageDto
            {
                NextCursor = page.NextCursor,
                Items = page.Items.Select(item => new DatabaseBackupCatalogItemDto
                {
                    Id = item.Id,
                    BackupRelativePath = item.BackupRelativePath,
                    DatabaseProvider = item.DatabaseProvider,
                    BackupFormat = item.BackupFormat,
                    Status = item.Status,
                    CreatedAt = DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Utc),
                    Copies = item.Copies.OrderBy(copy => copy.ProviderKey).ThenBy(copy => copy.ConnectionId)
                        .Select(copy => new DatabaseBackupCatalogCopyDto
                        {
                            ProviderKey = copy.ProviderKey,
                            ConnectionId = copy.ConnectionId,
                            Locator = copy.Locator,
                            Status = copy.Status,
                            AttemptCount = copy.AttemptCount,
                            LastError = copy.LastError,
                            LastVerifiedAt = copy.LastVerifiedAt.HasValue
                                ? DateTime.SpecifyKind(copy.LastVerifiedAt.Value, DateTimeKind.Utc) : null,
                            UpdatedAt = DateTime.SpecifyKind(copy.UpdatedAt, DateTimeKind.Utc),
                        }).ToList(),
                }).ToList(),
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("backups/catalog/import")]
    public async Task<ActionResult<DatabaseBackupCatalogImportResult>> ImportBackupCatalogAsync(
        [FromBody] ImportDatabaseBackupCatalogRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _databaseBackupService.ImportCatalogAsync(request.RelativePaths, request.DryRun, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("backups/discovery")]
    public async Task<ActionResult<DatabaseBackupDiscoveryPage>> DiscoverBackupsAsync(
        [FromBody] DiscoverDatabaseBackupsRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _databaseBackupService.DiscoverBackupsAsync(request.Cursor, request.PageSize, cancellationToken)); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
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
                S3UploadResults = result.S3UploadResults.Select(MapS3UploadResult).ToList(),
                YandexDiskUploadResults = result.YandexDiskUploadResults.Select(MapYandexDiskUploadResult).ToList(),
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("backups/s3-uploads/{uploadResultId}/retry")]
    public async Task<ActionResult<DatabaseBackupOverviewDto>> RetryBackupS3UploadAsync(
        string uploadResultId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _databaseBackupService.RetryS3UploadAsync(uploadResultId, cancellationToken);
            var overview = await _databaseBackupService.GetOverviewAsync(cancellationToken);
            return Ok(MapOverview(overview));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("backups/yandex-disk-uploads/{uploadResultId}/retry")]
    public async Task<ActionResult<DatabaseBackupOverviewDto>> RetryBackupYandexDiskUploadAsync(
        string uploadResultId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _databaseBackupService.RetryYandexDiskUploadAsync(uploadResultId, cancellationToken);
            var overview = await _databaseBackupService.GetOverviewAsync(cancellationToken);
            return Ok(MapOverview(overview));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("backups/file")]
    public async Task<ActionResult<DatabaseBackupDeleteResultDto>> DeleteBackupAsync(
        [FromQuery] string? relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _databaseBackupService.DeleteBackupAsync(relativePath, cancellationToken);
            var overview = await _databaseBackupService.GetOverviewAsync(cancellationToken);
            return Ok(new DatabaseBackupDeleteResultDto
            {
                DeletedCount = result.DeletedCount,
                DeletedAll = result.DeletedAll,
                Overview = MapOverview(overview),
            });
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpDelete("backups/all")]
    public async Task<ActionResult<DatabaseBackupDeleteResultDto>> DeleteAllBackupsAsync(CancellationToken cancellationToken)
    {
        var result = await _databaseBackupService.DeleteAllBackupsAsync(cancellationToken);
        var overview = await _databaseBackupService.GetOverviewAsync(cancellationToken);
        return Ok(new DatabaseBackupDeleteResultDto
        {
            DeletedCount = result.DeletedCount,
            DeletedAll = result.DeletedAll,
            Overview = MapOverview(overview),
        });
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

    [HttpGet("backups/verify")]
    public async Task<ActionResult<DatabaseBackupVerificationResult>> VerifyBackupAsync(
        [FromQuery] string? relativePath,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _databaseBackupService.VerifyBackupAsync(relativePath, cancellationToken));
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("backups/restore-drill")]
    public async Task<ActionResult<DatabaseRestoreDrillReport>> RunRestoreDrillAsync(
        [FromBody] DatabaseRestoreDrillRequest request,
        CancellationToken cancellationToken)
    {
        if (_databaseRestoreDrillService is null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Сервис тестового восстановления не настроен." });
        var result = await _databaseRestoreDrillService.RunAsync(
            request.RelativePath,
            request.ExecutePostgreSql,
            request.ConfirmationToken,
            cancellationToken);
        return result.Status == "failed" ? UnprocessableEntity(result) : Ok(result);
    }

    [HttpPost("import/sqlite")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportFileSizeBytes)]
    [RequestSizeLimit(MaxImportFileSizeBytes)]
    public async Task<ActionResult<DatabaseImportResultDto>> ImportSqliteAsync(
        IFormFile? file,
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

    [HttpPost("import/spreadsheets/preview")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportFileSizeBytes)]
    [RequestSizeLimit(MaxImportFileSizeBytes)]
    public async Task<ActionResult<SpreadsheetImportPreviewDto>> PreviewSpreadsheetImportAsync(
        [FromForm] List<IFormFile>? files,
        CancellationToken cancellationToken)
    {
        var uploadFiles = await ReadSpreadsheetFilesAsync(files, cancellationToken);
        if (uploadFiles.Count == 0)
        {
            return BadRequest(new { message = "Нужно выбрать хотя бы один Excel, XML или ZIP-файл для анализа." });
        }

        try
        {
            var result = await _spreadsheetImportService.PreviewAsync(uploadFiles, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("import/spreadsheets/apply")]
    [Consumes("multipart/form-data")]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportFileSizeBytes, ValueCountLimit = MaxImportFormValueCount)]
    [RequestSizeLimit(MaxImportFileSizeBytes)]
    public async Task<ActionResult<SpreadsheetImportBatchDetailDto>> ApplySpreadsheetImportAsync(
        [FromForm] List<IFormFile>? files,
        [FromForm] SpreadsheetImportApplyRequest? request,
        [FromForm] string? requestJson,
        CancellationToken cancellationToken)
    {
        var uploadFiles = await ReadSpreadsheetFilesAsync(files, cancellationToken);
        if (uploadFiles.Count == 0)
        {
            return BadRequest(new { message = "Нужно выбрать хотя бы один Excel, XML или ZIP-файл для импорта." });
        }

        try
        {
            var normalizedRequest = ParseSpreadsheetImportApplyRequest(requestJson) ?? request ?? new SpreadsheetImportApplyRequest();
            normalizedRequest.SelectedPreviewIds ??= new List<string>();
            normalizedRequest.CounterpartyOptions ??= new List<SpreadsheetImportCounterpartyOptionRequest>();

            var result = await _spreadsheetImportService.ApplyAsync(uploadFiles, normalizedRequest, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("import/spreadsheets/batches")]
    public async Task<ActionResult<IReadOnlyCollection<SpreadsheetImportBatchSummaryDto>>> GetSpreadsheetImportBatchesAsync(
        CancellationToken cancellationToken)
    {
        var items = await _spreadsheetImportService.GetBatchesAsync(cancellationToken);
        return Ok(items);
    }

    private static SpreadsheetImportApplyRequest? ParseSpreadsheetImportApplyRequest(string? requestJson)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SpreadsheetImportApplyRequest>(requestJson, SpreadsheetImportRequestJsonOptions)
                ?? new SpreadsheetImportApplyRequest();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Не удалось разобрать параметры импорта. Обновите страницу и попробуйте снова.", ex);
        }
    }

    [HttpGet("import/spreadsheets/batches/{batchId:int}")]
    public async Task<ActionResult<SpreadsheetImportBatchDetailDto>> GetSpreadsheetImportBatchAsync(
        int batchId,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25)
    {
        var batch = await _spreadsheetImportService.GetBatchAsync(batchId, page, pageSize, cancellationToken);
        return batch is null
            ? NotFound(new { message = "Сессия импорта не найдена." })
            : Ok(batch);
    }

    [HttpPost("import/spreadsheets/batches/{batchId:int}/rollback")]
    public async Task<ActionResult<SpreadsheetImportBatchDetailDto>> RollbackSpreadsheetImportBatchAsync(
        int batchId,
        CancellationToken cancellationToken)
    {
        try
        {
            var batch = await _spreadsheetImportService.RollbackAsync(batchId, cancellationToken);
            return Ok(batch);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
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
            SelectedFormat = overview.SelectedFormat,
            AvailableFormats = overview.AvailableFormats.Select(format => new DatabaseBackupFormatOptionDto
            {
                Key = format.Key,
                Label = format.Label,
                Description = format.Description,
                FileExtension = format.FileExtension,
                IsDefault = format.IsDefault,
            }).ToList(),
            AutomaticEnabled = overview.AutomaticEnabled,
            ScheduleLocal = overview.ScheduleLocal,
            RetentionDays = overview.RetentionDays,
            RootDirectory = overview.RootDirectory,
            TimeZone = overview.TimeZone,
            ToolCommand = overview.ToolCommand,
            S3Upload = new DatabaseBackupS3UploadSettingsDto
            {
                Enabled = overview.S3Upload.Enabled,
                ConnectionIds = overview.S3Upload.ConnectionIds.ToList(),
                PrefixTemplate = overview.S3Upload.PrefixTemplate,
            },
            S3UploadResults = overview.S3UploadResults.Select(MapS3UploadResult).ToList(),
            YandexDiskUpload = new DatabaseBackupYandexDiskUploadSettingsDto
            {
                Enabled = overview.YandexDiskUpload.Enabled,
                PrefixTemplate = overview.YandexDiskUpload.PrefixTemplate,
            },
            YandexDiskUploadResults = overview.YandexDiskUploadResults.Select(MapYandexDiskUploadResult).ToList(),
            Items = overview.Items.Select(MapFile).ToList(),
        };
    }

    private static DatabaseBackupS3UploadResultDto MapS3UploadResult(DatabaseBackupS3UploadResult result)
    {
        return new DatabaseBackupS3UploadResultDto
        {
            Id = result.Id,
            BackupRelativePath = result.BackupRelativePath,
            BackupFileName = result.BackupFileName,
            ConnectionId = result.ConnectionId,
            Status = result.Status,
            TargetPath = result.TargetPath,
            TargetKey = result.TargetKey,
            SizeBytes = result.SizeBytes,
            Error = result.Error,
            CreatedAt = result.CreatedAt,
            UpdatedAt = result.UpdatedAt,
        };
    }

    private static DatabaseBackupYandexDiskUploadResultDto MapYandexDiskUploadResult(DatabaseBackupYandexDiskUploadResult result)
    {
        return new DatabaseBackupYandexDiskUploadResultDto
        {
            Id = result.Id,
            BackupRelativePath = result.BackupRelativePath,
            BackupFileName = result.BackupFileName,
            Status = result.Status,
            TargetPath = result.TargetPath,
            SizeBytes = result.SizeBytes,
            Error = result.Error,
            CreatedAt = result.CreatedAt,
            UpdatedAt = result.UpdatedAt,
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

    private static async Task<IReadOnlyCollection<SpreadsheetImportUploadFile>> ReadSpreadsheetFilesAsync(
        IReadOnlyCollection<IFormFile>? files,
        CancellationToken cancellationToken)
    {
        var result = new List<SpreadsheetImportUploadFile>();
        if (files is null)
        {
            return result;
        }

        foreach (var file in files.Where(item => item is not null && item.Length > 0))
        {
            await using var stream = file.OpenReadStream();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            result.Add(new SpreadsheetImportUploadFile(file.FileName, memory.ToArray()));
        }

        return result;
    }
}
