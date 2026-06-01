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
[Route("api/s3-storages")]
public sealed class S3StorageController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IS3StorageService _service;
    private readonly PaymentContext _db;

    public S3StorageController(IS3StorageService service, PaymentContext db)
    {
        _service = service;
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<S3StorageConnectionsOverviewDto>> GetOverview(CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        return Ok(await _service.GetOverviewAsync(ct));
    }

    [HttpPost]
    public async Task<ActionResult<S3StorageConnectionDto>> UpsertConnection(
        [FromBody] UpsertS3StorageConnectionRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        var result = await ExecuteAsync(() => _service.UpsertConnectionAsync(request, ct));
        if (result.Result is null && result.Value is { } value)
        {
            await AddAuditAsync(
                string.IsNullOrWhiteSpace(request.Id) ? "ConnectionCreated" : "ConnectionUpdated",
                "Обновлены настройки S3-хранилища.",
                new { value.Id, value.Name, value.Provider, value.Endpoint, value.Region, value.Bucket, value.RootPrefix, value.IsEnabled },
                ct);
        }

        return result;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteConnection([FromRoute] string id, CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanDelete, ct);
        if (forbidden is not null) return forbidden;

        try
        {
            await _service.DeleteConnectionAsync(id, ct);
            await AddAuditAsync(
                "ConnectionDeleted",
                "Удалено подключение S3-хранилища.",
                new { Id = id },
                ct);
            return NoContent();
        }
        catch (S3StorageOperationException ex)
        {
            return MapError(ex);
        }
    }

    [HttpPost("{id}/connection-test")]
    public async Task<ActionResult<S3StorageConnectionTestDto>> TestConnection(
        [FromRoute] string id,
        [FromBody] TestS3StorageConnectionRequest? request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission =>
            permission.CanView && permission.CanEdit && permission.CanCreate && permission.CanDelete && permission.CanExport, ct);
        if (forbidden is not null) return forbidden;

        var result = await ExecuteAsync(() => _service.TestConnectionAsync(id, request ?? new TestS3StorageConnectionRequest(), ct));
        if (result.Result is null && result.Value is { } value)
        {
            await AddAuditAsync(
                "ConnectionTested",
                "Проверено подключение к S3-хранилищу.",
                new { value.ConnectionId, value.Endpoint, value.Region, value.Bucket, value.TestKey, value.Success },
                ct);
        }

        return result;
    }

    [HttpGet("{id}/objects")]
    public async Task<ActionResult<S3StorageListResponseDto>> ListObjects(
        [FromRoute] string id,
        [FromQuery] string? path,
        [FromQuery] int limit = 100,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanView, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAsync(() => _service.ListObjectsAsync(id, path, limit, offset, ct));
    }

    [HttpPost("{id}/folders")]
    public async Task<ActionResult<S3StorageOperationResultDto>> CreateFolder(
        [FromRoute] string id,
        [FromBody] CreateS3StorageFolderRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.CreateFolderAsync(id, request, ct),
            "FolderCreated",
            "Создана папка в S3-хранилище.",
            new { ConnectionId = id, request.Path },
            ct);
    }

    [HttpPost("{id}/upload")]
    [RequestSizeLimit(50L * 1024 * 1024 * 1024)]
    public async Task<ActionResult<S3StorageOperationResultDto>> Upload(
        [FromRoute] string id,
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

        var targetPath = string.IsNullOrWhiteSpace(path)
            ? file.FileName
            : path.Trim().EndsWith("/", StringComparison.Ordinal)
                ? $"{path.Trim()}{file.FileName}"
                : path;
        await using var stream = file.OpenReadStream();

        return await ExecuteAndAuditAsync(
            () => _service.UploadAsync(id, targetPath, stream, file.ContentType, overwrite, ct),
            "Uploaded",
            "Файл загружен в S3-хранилище.",
            new { ConnectionId = id, Path = targetPath, file.FileName, file.Length, file.ContentType, Overwrite = overwrite },
            ct);
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> Download(
        [FromRoute] string id,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanExport, ct);
        if (forbidden is not null) return forbidden;

        try
        {
            var file = await _service.DownloadAsync(id, path, ct);
            await AddAuditAsync(
                "Downloaded",
                "Файл скачан из S3-хранилища.",
                new { ConnectionId = id, file.Path, file.Key, Size = file.Content.Length },
                ct);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (S3StorageOperationException ex)
        {
            return MapError(ex);
        }
    }

    [HttpDelete("{id}/objects")]
    public async Task<ActionResult<S3StorageOperationResultDto>> DeleteObject(
        [FromRoute] string id,
        [FromQuery] string? path,
        [FromQuery] bool recursive = false,
        CancellationToken ct = default)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanDelete, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.DeleteObjectAsync(id, path, recursive, ct),
            "Deleted",
            "Объект удален из S3-хранилища.",
            new { ConnectionId = id, Path = path, Recursive = recursive },
            ct);
    }

    [HttpPost("{id}/copy")]
    public async Task<ActionResult<S3StorageOperationResultDto>> CopyObject(
        [FromRoute] string id,
        [FromBody] S3StorageObjectOperationRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanCreate, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.CopyObjectAsync(id, request, ct),
            "Copied",
            "Объект скопирован в S3-хранилище.",
            new { ConnectionId = id, request.FromPath, request.ToPath, request.Overwrite },
            ct);
    }

    [HttpPost("{id}/move")]
    public async Task<ActionResult<S3StorageOperationResultDto>> MoveObject(
        [FromRoute] string id,
        [FromBody] S3StorageObjectOperationRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.MoveObjectAsync(id, request, ct),
            "Moved",
            "Объект перемещен в S3-хранилище.",
            new { ConnectionId = id, request.FromPath, request.ToPath, request.Overwrite },
            ct);
    }

    [HttpGet("{id}/text")]
    public async Task<ActionResult<S3StorageTextFileDto>> GetTextFile(
        [FromRoute] string id,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAsync(() => _service.GetTextFileAsync(id, path, ct));
    }

    [HttpPut("{id}/text")]
    public async Task<ActionResult<S3StorageOperationResultDto>> SaveTextFile(
        [FromRoute] string id,
        [FromBody] UpdateS3StorageTextFileRequest request,
        CancellationToken ct)
    {
        var forbidden = await RequirePermissionAsync(permission => permission.CanEdit, ct);
        if (forbidden is not null) return forbidden;

        return await ExecuteAndAuditAsync(
            () => _service.SaveTextFileAsync(id, request, ct),
            "TextUpdated",
            "Текстовый файл обновлен в S3-хранилище.",
            new { ConnectionId = id, request.Path, ContentLength = request.Content?.Length ?? 0, request.Overwrite },
            ct);
    }

    private async Task<ActionResult?> RequirePermissionAsync(Func<RolePermission, bool> isAllowed, CancellationToken ct)
    {
        var userId = User.GetUserId();
        if (User.IsInRole("admin"))
        {
            return null;
        }

        if (userId is null)
        {
            return Forbid();
        }

        var permission = await _db.GetUserSectionPermissionAsync(userId.Value, RolePermissionCatalog.S3Storage, ct);
        if (permission is null || !isAllowed(permission))
        {
            return Forbid();
        }

        return null;
    }

    private static async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (S3StorageOperationException ex)
        {
            return MapError(ex);
        }
    }

    private async Task<ActionResult<T>> ExecuteAndAuditAsync<T>(
        Func<Task<T>> action,
        string auditAction,
        string description,
        object metadata,
        CancellationToken ct)
    {
        var result = await ExecuteAsync(action);
        if (result.Result is null)
        {
            await AddAuditAsync(auditAction, description, metadata, ct);
        }

        return result;
    }

    private async Task AddAuditAsync(string action, string description, object metadata, CancellationToken ct)
    {
        var userId = User.GetUserId();
        _db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "s3-storage",
            ObjectId = 0,
            Action = action,
            Description = description,
            ActorUserId = userId,
            ActorUserName = User.GetDisplayName(),
            CreatedAt = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
        });
        await _db.SaveChangesAsync(ct);
    }

    private static ActionResult MapError(S3StorageOperationException ex)
        => ex.StatusCode switch
        {
            400 => new BadRequestObjectResult(ex.Message),
            401 => new UnauthorizedObjectResult(ex.Message),
            403 => new ObjectResult(ex.Message) { StatusCode = StatusCodes.Status403Forbidden },
            404 => new NotFoundObjectResult(ex.Message),
            409 => new ConflictObjectResult(ex.Message),
            _ => new ObjectResult(ex.Message) { StatusCode = ex.StatusCode <= 0 ? 502 : ex.StatusCode },
        };
}
