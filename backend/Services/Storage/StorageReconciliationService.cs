using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Linq.Expressions;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageReconciliationIssue(
    Guid ObjectId,
    Guid GenerationId,
    long ReplicaId,
    string ProviderKey,
    string Locator,
    string Kind,
    string? Detail);

public sealed record StorageReconciliationReport(
    bool Applied,
    int ObjectsChecked,
    int ReplicasChecked,
    int HealthyReplicas,
    int MissingReplicas,
    int CorruptReplicas,
    int DeleteOperationsQueued,
    int RepairOperationsQueued,
    IReadOnlyList<StorageReconciliationIssue> Issues)
{
    public long TotalIssues { get; init; } = Issues.Count;
    public Guid ScanId { get; init; }
    public bool IssuesTruncated => TotalIssues > Issues.Count;
    public IReadOnlyDictionary<string, long> IssueCountsByKind { get; init; } = new Dictionary<string, long>();
}

public interface IStorageReconciliationService
{
    Task<StorageReconciliationReport> RunAsync(bool apply, CancellationToken cancellationToken);
}

public sealed class StorageReconciliationService(
    PaymentContext db,
    IStorageProviderRegistry providers,
    IOptionsMonitor<StorageOrchestrationOptions>? options = null) : IStorageReconciliationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int PageSize = 100;
    public const int ReplicaPageSize = 200;
    public const int MaximumReportedIssues = 1000;
    public Guid LastScanId { get; private set; }

    public async Task<StorageReconciliationReport> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        var scanId = LastScanId = Guid.NewGuid();
        // Stream only IDs into a private spool, then close the DB reader before any
        // provider I/O. No full catalogue/Include graph, OFFSET or long read transaction.
        await using var objectIds = new FixedRecordSpool(16, scanId);
        using var objectFingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await foreach (var id in db.StorageObjects.AsNoTracking().OrderBy(o => o.Id).Select(o => o.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var record = id.ToByteArray(); objectFingerprint.AppendData(record);
            await objectIds.WriteAsync(record, cancellationToken);
        }
        var originalObjects = objectFingerprint.GetHashAndReset();
        await objectIds.RewindAsync(cancellationToken);
        var issues = new List<StorageReconciliationIssue>();
        var issueCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalIssues = 0;
        void AddIssue(StorageReconciliationIssue issue)
        {
            totalIssues++;
            issueCounts[issue.Kind] = issueCounts.GetValueOrDefault(issue.Kind) + 1;
            if (issues.Count < MaximumReportedIssues) issues.Add(issue with { Detail = issue.Detail?.Length > 2000 ? issue.Detail[..2000] : issue.Detail });
        }
        var objectsChecked = 0;
        var checkedReplicas = 0;
        var healthy = 0;
        var missing = 0;
        var corrupt = 0;
        var deletes = 0;
        var repairs = 0;
        var now = DateTime.UtcNow;

        while (true)
        {
            var ids = ReadGuids(await objectIds.ReadBatchAsync(PageSize, cancellationToken), 16, 0);
            if (ids.Length == 0) break;
            var objects = await db.StorageObjects.AsNoTracking().Where(o => ids.Contains(o.Id))
                .Select(o => new ObjectProbe(o.Id, o.IsDeleted, o.Version, o.CurrentGeneration,
                    o.Tombstones.OrderByDescending(t => t.Generation).Select(t => (bool?)t.LegalHold).FirstOrDefault(),
                    o.Tombstones.OrderByDescending(t => t.Generation).Select(t => (DateTime?)t.RetainUntil).FirstOrDefault()))
                .ToDictionaryAsync(o => o.Id, cancellationToken);
            objectsChecked += objects.Count;
            foreach (var gone in ids.Where(id => !objects.ContainsKey(id))) AddIssue(new(gone, Guid.Empty, 0, "", "", "catalogChanged", "Object disappeared during verification."));
            await using var generations = new FixedRecordSpool(32, scanId);
            using var generationFingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var currentSeen = new HashSet<Guid>();
            await foreach (var item in db.StorageObjectGenerations.AsNoTracking().Where(g => ids.Contains(g.StorageObjectId))
                .OrderBy(g => g.StorageObjectId).ThenBy(g => g.Id).Select(g => new { g.Id, g.StorageObjectId, g.Generation, g.SizeBytes, g.Sha256 })
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
            {
                var record = new byte[32]; item.Id.TryWriteBytes(record); item.StorageObjectId.TryWriteBytes(record.AsSpan(16));
                await generations.WriteAsync(record, cancellationToken);
                generationFingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions));
                if (objects.TryGetValue(item.StorageObjectId, out var owner) && owner.CurrentGeneration == item.Generation) currentSeen.Add(owner.Id);
            }
            var originalGenerations = generationFingerprint.GetHashAndReset();
            foreach (var owner in objects.Values.Where(o => !o.IsDeleted && !currentSeen.Contains(o.Id)))
                AddIssue(new(owner.Id, Guid.Empty, 0, "", "", "generationMissing", "Current generation does not exist in the catalogue."));
            await generations.RewindAsync(cancellationToken);
            while (true)
            {
                var generationRecords = await generations.ReadBatchAsync(PageSize, cancellationToken);
                var generationIds = ReadGuids(generationRecords, 32, 0);
                if (generationIds.Length == 0) break;
                var page = await db.StorageObjectGenerations.AsNoTracking().Where(g => generationIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, cancellationToken);
                for (var index = 0; index < generationIds.Length; index++)
                {
                    var generationId = generationIds[index];
                    var parentId = new Guid(generationRecords.AsSpan(index * 32 + 16, 16));
                    if (!page.TryGetValue(generationId, out var generation) || generation.StorageObjectId != parentId || !objects.TryGetValue(parentId, out var storageObject))
                    { AddIssue(new(parentId, generationId, 0, "", "", "catalogChanged", "Generation disappeared during verification.")); continue; }
                    if (!storageObject.IsDeleted && storageObject.LegalHold.HasValue)
                    { AddIssue(new(parentId, generationId, 0, "", "", "catalogStateMismatch", "Live object has a deletion tombstone; probe suppressed.")); continue; }
                    StorageReplica? healthySource = null;
                    await using var targets = new FixedRecordSpool(41, scanId);
                    using var replicaFingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await FingerprintReplicaBindingsAsync(generation.Id, replicaFingerprint, cancellationToken);
                    var originalReplicas = replicaFingerprint.GetHashAndReset();
                    var upper = await db.StorageReplicas.Where(r => r.StorageObjectGenerationId == generation.Id).MaxAsync(r => (long?)r.Id, cancellationToken) ?? 0;
                    long after = 0;
                    var liveReplicas = 0;
                    while (true)
                    {
                        var replicas = await db.StorageReplicas.AsNoTracking().Where(r => r.StorageObjectGenerationId == generation.Id
                            && r.Id > after && r.Id <= upper && r.Status != StorageReplicaStatuses.Deleted)
                            .OrderBy(r => r.Id).Take(ReplicaPageSize).ToArrayAsync(cancellationToken);
                        if (replicas.Length == 0) break;
                        liveReplicas += replicas.Length;
                        after = replicas[^1].Id;
                        var queued = new List<StorageOperation>();
                        var destinations = new Dictionary<Guid, StorageReplica>();
                        var verified = new List<StorageReplica>();
                        var damaged = new Dictionary<string, List<StorageReplica>> { [StorageReplicaStatuses.Missing] = [], [StorageReplicaStatuses.Corrupt] = [] };
                        var deleteKeys = storageObject.IsDeleted && apply
                            ? replicas.Select(r => $"reconcile:delete:{generation.Id:N}:{r.Id}").ToArray() : [];
                        var existingDeletes = deleteKeys.Length == 0 ? new HashSet<string>() :
                            (await db.StorageOperations.AsNoTracking().Where(o => deleteKeys.Contains(o.IdempotencyKey)).Select(o => o.IdempotencyKey).ToArrayAsync(cancellationToken)).ToHashSet();
                        foreach (var replica in replicas)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            checkedReplicas++;
                            if (storageObject.IsDeleted)
                            {
                                var deletionDeferred = storageObject.LegalHold == true || storageObject.RetainUntil > now;
                                AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                    deletionDeferred ? "retained" : "deletePending",
                                    storageObject.LegalHold == true
                                        ? "Physical deletion is blocked by legal hold."
                                        : storageObject.RetainUntil > now
                                            ? $"Physical deletion is retained until {storageObject.RetainUntil:O}."
                                            : "Logical tombstone exists but physical replica remains."));
                                if (apply && !deletionDeferred
                                    && EnqueueDelete(storageObject.Id, generation.Id, replica, now, queued, existingDeletes)) deletes++;
                                continue;
                            }
                            if (replica.Status is StorageReplicaStatuses.RepairPending or StorageReplicaStatuses.RetainedCorrupt)
                            {
                                AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                    replica.Status, replica.Status == StorageReplicaStatuses.RepairPending
                                        ? "Isolated replacement is owned by its durable repair operation."
                                        : "Damaged bytes are retained separately from the verified replacement."));
                                continue;
                            }
                            if (!providers.TryGet(replica.ProviderKey, out var provider))
                            {
                                AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                    "providerUnavailable", "Provider is not registered."));
                                continue;
                            }
                            if (provider is IStorageConnectedLocatorProvider connected
                                && (!connected.TryGetConnectionId(replica.Locator, out var actualConnection) || actualConnection != replica.ConnectionId))
                            {
                                AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                    "locatorConnectionMismatch", "Locator does not match the registered storage connection; probe suppressed."));
                                continue;
                            }
                            StorageObjectMetadata metadata;
                            try
                            {
                                metadata = await provider.HeadAsync(replica.Locator, cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                    "verificationUnavailable", $"Provider probe failed ({exception.GetType().Name}); replica state was preserved."));
                                continue;
                            }
                            // A failed probe is not evidence of lost/corrupt bytes. Keep the last verified
                            // catalogue state and do not overwrite the replica on an ambiguous response.
                            if ((metadata.Status != StorageOperationStatus.Succeeded && metadata.Status != StorageOperationStatus.NotFound)
                                || (metadata.Status == StorageOperationStatus.Succeeded && metadata.SizeBytes == generation.SizeBytes
                                    && string.IsNullOrWhiteSpace(metadata.Sha256)))
                            {
                                AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                    "verificationUnavailable", metadata.Error ?? "Replica integrity could not be verified."));
                                continue;
                            }
                            if (metadata.Status == StorageOperationStatus.Succeeded
                                && metadata.SizeBytes == generation.SizeBytes
                                && string.Equals(metadata.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase))
                            {
                                healthy++;
                                if (apply)
                                {
                                    verified.Add(replica);
                                }
                                else healthySource ??= replica;
                                continue;
                            }

                            var kind = metadata.Status == StorageOperationStatus.NotFound ? "missing" : "corrupt";
                            if (kind == "missing") missing++; else corrupt++;
                            AddIssue(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                                kind, metadata.Error));
                            if (apply)
                            {
                                var status = kind == "missing" ? StorageReplicaStatuses.Missing : StorageReplicaStatuses.Corrupt;
                                damaged[status].Add(replica);
                            }
                        }
                        if (apply)
                        {
                            if (verified.Count > 0)
                            {
                                var updated = await BoundBatch(verified, generation).ExecuteUpdateAsync(s => s
                                    .SetProperty(r => r.Status, StorageReplicaStatuses.Available).SetProperty(r => r.SizeBytes, generation.SizeBytes)
                                    .SetProperty(r => r.Sha256, generation.Sha256).SetProperty(r => r.LastVerifiedAt, now)
                                    .SetProperty(r => r.LastError, (string?)null).SetProperty(r => r.UpdatedAt, now), cancellationToken);
                                if (updated == verified.Count)
                                {
                                    foreach (var row in verified) { row.SizeBytes = generation.SizeBytes; row.Sha256 = generation.Sha256; }
                                    healthySource ??= verified[0];
                                }
                                else AddIssue(new(storageObject.Id, generation.Id, 0, "", "", "catalogChanged", "Verified bindings changed during the batch; no source selected from it."));
                            }
                            foreach (var group in damaged.Where(group => group.Value.Count > 0))
                            {
                                var status = group.Key;
                                var updated = await BoundBatch(group.Value, generation).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status)
                                    .SetProperty(r => r.LastError, $"Replica is {status}.").SetProperty(r => r.UpdatedAt, now), cancellationToken);
                                if (updated != group.Value.Count)
                                { AddIssue(new(storageObject.Id, generation.Id, 0, "", "", "catalogChanged", "Damaged bindings changed during the batch; scheduling was suppressed.")); continue; }
                                foreach (var row in group.Value)
                                {
                                    row.Status = status; var record = new byte[41]; BinaryPrimitives.WriteInt64LittleEndian(record, row.Id);
                                    BindingDigest(row).CopyTo(record, 8); record[40] = status == StorageReplicaStatuses.Corrupt ? (byte)1 : (byte)0;
                                    await targets.WriteAsync(record, cancellationToken);
                                }
                            }
                            var skipped = await SaveAndDetachAsync(queued, destinations, cancellationToken);
                            deletes -= skipped.Deletes;
                        }
                    }
                    if (!storageObject.IsDeleted && liveReplicas == 0)
                        AddIssue(new(storageObject.Id, generation.Id, 0, "", "", "replicaMissing", "Generation has no live replica in the catalogue."));
                    await FingerprintReplicaBindingsAsync(generation.Id, replicaFingerprint, cancellationToken);
                    var replicaSetChanged = !originalReplicas.AsSpan().SequenceEqual(replicaFingerprint.GetHashAndReset());
                    if (replicaSetChanged)
                        AddIssue(new(storageObject.Id, generation.Id, 0, "", "", "catalogChanged", "Replica set or binding changed during verification; scheduling suppressed."));
                    if (apply && healthySource is not null && !replicaSetChanged)
                    {
                        await targets.RewindAsync(cancellationToken);
                        while (true)
                        {
                            var records = await targets.ReadBatchAsync(ReplicaPageSize, cancellationToken);
                            if (records.Length == 0) break;
                            var targetIds = Enumerable.Range(0, records.Length / 41).Select(i => BinaryPrimitives.ReadInt64LittleEndian(records.AsSpan(i * 41, 8))).ToArray();
                            var currentTargets = await db.StorageReplicas.AsNoTracking().Where(r => targetIds.Contains(r.Id) && r.StorageObjectGenerationId == generation.Id).ToDictionaryAsync(r => r.Id, cancellationToken);
                            var source = healthySource;
                            if (!await BoundReplica(source, generation, checkStatus: false).AnyAsync(r => r.Status == StorageReplicaStatuses.Available, cancellationToken))
                            { AddIssue(new(storageObject.Id, generation.Id, source.Id, source.ProviderKey, source.Locator, "catalogChanged", "Verified source binding changed before scheduling.")); break; }
                            var histories = await ReadRepairHistoriesAsync(storageObject.Id, generation.Id, targetIds, cancellationToken);
                            var queued = new List<StorageOperation>(); var destinations = new Dictionary<Guid, StorageReplica>();
                            var scheduledTargets = new List<StorageReplica>();
                            for (var i = 0; i < targetIds.Length; i++)
                            {
                                if (!currentTargets.TryGetValue(targetIds[i], out var target) || !BindingDigest(target).AsSpan().SequenceEqual(records.AsSpan(i * 41 + 8, 32))
                                    || target.Status != (records[i * 41 + 40] == 1 ? StorageReplicaStatuses.Corrupt : StorageReplicaStatuses.Missing))
                                { AddIssue(new(storageObject.Id, generation.Id, targetIds[i], "", "", "catalogChanged", "Verified target binding changed before scheduling.")); continue; }
                                target.Status = records[i * 41 + 40] == 1 ? StorageReplicaStatuses.Corrupt : StorageReplicaStatuses.Missing;
                                if (EnqueueRepair(storageObject.Id, generation.Id, source, target, now, queued, destinations, histories.GetValueOrDefault(target.Id)))
                                { repairs++; scheduledTargets.Add(target); }
                            }
                            if (options?.CurrentValue.RepairCapacityChecksEnabled == true)
                            {
                                var refusal = await SaveRepairsWithCapacityAsync(queued, destinations, generation, source, scheduledTargets, cancellationToken);
                                if (refusal is not null)
                                {
                                    repairs -= queued.Count;
                                    AddIssue(new(storageObject.Id, generation.Id, 0, "", "", "capacity", refusal));
                                }
                            }
                            else
                            {
                                var skipped = await SaveAndDetachAsync(queued, destinations, cancellationToken);
                                repairs -= skipped.Repairs;
                            }
                        }
                    }
                }
            }
            var finalObjects = await db.StorageObjects.AsNoTracking().Where(o => ids.Contains(o.Id))
                .Select(o => new ObjectProbe(o.Id, o.IsDeleted, o.Version, o.CurrentGeneration,
                    o.Tombstones.OrderByDescending(t => t.Generation).Select(t => (bool?)t.LegalHold).FirstOrDefault(),
                    o.Tombstones.OrderByDescending(t => t.Generation).Select(t => (DateTime?)t.RetainUntil).FirstOrDefault()))
                .ToDictionaryAsync(o => o.Id, cancellationToken);
            foreach (var original in objects.Values)
                if (!finalObjects.TryGetValue(original.Id, out var final) || original != final)
                    AddIssue(new(original.Id, Guid.Empty, 0, "", "", "catalogChanged", "Object state changed during verification; repeat required."));
            await foreach (var item in db.StorageObjectGenerations.AsNoTracking().Where(g => ids.Contains(g.StorageObjectId))
                .OrderBy(g => g.StorageObjectId).ThenBy(g => g.Id).Select(g => new { g.Id, g.StorageObjectId, g.Generation, g.SizeBytes, g.Sha256 })
                .AsAsyncEnumerable().WithCancellation(cancellationToken))
                generationFingerprint.AppendData(JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions));
            if (!originalGenerations.AsSpan().SequenceEqual(generationFingerprint.GetHashAndReset()))
                AddIssue(new(Guid.Empty, Guid.Empty, 0, "", "", "catalogChanged", "Generation set or metadata changed during verification; repeat required."));
        }
        await foreach (var id in db.StorageObjects.AsNoTracking().OrderBy(o => o.Id).Select(o => o.Id).AsAsyncEnumerable().WithCancellation(cancellationToken))
            objectFingerprint.AppendData(id.ToByteArray());
        if (!originalObjects.AsSpan().SequenceEqual(objectFingerprint.GetHashAndReset()))
            AddIssue(new(Guid.Empty, Guid.Empty, 0, "", "", "catalogChanged", "Object set changed during verification; repeat required."));
        return new(apply, objectsChecked, checkedReplicas, healthy, missing, corrupt, deletes, repairs, issues)
        { TotalIssues = totalIssues, IssueCountsByKind = issueCounts, ScanId = scanId };
    }

    private sealed record ObjectProbe(Guid Id, bool IsDeleted, long Version, long CurrentGeneration, bool? LegalHold, DateTime? RetainUntil);
    private sealed record RepairHistory(int Count, int Unfinished);

    private async Task FingerprintReplicaBindingsAsync(Guid generationId, IncrementalHash hash, CancellationToken ct)
    {
        // Integrity/status updates made by this scan do not change identity. Physical
        // routing changes, additions and deletions must invalidate scheduling evidence.
        await foreach (var item in db.StorageReplicas.AsNoTracking()
            .Where(r => r.StorageObjectGenerationId == generationId && r.Status != StorageReplicaStatuses.Deleted)
            .OrderBy(r => r.Id).Select(r => new { r.Id, r.ProviderKey, r.ConnectionId, r.Locator })
            .AsAsyncEnumerable().WithCancellation(ct))
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item, JsonOptions));
    }

    private IQueryable<StorageReplica> BoundBatch(List<StorageReplica> snapshots, StorageObjectGeneration generation)
    {
        var parameter = Expression.Parameter(typeof(StorageReplica), "r");
        Expression body = Expression.Constant(false);
        foreach (var snapshot in snapshots)
        {
            Expression<Func<StorageReplica, bool>> binding = r => r.Id == snapshot.Id && r.ProviderKey == snapshot.ProviderKey
                && r.ConnectionId == snapshot.ConnectionId && r.Locator == snapshot.Locator && r.Status == snapshot.Status
                && r.SizeBytes == snapshot.SizeBytes && r.Sha256 == snapshot.Sha256;
            body = Expression.OrElse(body, new ReplaceParameter(binding.Parameters[0], parameter).Visit(binding.Body)!);
        }
        return db.StorageReplicas.Where(r => r.StorageObjectGenerationId == generation.Id
            && r.StorageObjectGeneration.SizeBytes == generation.SizeBytes && r.StorageObjectGeneration.Sha256 == generation.Sha256
            && !r.StorageObjectGeneration.StorageObject.IsDeleted && !r.StorageObjectGeneration.StorageObject.Tombstones.Any())
            .Where(Expression.Lambda<Func<StorageReplica, bool>>(body, parameter));
    }

    private sealed class ReplaceParameter(ParameterExpression original, ParameterExpression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == original ? replacement : base.VisitParameter(node);
    }

    private IQueryable<StorageReplica> BoundReplica(StorageReplica snapshot, StorageObjectGeneration generation, bool checkStatus = true)
        => db.StorageReplicas.Where(r => r.Id == snapshot.Id && r.StorageObjectGenerationId == generation.Id
            && r.ProviderKey == snapshot.ProviderKey && r.ConnectionId == snapshot.ConnectionId && r.Locator == snapshot.Locator
            && r.SizeBytes == snapshot.SizeBytes && r.Sha256 == snapshot.Sha256 && (!checkStatus || r.Status == snapshot.Status)
            && r.StorageObjectGeneration.SizeBytes == generation.SizeBytes && r.StorageObjectGeneration.Sha256 == generation.Sha256
            && !r.StorageObjectGeneration.StorageObject.IsDeleted && !r.StorageObjectGeneration.StorageObject.Tombstones.Any());

    private async Task<Dictionary<long, RepairHistory>> ReadRepairHistoriesAsync(Guid objectId, Guid generationId, long[] targetIds, CancellationToken ct)
    {
        var prefix = $"reconcile:repair:{generationId:N}:";
        var ids = targetIds.Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var rows = await db.StorageOperations.AsNoTracking().Where(o => o.StorageObjectId == objectId && o.StorageObjectGenerationId == generationId
            && o.OperationType == StorageOperationTypes.Repair && o.IdempotencyKey.StartsWith(prefix))
            .Select(o => new { Tail = o.IdempotencyKey.Substring(prefix.Length), o.Status })
            .Select(o => new { Target = o.Tail.Contains(":") ? o.Tail.Substring(0, o.Tail.IndexOf(":")) : o.Tail, o.Status })
            .Where(o => ids.Contains(o.Target)).GroupBy(o => o.Target)
            .Select(group => new { Target = group.Key, Count = group.Count(), Unfinished = group.Count(o => o.Status != StorageOperationStatuses.Succeeded) })
            .ToArrayAsync(ct);
        return rows.ToDictionary(r => long.Parse(r.Target, System.Globalization.CultureInfo.InvariantCulture), r => new RepairHistory(r.Count, r.Unfinished));
    }

    private async Task<(int Deletes, int Repairs)> SaveAndDetachAsync(List<StorageOperation> queued, Dictionary<Guid, StorageReplica> destinations, CancellationToken ct)
    {
        if (queued.Count == 0) return (0, 0);
        var result = await SaveQueuedAsync(queued, destinations, ct);
        foreach (var operation in queued) db.Entry(operation).State = EntityState.Detached;
        foreach (var destination in destinations.Values) db.Entry(destination).State = EntityState.Detached;
        return result;
    }

    // The optional guard certifies the proposed repair queue work, not the later
    // physical write. All observations happen before the serializable DB section.
    private async Task<string?> SaveRepairsWithCapacityAsync(List<StorageOperation> queued,
        Dictionary<Guid, StorageReplica> destinations, StorageObjectGeneration generation,
        StorageReplica source, List<StorageReplica> targets, CancellationToken ct)
    {
        if (queued.Count == 0) return null;
        try
        {
            var setting = options!.CurrentValue;
            var minimumFreeBytes = setting.RepairMinimumFreeBytes;
            var observation = await StorageCapacityAdmission.ObserveAsync(db, providers,
                queued.Select(o => (o.ProviderKey!, o.ConnectionId)), minimumFreeBytes, ct);
            if (observation.Error is not null) return observation.Error;
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var current = await db.StorageObjectGenerations.AsNoTracking().AnyAsync(g => g.Id == generation.Id
                && g.StorageObjectId == generation.StorageObjectId && g.SizeBytes == generation.SizeBytes
                && g.Sha256 == generation.Sha256 && !g.StorageObject.IsDeleted
                && !g.StorageObject.Tombstones.Any(), ct);
            if (!current) return "Generation changed before repair scheduling; repeat reconciliation.";
            if (!await BoundReplica(source, generation, checkStatus: false)
                .AnyAsync(r => r.Status == StorageReplicaStatuses.Available, ct))
                return "Verified repair source changed before scheduling; repeat reconciliation.";
            var targetIds = targets.Select(r => r.Id).ToArray();
            var boundTargets = await db.StorageReplicas.AsNoTracking().Where(r => targetIds.Contains(r.Id)
                && r.StorageObjectGenerationId == generation.Id).ToDictionaryAsync(r => r.Id, ct);
            if (boundTargets.Count != targets.Count || targets.Any(target =>
                !boundTargets.TryGetValue(target.Id, out var bound) || bound.Status != target.Status
                || !BindingDigest(bound).AsSpan().SequenceEqual(BindingDigest(target))))
                return "Repair destination changed before scheduling; repeat reconciliation.";
            var debt = await StorageCapacityAdmission.ReadDebtAsync(db, observation, ct);
            if (debt.Error is not null) return debt.Error;
            var demand = queued.SelectMany(o => new[]
            {
                StorageCapacityAdmission.CopyDemand(o.ProviderKey!, o.ConnectionId, generation.SizeBytes, o.IdempotencyKey),
                StorageCapacityAdmission.SpoolDemand(generation.SizeBytes, o.IdempotencyKey)
            }).ToArray();
            var report = StorageCapacityAdmission.Evaluate(observation, debt, demand);
            if (!report.CanSchedule)
                return "Storage capacity is unknown, stale or insufficient; no repair was scheduled.";
            if (!options.CurrentValue.RepairCapacityChecksEnabled
                || options.CurrentValue.RepairMinimumFreeBytes != minimumFreeBytes)
                return "Repair capacity policy changed during scheduling; repeat reconciliation.";
            if (!await StorageCapacityAdmission.TryAdvanceEpochAsync(db, observation, ct))
                return "Storage capacity budget changed during repair scheduling; repeat reconciliation.";
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return null;
        }
        catch (Exception exception) when (StorageCapacityAdmission.IsConcurrentBudgetWrite(exception))
        {
            return "Concurrent storage scheduling changed capacity; repeat reconciliation.";
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" }
            or Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            return "A concurrent repair already changed the queue; repeat reconciliation.";
        }
        finally
        {
            foreach (var operation in queued) db.Entry(operation).State = EntityState.Detached;
            foreach (var destination in destinations.Values) db.Entry(destination).State = EntityState.Detached;
        }
    }

    private static byte[] BindingDigest(StorageReplica replica) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        replica.Id,
        replica.StorageObjectGenerationId,
        Provider = replica.ProviderKey.ToLowerInvariant(),
        replica.ConnectionId,
        replica.Locator,
        replica.SizeBytes,
        replica.Sha256,
    }, JsonOptions));

    private static Guid[] ReadGuids(byte[] records, int recordSize, int offset)
        => Enumerable.Range(0, records.Length / recordSize).Select(i => new Guid(records.AsSpan(i * recordSize + offset, 16))).ToArray();

    private sealed class FixedRecordSpool(int recordSize, Guid scanId) : IAsyncDisposable
    {
        private FileStream? file;
        public async Task WriteAsync(byte[] record, CancellationToken ct)
        {
            if (record.Length != recordSize) throw new InvalidDataException("Invalid catalogue spool record.");
            if (file is null)
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 81920,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                file = new(Path.Combine(Path.GetTempPath(), $"pp_reconcile_{scanId:N}_{Guid.NewGuid():N}.tmp"), options);
            }
            await file.WriteAsync(record, ct);
        }
        public async Task RewindAsync(CancellationToken ct)
        {
            if (file is null) return;
            await file.FlushAsync(ct); file.Position = 0;
        }
        public async Task<byte[]> ReadBatchAsync(int count, CancellationToken ct)
        {
            if (file is null) return [];
            var buffer = new byte[count * recordSize]; var read = 0;
            while (read < buffer.Length)
            {
                var next = await file.ReadAsync(buffer.AsMemory(read), ct);
                if (next == 0) break;
                read += next;
            }
            if (read % recordSize != 0) throw new InvalidDataException("Truncated catalogue spool.");
            return read == buffer.Length ? buffer : buffer[..read];
        }
        public async ValueTask DisposeAsync() { if (file is not null) await file.DisposeAsync(); }
    }

    private bool EnqueueDelete(
        Guid objectId,
        Guid generationId,
        StorageReplica replica,
        DateTime now,
        List<StorageOperation> queued,
        HashSet<string> existing)
    {
        var key = $"reconcile:delete:{generationId:N}:{replica.Id}";
        if (!existing.Add(key)) return false;
        var operation = new StorageOperation
        {
            StorageObjectId = objectId,
            StorageObjectGenerationId = generationId,
            OperationType = StorageOperationTypes.Delete,
            Status = StorageOperationStatuses.Pending,
            IdempotencyKey = key,
            ProviderKey = replica.ProviderKey,
            ConnectionId = replica.ConnectionId,
            PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(replica.Locator), JsonOptions),
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StorageOperations.Add(operation);
        queued.Add(operation);
        return true;
    }

    private bool EnqueueRepair(
        Guid objectId,
        Guid generationId,
        StorageReplica source,
        StorageReplica target,
        DateTime now,
        List<StorageOperation> queued,
        Dictionary<Guid, StorageReplica> repairDestinations,
        RepairHistory? history)
    {
        var key = $"reconcile:repair:{generationId:N}:{target.Id}";
        var cyclePrefix = key + ":";
        // Count completed cycles rather than ordering timestamps: clock skew and equal database
        // timestamps must not hide a newer operation. Existing terminal failures remain manual.
        if (history?.Unfinished > 0) return false;
        if (history is not null) key = cyclePrefix + history.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? originalLocator = null;
        var locator = target.Locator;
        if (target.Status == StorageReplicaStatuses.Corrupt)
        {
            if (!providers.TryGet(target.ProviderKey, out var provider) || provider is not IStorageRepairDestinationProvider destinations)
                return false; // Never invent opaque provider paths or overwrite damaged bytes.
            originalLocator = target.Locator;
            locator = destinations.CreateRepairLocator(originalLocator, generationId, key);
        }
        var operation = new StorageOperation
        {
            StorageObjectId = objectId,
            StorageObjectGenerationId = generationId,
            OperationType = StorageOperationTypes.Repair,
            Status = StorageOperationStatuses.Pending,
            IdempotencyKey = key,
            ProviderKey = target.ProviderKey,
            ConnectionId = target.ConnectionId,
            PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(locator, source.ProviderKey, source.Locator, originalLocator), JsonOptions),
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (originalLocator is not null)
        {
            var destination = new StorageReplica
            {
                StorageObjectGenerationId = generationId,
                ProviderKey = target.ProviderKey,
                ConnectionId = target.ConnectionId,
                Locator = locator,
                Status = StorageReplicaStatuses.RepairPending,
                SizeBytes = source.SizeBytes,
                Sha256 = source.Sha256,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.StorageReplicas.Add(destination);
            repairDestinations.Add(operation.Id, destination);
        }
        db.StorageOperations.Add(operation);
        queued.Add(operation);
        return true;
    }

    private async Task<(int Deletes, int Repairs)> SaveQueuedAsync(List<StorageOperation> queued, Dictionary<Guid, StorageReplica> repairDestinations, CancellationToken ct)
    {
        var deletes = 0;
        var repairs = 0;
        for (var attempt = 0; ; attempt++)
        {
            try { await db.SaveChangesAsync(ct); return (deletes, repairs); }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Another reconciliation may have inserted the same deterministic cycle after
                // our read. Drop only our matching proposal, never arbitrary tracked changes.
                var proposals = queued.Where(item => db.Entry(item).State == EntityState.Added).ToArray();
                if (proposals.Length == 0) throw;
                var keys = proposals.Select(item => item.IdempotencyKey).ToArray();
                var winners = await db.StorageOperations.AsNoTracking().Where(item => keys.Contains(item.IdempotencyKey))
                    .ToDictionaryAsync(item => item.IdempotencyKey, ct);
                var removed = 0;
                foreach (var proposal in proposals)
                {
                    if (!winners.TryGetValue(proposal.IdempotencyKey, out var winner)
                        || winner.StorageObjectId != proposal.StorageObjectId
                        || winner.StorageObjectGenerationId != proposal.StorageObjectGenerationId
                        || winner.OperationType != proposal.OperationType
                        || winner.ProviderKey != proposal.ProviderKey || winner.ConnectionId != proposal.ConnectionId
                        || winner.PayloadJson != proposal.PayloadJson) continue;
                    db.Entry(proposal).State = EntityState.Detached;
                    if (repairDestinations.TryGetValue(proposal.Id, out var destination)) db.Entry(destination).State = EntityState.Detached;
                    if (proposal.OperationType == StorageOperationTypes.Delete) deletes++; else repairs++;
                    removed++;
                }
                if (removed == 0) throw;
            }
        }
    }
}
