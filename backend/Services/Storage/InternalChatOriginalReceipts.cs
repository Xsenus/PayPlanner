using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed partial class InternalChatOriginalWriter
{
    private sealed record OriginalReceipt(string IdempotencyKey, string? PayloadJson, Guid StorageObjectId,
        string Status, string OperationType, long? Generation, bool Belongs, string DataClass, string OwnerType,
        string OwnerId, string LogicalName, string ContentType, long CurrentGeneration, bool Deleted,
        int? CreatedByUserId, long? SizeBytes, string? Sha256, bool LocalVerified);

    // One bounded catalog query for the whole frozen aggregate. Keep the object
    // write locks at the transaction boundary; this read does not replace them.
    private Task<Dictionary<string, OriginalReceipt>> ReadOriginalReceiptsAsync(
        InternalChatSendSnapshot snapshot, CancellationToken ct, int? onlyIndex = null)
    {
        var indices = onlyIndex is { } index ? new[] { index } : Enumerable.Range(0, snapshot.Files.Length).ToArray();
        var keys = indices.Select(index => StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, index))).ToArray();
        return db.StorageOperations.AsNoTracking().Where(operation => keys.Contains(operation.IdempotencyKey))
            .Select(operation => new OriginalReceipt(operation.IdempotencyKey, operation.PayloadJson,
                operation.StorageObjectId, operation.Status, operation.OperationType,
                operation.StorageObjectGeneration == null ? null : operation.StorageObjectGeneration.Generation,
                operation.StorageObjectGeneration != null && operation.StorageObjectGeneration.StorageObjectId == operation.StorageObjectId,
                operation.StorageObject.DataClass, operation.StorageObject.OwnerType, operation.StorageObject.OwnerId,
                operation.StorageObject.LogicalName, operation.StorageObject.ContentType, operation.StorageObject.CurrentGeneration,
                operation.StorageObject.IsDeleted || operation.StorageObject.Tombstones.Any(),
                operation.StorageObjectGeneration == null ? null : operation.StorageObjectGeneration.CreatedByUserId,
                operation.StorageObjectGeneration == null ? null : operation.StorageObjectGeneration.SizeBytes,
                operation.StorageObjectGeneration == null ? null : operation.StorageObjectGeneration.Sha256,
                operation.StorageObjectGeneration != null && operation.StorageObjectGeneration.Replicas.Any(replica =>
                    replica.ProviderKey == "local" && (replica.Status == StorageReplicaStatuses.Available || replica.Status == StorageReplicaStatuses.Pending)
                    && replica.LastVerifiedAt != null && replica.SizeBytes == operation.StorageObjectGeneration.SizeBytes
                    && replica.Sha256 == operation.StorageObjectGeneration.Sha256)))
            .ToDictionaryAsync(receipt => receipt.IdempotencyKey, ct);
    }

    private static bool IsOriginalReceipt(OriginalReceipt receipt, InternalChatSendAttempt attempt,
        InternalChatSendSnapshot snapshot, int index, Guid objectId, long generation, string? publishedOwner = null)
    {
        var file = snapshot.Files[index];
        return receipt.IdempotencyKey == StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, index))
            && receipt.OperationType == "commit" && receipt.Status == StorageOperationStatuses.Succeeded
            && receipt.PayloadJson == Context(attempt, index) && receipt.StorageObjectId == objectId
            && !receipt.Deleted && receipt.DataClass == "chat-attachment" && receipt.OwnerType == "chat-attachment"
            && receipt.OwnerId == (publishedOwner ?? Owner(attempt.Id, index)) && receipt.LogicalName == file.FileName
            && receipt.ContentType == file.ContentType && receipt.CurrentGeneration == generation
            && receipt.Generation == generation && receipt.Belongs && receipt.CreatedByUserId == snapshot.ActorId
            && receipt.SizeBytes == file.SizeBytes && receipt.Sha256 == file.Sha256 && receipt.LocalVerified;
    }

    private async Task<bool> VerifyOriginalAsync(InternalChatSendAttempt attempt, InternalChatSendSnapshot snapshot,
        int index, Guid objectId, long generation, CancellationToken ct)
    {
        var receipts = await ReadOriginalReceiptsAsync(snapshot, ct, index);
        return receipts.TryGetValue(StorageObjectService.GetCommitOperationKey(WriteKey(snapshot, index)), out var receipt)
            && IsOriginalReceipt(receipt, attempt, snapshot, index, objectId, generation);
    }
}
