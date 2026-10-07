using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Storage;

public sealed record ManagedPhotoUploadResult(string Status, string? PhotoUrl = null);
public interface IManagedProfilePhotoUploadService
{
    bool Enabled { get; }
    Task<ManagedPhotoUploadResult> UploadAsync(int userId, Guid uploadId, IFormFile file, string baseUrl, CancellationToken ct);
}

public sealed class ManagedProfilePhotoUploadService(PaymentContext db, IStorageObjectService storage,
    IProductStorageBindingService publisher, IWebHostEnvironment environment, IConfiguration configuration,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    IStorageStageCapacityService? stageCapacity = null) : IManagedProfilePhotoUploadService
{
    private const long MaxSize = 4L * 1024 * 1024;
    public bool Enabled => options.CurrentValue.Enabled
        && options.CurrentValue.ProductWriteEnabledClasses.Contains("profile-photo", StringComparer.Ordinal);

    public async Task<ManagedPhotoUploadResult> UploadAsync(int userId, Guid uploadId, IFormFile file, string baseUrl, CancellationToken ct)
    {
        if (!Enabled) return new("disabled");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var contentType = file.ContentType?.Trim().ToLowerInvariant();
        if (userId <= 0 || uploadId == Guid.Empty || file.Length <= 0 || file.Length > MaxSize
            || extension is not (".png" or ".jpg" or ".jpeg" or ".webp")
            || (!string.IsNullOrEmpty(contentType) && contentType is not ("image/png" or "image/jpeg" or "image/webp"))) return new("invalid");
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.PhotoUrl }).SingleOrDefaultAsync(ct);
        if (user is null) return new("notFound");
        var version = await db.StorageProductBindings.AsNoTracking()
            .Where(b => b.Source == LegacyStorageSources.ProfilePhotos && b.SourceId == userId)
            .Select(b => (long?)b.Version).SingleOrDefaultAsync(ct) ?? 0;
        var name = $"upload-{uploadId:N}{extension}";
        var publicUrl = $"{baseUrl.TrimEnd('/')}/user-photos/{userId}/{name}";
        var stagingRoot = Path.Combine(LocalStoragePaths.ResolveRoot(environment, configuration), ".photo-uploads");
        Directory.CreateDirectory(stagingRoot);
        var stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N") + ".tmp");
        StorageStageCapacityTicket? stageTicket = null;
        StorageStageCapacityTicket? compatibilityTicket = null;
        var ownsStage = false;
        var ownsCompatibilityStage = false;
        try
        {
            if (options.CurrentValue.UploadCapacityChecksEnabled)
            {
                if (stageCapacity is null) return new("unavailable");
                var (ticket, _) = await stageCapacity.ReserveAsync($"profile-photo:{userId}:{uploadId:N}", stage, file.Length, ct);
                if (ticket is null) return new("unavailable");
                stageTicket = ticket;
            }
            long size = 0;
            string sha;
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                ownsStage = true;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (read > MaxSize - size || read > file.Length - size) return new("invalid");
                    size += read;
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            if (size != file.Length) return new("invalid");
            if (stageTicket is not null) await stageCapacity!.SettlePhysicalAsync(stageTicket, ct);
            var photoDirectory = Path.Combine(UserPhotoStoragePaths.ResolveRoot(environment, configuration), userId.ToString());
            Directory.CreateDirectory(photoDirectory);
            var photoPath = Path.Combine(photoDirectory, name);
            if (options.CurrentValue.UploadCapacityChecksEnabled && !File.Exists(photoPath))
            {
                var temporary = Path.Combine(photoDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                var (ticket, _) = await stageCapacity!.ReserveCompatibilityAsync(
                    $"profile-photo:{userId}:{uploadId:N}:compat", temporary, size, "profile-current", ct);
                if (ticket is null) return new("unavailable");
                compatibilityTicket = ticket;
            }
            var proposed = new UploadContext(userId, user.PhotoUrl, publicUrl, version, sha, size);
            if (options.CurrentValue.UploadCapacityChecksEnabled)
            {
                var reservedJson = await StorageUploadCapacityReservations.ReadProductContextAsync(db,
                    StorageObjectService.GetCapacityReservationKey($"profile-photo:{userId}:{uploadId:N}"), ct);
                if (reservedJson is not null)
                {
                    UploadContext? reserved;
                    try { reserved = JsonSerializer.Deserialize<UploadContext>(reservedJson); }
                    catch (JsonException) { return new("conflict"); }
                    if (reserved is null || reserved.UserId != userId || reserved.NewPhotoUrl != publicUrl
                        || reserved.Sha256 != sha || reserved.SizeBytes != size || reserved.Version < 0 || reserved.Version == long.MaxValue)
                        return new("conflict");
                    proposed = reserved;
                }
            }
            await using var stagedInput = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var write = await storage.WriteAsync(new("profile-photo", "profile-photo", userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                name, "image/*", stagedInput, $"profile-photo:{userId}:{uploadId:N}", userId, JsonSerializer.Serialize(proposed), size, sha), ct);
            if (write.ObjectId == Guid.Empty) return new("unavailable");
            var savedJson = await db.StorageOperations.AsNoTracking()
                .Where(o => o.StorageObjectId == write.ObjectId && o.OperationType == "commit")
                .Select(o => o.PayloadJson).SingleOrDefaultAsync(ct);
            UploadContext? saved;
            try { saved = savedJson is null ? null : JsonSerializer.Deserialize<UploadContext>(savedJson); }
            catch (JsonException) { return new("conflict"); }
            if (saved is null || saved.UserId != userId || saved.Sha256 != sha || saved.SizeBytes != size
                || saved.Version < 0 || saved.Version == long.MaxValue
                || !Uri.TryCreate(saved.NewPhotoUrl, UriKind.Absolute, out var savedUri)
                || savedUri.Scheme is not ("http" or "https")
                || savedUri.AbsolutePath != $"/user-photos/{userId}/{name}"
                || savedUri.Query.Length != 0 || savedUri.Fragment.Length != 0) return new("conflict");
            if (write.Status != "available") return new("pending");

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Lock the domain row and verify the original snapshot or an exact retry.
            var locked = await db.Users.Where(u => u.Id == userId && (u.PhotoUrl == saved.OldPhotoUrl || u.PhotoUrl == saved.NewPhotoUrl))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.UpdatedAt, u => u.UpdatedAt), ct);
            if (locked != 1) return new("conflict");
            var result = await publisher.PublishAsync(new(LegacyStorageSources.ProfilePhotos, userId, write.ObjectId,
                name, "image/*", saved.Version), ct);
            if (!result.Published) return new(result.Status == "notReady" ? "pending" : "conflict");

            // Keep a verified compatibility copy for an explicit rollback to legacy
            // reading. Neither the previous photo nor an older object is deleted.
            if (File.Exists(photoPath))
            {
                await using var existing = new FileStream(photoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                if (existing.Length != size || Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).ToLowerInvariant() != sha)
                    return new("conflict");
            }
            else
            {
                if (options.CurrentValue.UploadCapacityChecksEnabled && compatibilityTicket is null)
                    throw new InvalidOperationException("Photo compatibility copy has no capacity reservation.");
                var compatibilityStage = compatibilityTicket?.AbsolutePath
                    ?? Path.Combine(photoDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await ExclusiveUploadFileWriter.WriteNewAsync(compatibilityStage, async (output, token) =>
                    {
                        ownsCompatibilityStage = true;
                        await using var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        await input.CopyToAsync(output, token);
                    }, ct);
                    File.Move(compatibilityStage, photoPath, overwrite: false);
                }
                finally { if (ownsCompatibilityStage && File.Exists(compatibilityStage)) File.Delete(compatibilityStage); }
            }
            var current = await db.Users.SingleAsync(u => u.Id == userId, ct);
            await db.Entry(current).ReloadAsync(ct);
            current.PhotoUrl = saved.NewPhotoUrl;
            current.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new("uploaded", saved.NewPhotoUrl);
        }
        finally
        {
            try
            {
                if (ownsStage && File.Exists(stage)) File.Delete(stage);
                if (compatibilityTicket is not null)
                {
                    if (ownsCompatibilityStage && File.Exists(compatibilityTicket.AbsolutePath)) File.Delete(compatibilityTicket.AbsolutePath);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await stageCapacity!.ReleaseAsync(compatibilityTicket, cleanup.Token); }
                    catch (InvalidOperationException) when (!ownsCompatibilityStage)
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
                if (compatibilityTicket is not null) await compatibilityTicket.CloseWriterAsync();
            }
        }
    }
    private sealed record UploadContext(int UserId, string? OldPhotoUrl, string NewPhotoUrl, long Version, string Sha256, long SizeBytes);
}
