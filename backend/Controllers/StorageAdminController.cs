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
    IStorageStageCapacityService stageCapacity,
    PaymentContext db,
    IStorageStageWriterMarkerRecoveryService markerRecovery) : ControllerBase
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
        var now = DateTime.UtcNow;
        // The worker claims operations with a conditional UPDATE. Retry must use
        // the same atomic pattern, otherwise a stale read can reset a live lease.
        var updated = await db.StorageOperations
            .Where(item => item.Id == id
                && (item.Status == StorageOperationStatuses.Pending
                    || item.Status == StorageOperationStatuses.Failed
                    || item.Status == StorageOperationStatuses.Unknown
                    || item.Status == StorageOperationStatuses.DeadLetter)
                && ((item.LeaseOwner == null && item.LeaseExpiresAt == null)
                    || (item.LeaseExpiresAt != null && item.LeaseExpiresAt <= now))
                && (item.OperationType != StorageOperationTypes.PublishCaseDocument
                    || !db.CaseDocumentUploadRetirements.Any(retired => retired.StorageObjectId == item.StorageObjectId)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, StorageOperationStatuses.Pending)
                .SetProperty(item => item.AttemptCount, 0)
                .SetProperty(item => item.NextAttemptAt, now)
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(item => item.LastError, (string?)null)
                .SetProperty(item => item.CompletedAt, (DateTime?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (updated == 1) return Accepted(new { id, status = StorageOperationStatuses.Pending });
        if (!await db.StorageOperations.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken)) return NotFound();
        return Conflict(new { message = "Завершённую или выполняющуюся операцию нельзя поставить в очередь повторно." });
    }

    [HttpGet("operations/case-documents")]
    public async Task<ActionResult<StorageCasePublicationPage>> GetCasePublicationOperations(
        [FromQuery] string? status = null, [FromQuery] string? after = null,
        [FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            return Ok(await new StorageCasePublicationQueueReader(db).ReadAsync(status, after, limit, cancellationToken));
        }
        catch (ArgumentException exception) when (exception.ParamName is "status" or "after" or "limit")
        {
            return BadRequest(new { message = "Проверьте состояние, размер страницы и курсор очереди." });
        }
    }

    [HttpPost("reconcile")]
    public async Task<ActionResult<StorageReconciliationReport>> Reconcile(
        [FromQuery] bool apply = false,
        CancellationToken cancellationToken = default)
        => Ok(await reconciliation.RunAsync(apply, cancellationToken));

    [HttpPost("capacity/stages/reconcile")]
    public async Task<ActionResult<StorageStageRecoveryReport>> ReconcileStages(
        [FromQuery] bool apply = false,
        [FromQuery] int limit = 100,
        [FromQuery] int minimumAgeMinutes = 10,
        [FromQuery] string? afterKey = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200 || minimumAgeMinutes is < 10 or > 10080)
            return BadRequest(new { message = "Укажите от 1 до 200 записей и возраст от 10 минут до 7 суток." });
        try
        {
            return Ok(await stageCapacity.ReconcilePendingAsync(apply, limit,
                TimeSpan.FromMinutes(minimumAgeMinutes), cancellationToken, afterKey));
        }
        catch (ArgumentException exception) when (exception.ParamName == "afterKey")
        {
            return BadRequest(new { message = "Неверный курсор страницы." });
        }
    }

    [HttpPost("capacity/stages/markers/reconcile")]
    public async Task<ActionResult<StorageStageWriterMarkerRecoveryReport>> ReconcileStageMarkers(
        [FromQuery] bool apply = false,
        [FromQuery] int limit = 50,
        [FromQuery] int minimumAgeMinutes = 10,
        [FromQuery] string? afterKey = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (limit is < 1 or > 200 || minimumAgeMinutes is < 10 or > 10080)
            return BadRequest(new { message = "Укажите от 1 до 200 записей и возраст от 10 минут до 7 суток." });
        try
        {
            return Ok(await markerRecovery.ReconcileReleasedWriterMarkersAsync(apply, limit,
                TimeSpan.FromMinutes(minimumAgeMinutes), cancellationToken, afterKey));
        }
        catch (ArgumentException exception) when (exception.ParamName == "afterKey")
        {
            return BadRequest(new { message = "Неверный курсор страницы." });
        }
    }

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
        var capacityDebt = await db.StorageCapacityReservations.AsNoTracking()
            .Where(item => item.Status == StorageCapacityReservationStatuses.Pending)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                count = group.Count(),
                oldest = group.Min(item => item.CreatedAt),
                stageCount = group.Count(item => item.IdempotencyKey.StartsWith("stage:")),
                oldestStage = group.Where(item => item.IdempotencyKey.StartsWith("stage:"))
                    .Min(item => (DateTime?)item.UpdatedAt)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var providerSnapshots = providerHealth.Snapshot(providers.Providers.Select(item => item.Key));
        // Healthy means no outstanding work or unverified/damaged copies, not
        // merely the absence of dead-letter operations and confirmed corruption.
        var degraded = operationCounts.Any(item => item.Value > 0 && item.Key != StorageOperationStatuses.Succeeded)
            || replicaCounts.Any(item => item.Value > 0
                && item.Key != StorageReplicaStatuses.Available && item.Key != StorageReplicaStatuses.Deleted)
            || capacityDebt is not null && capacityDebt.oldest <= DateTime.UtcNow.AddMinutes(-10)
            || providerSnapshots.Any(item => item.State != "healthy");
        return Ok(new
        {
            status = degraded ? "degraded" : "healthy",
            operationCounts,
            replicaCounts,
            oldestPendingAt = oldestPending,
            pendingCapacityReservations = capacityDebt?.count ?? 0,
            oldestCapacityReservationAt = capacityDebt?.oldest,
            pendingStageCapacityReservations = capacityDebt?.stageCount ?? 0,
            oldestStageCapacityReservationAt = capacityDebt?.oldestStage,
            providers = providerSnapshots,
        });
    }
}
