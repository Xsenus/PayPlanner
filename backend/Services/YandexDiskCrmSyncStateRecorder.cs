using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class YandexDiskCrmSyncStateRecorder
{
    public static async Task RecordSuccessAsync(
        PaymentContext db,
        string documentKind,
        string objectType,
        int objectId,
        string? fileName,
        string yandexDiskPath,
        int? parentObjectId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var state = await db.YandexDiskCrmSyncStates.FirstOrDefaultAsync(item =>
            item.DocumentKind == documentKind
            && item.ObjectType == objectType
            && item.ObjectId == objectId,
            ct);

        if (state is null)
        {
            state = new YandexDiskCrmSyncState
            {
                DocumentKind = documentKind,
                ObjectType = objectType,
                ObjectId = objectId,
                CreatedAt = now,
            };
            db.YandexDiskCrmSyncStates.Add(state);
        }

        state.ParentObjectId = parentObjectId;
        state.FileName = Normalize(fileName, maxLength: 512);
        state.YandexDiskPath = Normalize(yandexDiskPath, maxLength: 1024);
        state.Status = YandexDiskCrmSyncStatus.Success;
        state.LastError = null;
        state.AttemptCount += 1;
        state.LastSyncedAt = now;
        state.UpdatedAt = now;
    }

    public static async Task RecordFailureAsync(
        PaymentContext db,
        string documentKind,
        string objectType,
        int objectId,
        string? fileName,
        int? parentObjectId,
        Exception exception,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var state = await db.YandexDiskCrmSyncStates.FirstOrDefaultAsync(item =>
            item.DocumentKind == documentKind
            && item.ObjectType == objectType
            && item.ObjectId == objectId,
            ct);

        if (state is null)
        {
            state = new YandexDiskCrmSyncState
            {
                DocumentKind = documentKind,
                ObjectType = objectType,
                ObjectId = objectId,
                CreatedAt = now,
            };
            db.YandexDiskCrmSyncStates.Add(state);
        }

        state.ParentObjectId = parentObjectId;
        state.FileName = Normalize(fileName, maxLength: 512);
        state.Status = YandexDiskCrmSyncStatus.Failed;
        state.LastError = Normalize(exception.Message, maxLength: 2000);
        state.AttemptCount += 1;
        state.UpdatedAt = now;
    }

    private static string? Normalize(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength];
    }
}
