using System.Text.Json;
using System.Buffers;
using System.Security.Cryptography;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageObjectWriteRequest(
    string DataClass,
    string OwnerType,
    string OwnerId,
    string LogicalName,
    string ContentType,
    Stream Content,
    string IdempotencyKey,
    int? CreatedByUserId = null,
    string? ProductContextJson = null,
    long? ExpectedSizeBytes = null,
    string? ExpectedSha256 = null);

public sealed record StorageObjectWriteResult(
    Guid ObjectId,
    long Generation,
    string Status,
    int AvailableCopies,
    int RequiredCopies,
    string PolicyVersion,
    string? Error = null);

public sealed record StorageObjectReadResult(
    Guid ObjectId,
    long Generation,
    string ContentType,
    string LogicalName,
    long SizeBytes,
    Stream Content,
    string ProviderKey,
    string? Sha256 = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IStorageObjectService
{
    Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken cancellationToken);
    Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid objectId, int? deletedByUserId, string? reason, CancellationToken cancellationToken);
}

// Internal recovery capability: a reservation is not a published object.
public interface IStorageReservedReadService
{
    Task<StorageObjectReadResult?> OpenReservedReadAsync(StorageObjectWriteRequest request, CancellationToken ct);
}

public sealed partial class StorageObjectService(
    PaymentContext db,
    IStoragePolicyService policies,
    IStorageProviderRegistry providers,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    IStorageProviderHealthTracker? providerHealth = null,
    Func<Guid, FileStream>? verifiedReadSpoolFactory = null, IStorageOriginalReadStagingService? originalReadStages = null) : IStorageObjectService, IStorageOriginalReadService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object ReplayOwnerMismatchMarker = new();
    private sealed class VerifiedReadStagingException : Exception { }

    // Preserve the existing exception contract while allowing a product writer
    // to distinguish its completed owner handoff from unrelated write failures.
    public static bool IsReplayOwnerMismatch(Exception exception, string uploadKey)
        => exception is InvalidOperationException
            && exception.Data[ReplayOwnerMismatchMarker] is string key && key == uploadKey.Trim();

    public async Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        if (request.ExpectedSizeBytes is < 0)
            throw new ArgumentException("Expected file size must be non-negative.", nameof(request));
        if (request.ProductContextJson is { Length: > 65536 })
            throw new ArgumentException("Product upload context is too large.");
        var policy = policies.Evaluate(request.DataClass);
        if (!policy.RoutingEnabled)
            return new(Guid.Empty, 0, "disabled", 0, policy.RequiredCopies, policy.PolicyVersion, "Storage routing is disabled.");
        if (!policy.IsValid)
            return new(Guid.Empty, 0, "invalidPolicy", 0, policy.RequiredCopies, policy.PolicyVersion, string.Join(" ", policy.Errors));
        var writeProviderKeys = policy.WriteProviders.Select(key => key.ToLowerInvariant()).Distinct().ToArray();
        var checkUploadCapacity = options.CurrentValue.UploadCapacityChecksEnabled;

        var normalizedIdempotency = request.IdempotencyKey.Trim();
        var operationKey = BuildOperationKey(normalizedIdempotency, "commit");
        var replay = await ReplayAsync();
        if (replay is not null) return replay;

        async Task<StorageObjectWriteResult?> ReplayAsync()
        {
            var existing = await db.StorageOperations.AsNoTracking()
                .Where(item => item.IdempotencyKey == operationKey)
                .Select(item => new
                {
                    item.StorageObjectId,
                    item.StorageObjectGenerationId,
                    item.Status,
                    item.PayloadJson,
                    item.StorageObject.DataClass,
                    item.StorageObject.OwnerType,
                    item.StorageObject.OwnerId,
                    item.StorageObject.LogicalName,
                    item.StorageObject.ContentType,
                    item.StorageObject.IsDeleted,
                    CreatedByUserId = item.StorageObjectGeneration == null ? null : item.StorageObjectGeneration.CreatedByUserId,
                    SizeBytes = item.StorageObjectGeneration == null ? (long?)null : item.StorageObjectGeneration.SizeBytes,
                    Sha256 = item.StorageObjectGeneration == null ? null : item.StorageObjectGeneration.Sha256,
                })
                .SingleOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.DataClass, request.DataClass.Trim(), StringComparison.Ordinal)
                    || !string.Equals(existing.OwnerType, request.OwnerType.Trim(), StringComparison.Ordinal)
                    || !string.Equals(existing.LogicalName, request.LogicalName.Trim(), StringComparison.Ordinal)
                    || !string.Equals(existing.ContentType, NormalizeContentType(request.ContentType), StringComparison.Ordinal)
                    || existing.CreatedByUserId != request.CreatedByUserId)
                    throw new InvalidOperationException("Ключ повторной загрузки уже используется для другого файла.");
                if (!string.Equals(existing.OwnerId, request.OwnerId.Trim(), StringComparison.Ordinal))
                {
                    var mismatch = new InvalidOperationException("Ключ повторной загрузки уже используется для другого файла.");
                    mismatch.Data[ReplayOwnerMismatchMarker] = normalizedIdempotency;
                    throw mismatch;
                }
                if (existing.IsDeleted)
                    throw new InvalidOperationException("Файл этой загрузки уже удалён. Для нового файла нужен новый ключ загрузки.");
                if (checkUploadCapacity && (existing.SizeBytes != request.ExpectedSizeBytes
                    || !string.Equals(existing.Sha256, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                    || existing.PayloadJson != request.ProductContextJson))
                    throw new InvalidOperationException("Ключ повторной загрузки уже используется для другого содержимого.");

                var generationNumber = existing.StorageObjectGenerationId is { } generationId
                    ? await db.StorageObjectGenerations.Where(item => item.Id == generationId).Select(item => item.Generation).SingleAsync(cancellationToken)
                    : 0;
                var copies = existing.StorageObjectGenerationId is { } id
                    ? await db.StorageReplicas
                        .Where(item => item.StorageObjectGenerationId == id && item.Status == StorageReplicaStatuses.Available
                            && writeProviderKeys.Contains(item.ProviderKey.ToLower()))
                        .Select(item => item.ProviderKey.ToLower()).Distinct().CountAsync(cancellationToken)
                    : 0;
                return new(existing.StorageObjectId, generationNumber,
                    existing.Status == StorageOperationStatuses.Succeeded && copies >= policy.RequiredCopies ? "available" : "pending",
                    copies, policy.RequiredCopies, policy.PolicyVersion);
            }
            return null;
        }

        var objectId = Guid.NewGuid();
        const long generationNumberNew = 1;
        var safeName = SanitizeName(request.LogicalName);
        var relativeLocator = $"{SanitizeSegment(request.DataClass)}/{objectId:N}/{generationNumberNew}/{safeName}";
        StorageUploadCapacityReservations.Ticket? reservation = null;
        if (checkUploadCapacity)
        {
            var (ticket, error) = await StorageUploadCapacityReservations.PrepareAsync(db, providers, options,
                policy, request, BuildOperationKey(normalizedIdempotency, "capacity"), objectId, relativeLocator, cancellationToken);
            if (ticket is null)
                return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion, error);
            reservation = ticket;
            objectId = ticket.ObjectId;
            relativeLocator = ticket.LocalLocator;
        }
        var local = providers.GetRequired("local");
        if (providerHealth is not null && !providerHealth.CanAttempt(local.Key))
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion, "Local storage circuit is temporarily open.");
        StorageWriteResult localWrite;
        // The durable reservation fixes this locator and exact bytes across a
        // lost acknowledgement or process restart. Verify before retrying PUT;
        // an uncertain HEAD must never cause a second physical write.
        var staged = reservation is null ? null : await local.HeadAsync(relativeLocator, cancellationToken);
        if (staged is not null && staged.Status != StorageOperationStatus.NotFound)
        {
            if (staged.Status != StorageOperationStatus.Succeeded || staged.SizeBytes != reservation!.SizeBytes
                || !string.Equals(staged.Sha256, reservation.Sha256, StringComparison.OrdinalIgnoreCase))
                return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion,
                    "Reserved local staging is unavailable or differs from the upload intent.");
            localWrite = new(StorageOperationStatus.Succeeded, relativeLocator, staged.SizeBytes, staged.Sha256);
        }
        else
            localWrite = await local.PutAsync(new StorageWriteRequest(
                relativeLocator, request.Content, request.ContentType,
                ExpectedSizeBytes: request.ExpectedSizeBytes, ExpectedSha256: request.ExpectedSha256,
                Overwrite: false), cancellationToken);
        if (reservation is not null && localWrite.Status == StorageOperationStatus.Conflict)
        {
            var existingLocal = await local.HeadAsync(relativeLocator, cancellationToken);
            if (existingLocal.Status == StorageOperationStatus.Succeeded
                && existingLocal.SizeBytes == reservation.SizeBytes
                && string.Equals(existingLocal.Sha256, reservation.Sha256, StringComparison.OrdinalIgnoreCase))
                localWrite = new(StorageOperationStatus.Succeeded, relativeLocator,
                    existingLocal.SizeBytes, existingLocal.Sha256);
        }
        providerHealth?.Record(local.Key, localWrite.Status, localWrite.Error);
        if (localWrite.Status != StorageOperationStatus.Succeeded)
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion, localWrite.Error ?? "Local durable staging failed.");
        if (reservation is not null && (localWrite.SizeBytes != reservation.SizeBytes
            || !string.Equals(localWrite.Sha256, reservation.Sha256, StringComparison.OrdinalIgnoreCase)))
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion,
                "Local staged bytes do not match the reserved upload; the reservation was retained for recovery.");
        if (reservation is not null)
        {
            // Re-evaluate changed routing and physical groups after the local PUT.
            // Provider I/O remains outside the publication transaction.
            var currentPolicy = policies.Evaluate(request.DataClass);
            if (!currentPolicy.RoutingEnabled || !currentPolicy.IsValid
                || currentPolicy.PolicyVersion != policy.PolicyVersion
                || currentPolicy.RequiredCopies != policy.RequiredCopies
                || !currentPolicy.WriteProviders.SequenceEqual(policy.WriteProviders))
                return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion,
                    "Storage upload policy changed; retry with the same key.");
            var (verified, error) = await StorageUploadCapacityReservations.PrepareAsync(db, providers, options,
                currentPolicy, request, reservation.Key, objectId, relativeLocator, cancellationToken);
            if (verified is null || verified.BindingHash != reservation.BindingHash)
                return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion, error);
        }

        var now = DateTime.UtcNow;
        var storageObject = new StorageObject
        {
            Id = objectId,
            DataClass = request.DataClass.Trim(),
            OwnerType = request.OwnerType.Trim(),
            OwnerId = request.OwnerId.Trim(),
            LogicalName = request.LogicalName.Trim(),
            ContentType = NormalizeContentType(request.ContentType),
            CurrentGeneration = generationNumberNew,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var generation = new StorageObjectGeneration
        {
            StorageObject = storageObject,
            Generation = generationNumberNew,
            SizeBytes = localWrite.SizeBytes,
            Sha256 = localWrite.Sha256!,
            PolicyVersion = policy.PolicyVersion,
            CreatedByUserId = request.CreatedByUserId,
            CreatedAt = now,
        };
        storageObject.Generations.Add(generation);
        var localIsPolicyCopy = policy.WriteProviders.Contains("local", StringComparer.OrdinalIgnoreCase);
        generation.Replicas.Add(new StorageReplica
        {
            ProviderKey = "local",
            Locator = relativeLocator,
            Status = localIsPolicyCopy ? StorageReplicaStatuses.Available : StorageReplicaStatuses.Pending,
            SizeBytes = localWrite.SizeBytes,
            Sha256 = localWrite.Sha256,
            LastVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        var commitOperation = new StorageOperation
        {
            StorageObjectGeneration = generation,
            OperationType = "commit",
            PayloadJson = request.ProductContextJson,
            Status = StorageOperationStatuses.Succeeded,
            IdempotencyKey = operationKey,
            ProviderKey = "local",
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
        };
        storageObject.Operations.Add(commitOperation);
        if (options.CurrentValue.AutoCaseDocumentPublicationEnabled && storageObject.DataClass == "case-document"
            && request.ProductContextJson is not null
            && (storageObject.OwnerId.StartsWith("upload:case:", StringComparison.Ordinal)
                || GeneratedWorkOrderDocumentPublication.CanSchedule(request.ProductContextJson, storageObject.OwnerId)
                || GeneratedTemplateDocumentPublication.CanSchedule(request.ProductContextJson, storageObject.OwnerId)
                || GeneratedCourtDocumentPublication.CanSchedule(request.ProductContextJson, storageObject.OwnerId)
                || GeneratedFinancialDocumentPublication.CanSchedule(request.ProductContextJson, storageObject.OwnerId)))
        {
            storageObject.Operations.Add(CaseDocumentPublicationProcessor.CreateOperation(commitOperation, generation, now));
        }
        if (options.CurrentValue.AutoTaskFilePublicationEnabled && storageObject.DataClass == "task-file"
            && request.ProductContextJson is not null)
        {
            storageObject.Operations.Add(TaskFilePublicationProcessor.CreateOperation(commitOperation, generation, now));
        }

        foreach (var providerKey in policy.WriteProviders.Where(key => !string.Equals(key, "local", StringComparison.OrdinalIgnoreCase)))
        {
            var connectionId = reservation is null
                ? options.CurrentValue.ProviderConnections.GetValueOrDefault(providerKey)
                : reservation.Connections.GetValueOrDefault(providerKey);
            var targetLocator = reservation is null ? BuildConnectedLocator(providerKey, relativeLocator)
                : $"{connectionId}/{relativeLocator}";
            generation.Replicas.Add(new StorageReplica
            {
                ProviderKey = providerKey,
                ConnectionId = connectionId,
                Locator = targetLocator,
                Status = StorageReplicaStatuses.Pending,
                SizeBytes = localWrite.SizeBytes,
                Sha256 = localWrite.Sha256,
                CreatedAt = now,
                UpdatedAt = now,
            });
            storageObject.Operations.Add(new StorageOperation
            {
                StorageObjectGeneration = generation,
                OperationType = StorageOperationTypes.Replicate,
                Status = StorageOperationStatuses.Pending,
                IdempotencyKey = BuildOperationKey(normalizedIdempotency, providerKey),
                ProviderKey = providerKey,
                ConnectionId = connectionId,
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(targetLocator, "local", relativeLocator), JsonOptions),
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // A database exception can arrive AFTER commit. Deleting durable bytes here
        // would destroy an already registered object and break an idempotent retry.
        // Unregistered staging must be reconciled separately, never guessed away.
        db.StorageObjects.Add(storageObject);
        void DetachProposed()
        {
            var entries = db.ChangeTracker.Entries().Where(entry =>
                ReferenceEquals(entry.Entity, storageObject)
                || ReferenceEquals(entry.Entity, generation)
                || generation.Replicas.Any(replica => ReferenceEquals(entry.Entity, replica))
                || storageObject.Operations.Any(operation => ReferenceEquals(entry.Entity, operation))
                || reservation is not null && entry.Entity is StorageCapacityReservation row
                    && row.IdempotencyKey == reservation.Key).ToArray();
            foreach (var entry in entries) entry.State = EntityState.Detached;
        }
        try
        {
            if (reservation is null)
                await db.SaveChangesAsync(cancellationToken);
            else
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                await StorageUploadCapacityReservations.ReleaseForPublicationAsync(db, reservation, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex) when (db.Database.CurrentTransaction is null && IsIdempotencyCollision(ex))
        {
            // Only a proven unique-key collision can be replayed. Other failures
            // may be lost commit acknowledgements and must retain durable bytes.
            DetachProposed();

            // Our random object/locator is private and was never committed. Never
            // remove it if the database contains either its object or a reference.
            var winner = await db.StorageOperations.AsNoTracking()
                .Where(item => item.IdempotencyKey == operationKey).Select(item => (Guid?)item.StorageObjectId)
                .SingleOrDefaultAsync(cancellationToken);
            if (winner is null) throw;
            if (reservation is null && winner != objectId
                && !await db.StorageObjects.AsNoTracking().AnyAsync(item => item.Id == objectId, cancellationToken)
                && !await db.StorageReplicas.AsNoTracking().AnyAsync(item => item.ProviderKey == "local" && item.Locator == relativeLocator, cancellationToken))
                await local.DeleteAsync(relativeLocator, cancellationToken);
            replay = await ReplayAsync();
            if (replay is null) throw;
            return replay;
        }
        catch (Exception exception) when (reservation is not null && StorageCapacityAdmission.IsConcurrentBudgetWrite(exception))
        {
            DetachProposed();
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion,
                "Concurrent upload changed capacity; retry with the same key.");
        }
        catch (InvalidOperationException exception) when (reservation is not null
            && exception.Message == "Upload reservation changed before publication.")
        {
            // Another exact retry may have committed while this request was
            // verifying the staged file. Never remove its shared locator.
            DetachProposed();
            replay = await ReplayAsync();
            if (replay is not null) return replay;
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion,
                "Upload reservation changed; retry with the same key.");
        }

        var available = localIsPolicyCopy ? 1 : 0;
        return new(objectId, generationNumberNew,
            available >= policy.RequiredCopies ? "available" : "pending",
            available, policy.RequiredCopies, policy.PolicyVersion);
    }

    internal static bool IsIdempotencyCollision(DbUpdateException exception) => exception.InnerException switch
    {
        Npgsql.PostgresException pg => pg.SqlState == "23505"
            && pg.ConstraintName == "IX_StorageOperations_IdempotencyKey",
        Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteErrorCode == 19
            && sqlite.SqliteExtendedErrorCode == 2067
            && sqlite.Message.Contains("StorageOperations.IdempotencyKey", StringComparison.Ordinal),
        _ => false,
    };

    public Task<StorageObjectReadResult?> OpenReservedReadAsync(StorageObjectWriteRequest request, CancellationToken ct)
        => OpenReservedReadCoreAsync(request, false, ct);

    private async Task<StorageObjectReadResult?> OpenReservedReadCoreAsync(StorageObjectWriteRequest request, bool admittedOriginal, CancellationToken ct)
    {
        ValidateRequest(request);
        if (request.ExpectedSizeBytes is not { } size || size < 0 || request.ExpectedSha256 is not { Length: 64 } sha
            || !sha.All(Uri.IsHexDigit) || request.ProductContextJson is not { Length: <= 65536 }) return null;
        var policy = policies.Evaluate(request.DataClass);
        var settings = options.CurrentValue;
        if (!settings.UploadCapacityChecksEnabled || !policy.RoutingEnabled || !policy.IsValid) return null;
        var binding = StorageUploadCapacityReservations.BindingHash(request, policy, settings);
        var key = GetCapacityReservationKey(request.IdempotencyKey);
        var resource = StorageCapacityAdmission.ResourceKey("local", null);
        var row = await db.StorageCapacityReservations.AsNoTracking()
            .SingleOrDefaultAsync(r => r.IdempotencyKey == key && r.ResourceKey == resource, ct);
        if (row is null || row.Status != StorageCapacityReservationStatuses.Pending || row.BindingHash != binding
            || row.Bytes != size || row.ProductContextJson != request.ProductContextJson || row.ObjectId == Guid.Empty
            || row.LocalLocator != $"{SanitizeSegment(request.DataClass)}/{row.ObjectId:N}/1/{SanitizeName(request.LogicalName)}") return null;
        async Task<bool> StillReservedAsync()
        {
            var currentPolicy = policies.Evaluate(request.DataClass);
            return options.CurrentValue.UploadCapacityChecksEnabled && currentPolicy.RoutingEnabled && currentPolicy.IsValid
                && StorageUploadCapacityReservations.BindingHash(request, currentPolicy, options.CurrentValue) == binding
                && await db.StorageCapacityReservations.AsNoTracking().AnyAsync(r => r.IdempotencyKey == key
                    && r.ResourceKey == resource && r.Status == StorageCapacityReservationStatuses.Pending
                    && r.BindingHash == binding && r.Bytes == size && r.ProductContextJson == request.ProductContextJson
                    && r.ObjectId == row.ObjectId && r.LocalLocator == row.LocalLocator && r.GroupId == row.GroupId
                    && !db.StorageObjects.Any(o => o.Id == row.ObjectId)
                    && !db.StorageOperations.Any(o => o.IdempotencyKey == GetCommitOperationKey(request.IdempotencyKey))
                    && !db.StorageTombstones.Any(t => t.StorageObjectId == row.ObjectId), ct);
        }
        if (!await StillReservedAsync() || !providers.TryGet("local", out var local)
            || providerHealth is not null && !providerHealth.CanAttempt("local")) return null;
        FileStream? verified = null;
        var handedOff = false;
        try
        {
            if (admittedOriginal)
            {
                if (originalReadStages is null) return null;
                verified = await originalReadStages.VerifyAsync(key, row.LocalLocator, size, sha, async token =>
                {
                    var head = await local.HeadAsync(row.LocalLocator, token);
                    if (head.Status != StorageOperationStatus.Succeeded || head.Locator != row.LocalLocator
                        || head.SizeBytes != size || !string.Equals(head.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                        return new StorageReadResult(StorageOperationStatus.Unavailable, row.LocalLocator, null, 0);
                    return await local.OpenReadAsync(row.LocalLocator, token);
                }, ct);
            }
            else
            {
                var head = await local.HeadAsync(row.LocalLocator, ct);
                if (head.Status != StorageOperationStatus.Succeeded || head.Locator != row.LocalLocator
                    || head.SizeBytes != size || !string.Equals(head.Sha256, sha, StringComparison.OrdinalIgnoreCase)) return null;
                await using var read = await local.OpenReadAsync(row.LocalLocator, ct);
                if (read.Status != StorageOperationStatus.Succeeded || read.Content is null || read.Locator != row.LocalLocator) return null;
                verified = await StageVerifiedReadAsync(row.ObjectId, read.Content,
                    new StorageObjectGeneration { SizeBytes = size, Sha256 = sha }, ct);
            }
            if (verified is null || !await StillReservedAsync()) return null;
            handedOff = true;
            return new(row.ObjectId, 1, request.ContentType, request.LogicalName, size, verified, "local", sha);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or VerifiedReadStagingException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (!handedOff && verified is not null) await verified.DisposeAsync();
        }
    }

    public async Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken)
    {
        var storageObject = await db.StorageObjects.AsNoTracking()
            .Where(item => !item.IsDeleted && !db.StorageTombstones.Any(t => t.StorageObjectId == item.Id))
            .SingleOrDefaultAsync(item => item.Id == objectId, cancellationToken);
        if (storageObject is null || storageObject.IsDeleted) return null;
        var generation = await db.StorageObjectGenerations.AsNoTracking()
            .Include(item => item.Replicas)
            .SingleAsync(item => item.StorageObjectId == objectId && item.Generation == storageObject.CurrentGeneration, cancellationToken);
        var policy = policies.Evaluate(storageObject.DataClass);
        var ranking = policy.ReadProviders.Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
        var replicas = generation.Replicas
            .Where(item => item.Status == StorageReplicaStatuses.Available)
            .OrderBy(item => ranking.GetValueOrDefault(item.ProviderKey, int.MaxValue))
            .ThenByDescending(item => item.LastVerifiedAt)
            .ToArray();
        foreach (var replica in replicas)
        {
            if (!providers.TryGet(replica.ProviderKey, out var provider)) continue;
            if (provider is IStorageConnectedLocatorProvider connected
                && (!connected.TryGetConnectionId(replica.Locator, out var actualConnection) || actualConnection != replica.ConnectionId))
            {
                await MarkReplicaSuspectAsync(replica, generation, "Replica locator does not match its registered storage connection.", cancellationToken);
                continue;
            }
            if (providerHealth is not null && !providerHealth.CanAttempt(replica.ProviderKey)) continue;
            FileStream? verified = null;
            try
            {
                await using var read = await provider.OpenReadAsync(replica.Locator, cancellationToken);
                if (read.Status == StorageOperationStatus.Succeeded && read.Content is not null)
                    verified = await StageVerifiedReadAsync(objectId, read.Content, generation, cancellationToken);
                providerHealth?.Record(replica.ProviderKey,
                    verified is not null ? StorageOperationStatus.Succeeded
                        : read.Status == StorageOperationStatus.Succeeded ? StorageOperationStatus.Unavailable : read.Status,
                    verified is not null ? null : "Replica stream could not be verified.");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (verified is not null) await verified.DisposeAsync();
                verified = null;
                providerHealth?.Record(replica.ProviderKey, StorageOperationStatus.Unavailable, "Replica stream read failed.");
            }
            catch (VerifiedReadStagingException)
            {
                // A full/inaccessible local spool is not evidence that the remote
                // replica is corrupt. Every candidate needs this same spool.
                if (verified is not null) await verified.DisposeAsync();
                return null;
            }
            catch
            {
                if (verified is not null) await verified.DisposeAsync();
                throw;
            }
            if (verified is null)
            {
                await MarkReplicaSuspectAsync(replica, generation, "Replica stream could not be verified.", cancellationToken);
                continue;
            }
            var handedOff = false;
            try
            {
                // Provider I/O is outside a database transaction. Recheck the
                // exact catalog authority before returning any verified bytes.
                var current = await db.StorageReplicas.AsNoTracking().AnyAsync(item => item.Id == replica.Id
                    && item.Status == StorageReplicaStatuses.Available
                    && item.ProviderKey == replica.ProviderKey && item.ConnectionId == replica.ConnectionId
                    && item.Locator == replica.Locator && item.SizeBytes == replica.SizeBytes && item.Sha256 == replica.Sha256
                    && item.StorageObjectGenerationId == generation.Id
                    && item.StorageObjectGeneration.SizeBytes == generation.SizeBytes
                    && item.StorageObjectGeneration.Sha256 == generation.Sha256
                    && item.StorageObjectGeneration.StorageObject.CurrentGeneration == generation.Generation
                    && item.StorageObjectGeneration.StorageObject.Version == storageObject.Version
                    && !item.StorageObjectGeneration.StorageObject.IsDeleted
                    && !db.StorageTombstones.Any(tombstone => tombstone.StorageObjectId == objectId), cancellationToken);
                if (!current)
                {
                    // A replaced/disabled replica does not invalidate a healthy
                    // sibling; a changed or deleted object/generation does.
                    var sameGeneration = await db.StorageObjectGenerations.AsNoTracking().AnyAsync(item => item.Id == generation.Id
                        && item.SizeBytes == generation.SizeBytes && item.Sha256 == generation.Sha256
                        && item.StorageObject.CurrentGeneration == generation.Generation
                        && item.StorageObject.Version == storageObject.Version && !item.StorageObject.IsDeleted
                        && !db.StorageTombstones.Any(t => t.StorageObjectId == objectId), cancellationToken);
                    if (!sameGeneration) return null;
                    continue;
                }
                handedOff = true;
                return new(objectId, generation.Generation, storageObject.ContentType, storageObject.LogicalName,
                    generation.SizeBytes, verified, replica.ProviderKey, generation.Sha256);
            }
            finally
            {
                if (!handedOff) await verified.DisposeAsync();
            }
        }
        return null;
    }

    private async Task<FileStream?> StageVerifiedReadAsync(Guid objectId, Stream source, StorageObjectGeneration generation, CancellationToken ct)
    {
        if (generation.SizeBytes < 0 || generation.Sha256.Length != 64 || !generation.Sha256.All(Uri.IsHexDigit)) return null;
        FileStream spool;
        try { spool = (verifiedReadSpoolFactory ?? CreateVerifiedReadSpool)(objectId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new VerifiedReadStagingException(); }
        byte[]? buffer = null;
        var valid = false;
        try
        {
            buffer = ArrayPool<byte>.Shared.Rent(81920);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long size = 0;
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, 81920), ct)) > 0)
            {
                if (count > generation.SizeBytes - size) return null;
                size += count;
                hash.AppendData(buffer, 0, count);
                try { await spool.WriteAsync(buffer.AsMemory(0, count), ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new VerifiedReadStagingException(); }
            }
            if (size != generation.SizeBytes
                || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), generation.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
            try { await spool.FlushAsync(ct); spool.Position = 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new VerifiedReadStagingException(); }
            valid = true;
            return spool;
        }
        finally
        {
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (!valid) await spool.DisposeAsync();
        }
    }

    private static FileStream CreateVerifiedReadSpool(Guid objectId)
    {
        var settings = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(Path.GetTempPath(), $"pp_verified_read_{objectId:N}_{Guid.NewGuid():N}.tmp"), settings);
    }

    public async Task<bool> DeleteAsync(Guid objectId, int? deletedByUserId, string? reason, CancellationToken cancellationToken)
    {
        var objectDataClass = await db.StorageObjects.AsNoTracking()
            .Where(item => item.Id == objectId)
            .Select(item => item.DataClass)
            .SingleOrDefaultAsync(cancellationToken);
        if (objectDataClass is null) return false;
        var policy = policies.Evaluate(objectDataClass);
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var now = DateTime.UtcNow;
        var updated = await db.StorageObjects
            .Where(item => item.Id == objectId && !item.IsDeleted)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsDeleted, true)
                .SetProperty(item => item.Version, item => item.Version + 1)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (updated == 0)
        {
            var exists = await db.StorageObjects.AsNoTracking().AnyAsync(item => item.Id == objectId, cancellationToken);
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            return exists;
        }

        var trackedObject = db.ChangeTracker.Entries<StorageObject>().SingleOrDefault(e => e.Entity.Id == objectId);
        if (trackedObject is not null) await trackedObject.ReloadAsync(cancellationToken);
        var currentGeneration = await db.StorageObjects.AsNoTracking()
            .Where(item => item.Id == objectId)
            .Select(item => item.CurrentGeneration)
            .SingleAsync(cancellationToken);
        var replicas = await db.StorageReplicas.AsNoTracking()
            .Where(item => item.StorageObjectGeneration.StorageObjectId == objectId
                           && item.Status != StorageReplicaStatuses.Deleted
                           && item.Status != StorageReplicaStatuses.DeletePending)
            .ToArrayAsync(cancellationToken);
        db.StorageTombstones.Add(new StorageTombstone
        {
            StorageObjectId = objectId,
            Generation = currentGeneration,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            LegalHold = policy.LegalHold,
            RetainUntil = policy.RetentionDays is { } retentionDays ? now.AddDays(retentionDays) : null,
            DeletedByUserId = deletedByUserId,
            DeletedAt = now,
        });
        var physicalDeleteAt = policy.RetentionDays is { } days ? now.AddDays(days) : now;
        foreach (var replica in replicas)
        {
            await db.StorageReplicas.Where(item => item.Id == replica.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, StorageReplicaStatuses.DeletePending)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (policy.LegalHold) continue;
            db.StorageOperations.Add(new StorageOperation
            {
                StorageObjectId = objectId,
                StorageObjectGenerationId = replica.StorageObjectGenerationId,
                OperationType = StorageOperationTypes.Delete,
                Status = StorageOperationStatuses.Pending,
                IdempotencyKey = $"delete:{objectId:N}:{replica.Id}:{replica.ProviderKey}",
                ProviderKey = replica.ProviderKey,
                ConnectionId = replica.ConnectionId,
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(replica.Locator), JsonOptions),
                NextAttemptAt = physicalDeleteAt,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task MarkReplicaSuspectAsync(StorageReplica replica, StorageObjectGeneration generation, string error, CancellationToken cancellationToken)
    {
        var limitedError = error.Length <= 2000 ? error : error[..2000];
        var now = DateTime.UtcNow;
        await db.StorageReplicas.Where(item => item.Id == replica.Id && item.Status == replica.Status
            && item.ProviderKey == replica.ProviderKey && item.ConnectionId == replica.ConnectionId
            && item.Locator == replica.Locator && item.SizeBytes == replica.SizeBytes && item.Sha256 == replica.Sha256
            && item.StorageObjectGenerationId == replica.StorageObjectGenerationId
            && item.StorageObjectGeneration.SizeBytes == generation.SizeBytes && item.StorageObjectGeneration.Sha256 == generation.Sha256
            && !item.StorageObjectGeneration.StorageObject.IsDeleted
            && item.StorageObjectGeneration.StorageObject.CurrentGeneration == item.StorageObjectGeneration.Generation
            && !db.StorageTombstones.Any(tombstone => tombstone.StorageObjectId == item.StorageObjectGeneration.StorageObjectId))
            .ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, StorageReplicaStatuses.Suspect)
            .SetProperty(item => item.LastError, limitedError)
            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private string BuildConnectedLocator(string providerKey, string relativeLocator)
    {
        var connectionId = options.CurrentValue.ProviderConnections.GetValueOrDefault(providerKey);
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new InvalidOperationException($"Storage provider '{providerKey}' has no configured connection id.");
        return $"{connectionId.Trim()}/{relativeLocator}";
    }

    private static string BuildOperationKey(string idempotencyKey, string suffix)
    {
        var value = $"write:{idempotencyKey}:{suffix}";
        return value.Length <= 160 ? value : $"write:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}:{suffix}"[..160];
    }

    public static string GetCommitOperationKey(string idempotencyKey) => BuildOperationKey(idempotencyKey.Trim(), "commit");
    public static string GetCapacityReservationKey(string idempotencyKey) => BuildOperationKey(idempotencyKey.Trim(), "capacity");

    private static string SanitizeName(string name)
    {
        var safe = string.Concat(Path.GetFileName(name.Trim()).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(safe) ? "file.bin" : safe[..Math.Min(safe.Length, 180)];
    }

    private static string SanitizeSegment(string value)
    {
        var safe = new string(value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "data" : safe[..Math.Min(safe.Length, 64)];
    }

    private static void ValidateRequest(StorageObjectWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DataClass) || string.IsNullOrWhiteSpace(request.OwnerType)
            || string.IsNullOrWhiteSpace(request.OwnerId) || string.IsNullOrWhiteSpace(request.LogicalName)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Data class, owner, logical name and idempotency key are required.");
        if (!request.Content.CanRead) throw new ArgumentException("Content stream must be readable.", nameof(request));
    }

    private static string NormalizeContentType(string? contentType)
        => string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();
}
