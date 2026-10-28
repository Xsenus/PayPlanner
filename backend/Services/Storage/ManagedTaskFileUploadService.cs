using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record ManagedTaskFileUploadRequest(int TaskId, int ActorId, string ActorName, Guid UploadKey,
    IFormFile File, string FileName, string? DocumentKey, Func<CancellationToken, Task<bool>>? AuthorizePublication = null);
public sealed record ManagedTaskFileUploadResult(string Status, int? FileId = null, bool IsReplay = false, int? CaseFileId = null);

// The endpoint must authorize the current task and upload permission on EVERY
// request, including retries. An outbox id or an upload key never grants access.
public interface IManagedTaskFileUploadService
{
    bool Enabled { get; }
    Task<ManagedTaskFileUploadResult> UploadAsync(ManagedTaskFileUploadRequest request, CancellationToken ct);
    // Internal worker receipt only: no publication, byte reads or user access grant.
    Task<ManagedTaskFileUploadResult> VerifyPublishedAsync(Guid operationId, CancellationToken ct);
    Task<ManagedTaskFileUploadResult> ResumeAsync(Guid operationId,
        Func<int, int, CancellationToken, Task<bool>> authorizePublication, CancellationToken ct);
}

public sealed class ManagedTaskFileUploadService(PaymentContext db, IStorageObjectService storage,
    IProductStorageBindingService publisher, IWebHostEnvironment environment, IConfiguration configuration,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    IStorageStageCapacityService? stageCapacity = null) : IManagedTaskFileUploadService
{
    private const long MaxSize = 25L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public bool Enabled => options.CurrentValue.Enabled
        && options.CurrentValue.ProductWriteEnabledClasses.Contains("task-file", StringComparer.Ordinal);

    // Resume from durable bytes, never from a browser's original File instance.
    // The caller must resolve the actor's CURRENT permissions, both before reading
    // and inside UploadAsync's publication transaction. An intent is not authority.
    public async Task<ManagedTaskFileUploadResult> ResumeAsync(Guid operationId,
        Func<int, int, CancellationToken, Task<bool>> authorizePublication, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorizePublication);
        return await ResumeCoreAsync(operationId, authorizePublication, false, ct);
    }

    public Task<ManagedTaskFileUploadResult> VerifyPublishedAsync(Guid operationId, CancellationToken ct)
        => ResumeCoreAsync(operationId, (_, _, _) => Task.FromResult(false), true, ct);

    private async Task<ManagedTaskFileUploadResult> ResumeCoreAsync(Guid operationId,
        Func<int, int, CancellationToken, Task<bool>> authorizePublication, bool receiptOnly, CancellationToken ct)
    {
        if (!Enabled) return new("disabled");
        var operation = await db.StorageOperations.AsNoTracking()
            .Where(o => o.Id == operationId && o.OperationType == "commit")
            .Select(o => new { o.StorageObjectId, o.IdempotencyKey, o.PayloadJson }).SingleOrDefaultAsync(ct);
        var intent = ParseIntent(operation?.PayloadJson);
        if (operation is null || intent is null || intent.TaskId <= 0 || intent.ActorId <= 0
            || intent.SizeBytes <= 0 || intent.SizeBytes > MaxSize || intent.Version <= 0 || intent.CaseVersion <= 0
            || intent.ActorName is null || intent.ActorName.Length > 256
            || string.IsNullOrWhiteSpace(intent.FileName) || intent.FileName.Length > 260
            || intent.FileName.Contains('/') || intent.FileName.Contains('\\')
            || string.IsNullOrWhiteSpace(intent.ContentType) || intent.ContentType.Length > 200
            || string.IsNullOrWhiteSpace(intent.DocumentKey) || intent.DocumentKey.Length > 64
            || intent.RequestedDocumentKey is { Length: > 64 }
            || intent.Sha256 is null || intent.Sha256.Length != 64 || !intent.Sha256.All(Uri.IsHexDigit)) return new("invalid");
        var prefix = $"write:task-file:{intent.TaskId}:{intent.ActorId}:";
        var operationKey = operation.IdempotencyKey;
        if (operationKey.Length != prefix.Length + 32 + 7
            || !operationKey.StartsWith(prefix, StringComparison.Ordinal) || !operationKey.EndsWith(":commit", StringComparison.Ordinal)
            || !Guid.TryParseExact(operationKey[prefix.Length..^7], "N", out var uploadKey) || uploadKey == Guid.Empty)
            return new("conflict");
        var key = $"task-file:{intent.TaskId}:{intent.ActorId}:{uploadKey:N}";
        if (operationKey != StorageObjectService.GetCommitOperationKey(key)) return new("conflict");
        if (receiptOnly)
            return intent.FileId.HasValue ? await PublishedResultAsync(intent, operation.StorageObjectId, key, ct) : new("pending");
        if (!await authorizePublication(intent.ActorId, intent.TaskId, ct)) return new("forbidden");
        if (intent.FileId.HasValue) return await PublishedResultAsync(intent, operation.StorageObjectId, key, ct);
        if (!await db.TaskJournalItems.AsNoTracking().AnyAsync(t => t.Id == intent.TaskId && t.ClientCaseId == intent.CaseId, ct))
            return new("conflict");
        var pendingOwner = $"upload:{intent.TaskId}:{intent.ActorId}:{uploadKey:N}";
        if (!await db.StorageObjects.AsNoTracking().AnyAsync(o => o.Id == operation.StorageObjectId && !o.IsDeleted
                && o.DataClass == "task-file" && o.OwnerType == "task-file" && o.OwnerId == pendingOwner
                && o.LogicalName == intent.FileName && o.ContentType == intent.ContentType, ct))
        {
            // A competing publisher may already have transferred ownership.
            // Accept only this exact completed intent, not an unrelated object.
            var refreshed = await FindIntentAsync(operationKey, ct);
            var published = ParseIntent(refreshed?.Json);
            if (refreshed?.ObjectId == operation.StorageObjectId && published?.FileId is not null
                && (published with { FileId = null, CaseFileId = null }) == (intent with { FileId = null, CaseFileId = null }))
                return await PublishedResultAsync(published, operation.StorageObjectId, key, ct);
            return new("conflict");
        }
        await using var read = await storage.OpenReadAsync(operation.StorageObjectId, ct);
        if (read is null)
        {
            // Publication may change catalog authority while the verified read is
            // in flight. A missing read is not success: accept only the exact
            // completed intent and verify its published bindings/copies again.
            var refreshed = await FindIntentAsync(operationKey, ct);
            var published = ParseIntent(refreshed?.Json);
            if (refreshed?.ObjectId != operation.StorageObjectId || published?.FileId is null
                || (published with { FileId = null, CaseFileId = null }) != (intent with { FileId = null, CaseFileId = null }))
                return new("unavailable");
            if (!await authorizePublication(intent.ActorId, intent.TaskId, ct)) return new("forbidden");
            return await PublishedResultAsync(published, operation.StorageObjectId, key, ct);
        }
        if (read.SizeBytes != intent.SizeBytes || !string.Equals(read.Sha256, intent.Sha256, StringComparison.OrdinalIgnoreCase))
            return new("conflict");
        return await UploadAsync(new(intent.TaskId, intent.ActorId, intent.ActorName, uploadKey,
            new StoredUploadFile(read.Content, intent), intent.FileName, intent.RequestedDocumentKey,
            token => authorizePublication(intent.ActorId, intent.TaskId, token)), ct);
    }

    public async Task<ManagedTaskFileUploadResult> UploadAsync(ManagedTaskFileUploadRequest request, CancellationToken ct)
    {
        if (!Enabled) return new("disabled");
        if (request.TaskId <= 0 || request.ActorId <= 0 || request.UploadKey == Guid.Empty
            || string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 260
            || request.FileName.Contains('/') || request.FileName.Contains('\\')
            || request.File.Length <= 0 || request.File.Length > MaxSize
            || request.ActorName.Length > 256 || request.DocumentKey is { Length: > 64 }) return new("invalid");
        return await DatabaseConnectionOperation.RunAsync(db, () => UploadCoreAsync(request, ct), ct);
    }

    private async Task<ManagedTaskFileUploadResult> UploadCoreAsync(ManagedTaskFileUploadRequest request, CancellationToken ct)
    {
        var task = await db.TaskJournalItems.AsNoTracking().Where(t => t.Id == request.TaskId)
            .Select(t => new { t.ClientCaseId, t.UpdatedAt }).SingleOrDefaultAsync(ct);
        if (task is null) return new("notFound");
        if (task.ClientCaseId.HasValue && !options.CurrentValue.ProductWriteEnabledClasses.Contains("case-document", StringComparer.Ordinal))
            return new("disabled");
        var type = string.IsNullOrWhiteSpace(request.File.ContentType) ? "application/octet-stream" : request.File.ContentType.Trim();
        if (type.Length > 200) return new("invalid");
        var key = $"task-file:{request.TaskId}:{request.ActorId}:{request.UploadKey:N}";
        var operationKey = StorageObjectService.GetCommitOperationKey(key);
        var pendingOwner = $"upload:{request.TaskId}:{request.ActorId}:{request.UploadKey:N}";
        var stageRoot = Path.Combine(LocalStoragePaths.ResolveRoot(environment, configuration), ".task-uploads");
        Directory.CreateDirectory(stageRoot);
        var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
        StorageStageCapacityTicket? stageTicket = null;
        var ownsStage = false;
        var compatibilityTickets = new Dictionary<string, StorageStageCapacityTicket>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var ownedCompatibilityPaths = new HashSet<string>(compatibilityTickets.Comparer);
        try
        {
            if (options.CurrentValue.UploadCapacityChecksEnabled)
            {
                if (stageCapacity is null) return new("unavailable");
                var (ticket, _) = await stageCapacity.ReserveAsync(key, stage, request.File.Length, ct);
                if (ticket is null) return new("unavailable");
                stageTicket = ticket;
            }
            string sha;
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                ownsStage = true;
                try { sha = await UploadValidationHelper.CopyValidatedAndHashAsync(request.File, output, MaxSize, ct); }
                catch (InvalidDataException) { return new("invalid"); }
            }
            if (stageTicket is not null) await stageCapacity!.SettlePhysicalAsync(stageTicket, ct);
            var existing = await FindIntentAsync(operationKey, ct);
            UploadIntent? intent;
            if (existing is null)
            {
                var reservedJson = await StorageUploadCapacityReservations.ReadProductContextAsync(db,
                    StorageObjectService.GetCapacityReservationKey(key), ct);
                if (reservedJson is not null)
                {
                    intent = ParseIntent(reservedJson);
                    if (!Matches(intent, request, type, sha) || intent!.FileId.HasValue) return new("conflict");
                }
                else
                {
                    var documentKey = request.DocumentKey ?? Guid.NewGuid().ToString("N");
                    var latest = await GetTaskVersionAsync(request.TaskId, documentKey, ct);
                    if (request.DocumentKey is not null && latest == 0) return new("invalid");
                    if (latest == int.MaxValue) return new("conflict");
                    var caseVersion = task.ClientCaseId.HasValue
                        ? await GetCaseVersionAsync(task.ClientCaseId.Value, CaseKey(request.TaskId, documentKey), ct) : 0;
                    if (caseVersion == int.MaxValue) return new("conflict");
                    intent = new(request.TaskId, request.ActorId, request.ActorName, task.ClientCaseId, task.UpdatedAt,
                        request.DocumentKey, documentKey, latest + 1, caseVersion + 1, request.FileName, type, request.File.Length, sha, DateTime.UtcNow);
                }
            }
            else
            {
                intent = ParseIntent(existing.Json);
                if (!Matches(intent, request, type, sha)) return new("conflict");
                if (intent!.FileId.HasValue) return await PublishedResultAsync(intent, existing.ObjectId, key, ct);
            }

            if (options.CurrentValue.UploadCapacityChecksEnabled)
            {
                var taskName = StoredName(intent!.DocumentKey, intent.Version, request.UploadKey, request.FileName);
                var taskPath = Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration),
                    "task-files", intent.TaskId.ToString(CultureInfo.InvariantCulture), taskName);
                var targets = new List<string> { taskPath };
                if (intent.CaseId.HasValue)
                {
                    var caseStorage = new LocalCaseDocumentStorage(environment, configuration);
                    var caseKey = CaseKey(intent.TaskId, intent.DocumentKey);
                    var caseName = StoredName(caseKey, intent.CaseVersion, request.UploadKey, request.FileName);
                    targets.Add(caseStorage.ResolvePath(caseStorage.BuildRelativePath(intent.CaseId.Value, caseName)));
                }
                foreach (var target in targets)
                {
                    if (File.Exists(target)) continue;
                    var parent = Path.GetDirectoryName(target)!;
                    Directory.CreateDirectory(parent);
                    var temporary = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
                    var (ticket, _) = await stageCapacity!.ReserveCompatibilityAsync(
                        key + ":compat:" + compatibilityTickets.Count, temporary, request.File.Length, "files", ct);
                    if (ticket is null) return new("unavailable");
                    compatibilityTickets.Add(target, ticket);
                }
            }

            await using var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            StorageObjectWriteResult taskWrite;
            try
            {
                taskWrite = await storage.WriteAsync(new("task-file", "task-file", pendingOwner,
                    request.FileName, type, input, key, request.ActorId, JsonSerializer.Serialize(intent, JsonOptions), request.File.Length, sha), ct);
            }
            catch (InvalidOperationException)
            {
                // Another request may have published this exact intent while we
                // were staging. Ownership is transferred only during publication.
                var refreshed = await FindIntentAsync(operationKey, ct);
                var published = ParseIntent(refreshed?.Json);
                if (refreshed is not null && Matches(published, request, type, sha) && published!.FileId.HasValue)
                    return await PublishedResultAsync(published, refreshed.ObjectId, key, ct);
                if (refreshed is not null && published is not null && !Matches(published, request, type, sha))
                    return new("conflict");
                throw;
            }
            if (taskWrite.ObjectId == Guid.Empty) return new("unavailable");
            existing = await FindIntentAsync(operationKey, ct);
            intent = ParseIntent(existing?.Json);
            if (existing is null || existing.ObjectId != taskWrite.ObjectId || !Matches(intent, request, type, sha)) return new("conflict");
            if (intent!.FileId.HasValue) return await PublishedResultAsync(intent, existing.ObjectId, key, ct);
            StorageObjectWriteResult? caseWrite = null;
            if (intent.CaseId.HasValue)
            {
                input.Position = 0;
                try
                {
                    caseWrite = await storage.WriteAsync(new("case-document", "case-document", pendingOwner,
                        request.FileName, type, input, key + ":case", request.ActorId,
                        ExpectedSizeBytes: request.File.Length, ExpectedSha256: sha), ct);
                }
                catch (InvalidOperationException)
                {
                    var refreshed = await FindIntentAsync(operationKey, ct);
                    var published = ParseIntent(refreshed?.Json);
                    if (refreshed is not null && Matches(published, request, type, sha) && published!.FileId.HasValue)
                        return await PublishedResultAsync(published, refreshed.ObjectId, key, ct);
                    if (refreshed is not null && published is not null && !Matches(published, request, type, sha))
                        return new("conflict");
                    throw;
                }
                if (caseWrite.ObjectId == Guid.Empty) return new("unavailable");
            }
            if (taskWrite.Status != "available" || (caseWrite is not null && caseWrite.Status != "available")) return new("pending");

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var committed = false;
            try
            {
                // Lock actor authority in the same order as task deadline and
                // legacy upload transactions before locking the task itself.
                if (db.Database.IsNpgsql())
                {
                    try
                    {
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {intent.ActorId} FOR SHARE NOWAIT", ct);
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {intent.ActorId}) FOR SHARE NOWAIT", ct);
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = (SELECT \"RoleId\" FROM \"Users\" WHERE \"Id\" = {intent.ActorId}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                    }
                    catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
                    {
                        return new("pending");
                    }
                }
                // Serialize publication for this task, but do not use UpdatedAt
                // as a version: another independent attachment or comment also
                // changes it. Document versions and the case link have their own
                // checks, and live permission is checked again under this lock.
                var locked = await db.TaskJournalItems.Where(t => t.Id == intent.TaskId
                        && t.ClientCaseId == intent.CaseId)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.UpdatedAt, t => t.UpdatedAt), ct);
                if (locked != 1)
                {
                    var refreshed = await FindIntentAsync(operationKey, ct);
                    var published = ParseIntent(refreshed?.Json);
                    if (refreshed is not null && Matches(published, request, type, sha) && published!.FileId.HasValue)
                        return await PublishedResultAsync(published, refreshed.ObjectId, key, ct);
                    return new("conflict");
                }
                if (db.Database.IsNpgsql())
                {
                    try
                    {
                        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"TaskJournalParticipants\" WHERE \"TaskJournalItemId\" = {intent.TaskId} ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                    }
                    catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
                    {
                        return new("pending");
                    }
                    var salesRequestId = await db.TaskJournalItems.AsNoTracking().Where(task => task.Id == intent.TaskId)
                        .Select(task => task.SalesRequestId).SingleOrDefaultAsync(ct);
                    if (salesRequestId.HasValue)
                    {
                        try
                        {
                            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} FOR SHARE NOWAIT", ct);
                            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" IN (SELECT \"ResponsibleUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value} UNION SELECT \"CreatedByUserId\" FROM \"SalesRequests\" WHERE \"Id\" = {salesRequestId.Value}) ORDER BY \"Id\" FOR SHARE NOWAIT", ct);
                        }
                        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure)
                        {
                            return new("pending");
                        }
                    }
                }
                if (request.AuthorizePublication is not null && !await request.AuthorizePublication(ct)) return new("forbidden");
                // A concurrent request may have completed this same intent while
                // this request waited for the task lock. Re-read under the lock
                // before treating its now-existing document version as a conflict.
                var completed = await FindIntentAsync(operationKey, ct);
                var completedIntent = ParseIntent(completed?.Json);
                if (completed is not null && Matches(completedIntent, request, type, sha) && completedIntent!.FileId.HasValue)
                    return await PublishedResultAsync(completedIntent, completed.ObjectId, key, ct);
                if (await GetTaskVersionAsync(intent.TaskId, intent.DocumentKey, ct) != intent.Version - 1) return new("conflict");
                if (intent.CaseId.HasValue && await GetCaseVersionAsync(intent.CaseId.Value, CaseKey(intent.TaskId, intent.DocumentKey), ct) != intent.CaseVersion - 1)
                    return new("conflict");

                var storedName = StoredName(intent.DocumentKey, intent.Version, request.UploadKey, request.FileName);
                var relative = Path.Combine(intent.TaskId.ToString(CultureInfo.InvariantCulture), storedName);
                var taskFile = new TaskJournalFile
                {
                    TaskJournalItemId = intent.TaskId,
                    DocumentKey = intent.DocumentKey,
                    Version = intent.Version,
                    OriginalFileName = intent.FileName,
                    StoredFileName = storedName,
                    StoragePath = relative,
                    ContentType = intent.ContentType,
                    SizeBytes = intent.SizeBytes,
                    UploadedByUserId = intent.ActorId,
                    UploadedByName = intent.ActorName,
                    UploadedAt = intent.UploadedAt,
                };
                db.TaskJournalFiles.Add(taskFile);
                ClientCaseDocument? caseDocument = null;
                var caseStorage = new LocalCaseDocumentStorage(environment, configuration);
                if (intent.CaseId.HasValue)
                {
                    var caseKey = CaseKey(intent.TaskId, intent.DocumentKey);
                    var caseName = StoredName(caseKey, intent.CaseVersion, request.UploadKey, intent.FileName);
                    caseDocument = new ClientCaseDocument
                    {
                        ClientCaseId = intent.CaseId.Value,
                        DocumentKey = caseKey,
                        Version = intent.CaseVersion,
                        DocumentType = "Документ задачи",
                        Status = "Загружен",
                        OriginalFileName = intent.FileName,
                        StoredFileName = caseName,
                        StoragePath = caseStorage.BuildRelativePath(intent.CaseId.Value, caseName),
                        ContentType = intent.ContentType,
                        SizeBytes = intent.SizeBytes,
                        UploadedByUserId = intent.ActorId,
                        UploadedByName = intent.ActorName,
                        UploadedAt = intent.UploadedAt,
                    };
                    db.ClientCaseDocuments.Add(caseDocument);
                    taskFile.ClientCaseDocument = caseDocument;
                }
                var currentTask = await db.TaskJournalItems.SingleAsync(t => t.Id == intent.TaskId, ct);
                await db.Entry(currentTask).ReloadAsync(ct);
                currentTask.UpdatedAt = DateTime.UtcNow;
                db.TaskJournalComments.Add(new()
                {
                    TaskJournalItemId = intent.TaskId,
                    AuthorUserId = intent.ActorId,
                    AuthorName = intent.ActorName,
                    Kind = "system",
                    Text = intent.RequestedDocumentKey is null ? $"Добавлен файл: {intent.FileName}."
                        : $"Загружена новая версия файла: {intent.FileName} (v{intent.Version}).",
                    MetadataJson = JsonSerializer.Serialize(new { fileName = intent.FileName, documentKey = intent.DocumentKey, version = intent.Version }, JsonOptions),
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                var taskBinding = await PublishObjectAsync(LegacyStorageSources.TaskFiles, taskFile.Id, taskWrite.ObjectId, pendingOwner, intent, ct);
                if (!taskBinding.Published) return new(taskBinding.Status == "notReady" ? "pending" : "conflict");
                if (caseDocument is not null)
                {
                    var caseBinding = await PublishObjectAsync(LegacyStorageSources.CaseDocuments, caseDocument.Id, caseWrite!.ObjectId, pendingOwner, intent, ct);
                    if (!caseBinding.Published) return new(caseBinding.Status == "notReady" ? "pending" : "conflict");
                }
                var taskTarget = Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration), "task-files", relative);
                compatibilityTickets.TryGetValue(taskTarget, out var taskTicket);
                await EnsureCompatibilityAsync(stage, taskTarget, intent, taskTicket?.AbsolutePath,
                    options.CurrentValue.UploadCapacityChecksEnabled, path => { ownedCompatibilityPaths.Add(path); }, ct);
                if (caseDocument is not null)
                {
                    var caseTarget = caseStorage.ResolvePath(caseDocument.StoragePath);
                    compatibilityTickets.TryGetValue(caseTarget, out var caseTicket);
                    await EnsureCompatibilityAsync(stage, caseTarget, intent, caseTicket?.AbsolutePath,
                        options.CurrentValue.UploadCapacityChecksEnabled, path => { ownedCompatibilityPaths.Add(path); }, ct);
                }
                var publishedJson = JsonSerializer.Serialize(intent with { FileId = taskFile.Id, CaseFileId = caseDocument?.Id }, JsonOptions);
                var updated = await db.StorageOperations.Where(o => o.Id == existing.OperationId && o.PayloadJson == existing.Json)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson, publishedJson), ct);
                if (updated != 1) return new("conflict");
                await transaction.CommitAsync(ct);
                committed = true;
                return new("uploaded", taskFile.Id, CaseFileId: caseDocument?.Id);
            }
            finally
            {
                // Failed publication must not leave added domain entities queued
                // for a later SaveChanges on this scoped context.
                if (!committed) db.ChangeTracker.Clear();
            }
        }
        finally
        {
            try
            {
                if (ownsStage && File.Exists(stage)) File.Delete(stage);
                foreach (var ticket in compatibilityTickets.Values)
                {
                    var ownsTemporary = ownedCompatibilityPaths.Contains(ticket.AbsolutePath);
                    if (ownsTemporary && File.Exists(ticket.AbsolutePath)) File.Delete(ticket.AbsolutePath);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await stageCapacity!.ReleaseAsync(ticket, cleanup.Token); }
                    catch (InvalidOperationException) when (!ownsTemporary)
                    { /* Keep debt for unowned bytes or uncertain authority; preserve the original result. */ }
                }
                if (stageTicket is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await stageCapacity!.ReleaseAsync(stageTicket, cleanup.Token); }
                    catch (InvalidOperationException) when (!ownsStage)
                    { /* A failed CreateNew does not grant ownership of an existing file. */ }
                }
            }
            finally
            {
                if (stageTicket is not null) await stageTicket.CloseWriterAsync();
                foreach (var ticket in compatibilityTickets.Values) await ticket.CloseWriterAsync();
            }
        }
    }

    private async Task<ProductStorageBindingResult> PublishObjectAsync(string source, int sourceId, Guid objectId, string pendingOwner, UploadIntent intent, CancellationToken ct)
    {
        // One-way ownership transfer, atomic with domain rows and the binding.
        // A pending upload is not a product row and is never readable through a
        // product endpoint. The permanent numeric owner is assigned only here.
        var updated = await db.StorageObjects.Where(o => o.Id == objectId && !o.IsDeleted && o.OwnerId == pendingOwner)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, sourceId.ToString(CultureInfo.InvariantCulture))
                .SetProperty(o => o.Version, o => o.Version + 1), ct);
        if (updated != 1) return new(false, null, "invalidObject");
        return await publisher.PublishAsync(new(source, sourceId, objectId, intent.FileName, intent.ContentType, 0), ct);
    }
    private async Task<IntentRow?> FindIntentAsync(string key, CancellationToken ct) => await db.StorageOperations.AsNoTracking()
        .Where(o => o.OperationType == "commit" && o.IdempotencyKey == key)
        .Select(o => new IntentRow(o.Id, o.StorageObjectId, o.PayloadJson)).SingleOrDefaultAsync(ct);
    private static UploadIntent? ParseIntent(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<UploadIntent>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
    private static bool Matches(UploadIntent? intent, ManagedTaskFileUploadRequest request, string type, string sha)
        => intent is not null && intent.TaskId == request.TaskId && intent.ActorId == request.ActorId
            && intent.RequestedDocumentKey == request.DocumentKey && intent.FileName == request.FileName
            && intent.ContentType == type && intent.SizeBytes == request.File.Length && intent.Sha256 == sha
            && intent.Version > 0 && intent.CaseVersion > 0 && !string.IsNullOrEmpty(intent.DocumentKey) && intent.DocumentKey.Length <= 64;
    private async Task<ManagedTaskFileUploadResult> PublishedResultAsync(UploadIntent intent, Guid objectId, string key, CancellationToken ct)
    {
        var file = await db.TaskJournalFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == intent.FileId && f.TaskJournalItemId == intent.TaskId, ct);
        if (file is null || file.DocumentKey != intent.DocumentKey || file.Version != intent.Version || file.OriginalFileName != intent.FileName
            || file.ContentType != intent.ContentType || file.SizeBytes != intent.SizeBytes || file.UploadedByUserId != intent.ActorId
            || file.ClientCaseDocumentId != intent.CaseFileId) return new("conflict");
        if (!await db.StorageProductBindings.AsNoTracking().AnyAsync(b => b.Source == LegacyStorageSources.TaskFiles && b.SourceId == file.Id && b.StorageObjectId == objectId, ct)) return new("conflict");
        if (!await VerifyPublishedObjectAsync(objectId, LegacyStorageSources.TaskFiles, file.Id, key, intent, ct)) return new("conflict");
        if (intent.CaseId.HasValue)
        {
            var caseObject = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(key + ":case"), ct);
            if (caseObject is null || !intent.CaseFileId.HasValue
                || !await db.ClientCaseDocuments.AsNoTracking().AnyAsync(d => d.Id == intent.CaseFileId && d.ClientCaseId == intent.CaseId
                    && d.DocumentKey == CaseKey(intent.TaskId, intent.DocumentKey) && d.Version == intent.CaseVersion
                    && d.ContentType == intent.ContentType && d.SizeBytes == intent.SizeBytes
                    && d.OriginalFileName == intent.FileName && d.UploadedByUserId == intent.ActorId, ct)
                || !await db.StorageProductBindings.AsNoTracking().AnyAsync(b => b.Source == LegacyStorageSources.CaseDocuments && b.SourceId == intent.CaseFileId && b.StorageObjectId == caseObject.ObjectId, ct)) return new("conflict");
            if (!await VerifyPublishedObjectAsync(caseObject.ObjectId, LegacyStorageSources.CaseDocuments, intent.CaseFileId.Value, key + ":case", intent, ct)) return new("conflict");
        }
        return new("uploaded", file.Id, true, intent.CaseFileId);
    }
    private Task<bool> VerifyPublishedObjectAsync(Guid objectId, string source, int sourceId, string key, UploadIntent intent, CancellationToken ct)
    {
        var owner = sourceId.ToString(CultureInfo.InvariantCulture);
        var dataClass = LegacyStorageSources.GetDataClass(source);
        var commitKey = StorageObjectService.GetCommitOperationKey(key);
        return db.StorageOperations.AsNoTracking().AnyAsync(o => o.OperationType == "commit" && o.IdempotencyKey == commitKey
            && o.Status == StorageOperationStatuses.Succeeded && o.StorageObjectId == objectId
            && !o.StorageObject.IsDeleted && o.StorageObject.DataClass == dataClass && o.StorageObject.OwnerType == dataClass
            && o.StorageObject.OwnerId == owner && o.StorageObject.LogicalName == intent.FileName
            && o.StorageObject.ContentType == intent.ContentType && o.StorageObjectGenerationId != null
            && o.StorageObjectGeneration!.StorageObjectId == objectId
            && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration
            && o.StorageObjectGeneration.SizeBytes == intent.SizeBytes && o.StorageObjectGeneration.Sha256 == intent.Sha256, ct);
    }
    private async Task<int> GetTaskVersionAsync(int id, string key, CancellationToken ct)
        => await db.TaskJournalFiles.Where(f => f.TaskJournalItemId == id && f.DocumentKey == key).MaxAsync(f => (int?)f.Version, ct) ?? 0;
    private async Task<int> GetCaseVersionAsync(int id, string key, CancellationToken ct)
        => await db.ClientCaseDocuments.Where(f => f.ClientCaseId == id && f.DocumentKey == key).MaxAsync(f => (int?)f.Version, ct) ?? 0;
    private static string CaseKey(int taskId, string documentKey)
    {
        var prefix = $"task-{taskId}-";
        return prefix + documentKey[..Math.Min(documentKey.Length, Math.Max(1, 64 - prefix.Length))];
    }
    private static string StoredName(string key, int version, Guid uploadId, string name)
    {
        var extension = Path.GetExtension(name);
        return $"{key}-v{version}-{uploadId:N}{(extension.Length <= 24 ? extension : "")}";
    }
    private static async Task EnsureCompatibilityAsync(string stage, string target, UploadIntent intent,
        string? reservedTemporary, bool requireReservation, Action<string> markCreated, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            await using var existing = File.OpenRead(target);
            if (existing.Length != intent.SizeBytes || Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).ToLowerInvariant() != intent.Sha256)
                throw new InvalidDataException("Existing compatibility copy does not match the upload.");
            return;
        }
        if (requireReservation && reservedTemporary is null)
            throw new InvalidOperationException("Compatibility copy has no capacity reservation.");
        var temporary = reservedTemporary ?? Path.Combine(Path.GetDirectoryName(target)!, Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        try
        {
            await ExclusiveUploadFileWriter.WriteNewAsync(temporary, async (output, token) =>
            {
                ownsTemporary = true;
                markCreated(temporary);
                await using var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await input.CopyToAsync(output, token);
            }, ct);
            File.Move(temporary, target, false);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }
    private sealed record IntentRow(Guid OperationId, Guid ObjectId, string? Json);
    private sealed class StoredUploadFile(Stream content, UploadIntent intent) : IFormFile
    {
        public string ContentType => intent.ContentType;
        public string ContentDisposition => string.Empty;
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => intent.SizeBytes;
        public string Name => "file";
        public string FileName => intent.FileName;
        public Stream OpenReadStream() => content;
        public void CopyTo(Stream target) => content.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
            => content.CopyToAsync(target, cancellationToken);
    }
    private sealed record UploadIntent(int TaskId, int ActorId, string ActorName, int? CaseId, DateTime? TaskUpdatedAt,
        string? RequestedDocumentKey, string DocumentKey, int Version, int CaseVersion, string FileName,
        string ContentType, long SizeBytes, string Sha256, DateTime UploadedAt, int? FileId = null, int? CaseFileId = null);
}
