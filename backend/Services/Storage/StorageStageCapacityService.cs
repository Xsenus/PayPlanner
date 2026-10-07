using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageStageCapacityTicket(string Key, string BindingHash, string ResourceKey,
    string GroupId, string RelativePath, string AbsolutePath, long SizeBytes, Guid ObjectId)
{
    internal StorageStageWriterFence? WriterFence { get; init; }
    public string? WriterMarkerIdentity { get; init; }
    public string? RootIdentity { get; init; }
    public ValueTask CloseWriterAsync() => WriterFence?.DisposeAsync() ?? ValueTask.CompletedTask;
}
public sealed record StorageStageRecoveryItem(string Key, string Status)
{
    public string? RelativePath { get; init; }
    public long ExpectedBytes { get; init; }
    public long? ObservedBytes { get; init; }
    public DateTime UpdatedAt { get; init; }
}
public sealed record StorageStageRecoveryReport(int Inspected, int Eligible, int Settled,
    int Retained, bool HasMore, IReadOnlyList<StorageStageRecoveryItem> Items)
{
    public string? NextAfterKey { get; init; }
}

public interface IStorageStageCapacityService
{
    Task<StorageStageFileReceipt> WriteCheckpointedFileAsync(StorageStageCapacityTicket ticket,
        Func<Stream, StorageStageFileReceipt, CancellationToken, Task> write, CancellationToken ct)
        => throw new NotSupportedException("Checkpointed stage writing is required for this producer.");
    Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(
        string uploadIdentity, string absolutePath, long sizeBytes, CancellationToken ct);
    Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(
        string uploadIdentity, string absolutePath, long sizeBytes, string rootAlias, CancellationToken ct);
    Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket, long actualSizeBytes, CancellationToken ct);
    Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct);
    Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct);
    Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit,
        TimeSpan minimumAge, CancellationToken ct, string? afterKey = null);
}

// Reserves the FIRST physical copy, before a managed product upload writes its
// temporary file. The final object writer holds its own reservation in parallel.
public sealed partial class StorageStageCapacityService(PaymentContext db, IStorageProviderRegistry providers,
    IOptionsMonitor<StorageOrchestrationOptions> options, IWebHostEnvironment environment,
    IConfiguration configuration, ILogger<StorageStageCapacityService>? logger = null) : IStorageStageCapacityService
{
    private const string StagePrefix = "stage:";
    private const string FencedStagePrefix = "stage:writer-v1:";

    public async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(
        string uploadIdentity, string absolutePath, long sizeBytes, CancellationToken ct)
        => await ReserveCoreAsync(uploadIdentity, absolutePath, sizeBytes,
            LocalStoragePaths.ResolveRoot(environment, configuration), "local", null, ct);

    public async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(
        string uploadIdentity, string absolutePath, long sizeBytes, string rootAlias, CancellationToken ct)
    {
        var root = rootAlias switch
        {
            "files" => ApplicationFileStoragePaths.ResolveRoot(environment, configuration),
            "profile-current" => UserPhotoStoragePaths.ResolveRoot(environment, configuration),
            "esign-current" => ElectronicSignatureStoragePaths.ResolveRoot(environment, configuration),
            _ => null
        };
        return root is null ? (null, "Unknown compatibility storage root.")
            : await ReserveCoreAsync(uploadIdentity, absolutePath, sizeBytes,
                root, "legacy-local", rootAlias, ct);
    }

    private async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCoreAsync(
        string uploadIdentity, string absolutePath, long sizeBytes, string root,
        string providerKey, string? connectionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uploadIdentity) || sizeBytes <= 0)
            return (null, "Stage reservation needs an upload identity and positive size.");
        if (db.ChangeTracker.HasChanges())
            return (null, "Stage reservation requires a clean database unit of work.");
        var configured = options.CurrentValue;
        if (!configured.UploadCapacityChecksEnabled)
            return (null, "Upload capacity policy changed before staging.");
        var minimumFreeBytes = configured.UploadMinimumFreeBytes;
        if (minimumFreeBytes < 0)
            return (null, "Upload minimum free space must be non-negative.");
        var fullPath = Path.GetFullPath(absolutePath);
        var relativePath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (Path.IsPathRooted(relativePath) || relativePath == "." || relativePath == ".."
            || relativePath.StartsWith("../", StringComparison.Ordinal) || relativePath.Length > 2048)
            return (null, "Stage path is outside the managed local storage root.");
        try
        {
            var parent = root;
            foreach (var segment in relativePath.Split('/').SkipLast(1))
            {
                parent = Path.Combine(parent, segment);
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    return (null, "Stage directory cannot be a symbolic link or reparse point.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, "Stage directory is unavailable for capacity admission.");
        }
        var identityBytes = Encoding.UTF8.GetBytes(uploadIdentity + "\n" + providerKey + "\n" + connectionId + "\n" + relativePath);
        var key = FencedStagePrefix + Convert.ToHexString(SHA256.HashData(identityBytes)).ToLowerInvariant();
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            uploadIdentity + "\n" + providerKey + "\n" + connectionId + "\n" + relativePath + "\n" + sizeBytes))).ToLowerInvariant();
        var resourceKey = StorageCapacityAdmission.ResourceKey(providerKey, connectionId);
        var rootIdentity = StorageStageRootIdentity.Observe(root);
        if (rootIdentity is null) return (null, "Stage root native identity is unavailable.");
        var observed = await StorageCapacityAdmission.ObserveAsync(db, providers,
            [(providerKey, connectionId)], minimumFreeBytes, ct);
        if (observed.Error is not null) return (null, observed.Error);
        if (!observed.Resources.TryGetValue(resourceKey, out var resource)
            || string.IsNullOrWhiteSpace(resource.GroupId))
            return (null, "Local stage capacity authority is unavailable.");
        var objectId = Guid.NewGuid();
        var ticket = new StorageStageCapacityTicket(key, binding, resourceKey, resource.GroupId,
            relativePath, fullPath, sizeBytes, objectId)
        { RootIdentity = rootIdentity };
        StorageCapacityReservation? row = null;
        var ticketIssued = false;
        try
        {
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var debt = await StorageCapacityAdmission.ReadDebtAsync(db, observed, ct);
                if (debt.Error is not null) return (null, debt.Error);
                var proposed = new StorageCapacityDemand(resourceKey, sizeBytes, key);
                if (!StorageCapacityAdmission.Evaluate(observed, debt, [proposed]).CanSchedule)
                    return (null, "Local stage capacity is unknown or insufficient.");
                if (!options.CurrentValue.UploadCapacityChecksEnabled
                    || options.CurrentValue.UploadMinimumFreeBytes != minimumFreeBytes)
                    return (null, "Upload capacity policy changed during staging; retry the upload.");
                if (StorageStageRootIdentity.Observe(root) != rootIdentity)
                    return (null, "Stage root changed during capacity admission; retry the upload.");
                var now = DateTime.UtcNow;
                row = new StorageCapacityReservation
                {
                    IdempotencyKey = key,
                    ResourceKey = resourceKey,
                    GroupId = resource.GroupId,
                    BindingHash = binding,
                    Bytes = sizeBytes,
                    ObjectId = objectId,
                    StageWriterAdmissionPending = true,
                    StageRootIdentity = rootIdentity,
                    LocalLocator = relativePath,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.StorageCapacityReservations.Add(row);
                if (!await StorageCapacityAdmission.TryAdvanceEpochAsync(db, observed, ct))
                    return (null, "Storage budget changed during staging; retry the upload.");
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            // No persisted marker may precede its durable reservation. A crash
            // here retains debt; recovery never recreates a missing marker.
            // Producers receive write authority only after the marker is held.
            {
                await using var activation = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                if (!options.CurrentValue.UploadCapacityChecksEnabled
                    || options.CurrentValue.UploadMinimumFreeBytes != minimumFreeBytes)
                    return (null, "Upload capacity policy changed before writer activation; retry the upload.");
                if (StorageStageRootIdentity.Observe(root) != rootIdentity)
                    return (null, "Stage root changed before writer activation; retry the upload.");
                // This conditional update locks the row until the marker and
                // activation commit succeed. Recovery competes for the SAME
                // preparing row; a released preparation can never issue a writer.
                var activatedAt = DateTime.UtcNow;
                var activated = await db.StorageCapacityReservations.Where(item => item.IdempotencyKey == ticket.Key
                    && item.ResourceKey == ticket.ResourceKey && item.ObjectId == ticket.ObjectId
                    && item.GroupId == ticket.GroupId && item.BindingHash == ticket.BindingHash
                    && item.Bytes == ticket.SizeBytes && item.LocalLocator == ticket.RelativePath
                    && item.Status == StorageCapacityReservationStatuses.Pending && item.StageWriterAdmissionPending
                    && item.StageWriterMarkerIdentity == null && item.StageRootIdentity == ticket.RootIdentity)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StageWriterAdmissionPending, false)
                        .SetProperty(item => item.UpdatedAt, activatedAt), ct);
                if (activated != 1) return (null, "Stage preparation ended before writer activation; retry the upload.");
                if (!TryResolvePendingPath(row, out var activatedPath, out var activatedRoot)
                    || !string.Equals(activatedPath, ticket.AbsolutePath,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return (null, "Stage path changed before writer activation; retry the upload.");
                var physical = LocalStorageCapacityProbe.Observe(activatedRoot, resourceKey, 0);
                if (physical.GroupId != ticket.GroupId || physical.AvailableBytes is null)
                    return (null, "Stage physical authority changed before writer activation; retry the upload.");
                try
                {
                    ticket = ticket with { WriterFence = await StorageStageWriterFence.CreateAsync(root, ticket, ct) };
                    ticket = ticket with { WriterMarkerIdentity = ticket.WriterFence.Identity };
                    if (await db.StorageCapacityReservations.Where(item => item.IdempotencyKey == ticket.Key
                        && item.ResourceKey == ticket.ResourceKey && item.ObjectId == ticket.ObjectId
                        && item.GroupId == ticket.GroupId && item.BindingHash == ticket.BindingHash
                        && item.Bytes == ticket.SizeBytes && item.LocalLocator == ticket.RelativePath
                        && item.Status == StorageCapacityReservationStatuses.Pending && !item.StageWriterAdmissionPending
                        && item.StageWriterMarkerIdentity == null && item.StageRootIdentity == ticket.RootIdentity)
                        .ExecuteUpdateAsync(s => s.SetProperty(item => item.StageWriterMarkerIdentity, ticket.WriterMarkerIdentity), ct) != 1)
                        throw new IOException("Stage marker identity changed during activation.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return (null, "Stage writer fence is unavailable; retry the upload.");
                }
                await activation.CommitAsync(ct);
            }
            ticketIssued = true;
            return (ticket, null);
        }
        catch (Exception exception) when (StorageCapacityAdmission.IsConcurrentBudgetWrite(exception)
            || exception is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } }
            || exception is DbUpdateException { InnerException: Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } })
        {
            return (null, "Concurrent upload changed stage capacity; retry the upload.");
        }
        finally
        {
            if (row is not null) db.Entry(row).State = EntityState.Detached;
            if (!ticketIssued && row is not null)
            {
                // This invocation has not handed its random ObjectId to a
                // writer. Its transaction is disposed before this finally.
                // A same-key contender must never retire the winning ticket.
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var durable = await db.StorageCapacityReservations.AsNoTracking().SingleOrDefaultAsync(item =>
                        item.IdempotencyKey == ticket.Key && item.ResourceKey == ticket.ResourceKey
                        && item.ObjectId == ticket.ObjectId, cleanup.Token);
                    if (durable is not null)
                    {
                        var cleanupTicket = ticket;
                        var unactivated = ticket with { WriterMarkerIdentity = null };
                        // Activation rolled back, and this invocation never
                        // handed out its writer. Release only its exact fresh
                        // preparation; retirement still uses the held original
                        // native identity. Unknown/mutated rows stay retained.
                        if (durable.StageWriterAdmissionPending && durable.StageWriterMarkerIdentity is null
                            && durable.Status == StorageCapacityReservationStatuses.Pending
                            && durable.StageFileReceiptJson is null && durable.ProductContextJson is null
                            && Matches(durable, unactivated)) cleanupTicket = unactivated;
                        await ReleaseAsync(cleanupTicket, cleanup.Token);
                    }
                    else if (!await db.StorageCapacityReservations.AsNoTracking()
                        .AnyAsync(item => item.ObjectId == ticket.ObjectId, cleanup.Token))
                        // The transaction is disposed, no ticket was handed to
                        // a producer, and this random identity has no durable
                        // row. Retire only the marker held by this invocation.
                        // A changed row or unavailable proof must retain it.
                        ticket.WriterFence?.Retire();
                }
                catch (Exception exception)
                {
                    // Keep debt if bytes exist or fresh authority/database
                    // access cannot be confirmed. Preserve the original error.
                    logger?.LogWarning(exception, "Could not release undelivered stage ticket {ReservationKey}", ticket.Key);
                }
            }
            if (!ticketIssued)
            {
                if (row is null) ticket.WriterFence?.Retire();
                await ticket.CloseWriterAsync();
            }
        }
    }

    public async Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(
        StorageStageCapacityTicket ticket, long actualSizeBytes, CancellationToken ct)
    {
        if (actualSizeBytes <= 0 || actualSizeBytes > ticket.SizeBytes)
            throw new ArgumentOutOfRangeException(nameof(actualSizeBytes), "Written stage must fit its admitted upper bound.");
        var confirmed = ticket with { SizeBytes = actualSizeBytes, BindingHash = PhysicalSizeBinding(ticket.BindingHash, actualSizeBytes) };
        StorageStageCapacityTicket CheckRecordedSize(StorageCapacityReservation row)
        {
            if (!TryReadPhysicalSizeReceipt(row, out var receipt))
                throw new InvalidOperationException("Stage written-size receipt is invalid.");
            if (receipt is null) return confirmed;
            var originalReceipt = ticket.BindingHash == receipt.InitialBindingHash && ticket.SizeBytes == receipt.UpperBound;
            var writtenReceipt = ticket.BindingHash == row.BindingHash && ticket.SizeBytes == row.Bytes;
            if (receipt.ActualBytes != actualSizeBytes || (!originalReceipt && !writtenReceipt))
                throw new InvalidOperationException("Completed stage size is immutable.");
            return ticket with { SizeBytes = actualSizeBytes, BindingHash = row.BindingHash };
        }
        var row = await db.StorageCapacityReservations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == ticket.Key && item.ResourceKey == ticket.ResourceKey, ct);
        if (row is null || row.StageWriterAdmissionPending || (!Matches(row, ticket) && !Matches(row, confirmed))
            || row.Status is not (StorageCapacityReservationStatuses.Pending or StorageCapacityReservationStatuses.Released)
            || !TryResolvePendingPath(row, out var path, out var root)
            || !string.Equals(Path.GetFullPath(ticket.AbsolutePath), path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Stage reservation changed before physical size confirmation.");
        confirmed = CheckRecordedSize(row);
        var physical = LocalStorageCapacityProbe.Observe(root, row.ResourceKey, 0);
        if (physical.GroupId != row.GroupId || physical.AvailableBytes is null)
            throw new InvalidOperationException("Stage physical group changed before size confirmation.");
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Stage path is not an ordinary file.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous);
        if (file.Length != actualSizeBytes)
            throw new InvalidOperationException("Complete stage bytes do not match the confirmed size.");
        var fileReceiptJson = row.StageFileReceiptJson;
        await VerifyCheckpointFileAsync(file, row, ticket, actualSizeBytes, ct);
        if (row.ProductContextJson is not null && Matches(row, confirmed)) return confirmed;
        if (row.Status != StorageCapacityReservationStatuses.Pending)
        {
            if (actualSizeBytes == ticket.SizeBytes && Matches(row, ticket)) return ticket;
            throw new InvalidOperationException("Already released stage cannot change its written size.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        row = await db.StorageCapacityReservations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == ticket.Key && item.ResourceKey == ticket.ResourceKey, ct);
        if (row is not null && (row.StageWriterAdmissionPending || row.StageFileReceiptJson != fileReceiptJson))
            throw new InvalidOperationException("Stage file receipt changed during physical size confirmation.");
        if (row is not null)
        {
            confirmed = CheckRecordedSize(row);
            if (row.ProductContextJson is not null && Matches(row, confirmed)
                && row.Status is StorageCapacityReservationStatuses.Pending or StorageCapacityReservationStatuses.Released)
                return confirmed;
        }
        if (row is null || !Matches(row, ticket) || row.Status != StorageCapacityReservationStatuses.Pending)
            throw new InvalidOperationException("Stage reservation changed during physical size confirmation.");
        var now = DateTime.UtcNow;
        var receiptJson = JsonSerializer.Serialize(new PhysicalSizeReceipt(1, ticket.SizeBytes, actualSizeBytes, ticket.BindingHash));
        var changed = await db.StorageCapacityReservations.Where(item => item.IdempotencyKey == ticket.Key
            && item.ResourceKey == ticket.ResourceKey && item.Status == StorageCapacityReservationStatuses.Pending
            && item.GroupId == ticket.GroupId && item.BindingHash == ticket.BindingHash && item.Bytes == ticket.SizeBytes
            && item.ObjectId == ticket.ObjectId && item.LocalLocator == ticket.RelativePath && item.ProductContextJson == null
            && item.StageWriterMarkerIdentity == ticket.WriterMarkerIdentity && item.StageRootIdentity == ticket.RootIdentity
            && item.StageFileReceiptJson == fileReceiptJson && !item.StageWriterAdmissionPending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Bytes, actualSizeBytes)
                .SetProperty(item => item.BindingHash, confirmed.BindingHash).SetProperty(item => item.ProductContextJson, receiptJson)
                .SetProperty(item => item.UpdatedAt, now), ct);
        if (changed != 1) throw new InvalidOperationException("Stage reservation changed before confirmed size save.");
        await StorageCapacityAdmission.AdvanceEpochAsync(db, ct);
        await transaction.CommitAsync(ct);
        return confirmed;
    }

    private sealed record PhysicalSizeReceipt(int Version, long UpperBound, long ActualBytes, string InitialBindingHash);

    private static string PhysicalSizeBinding(string initialBindingHash, long actualBytes)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(initialBindingHash + "\nphysical-size:"
            + actualBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();

    private static bool TryReadPhysicalSizeReceipt(StorageCapacityReservation row, out PhysicalSizeReceipt? receipt)
    {
        receipt = null;
        if (row.ProductContextJson is null) return true;
        if (row.ProductContextJson.Length > 512) return false;
        try
        {
            using var document = JsonDocument.Parse(row.ProductContextJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 4 || names.Distinct(StringComparer.Ordinal).Count() != 4
                || names.Any(name => name is not ("Version" or "UpperBound" or "ActualBytes" or "InitialBindingHash"))) return false;
            receipt = JsonSerializer.Deserialize<PhysicalSizeReceipt>(row.ProductContextJson);
            return receipt is not null && receipt.Version == 1 && receipt.UpperBound > 0
                && receipt.ActualBytes > 0 && receipt.ActualBytes <= receipt.UpperBound && row.Bytes == receipt.ActualBytes
                && receipt.InitialBindingHash is { Length: 64 } && receipt.InitialBindingHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                && row.BindingHash == PhysicalSizeBinding(receipt.InitialBindingHash, receipt.ActualBytes);
        }
        catch (JsonException) { return false; }
    }

    public async Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        // Once the entire staged copy is physically present, fresh disk-free
        // observations already include it. Keep the reservation only while its
        // bytes are still being written, otherwise the same copy is charged twice.
        var row = await db.StorageCapacityReservations.AsNoTracking().SingleOrDefaultAsync(r =>
            r.IdempotencyKey == ticket.Key && r.ResourceKey == ticket.ResourceKey, ct);
        if (row is null || row.StageWriterAdmissionPending || !Matches(row, ticket))
            throw new InvalidOperationException("Stage reservation changed before physical settlement.");
        var snapshot = new StageReceiptSnapshot(row.StageFileReceiptJson, row.ProductContextJson, row.StageWriterAdmissionPending);
        var attributes = File.GetAttributes(ticket.AbsolutePath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Stage path is not an ordinary file.");
        await using (var file = new FileStream(ticket.AbsolutePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous))
        {
            if (file.Length != ticket.SizeBytes)
                throw new InvalidOperationException("Stage bytes do not match their capacity reservation.");
            await VerifyCheckpointFileAsync(file, row, ticket, ticket.SizeBytes, ct);
            await MarkReleasedAsync(ticket, ct, snapshot);
        }
        ticket.WriterFence?.Retire();
        await ticket.CloseWriterAsync();
    }

    public async Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        try
        {
            // A crash before deletion intentionally keeps the pending debt. An
            // independent recovery procedure must verify stale bytes before release.
            try
            {
                _ = File.GetAttributes(ticket.AbsolutePath);
                throw new InvalidOperationException("Stage bytes still exist; capacity reservation cannot be released.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var row = await db.StorageCapacityReservations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == ticket.Key && item.ResourceKey == ticket.ResourceKey, ct);
            if (row is null || row.ObjectId != ticket.ObjectId || row.GroupId != ticket.GroupId
                || row.LocalLocator != ticket.RelativePath || !TryReadPhysicalSizeReceipt(row, out var receipt)
                || !TryResolvePendingPath(row, out var path, out _)
                || !string.Equals(Path.GetFullPath(ticket.AbsolutePath), path,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Stage reservation authority changed before missing-file release.");
            if (receipt is not null && ticket.BindingHash == receipt.InitialBindingHash && ticket.SizeBytes == receipt.UpperBound)
                ticket = ticket with { SizeBytes = receipt.ActualBytes, BindingHash = row.BindingHash };
            await MarkReleasedAsync(ticket, ct);
            ticket.WriterFence?.Retire();
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    public async Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit,
        TimeSpan minimumAge, CancellationToken ct, string? afterKey = null)
    {
        if (limit is < 1 or > 200 || minimumAge < TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(limit), "Recovery requires 1–200 rows and at least ten minutes of age.");
        if (afterKey is not null && !IsStageKey(afterKey))
            throw new ArgumentException("Recovery cursor is invalid.", nameof(afterKey));
        if (db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Stage recovery requires a clean database unit of work.");
        var cutoff = DateTime.UtcNow - minimumAge;
        var query = db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.Status == StorageCapacityReservationStatuses.Pending
                && r.IdempotencyKey.StartsWith(StagePrefix) && r.UpdatedAt <= cutoff);
        if (afterKey is not null) query = query.Where(r => string.Compare(r.IdempotencyKey, afterKey) > 0);
        var rows = await query.OrderBy(r => r.IdempotencyKey).Take(limit + 1).ToArrayAsync(ct);
        var items = new List<StorageStageRecoveryItem>(Math.Min(rows.Length, limit));
        var eligible = 0;
        var settled = 0;
        foreach (var row in rows.Take(limit))
        {
            ct.ThrowIfCancellationRequested();
            if (!TryResolvePendingPath(row, out var path, out var root))
            {
                items.Add(ToRecoveryItem(row, "invalidAuthority"));
                continue;
            }
            var physical = LocalStorageCapacityProbe.Observe(root, row.ResourceKey, 0);
            if (physical.GroupId != row.GroupId || physical.AvailableBytes is null)
            {
                items.Add(ToRecoveryItem(row, "changedVolume", pathValidated: true));
                continue;
            }
            var ticket = new StorageStageCapacityTicket(row.IdempotencyKey, row.BindingHash, row.ResourceKey,
                row.GroupId, row.LocalLocator, path, row.Bytes, row.ObjectId)
            { WriterMarkerIdentity = row.StageWriterMarkerIdentity, RootIdentity = row.StageRootIdentity };
            if (row.StageWriterAdmissionPending)
            {
                try
                {
                    var status = await ReconcilePreparationAsync(row, ticket, apply, ct);
                    if (status is "eligibleMissingStage" or "releasedMissingStage") eligible++;
                    if (status == "releasedMissingStage") settled++;
                    items.Add(ToRecoveryItem(row, status, pathValidated: true));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    items.Add(ToRecoveryItem(row, "unavailableFile", pathValidated: true));
                }
                continue;
            }
            StorageStageWriterFence? writerFence = null;
            if (row.IdempotencyKey.StartsWith(FencedStagePrefix, StringComparison.Ordinal))
            {
                try
                {
                    writerFence = await StorageStageWriterFence.OpenForRecoveryAsync(root, ticket, ct);
                    ticket = ticket with { WriterFence = writerFence };
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    items.Add(ToRecoveryItem(row, "writerNotProvenStopped", pathValidated: true));
                    continue;
                }
            }
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    items.Add(ToRecoveryItem(row, "unsafeFile", pathValidated: true));
                    continue;
                }
                var size = new FileInfo(path).Length;
                if (size != row.Bytes)
                {
                    items.Add(ToRecoveryItem(row, "incompleteFile", pathValidated: true, observedBytes: size));
                    continue;
                }
                if (!apply && row.StageFileReceiptJson is not null)
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.Asynchronous);
                    await VerifyCheckpointFileAsync(file, row, ticket, row.Bytes, ct);
                }
                eligible++;
                if (apply)
                {
                    try { await SettlePhysicalAsync(ticket, ct); }
                    catch { eligible--; throw; }
                    settled++;
                }
                items.Add(ToRecoveryItem(row, apply ? "settled" : "eligible", pathValidated: true, observedBytes: size));
            }
            catch (Exception exception) when (writerFence is not null
                && exception is FileNotFoundException or DirectoryNotFoundException)
            {
                eligible++;
                try
                {
                    if (apply)
                    {
                        await ReleaseAsync(ticket, ct);
                        settled++;
                    }
                    items.Add(ToRecoveryItem(row, apply ? "releasedMissingStage" : "eligibleMissingStage", pathValidated: true));
                }
                catch (Exception releaseError) when (releaseError is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    items.Add(ToRecoveryItem(row, "unavailableFile", pathValidated: true));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException)
            {
                items.Add(ToRecoveryItem(row, "unavailableFile", pathValidated: true));
            }
            finally { if (writerFence is not null) await writerFence.DisposeAsync(); }
        }
        return new(rows.Length <= limit ? rows.Length : limit, eligible, settled,
            items.Count - settled, rows.Length > limit, items)
        {
            NextAfterKey = rows.Length > limit ? rows[limit - 1].IdempotencyKey : null
        };
    }

    private static bool IsStageKey(string key)
    {
        var prefix = key.StartsWith(FencedStagePrefix, StringComparison.Ordinal) ? FencedStagePrefix : StagePrefix;
        return key.Length == prefix.Length + 64 && key.StartsWith(prefix, StringComparison.Ordinal)
            && key.AsSpan(prefix.Length).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static StorageStageRecoveryItem ToRecoveryItem(StorageCapacityReservation row, string status,
        bool pathValidated = false, long? observedBytes = null)
        => new(row.IdempotencyKey, status)
        {
            RelativePath = pathValidated ? row.LocalLocator : null,
            ExpectedBytes = row.Bytes,
            ObservedBytes = observedBytes,
            UpdatedAt = row.UpdatedAt
        };

    private bool TryResolvePendingPath(StorageCapacityReservation row, out string path, out string root)
    {
        path = root = string.Empty;
        if (!TryReadPhysicalSizeReceipt(row, out _)) return false;
        root = row.ResourceKey switch
        {
            var key when key == StorageCapacityAdmission.ResourceKey("local", null)
                => LocalStoragePaths.ResolveRoot(environment, configuration),
            var key when key == StorageCapacityAdmission.ResourceKey("legacy-local", "files")
                => ApplicationFileStoragePaths.ResolveRoot(environment, configuration),
            var key when key == StorageCapacityAdmission.ResourceKey("legacy-local", "profile-current")
                => UserPhotoStoragePaths.ResolveRoot(environment, configuration),
            var key when key == StorageCapacityAdmission.ResourceKey("legacy-local", "esign-current")
                => ElectronicSignatureStoragePaths.ResolveRoot(environment, configuration),
            _ => string.Empty
        };
        if (root.Length == 0 || string.IsNullOrWhiteSpace(row.LocalLocator)
            || row.LocalLocator.Length > 2048 || Path.IsPathRooted(row.LocalLocator)
            || row.LocalLocator.Contains('\\') || row.LocalLocator.Contains(':')
            || row.LocalLocator.Any(char.IsControl)
            || row.LocalLocator.Split('/').Any(s => s is "" or "." or ".."))
            return false;
        try
        {
            path = Path.GetFullPath(Path.Combine(root, row.LocalLocator.Replace('/', Path.DirectorySeparatorChar)));
            if (Path.GetRelativePath(root, path).Replace('\\', '/') != row.LocalLocator)
                return false;
            var parent = root;
            foreach (var segment in row.LocalLocator.Split('/').SkipLast(1))
            {
                parent = Path.Combine(parent, segment);
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
            }
            // Never backfill the original directory from today's path. Older
            // fenced rows without native root evidence remain unknown.
            if (row.IdempotencyKey.StartsWith(FencedStagePrefix, StringComparison.Ordinal) && (row.StageRootIdentity is null
                || StorageStageRootIdentity.Observe(root) != row.StageRootIdentity)) return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        { return false; }
        return true;
    }

    private async Task MarkReleasedAsync(StorageStageCapacityTicket ticket, CancellationToken ct,
        StageReceiptSnapshot? expectedReceipt = null)
    {
        var compareReceipt = expectedReceipt is not null;
        var fileJson = expectedReceipt?.FileJson;
        var sizeJson = expectedReceipt?.SizeJson;
        var admissionPending = expectedReceipt?.AdmissionPending ?? false;
        var commitStarted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var row = await db.StorageCapacityReservations.AsNoTracking().SingleOrDefaultAsync(r =>
                r.IdempotencyKey == ticket.Key && r.ResourceKey == ticket.ResourceKey, ct);
            if (row is null || row.BindingHash != ticket.BindingHash || row.GroupId != ticket.GroupId
                || row.Bytes != ticket.SizeBytes || row.ObjectId != ticket.ObjectId
                || row.LocalLocator != ticket.RelativePath
                || row.StageWriterMarkerIdentity != ticket.WriterMarkerIdentity || row.StageRootIdentity != ticket.RootIdentity
                || (compareReceipt && (row.StageFileReceiptJson != fileJson || row.ProductContextJson != sizeJson
                    || row.StageWriterAdmissionPending != admissionPending)))
                throw new InvalidOperationException("Stage reservation changed before release.");
            if (row.Status == StorageCapacityReservationStatuses.Released) return;
            if (row.Status != StorageCapacityReservationStatuses.Pending)
                throw new InvalidOperationException("Stage reservation has an invalid status.");
            var releasedAt = DateTime.UtcNow;
            var changed = await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == ticket.Key
                && r.ResourceKey == ticket.ResourceKey && r.Status == StorageCapacityReservationStatuses.Pending
                && r.BindingHash == ticket.BindingHash && r.GroupId == ticket.GroupId && r.Bytes == ticket.SizeBytes
                && r.ObjectId == ticket.ObjectId && r.LocalLocator == ticket.RelativePath
                && r.StageWriterMarkerIdentity == ticket.WriterMarkerIdentity && r.StageRootIdentity == ticket.RootIdentity
                && (!compareReceipt || (r.StageFileReceiptJson == fileJson && r.ProductContextJson == sizeJson
                    && r.StageWriterAdmissionPending == admissionPending)))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageCapacityReservationStatuses.Released)
                    .SetProperty(r => r.ReleasedAt, releasedAt).SetProperty(r => r.UpdatedAt, releasedAt), ct);
            if (changed != 1)
                throw new InvalidOperationException("Stage reservation changed before release.");
            await StorageCapacityAdmission.AdvanceEpochAsync(db, ct);
            commitStarted = true;
            await transaction.CommitAsync(ct);
        }
        catch (Exception) when (commitStarted && !ct.IsCancellationRequested)
        {
            // The transaction has been disposed before this fresh read. A lost
            // acknowledgement is successful only for the same durable receipt.
            try
            {
                if (await db.StorageCapacityReservations.AsNoTracking().AnyAsync(r =>
                    r.IdempotencyKey == ticket.Key && r.ResourceKey == ticket.ResourceKey
                    && r.BindingHash == ticket.BindingHash && r.GroupId == ticket.GroupId
                    && r.Bytes == ticket.SizeBytes && r.ObjectId == ticket.ObjectId
                    && r.LocalLocator == ticket.RelativePath
                    && r.StageWriterMarkerIdentity == ticket.WriterMarkerIdentity && r.StageRootIdentity == ticket.RootIdentity
                    && (!compareReceipt || (r.StageFileReceiptJson == fileJson && r.ProductContextJson == sizeJson
                        && r.StageWriterAdmissionPending == admissionPending))
                    && r.Status == StorageCapacityReservationStatuses.Released && r.ReleasedAt != null, ct))
                    return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Unavailable proof preserves the original failure and marker.
            }
            throw;
        }
    }
}
