using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Controllers;

[ApiController]
[Route("api/storage")]
[Authorize(Policy = "Admin")]
public sealed class StorageAdminController(
    IStoragePolicyService policies,
    IStorageProviderRegistry providers,
    IStorageProviderHealthTracker providerHealth,
    IStorageObjectService storageObjects,
    IStorageReconciliationService reconciliation,
    ILegacyStorageCatalogBackfillService legacyBackfill,
    PaymentContext db) : ControllerBase
{
    [HttpGet("objects")]
    public async Task<ActionResult<object>> GetObjects(
        [FromQuery] string? dataClass,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.StorageObjects.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(dataClass))
        {
            var normalized = dataClass.Trim();
            query = query.Where(item => item.DataClass == normalized);
        }
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.UpdatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new
            {
                item.Id,
                item.DataClass,
                item.OwnerType,
                item.OwnerId,
                item.LogicalName,
                item.ContentType,
                item.CurrentGeneration,
                item.IsDeleted,
                item.CreatedAt,
                item.UpdatedAt,
                availableCopies = item.Generations
                    .Where(generation => generation.Generation == item.CurrentGeneration)
                    .SelectMany(generation => generation.Replicas)
                    .Count(replica => replica.Status == StorageReplicaStatuses.Available),
            }).ToListAsync(cancellationToken);
        return Ok(new { page, pageSize, total, items });
    }

    [HttpGet("objects/{id:guid}/content")]
    public async Task<IActionResult> DownloadObject(Guid id, CancellationToken cancellationToken)
    {
        var result = await storageObjects.OpenReadAsync(id, cancellationToken);
        if (result is null) return NotFound();
        return new FileStreamResult(result.Content, result.ContentType)
        {
            FileDownloadName = result.LogicalName,
            EnableRangeProcessing = true,
        };
    }

    [HttpDelete("objects/{id:guid}")]
    public async Task<IActionResult> DeleteObject(Guid id, [FromQuery] string? reason, CancellationToken cancellationToken)
        => await storageObjects.DeleteAsync(id, User.GetUserId(), reason, cancellationToken)
            ? Accepted()
            : NotFound();

    [HttpGet("policy/explain")]
    public ActionResult<StoragePolicyEvaluation> ExplainPolicy([FromQuery] string? dataClass)
        => Ok(policies.Evaluate(dataClass ?? "default"));

    [HttpGet("providers")]
    public ActionResult<object> GetProviders()
        => Ok(providers.Providers
            .OrderBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
            .Select(provider => new { provider.Key, provider.Capabilities }));

    [HttpGet("operations")]
    public async Task<ActionResult<object>> GetOperations(
        [FromQuery] string? status,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var query = db.StorageOperations.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim();
            query = query.Where(item => item.Status == normalized);
        }

        var items = await query
            .OrderByDescending(item => item.UpdatedAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(item => new
            {
                item.Id,
                item.StorageObjectId,
                item.StorageObjectGenerationId,
                item.OperationType,
                item.Status,
                item.ProviderKey,
                item.ConnectionId,
                item.AttemptCount,
                item.NextAttemptAt,
                item.LeaseOwner,
                item.LeaseExpiresAt,
                item.LastError,
                item.CreatedAt,
                item.UpdatedAt,
                item.CompletedAt,
            })
            .ToListAsync(cancellationToken);
        return Ok(new { count = items.Count, items });
    }

    [HttpPost("operations/{id:guid}/retry")]
    public async Task<ActionResult> RetryOperation(Guid id, CancellationToken cancellationToken)
    {
        var operation = await db.StorageOperations.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (operation is null) return NotFound();
        if (operation.Status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.Running)
        {
            return Conflict(new { message = "Завершённую или выполняющуюся операцию нельзя поставить в очередь повторно." });
        }

        operation.Status = StorageOperationStatuses.Pending;
        operation.AttemptCount = 0;
        operation.NextAttemptAt = DateTime.UtcNow;
        operation.LeaseOwner = null;
        operation.LeaseExpiresAt = null;
        operation.LastError = null;
        operation.CompletedAt = null;
        operation.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Accepted(new { operation.Id, operation.Status });
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<StorageReconciliationReport>> Reconcile(
        [FromQuery] bool apply = false,
        CancellationToken cancellationToken = default)
        => Ok(await reconciliation.RunAsync(apply, cancellationToken));

    [HttpPost("legacy-backfill")]
    public async Task<ActionResult<LegacyStorageBackfillReport>> BackfillLegacyStorage(
        [FromBody] LegacyStorageBackfillRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await legacyBackfill.RunAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("health")]
    public async Task<ActionResult<object>> GetHealth(CancellationToken cancellationToken)
    {
        var operationCounts = await db.StorageOperations.AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { status = group.Key, count = group.Count() })
            .ToDictionaryAsync(item => item.status, item => item.count, cancellationToken);
        var replicaCounts = await db.StorageReplicas.AsNoTracking()
            .GroupBy(item => item.Status)
            .Select(group => new { status = group.Key, count = group.Count() })
            .ToDictionaryAsync(item => item.status, item => item.count, cancellationToken);
        var oldestPending = await db.StorageOperations.AsNoTracking()
            .Where(item => item.Status == StorageOperationStatuses.Pending
                           || item.Status == StorageOperationStatuses.Failed
                           || item.Status == StorageOperationStatuses.Unknown)
            .MinAsync(item => (DateTime?)item.CreatedAt, cancellationToken);
        return Ok(new
        {
            status = operationCounts.GetValueOrDefault(StorageOperationStatuses.DeadLetter) > 0
                || replicaCounts.GetValueOrDefault(StorageReplicaStatuses.Corrupt) > 0 ? "degraded" : "healthy",
            operationCounts,
            replicaCounts,
            oldestPendingAt = oldestPending,
            providers = providerHealth.Snapshot(providers.Providers.Select(item => item.Key)),
        });
    }
}
