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

    public YandexDiskController(IYandexDiskService service, PaymentContext db, IWebHostEnvironment? environment = null, ICaseDocumentStorage? caseDocumentStorage = null)
    {
        _service = service;
        _db = db;
        _caseDocumentStorage = caseDocumentStorage ?? new LocalCaseDocumentStorage(environment);
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
            ct);
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
        CancellationToken ct)
    {
        try
        {
            var result = await action();
            await AddAuditAsync(auditAction, description, metadata, ct);
            return result;
        }
        catch (YandexDiskOperationException ex)
        {
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
