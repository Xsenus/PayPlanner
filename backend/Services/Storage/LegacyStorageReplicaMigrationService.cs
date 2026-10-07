using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record LegacyStorageReplicaMigrationRequest(string Source, Guid RunId,
    long AfterId = 0, int BatchSize = 100, bool Apply = false, string? ExpectedPolicyFingerprint = null, long? EndId = null);

public sealed record LegacyStorageReplicaMigrationItem(long SourceId, Guid? ObjectId, string ProviderKey,
    string? ConnectionId, string? Locator, string Status, string? Error);

public sealed record LegacyStorageReplicaMigrationReport(string Source, Guid RunId, bool Applied,
    string PolicyVersion, int RequiredCopies, bool BatchVerified, int Queued,
    LegacyStorageBackfillReport SourceVerification, IReadOnlyList<LegacyStorageReplicaMigrationItem> Items,
    IReadOnlyList<string> Errors)
{
    public string PolicyFingerprint { get; init; } = "";
    public StorageCapacityBudgetReport? Capacity { get; init; }
}

public interface ILegacyStorageReplicaMigrationService
{
    Task<LegacyStorageReplicaMigrationReport> RunAsync(LegacyStorageReplicaMigrationRequest request, CancellationToken ct);
}

// Plans only this bounded, rehashed page. All byte writes and receipts belong to
// the existing leased worker; sources and product pointers are never changed here.
public sealed class LegacyStorageReplicaMigrationService(PaymentContext db,
    ILegacyStorageCatalogBackfillService backfill, IStoragePolicyService policies,
    IStorageProviderRegistry providers, IOptionsMonitor<StorageOrchestrationOptions> options)
    : ILegacyStorageReplicaMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const int MaximumBatchSize = 100;
    public const int MaximumWriters = 20;

    public async Task<LegacyStorageReplicaMigrationReport> RunAsync(LegacyStorageReplicaMigrationRequest request, CancellationToken ct)
    {
        var source = (request.Source ?? "").Trim().ToLowerInvariant();
        if (!LegacyStorageSources.All.Contains(source) || request.RunId == Guid.Empty || request.AfterId < 0
            || request.BatchSize < 1 || request.BatchSize > MaximumBatchSize || request.EndId is < 0 || request.EndId < request.AfterId)
            throw new ArgumentException("A known source, non-empty run ID, valid cursor and batch size 1..100 are required.");
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Migration requires a clean scoped context.");
        if (request.Apply && string.IsNullOrWhiteSpace(request.ExpectedPolicyFingerprint))
            throw new ArgumentException("Apply requires the policy fingerprint returned by the dry-run preview.");
        var dataClass = LegacyStorageSources.GetDataClass(source);
        var policy = policies.Evaluate(dataClass);
        var connections = new Dictionary<string, string>(options.CurrentValue.ProviderConnections, StringComparer.OrdinalIgnoreCase);
        var checkCapacity = options.CurrentValue.MigrationCapacityChecksEnabled;
        var minimumFree = options.CurrentValue.MigrationMinimumFreeBytes;
        var fingerprint = PolicyFingerprint(policy, connections, checkCapacity, minimumFree);
        var verification = await backfill.RunAsync(new(source, request.AfterId, request.BatchSize, Apply: false, EndId: request.EndId), ct);
        var result = new List<LegacyStorageReplicaMigrationItem>();
        var errors = new List<string>();
        var queued = 0;
        StorageCapacityBudgetReport? capacity = null;
        LegacyStorageReplicaMigrationReport Report(bool verified = false)
            => new(source, request.RunId, request.Apply, policy.PolicyVersion, policy.RequiredCopies,
                verified, queued, verification, result, errors)
            { PolicyFingerprint = fingerprint, Capacity = capacity };
        if ((request.ExpectedPolicyFingerprint is not null && request.ExpectedPolicyFingerprint != fingerprint)
            || PolicyFingerprint(policies.Evaluate(dataClass), options.CurrentValue.ProviderConnections,
                options.CurrentValue.MigrationCapacityChecksEnabled, options.CurrentValue.MigrationMinimumFreeBytes) != fingerprint)
        { errors.Add("Storage policy or explicit connections changed; prepare a new preview."); return Report(); }
        if (!policy.RoutingEnabled || !policy.IsValid)
        { errors.Add(!policy.RoutingEnabled ? "Storage routing is disabled." : "Storage policy is invalid."); return Report(); }
        var writers = policy.WriteProviders.Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        if (writers.Length == 0 || writers.Length > MaximumWriters || writers.Contains("legacy-local"))
        { errors.Add("Policy writer count is unsupported or includes readonly legacy storage."); return Report(); }
        var destinations = new Dictionary<string, string?>();
        foreach (var key in writers)
        {
            if (!providers.TryGet(key, out var provider)) { errors.Add("A policy writer is unavailable."); continue; }
            var connection = provider is IStorageConnectedLocatorProvider
                ? connections.GetValueOrDefault(key)?.Trim() : null;
            if (provider is IStorageConnectedLocatorProvider && string.IsNullOrWhiteSpace(connection))
                errors.Add("A policy writer has no explicit connection.");
            destinations[key] = connection;
        }
        if (errors.Count > 0) return Report();
        var candidates = verification.Items.Where(i => i.Status == "alreadyRegistered" && i.ObjectId.HasValue).ToArray();
        foreach (var item in verification.Items.Except(candidates))
            result.Add(new(item.SourceId, item.ObjectId, "", null, null,
                item.Status == "ready" ? "registrationRequired" : "sourceConflict", "Source must be registered and verified first."));
        if (candidates.Length == 0) return Report(verification.Scanned == 0 && verification.Missing == 0 && verification.Invalid == 0);
        StorageCapacityAdmission.Observation? observed = null;
        StorageCapacityAdmission.Debt? debt = null;
        var newDemands = new List<StorageCapacityDemand>();
        if (checkCapacity)
        {
            // Native/provider observations stay outside row-lock transactions.
            observed = await StorageCapacityAdmission.ObserveAsync(db, providers,
                writers.Select(writer => (writer, destinations[writer])), minimumFree, ct);
            if (observed.Error is not null) { errors.Add(observed.Error); return Report(); }
        }
        var ids = candidates.Select(i => i.ObjectId!.Value).ToArray();
        // No provider I/O inside this short transaction. Object locks serialize a
        // concurrent planner with deletion/rebinding and worker receipt publication.
        await using var transaction = request.Apply
            ? checkCapacity ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                : await db.Database.BeginTransactionAsync(ct) : null;
        var added = new List<object>();
        try
        {
            if (checkCapacity)
            {
                // Durable debt shares this serializable transaction with inserts.
                debt = await StorageCapacityAdmission.ReadDebtAsync(db, observed!, ct);
                if (debt.Error is not null) { errors.Add(debt.Error); return Report(); }
            }
            if (request.Apply)
                await db.StorageObjects.Where(o => ids.Contains(o.Id) && !o.IsDeleted && !o.Tombstones.Any())
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.Version, o => o.Version), ct);
            var objects = await db.StorageObjects.AsNoTracking().Where(o => ids.Contains(o.Id))
                .Select(o => new ObjectState(o.Id, o.Version, o.DataClass, o.OwnerType, o.OwnerId, o.LogicalName,
                    o.ContentType, o.CurrentGeneration, o.IsDeleted, o.Tombstones.Any())).ToDictionaryAsync(o => o.Id, ct);
            var generations = await db.StorageObjectGenerations.AsNoTracking().Where(g => ids.Contains(g.StorageObjectId) && g.Generation == 1)
                .ToDictionaryAsync(g => g.StorageObjectId, ct);
            var generationIds = generations.Values.Select(g => g.Id).ToArray();
            // A malicious/unbounded replica graph must not make this admin page unbounded.
            var replicas = await db.StorageReplicas.AsNoTracking().Where(r => generationIds.Contains(r.StorageObjectGenerationId))
                .OrderBy(r => r.Id).Take(MaximumBatchSize * (MaximumWriters + 1) + 1).ToArrayAsync(ct);
            if (replicas.Length > MaximumBatchSize * (MaximumWriters + 1))
            { errors.Add("Replica graph exceeds the bounded migration page; narrow the batch."); return Report(); }
            var copyKeys = generations.Values.SelectMany(g => writers.Select(p => CopyKey(g.Id, p, destinations[p])));
            var verifyKeys = generations.Values.SelectMany(g => writers.Select(p => VerifyKey(request.RunId, g.Id, p, destinations[p], fingerprint)));
            var keys = copyKeys.Concat(verifyKeys).ToArray();
            var operations = await db.StorageOperations.AsNoTracking().Where(o => keys.Contains(o.IdempotencyKey))
                .ToDictionaryAsync(o => o.IdempotencyKey, ct);
            var byGeneration = replicas.ToLookup(r => r.StorageObjectGenerationId);
            var now = DateTime.UtcNow;
            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var id = item.ObjectId!.Value;
                if (!objects.TryGetValue(id, out var owner) || !generations.TryGetValue(id, out var generation)
                    || owner.IsDeleted || owner.Tombstone || owner.Generation != 1 || owner.Version != item.CatalogVersion
                    || owner.DataClass != dataClass || owner.OwnerType != item.OwnerType || owner.OwnerId != item.OwnerId
                    || owner.Name != item.LogicalName || owner.ContentType != item.ContentType
                    || generation.SizeBytes != item.SizeBytes || generation.Sha256 != item.Sha256)
                { result.Add(new(item.SourceId, id, "", null, null, "sourceConflict", "Catalogue changed after source verification.")); continue; }
                var group = byGeneration[generation.Id];
                if (group.Count(r => r.ProviderKey == "legacy-local") != 1 || !group.Any(r => r.ProviderKey == "legacy-local"
                    && r.ConnectionId == null && r.Locator == item.Locator && r.Status == StorageReplicaStatuses.Available
                    && r.SizeBytes == generation.SizeBytes && r.Sha256 == generation.Sha256 && r.LastVerifiedAt != null))
                { result.Add(new(item.SourceId, id, "", null, null, "sourceConflict", "Legacy replica authority changed.")); continue; }
                foreach (var key in writers)
                {
                    var connection = destinations[key];
                    var relative = $"migration/{id:N}/1/data.bin";
                    var target = connection is null ? relative : $"{connection}/{relative}";
                    var provider = providers.GetRequired(key);
                    if (provider is IStorageConnectedLocatorProvider connected
                        && (!connected.TryGetConnectionId(target, out var actual) || actual != connection))
                    { result.Add(new(item.SourceId, id, key, connection, null, "conflict", "Explicit connection cannot address the target.")); continue; }
                    var existing = group.Where(r => r.ProviderKey.ToLower() == key && r.ConnectionId == connection).ToArray();
                    var replica = existing.Length == 1 && existing[0].Locator == target ? existing[0] : null;
                    // Do not overwrite, rebind or reset damaged/unowned/retained copies.
                    if (existing.Length > 1 || (existing.Length == 1 && replica is null)
                        || (replica is not null && replica.Status is not StorageReplicaStatuses.Pending and not StorageReplicaStatuses.Available))
                    { result.Add(new(item.SourceId, id, key, connection, target, "conflict", "Destination requires separate reconciliation.")); continue; }
                    var payload = JsonSerializer.Serialize(new StorageOperationPayload(target, "legacy-local", item.Locator), JsonOptions);
                    var copyKey = CopyKey(generation.Id, key, connection);
                    var verifyKey = VerifyKey(request.RunId, generation.Id, key, connection, fingerprint);
                    operations.TryGetValue(copyKey, out var copy);
                    operations.TryGetValue(verifyKey, out var verify);
                    bool Bound(StorageOperation op, string type) => op.OperationType == type && op.StorageObjectId == id
                        && op.StorageObjectGenerationId == generation.Id && op.ProviderKey == key && op.ConnectionId == connection && op.PayloadJson == payload;
                    if ((copy is not null && !Bound(copy, StorageOperationTypes.Replicate)) || (verify is not null && !Bound(verify, StorageOperationTypes.Verify))
                        || copy?.Status == StorageOperationStatuses.DeadLetter || verify?.Status == StorageOperationStatuses.DeadLetter
                        || (replica is null && copy is not null))
                    { result.Add(new(item.SourceId, id, key, connection, target, "conflict", "A durable operation has conflicting or terminal authority.")); continue; }
                    if (replica is not null && replica.Status == StorageReplicaStatuses.Available && replica.LastVerifiedAt != null
                        && replica.SizeBytes == generation.SizeBytes && replica.Sha256 == generation.Sha256
                        && verify?.Status == StorageOperationStatuses.Succeeded && verify.CompletedAt != null)
                    { result.Add(new(item.SourceId, id, key, connection, target, "verified", null)); continue; }
                    if (request.Apply)
                    {
                        if (replica is null)
                        {
                            replica = new()
                            {
                                StorageObjectGenerationId = generation.Id,
                                ProviderKey = key,
                                ConnectionId = connection,
                                Locator = target,
                                Status = StorageReplicaStatuses.Pending,
                                SizeBytes = generation.SizeBytes,
                                Sha256 = generation.Sha256
                            };
                            db.StorageReplicas.Add(replica); added.Add(replica);
                        }
                        if (replica.Status == StorageReplicaStatuses.Pending && copy is null)
                            AddOperation(copyKey, StorageOperationTypes.Replicate);
                        if (verify is null) AddOperation(verifyKey, StorageOperationTypes.Verify);
                    }
                    result.Add(new(item.SourceId, id, key, connection, target, request.Apply ? "pending" : "planned", null));
                    if (checkCapacity && (replica is null || replica.Status == StorageReplicaStatuses.Pending) && copy is null)
                    {
                        newDemands.Add(StorageCapacityAdmission.CopyDemand(key, connection, generation.SizeBytes, copyKey));
                        newDemands.Add(StorageCapacityAdmission.SpoolDemand(generation.SizeBytes, copyKey));
                    }
                    void AddOperation(string operationKey, string type)
                    {
                        var operation = new StorageOperation
                        {
                            StorageObjectId = id,
                            StorageObjectGenerationId = generation.Id,
                            OperationType = type,
                            ProviderKey = key,
                            ConnectionId = connection,
                            IdempotencyKey = operationKey,
                            PayloadJson = payload,
                            Status = StorageOperationStatuses.Pending,
                            NextAttemptAt = now,
                            CreatedAt = now,
                            UpdatedAt = now
                        };
                        db.StorageOperations.Add(operation); added.Add(operation); queued++;
                    }
                }
            }
            if (checkCapacity)
            {
                capacity = StorageCapacityAdmission.Evaluate(observed!, debt!, newDemands);
                if (!capacity.CanSchedule)
                { queued = 0; errors.Add("Storage capacity is unknown, stale or insufficient; no new operations were saved."); return Report(); }
            }
            // Capacity/provider observation may take time. A settings change while
            // it runs must not publish work authorised by an obsolete preview.
            if (PolicyFingerprint(policies.Evaluate(dataClass), options.CurrentValue.ProviderConnections,
                options.CurrentValue.MigrationCapacityChecksEnabled, options.CurrentValue.MigrationMinimumFreeBytes) != fingerprint)
            { queued = 0; errors.Add("Storage settings changed during planning; prepare a new preview."); return Report(); }
            if (request.Apply)
            {
                if (checkCapacity && added.Count > 0
                    && !await StorageCapacityAdmission.TryAdvanceEpochAsync(db, observed!, ct))
                { queued = 0; errors.Add("Storage capacity budget changed during planning; repeat the complete preview/apply."); return Report(); }
                if (added.Count > 0) await db.SaveChangesAsync(ct);
                await transaction!.CommitAsync(ct);
            }
            return Report(verification.Missing == 0 && verification.Invalid == 0
                && result.Count == verification.Scanned * writers.Length && result.All(r => r.Status == "verified"));
        }
        catch (Exception exception) when (request.Apply && checkCapacity && StorageCapacityAdmission.IsConcurrentBudgetWrite(exception))
        {
            // Do not retry only SaveChanges or reuse a snapshot from the aborted
            // transaction. The caller must repeat the complete plan with fresh debt.
            queued = 0; capacity = null;
            errors.Add("Concurrent storage planning changed capacity; no new operations were saved. Repeat the complete preview/apply.");
            return Report();
        }
        finally
        {
            // Own only these new entities; never clear unrelated tracking. A lost
            // commit ACK is resolved by stable keys on the next identical request.
            foreach (var entity in added) db.Entry(entity).State = EntityState.Detached;
        }
    }

    private static string Identity(Guid generation, string provider, string? connection)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{generation:N}:{provider}:{connection}"))).ToLowerInvariant();
    private static string CopyKey(Guid generation, string provider, string? connection) => "legacy-copy:" + Identity(generation, provider, connection);
    private static string VerifyKey(Guid run, Guid generation, string provider, string? connection, string fingerprint)
        => $"legacy-verify:{run:N}:" + Identity(generation, provider, connection + ":" + fingerprint);
    private static string PolicyFingerprint(StoragePolicyEvaluation policy, IReadOnlyDictionary<string, string> connections,
        bool checkCapacity, long minimumFree)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            policy.DataClass,
            policy.PolicyVersion,
            policy.RoutingEnabled,
            policy.IsValid,
            policy.RequiredCopies,
            policy.WriteProviders,
            policy.ReadProviders,
            policy.RetentionDays,
            policy.LegalHold,
            checkCapacity,
            minimumFree,
            Connections = policy.WriteProviders.Select(p => new { Provider = p.ToLowerInvariant(), Connection = connections.GetValueOrDefault(p)?.Trim() })
        }, JsonOptions))).ToLowerInvariant();
    private sealed record ObjectState(Guid Id, long Version, string DataClass, string OwnerType, string OwnerId,
        string Name, string ContentType, long Generation, bool IsDeleted, bool Tombstone);
}
