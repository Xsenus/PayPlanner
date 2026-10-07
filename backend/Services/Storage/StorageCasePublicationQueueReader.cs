using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageCasePublicationOperation(Guid Id, Guid StorageObjectId, string FileName,
    string Status, int AttemptCount, DateTime? NextAttemptAt, DateTime? LeaseExpiresAt,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? CompletedAt, bool CanRetry, bool IsRetired = false);

public sealed record StorageCasePublicationPage(IReadOnlyList<StorageCasePublicationOperation> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

public sealed class StorageCasePublicationQueueReader(PaymentContext db)
{
    private sealed record Cursor(DateTime CreatedAt, Guid Id, string? Status);

    public async Task<StorageCasePublicationPage> ReadAsync(string? status, string? after, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 200) throw new ArgumentException("Invalid page size.", nameof(limit));
        if (status is not null && status is not (StorageOperationStatuses.Pending or StorageOperationStatuses.Running
            or StorageOperationStatuses.Unknown or StorageOperationStatuses.Succeeded or StorageOperationStatuses.Failed
            or StorageOperationStatuses.DeadLetter)) throw new ArgumentException("Invalid status.", nameof(status));
        Cursor? pivot = null;
        if (after is not null)
        {
            try
            {
                if (after.Length is < 1 or > 512) throw new FormatException();
                var json = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(after));
                pivot = JsonSerializer.Deserialize<Cursor>(json);
                if (pivot is null || pivot.Id == Guid.Empty || pivot.Status != status
                    || pivot.CreatedAt.Kind != DateTimeKind.Utc || pivot.CreatedAt == DateTime.MinValue)
                    throw new FormatException();
            }
            catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
            { throw new ArgumentException("Invalid cursor.", nameof(after)); }
        }
        var query = db.StorageOperations.AsNoTracking()
            .Where(item => item.OperationType == StorageOperationTypes.PublishCaseDocument);
        if (status is not null) query = query.Where(item => item.Status == status);
        if (pivot is not null) query = query.Where(item => item.CreatedAt < pivot.CreatedAt
            || (item.CreatedAt == pivot.CreatedAt && item.Id.CompareTo(pivot.Id) < 0));
        var now = DateTime.UtcNow;
        // One bounded metadata query. Do not expose intents, locators, worker IDs
        // or raw provider errors in this operator list.
        var rows = await query.TagWith("case-publication-admin-page")
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(limit + 1)
            .Select(item => new
            {
                item.Id,
                item.StorageObjectId,
                FileName = item.StorageObject.LogicalName,
                item.Status,
                item.AttemptCount,
                item.NextAttemptAt,
                item.LeaseOwner,
                item.LeaseExpiresAt,
                item.CreatedAt,
                item.UpdatedAt,
                item.CompletedAt,
                Retired = db.CaseDocumentUploadRetirements.Any(retired => retired.StorageObjectId == item.StorageObjectId),
            }).ToArrayAsync(ct);
        var items = rows.Take(limit).Select(item => new StorageCasePublicationOperation(item.Id, item.StorageObjectId,
            item.FileName, item.Status, item.AttemptCount, Utc(item.NextAttemptAt), Utc(item.LeaseExpiresAt),
            Utc(item.CreatedAt)!.Value, Utc(item.UpdatedAt)!.Value, Utc(item.CompletedAt),
            item.Status is StorageOperationStatuses.Pending or StorageOperationStatuses.Failed
                or StorageOperationStatuses.Unknown or StorageOperationStatuses.DeadLetter
            && !item.Retired && ((item.LeaseOwner is null && item.LeaseExpiresAt is null)
                || item.LeaseExpiresAt <= now), item.Retired)).ToArray();
        var next = rows.Length > limit
            ? WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new Cursor(items[^1].CreatedAt, items[^1].Id, status)))) : null;
        return new StorageCasePublicationPage(items, next);
    }

    private static DateTime? Utc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
