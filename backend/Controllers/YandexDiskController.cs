using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/yandex-disk")]
public sealed class YandexDiskController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IYandexDiskService _service;
    private readonly PaymentContext _db;
    private readonly ICaseDocumentStorage _caseDocumentStorage;
    private readonly string _taskFilesRoot;

    public YandexDiskController(IYandexDiskService service, PaymentContext db, IWebHostEnvironment? environment = null, ICaseDocumentStorage? caseDocumentStorage = null)
    {
        _service = service;
        _db = db;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment);
        _taskFilesRoot = Path.Combine(environment?.ContentRootPath ?? AppContext.BaseDirectory, "App_Data", "task-files");
    }

    [HttpGet("settings")]
    public async Task<ActionResult<YandexDiskSettingsDto>> GetSettings(CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        return Ok(await _service.GetSettingsAsync(ct));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<YandexDiskSettingsDto>> UpdateSettings(
        [FromBody] UpdateYandexDiskSettingsRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        var result = await _service.UpdateSettingsAsync(request, ct);
        await AddAuditAsync(
            "SettingsUpdated",
            "Обновлены настройки подключения Яндекс.Диска.",
            new { result.RootPath, result.IsConfigured, request.ClearToken },
            ct);
        return result;
    }

    [HttpGet("crm-sync/settings")]
    public async Task<ActionResult<YandexDiskCrmSyncSettingsDto>> GetCrmSyncSettings(CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        return Ok(await _service.GetCrmSyncSettingsAsync(ct));
    }

    [HttpGet("crm-sync/preview")]
    public async Task<ActionResult<YandexDiskCrmSyncPreviewDto>> PreviewCrmSync(CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        return Ok(await _service.PreviewCrmSyncAsync(ct));
    }

    [HttpGet("crm-sync/status")]
    public async Task<ActionResult<YandexDiskCrmSyncStatusReportDto>> GetCrmSyncStatus(CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        var counts = await _db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count, ct);

        var lastSuccessAt = await _db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .Where(item => item.Status == YandexDiskCrmSyncStatus.Success)
            .MaxAsync(item => (DateTime?)item.LastSyncedAt, ct);

        var recent = await _db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .Take(20)
            .Select(item => new YandexDiskCrmSyncStatusItemDto
            {
                DocumentKind = item.DocumentKind,
                ObjectType = item.ObjectType,
                ObjectId = item.ObjectId,
                ParentObjectId = item.ParentObjectId,
                FileName = item.FileName,
                YandexDiskPath = item.YandexDiskPath,
                Status = item.Status,
                StatusText = item.Status == YandexDiskCrmSyncStatus.Success
                    ? "Успешно"
                    : item.Status == YandexDiskCrmSyncStatus.Failed
                        ? "Ошибка"
                        : "Ожидает",
                LastError = item.LastError,
                AttemptCount = item.AttemptCount,
                LastSyncedAt = item.LastSyncedAt,
                UpdatedAt = item.UpdatedAt,
            })
            .ToListAsync(ct);

        return Ok(new YandexDiskCrmSyncStatusReportDto
        {
            PendingCount = counts.GetValueOrDefault(YandexDiskCrmSyncStatus.Pending),
            FailedCount = counts.GetValueOrDefault(YandexDiskCrmSyncStatus.Failed),
            SuccessCount = counts.GetValueOrDefault(YandexDiskCrmSyncStatus.Success),
            LastSuccessfulSyncAt = lastSuccessAt,
            RecentItems = recent,
        });
    }

    [HttpPut("crm-sync/settings")]
    public async Task<ActionResult<YandexDiskCrmSyncSettingsDto>> UpdateCrmSyncSettings(
        [FromBody] UpdateYandexDiskCrmSyncSettingsRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        var result = await _service.UpdateCrmSyncSettingsAsync(request, ct);
        await AddAuditAsync(
            "CrmSyncSettingsUpdated",
            "Обновлены правила синхронизации CRM-документов с Яндекс.Диском.",
            new
            {
                result.Enabled,
                result.SyncInvoices,
                result.SyncActs,
                result.SyncContracts,
                result.SyncCaseDocuments,
                result.SyncTaskFiles,
                result.OverwriteExisting,
                result.ConflictResolutionMode,
                result.FolderTemplate,
                result.FileNameTemplate,
            },
            ct);
        return result;
    }

    [HttpGet("resources")]
    public async Task<ActionResult<YandexDiskListResponseDto>> List(
        [FromQuery] string? path,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAsync(() => _service.ListAsync(path, limit, offset, ct));
    }

    [HttpPost("folders")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> CreateFolder(
        [FromBody] CreateYandexDiskFolderRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.CreateFolderAsync(request.Path, ct),
            "FolderCreated",
            "Создана папка на Яндекс.Диске.",
            new { request.Path },
            null,
            ct);
    }

    [HttpPost("copy")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Copy(
        [FromBody] YandexDiskResourceOperationRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.CopyAsync(request.FromPath, request.ToPath, request.Overwrite, ct),
            "Copied",
            "Ресурс скопирован на Яндекс.Диске.",
            new { request.FromPath, request.ToPath, request.Overwrite },
            null,
            ct);
    }

    [HttpPost("move")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Move(
        [FromBody] YandexDiskResourceOperationRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.MoveAsync(request.FromPath, request.ToPath, request.Overwrite, ct),
            "Moved",
            "Ресурс перемещен или переименован на Яндекс.Диске.",
            new { request.FromPath, request.ToPath, request.Overwrite },
            null,
            ct);
    }

    [HttpDelete("resources")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Delete(
        [FromQuery] string? path,
        [FromQuery] bool permanently = false,
        CancellationToken ct = default)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanDelete, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.DeleteAsync(path, permanently, ct),
            "Deleted",
            permanently
                ? "Ресурс окончательно удален с Яндекс.Диска."
                : "Ресурс удален с Яндекс.Диска.",
            new { Path = path, Permanently = permanently },
            null,
            ct);
    }

    [HttpPost("upload")]
    [RequestSizeLimit(50L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<YandexDiskOperationResultDto>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? path,
        [FromForm] bool overwrite = true,
        CancellationToken ct = default)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (forbidden is not null) return forbidden;

        if (file is null || file.Length == 0)
        {
            return BadRequest("Выберите файл для загрузки.");
        }

        await using var stream = file.OpenReadStream();
        return await ExecuteAndAuditAsync(
            () => _service.UploadAsync(path, stream, file.ContentType, overwrite, ct),
            "Uploaded",
            "Файл загружен на Яндекс.Диск.",
            new { Path = path, file.FileName, file.Length, file.ContentType, Overwrite = overwrite },
            null,
            ct);
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string? path, CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanExport, ct);
        if (forbidden is not null) return forbidden;

        try
        {
            var file = await _service.DownloadAsync(path, ct);
            await AddAuditAsync(
                "Downloaded",
                "Файл скачан с Яндекс.Диска.",
                new { Path = path, file.FileName, Size = file.Content.Length },
                ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (YandexDiskOperationException ex)
        {
            return MapError(ex);
        }
    }

    [HttpGet("text")]
    public async Task<ActionResult<YandexDiskTextFileDto>> GetText([FromQuery] string? path, CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAsync(() => _service.GetTextFileAsync(path, ct));
    }

    [HttpPut("text")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> SaveText(
        [FromBody] UpdateYandexDiskTextFileRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.SaveTextFileAsync(request, ct),
            "TextUpdated",
            "Текстовый файл обновлен на Яндекс.Диске.",
            new { request.Path, ContentLength = request.Content?.Length ?? 0, request.Overwrite },
            null,
            ct);
    }

    [HttpPost("crm/case-documents/{documentId:int}/upload")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> UploadCaseDocument(
        int documentId,
        [FromQuery] bool? overwrite,
        CancellationToken ct)
    {
        var yandexForbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (yandexForbidden is not null) return yandexForbidden;

        var caseForbidden = await RequireSectionPermissionAsync(RolePermissionCatalog.Cases, permission => permission.CanExport, ct);
        if (caseForbidden is not null) return caseForbidden;

        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Cases, ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var document = await _db.ClientCaseDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId, ct);
        if (document is null)
        {
            return NotFound();
        }

        var clientCase = await _db.ClientCases
            .AsNoTracking()
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .ApplyClientCaseVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == document.ClientCaseId, ct);
        if (clientCase is null || !_caseDocumentStorage.Exists(document.StoragePath))
        {
            return NotFound();
        }

        await using var stream = System.IO.File.OpenRead(_caseDocumentStorage.ResolvePath(document.StoragePath));
        return await ExecuteAndAuditAsync(
            () => _service.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
                stream,
                document.ContentType,
                document.OriginalFileName,
                "caseDocument",
                document.DocumentType,
                document.DocumentKey,
                document.UploadedAt,
                Path.GetFileNameWithoutExtension(document.OriginalFileName),
                clientCase.Client?.Name,
                clientCase.LegalEntity?.ShortName ?? clientCase.LegalEntity?.FullName,
                clientCase.CaseNumber,
                clientCase.Title,
                overwrite),
                ct),
            "CrmCaseDocumentUploaded",
            "Документ дела отправлен на Яндекс.Диск.",
            new
            {
                DocumentId = document.Id,
                document.ClientCaseId,
                document.OriginalFileName,
                document.DocumentType,
                Overwrite = overwrite,
            },
            result => YandexDiskCrmSyncStateRecorder.RecordSuccessAsync(
                _db,
                "caseDocument",
                "caseDocument",
                document.Id,
                document.OriginalFileName,
                result.Path,
                document.ClientCaseId,
                ct),
            ct,
            async ex =>
            {
                await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
                    _db,
                    "caseDocument",
                    "caseDocument",
                    document.Id,
                    document.OriginalFileName,
                    document.ClientCaseId,
                    ex,
                    ct);
                await _db.SaveChangesAsync(ct);
            });
    }

    [HttpPost("crm/tasks/{taskId:int}/files/{fileId:int}/upload")]
    public async Task<ActionResult<YandexDiskOperationResultDto>> UploadTaskFile(
        int taskId,
        int fileId,
        [FromQuery] bool? overwrite,
        CancellationToken ct)
    {
        var yandexForbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (yandexForbidden is not null) return yandexForbidden;

        var taskForbidden = await RequireSectionPermissionAsync(RolePermissionCatalog.Tasks, permission => permission.CanExport, ct);
        if (taskForbidden is not null) return taskForbidden;

        var visibility = await _db.ResolveDataVisibilityAsync(User, RolePermissionCatalog.Tasks, ct);
        if (visibility is null)
        {
            return Unauthorized();
        }

        var task = await _db.TaskJournalItems
            .AsNoTracking()
            .Include(item => item.Client)
            .Include(item => item.LegalEntity)
            .Include(item => item.ClientCase)
            .Include(item => item.Participants)
            .ApplyTaskVisibility(visibility)
            .FirstOrDefaultAsync(item => item.Id == taskId, ct);
        if (task is null)
        {
            return NotFound();
        }

        var taskFile = await _db.TaskJournalFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == fileId && item.TaskJournalItemId == taskId, ct);
        if (taskFile is null)
        {
            return NotFound();
        }

        var fullPath = ResolveTaskFilePath(taskFile.StoragePath);
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound(new { message = "Файл не найден в хранилище." });
        }

        await using var stream = System.IO.File.OpenRead(fullPath);
        return await ExecuteAndAuditAsync(
            () => _service.UploadCrmDocumentAsync(new YandexDiskCrmDocumentUpload(
                stream,
                taskFile.ContentType,
                taskFile.OriginalFileName,
                "taskFile",
                "Файл задачи",
                taskFile.DocumentKey,
                taskFile.UploadedAt,
                Path.GetFileNameWithoutExtension(taskFile.OriginalFileName),
                task.Client?.Name,
                task.LegalEntity?.ShortName ?? task.LegalEntity?.FullName,
                task.ClientCase?.CaseNumber,
                task.ClientCase?.Title ?? task.Title,
                overwrite),
                ct),
            "CrmTaskFileUploaded",
            "Файл задачи отправлен на Яндекс.Диск.",
            new
            {
                TaskId = task.Id,
                FileId = taskFile.Id,
                taskFile.OriginalFileName,
                task.Title,
                Overwrite = overwrite,
            },
            result => YandexDiskCrmSyncStateRecorder.RecordSuccessAsync(
                _db,
                "taskFile",
                "taskFile",
                taskFile.Id,
                taskFile.OriginalFileName,
                result.Path,
                task.Id,
                ct),
            ct,
            async ex =>
            {
                await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
                    _db,
                    "taskFile",
                    "taskFile",
                    taskFile.Id,
                    taskFile.OriginalFileName,
                    task.Id,
                    ex,
                    ct);
                await _db.SaveChangesAsync(ct);
            });
    }

    private async Task<ActionResult?> RequirePermissionAsync(Func<RolePermission, bool> isAllowed, CancellationToken ct)
    {
        if (User.IsInRole("admin"))
        {
            return null;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.YandexDisk, ct);
        return permission is not null && permission.CanView && isAllowed(permission)
            ? null
            : Forbid();
    }

    private async Task<ActionResult?> RequireSectionPermissionAsync(
        string section,
        Func<RolePermission, bool> isAllowed,
        CancellationToken ct)
    {
        if (User.IsInRole("admin"))
        {
            return null;
        }

        var userId = User.GetUserId();
        if (!userId.HasValue)
        {
            return Forbid();
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, section, ct);
        return permission is not null && permission.CanView && isAllowed(permission)
            ? null
            : Forbid();
    }

    private async Task<ActionResult<T>> ExecuteAndAuditAsync<T>(
        Func<Task<T>> action,
        string auditAction,
        string description,
        object metadata,
        Func<T, Task>? afterSuccess,
        CancellationToken ct,
        Func<YandexDiskOperationException, Task>? afterFailure = null)
    {
        try
        {
            var result = await action();
            if (afterSuccess is not null)
            {
                await afterSuccess(result);
            }

            await AddAuditAsync(auditAction, description, metadata, ct);
            return result;
        }
        catch (YandexDiskOperationException ex)
        {
            if (afterFailure is not null)
            {
                await afterFailure(ex);
            }

            return MapError(ex);
        }
    }

    private static async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (YandexDiskOperationException ex)
        {
            return MapError(ex);
        }
    }

    private async Task AddAuditAsync(string action, string description, object metadata, CancellationToken ct)
    {
        _db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "yandex-disk",
            ObjectId = 0,
            Action = action,
            Description = description,
            ActorUserId = User.GetUserId(),
            ActorUserName = User.GetDisplayName(),
            MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    private string ResolveTaskFilePath(string storagePath)
    {
        var root = Path.GetFullPath(_taskFilesRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, storagePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Некорректный путь файла задачи.");
        }

        return fullPath;
    }

    private static ActionResult MapError(YandexDiskOperationException ex)
    {
        if (ex.StatusCode >= 400 && ex.StatusCode < 500)
        {
            return new BadRequestObjectResult(ex.Message);
        }

        return new ObjectResult(ex.Message)
        {
            StatusCode = StatusCodes.Status502BadGateway,
        };
    }
}
