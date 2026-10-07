using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record TaskFileUploadState(Guid UploadKey, string FileName, string Status, int? FileId, string Message);
public sealed record TaskFileUploadPage(IReadOnlyList<TaskFileUploadState> Items, Guid? NextCursor);

public interface ITaskFileUploadStatusReader
{
    // Null means that the current actor may no longer collaborate on this task.
    Task<IReadOnlyList<TaskFileUploadState>?> ListAsync(int taskId, int actorId, CancellationToken ct,
        IReadOnlyCollection<Guid>? uploadKeys = null, bool pendingOnly = false, int page = 1);
    Task<TaskFileUploadPage?> ListPageAsync(int taskId, int actorId, CancellationToken ct, Guid? after = null);
}

public sealed class TaskFileUploadStatusReader(PaymentContext db,
    ITaskFilePublicationAuthorizationService authorization,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    StorageWorkerAvailability? workerAvailability = null) : ITaskFileUploadStatusReader
{
    public async Task<IReadOnlyList<TaskFileUploadState>?> ListAsync(int taskId, int actorId, CancellationToken ct,
        IReadOnlyCollection<Guid>? uploadKeys = null, bool pendingOnly = false, int page = 1)
        => (await ReadAsync(taskId, actorId, ct, uploadKeys, pendingOnly, page, null))?.Items;

    public Task<TaskFileUploadPage?> ListPageAsync(int taskId, int actorId, CancellationToken ct, Guid? after = null)
        => ReadAsync(taskId, actorId, ct, null, true, 1, after);

    private async Task<TaskFileUploadPage?> ReadAsync(int taskId, int actorId, CancellationToken ct,
        IReadOnlyCollection<Guid>? uploadKeys, bool pendingOnly, int page, Guid? after)
    {
        if (page is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(page));
        if (after == Guid.Empty) throw new ArgumentException("Invalid cursor.", nameof(after));
        if (uploadKeys is { Count: > 50 } || uploadKeys?.Contains(Guid.Empty) == true) throw new ArgumentException("Invalid upload keys.", nameof(uploadKeys));
        return await DatabaseConnectionOperation.RunAsync(db,
            () => ReadCoreAsync(taskId, actorId, ct, uploadKeys, pendingOnly, page, after), ct);
    }

    private async Task<TaskFileUploadPage?> ReadCoreAsync(int taskId, int actorId, CancellationToken ct,
        IReadOnlyCollection<Guid>? uploadKeys, bool pendingOnly, int page, Guid? after)
    {
        if (!await authorization.CanPublishAsync(actorId, taskId, ct)) return null;
        var prefix = $"write:task-file:{taskId}:{actorId}:";
        // Bound the result to this actor and task before materializing payloads.
        // Never return raw payloads, physical locators, provider errors or other actors' intents.
        var query = db.StorageOperations.AsNoTracking()
            .Where(o => o.OperationType == "commit" && o.IdempotencyKey.StartsWith(prefix)
                && o.StorageObject.DataClass == "task-file" && o.StorageObject.OwnerType == "task-file"
                && !o.StorageObject.IsDeleted);
        var scopedQuery = query;
        if (after.HasValue)
        {
            // Resolve only inside this actor/task scope, before pending filtering: the pivot may have published.
            var pivot = await query.Where(o => o.Id == after.Value).Select(o => new { o.Id, o.CreatedAt })
                .SingleOrDefaultAsync(ct) ?? throw new ArgumentException("Invalid cursor.", nameof(after));
            query = query.Where(o => o.CreatedAt < pivot.CreatedAt
                || (o.CreatedAt == pivot.CreatedAt && o.Id.CompareTo(pivot.Id) < 0));
        }
        if (uploadKeys is not null)
        {
            var exactKeys = uploadKeys.Distinct().Select(k => prefix + k.ToString("N") + ":commit").ToArray();
            query = query.Where(o => exactKeys.Contains(o.IdempotencyKey));
        }
        if (pendingOnly) query = query.Where(o => o.StorageObject.OwnerId.StartsWith($"upload:{taskId}:{actorId}:"));
        var commits = await query.OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip((page - 1) * 50).Take(51)
            .Select(o => new
            {
                o.Id,
                o.StorageObjectId,
                o.StorageObjectGenerationId,
                o.IdempotencyKey,
                o.PayloadJson,
                Owner = o.StorageObject.OwnerId,
                IsCurrentGeneration = o.StorageObjectGeneration != null
                    && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration
            }).ToArrayAsync(ct);
        // Advance using the raw bounded window, not the number of valid payloads.
        var nextCursor = commits.Length > 50 ? commits[49].Id : (Guid?)null;
        if (commits.Length > 50) commits = commits[..50];
        // One exact-key indexed batch replaces a correlated scan for every file.
        var publicationKeys = commits.Select(c => TaskFilePublicationProcessor.Key(c.Id)).ToArray();
        var queues = publicationKeys.Length == 0 ? null : await db.StorageOperations.AsNoTracking()
            .Where(q => q.OperationType == StorageOperationTypes.PublishTaskFile && publicationKeys.Contains(q.IdempotencyKey))
            .Select(q => new { q.IdempotencyKey, q.StorageObjectId, q.StorageObjectGenerationId, q.Status })
            .ToDictionaryAsync(q => q.IdempotencyKey, ct);
        // Publication can commit between the page and batch reads. Refresh only
        // pending receipts whose queue became terminal, before judging their result.
        var completedDuringRead = commits.Where(c => c.Owner.StartsWith($"upload:{taskId}:{actorId}:", StringComparison.Ordinal)
            && queues is not null && queues.TryGetValue(TaskFilePublicationProcessor.Key(c.Id), out var q)
            && q.StorageObjectId == c.StorageObjectId && q.StorageObjectGenerationId == c.StorageObjectGenerationId
            && (q.Status == StorageOperationStatuses.Succeeded || q.Status == StorageOperationStatuses.DeadLetter)).Select(c => c.Id).ToArray();
        if (completedDuringRead.Length > 0)
        {
            var refreshed = await scopedQuery.Where(o => completedDuringRead.Contains(o.Id))
                .Select(o => new
                {
                    o.Id,
                    o.StorageObjectId,
                    o.StorageObjectGenerationId,
                    o.IdempotencyKey,
                    o.PayloadJson,
                    Owner = o.StorageObject.OwnerId,
                    IsCurrentGeneration = o.StorageObjectGeneration != null
                        && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration
                }).ToDictionaryAsync(c => c.Id, ct);
            commits = commits.Select(c => refreshed.GetValueOrDefault(c.Id) ?? c).ToArray();
        }
        var parsed = new List<(Guid Key, string Name, int? FileId, int? CaseId, string? Queue)>();
        foreach (var commit in commits)
        {
            if (commit.IdempotencyKey.Length != prefix.Length + 39
                || !commit.IdempotencyKey.EndsWith(":commit", StringComparison.Ordinal)
                || !Guid.TryParseExact(commit.IdempotencyKey[prefix.Length..^7], "N", out var key) || key == Guid.Empty) continue;
            try
            {
                using var json = JsonDocument.Parse(commit.PayloadJson ?? string.Empty);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !Number(root, "taskId", out var intentTask) || intentTask != taskId
                    || !Number(root, "actorId", out var intentActor) || intentActor != actorId
                    || !root.TryGetProperty("fileName", out var name) || name.ValueKind != JsonValueKind.String) continue;
                var fileName = name.GetString();
                if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 260 || fileName.Contains('/') || fileName.Contains('\\')) continue;
                if (!OptionalNumber(root, "fileId", out var fileId) || !OptionalNumber(root, "caseId", out var caseId)) continue;
                var expectedOwner = fileId.HasValue ? fileId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"upload:{taskId}:{actorId}:{key:N}";
                if (!commit.IsCurrentGeneration || commit.Owner != expectedOwner) continue;
                var queueStatus = queues is not null && queues.TryGetValue(TaskFilePublicationProcessor.Key(commit.Id), out var queue)
                    && queue.StorageObjectId == commit.StorageObjectId
                    && queue.StorageObjectGenerationId == commit.StorageObjectGenerationId ? queue.Status : null;
                parsed.Add((key, fileName, fileId, caseId, queueStatus));
            }
            catch (JsonException) { /* Corrupt internal payloads are not exposed as user-controlled diagnostics. */ }
        }
        var fileIds = parsed.Where(p => p.FileId.HasValue).Select(p => p.FileId!.Value).Distinct().ToArray();
        var existing = fileIds.Length == 0 ? new HashSet<int>() : (await db.TaskJournalFiles.AsNoTracking()
            .Where(f => f.TaskJournalItemId == taskId && f.UploadedByUserId == actorId && fileIds.Contains(f.Id))
            .Select(f => f.Id).ToArrayAsync(ct)).ToHashSet();
        var task = await db.TaskJournalItems.AsNoTracking().Where(t => t.Id == taskId)
            .Select(t => new { t.ClientCaseId }).SingleOrDefaultAsync(ct);
        if (task is null) return null;
        var currentCase = task.ClientCaseId;
        var configuration = options.CurrentValue;
        var automatic = workerAvailability?.IsRegistered == true && configuration.WorkerEnabled
            && configuration.Enabled && configuration.AutoTaskFilePublicationEnabled
            && configuration.ProductWriteEnabledClasses.Contains("task-file", StringComparer.Ordinal)
            && (!currentCase.HasValue || configuration.ProductWriteEnabledClasses.Contains("case-document", StringComparer.Ordinal));
        // Recheck after reading metadata: removal of access must not disclose pending names.
        if (!await authorization.CanPublishAsync(actorId, taskId, ct)) return null;
        var states = parsed.Select(p =>
        {
            var uploaded = p.FileId.HasValue && existing.Contains(p.FileId.Value);
            var failed = !uploaded && (p.FileId.HasValue || p.CaseId != currentCase
                || p.Queue == StorageOperationStatuses.DeadLetter || p.Queue == StorageOperationStatuses.Succeeded);
            var status = uploaded ? "uploaded" : failed ? "failed" : p.Queue is null ? "manualRetry"
                : !automatic ? "paused" : p.Queue == StorageOperationStatuses.Failed ? "retrying" : "waiting";
            var message = status switch
            {
                "uploaded" => "Файл добавлен к задаче.",
                "failed" => "Загрузка не завершена. Обновите карточку и уточните причину у администратора перед повторной отправкой.",
                "manualRetry" => "Файл ещё не прикреплён. Автоматическое завершение не назначено; повторите отправку того же файла позже.",
                "paused" => "Автоматическое завершение загрузки приостановлено. Файл ещё не прикреплён к задаче. Дождитесь возобновления обработки; отправлять файл заново не нужно.",
                "retrying" => "Не удалось завершить загрузку. Система повторит попытку автоматически; отправлять файл заново не нужно.",
                _ => "Файл ещё не прикреплён. Система завершит загрузку после подготовки обязательных копий; отправлять файл заново не нужно."
            };
            return new TaskFileUploadState(p.Key, p.Name, status, uploaded ? p.FileId : null, message);
        }).ToArray();
        return new TaskFileUploadPage(states, nextCursor);
    }

    private static bool Number(JsonElement root, string key, out int value)
    {
        value = 0;
        return root.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }
    private static bool OptionalNumber(JsonElement root, string key, out int? value)
    {
        value = null;
        if (!root.TryGetProperty(key, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number) || number <= 0) return false;
        value = number;
        return true;
    }
}
