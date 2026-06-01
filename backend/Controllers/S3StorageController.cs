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
