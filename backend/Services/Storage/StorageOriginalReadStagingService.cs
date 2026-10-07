using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services.Storage;

public interface IStorageOriginalReadStagingService
{
    Task<FileStream?> VerifyAsync(string identity, string locator, long sizeBytes, string sha256,
        Func<CancellationToken, Task<StorageReadResult>> openRead, CancellationToken ct);
}

// Only verified originals use this path. Admission precedes the provider read;
// the returned read-only file remains charged by real disk usage until disposal.
public sealed class StorageOriginalReadStagingService(IStorageStageCapacityService stages,
    IWebHostEnvironment environment, IConfiguration configuration, IOptionsMonitor<StorageOrchestrationOptions> options,
    ILogger<StorageOriginalReadStagingService> logger) : IStorageOriginalReadStagingService
{
    public async Task<FileStream?> VerifyAsync(string identity, string locator, long sizeBytes, string sha256,
        Func<CancellationToken, Task<StorageReadResult>> openRead, CancellationToken ct)
    {
        if (!options.CurrentValue.UploadCapacityChecksEnabled || sizeBytes <= 0
            || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;
        var root = Path.Combine(LocalStoragePaths.ResolveRoot(environment, configuration), ".original-read-staging");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        StorageStageCapacityTicket? ticket = null;
        FileStream? verified = null;
        var handedOff = false;
        var created = false;
        string? fileIdentity = null;
        long writtenBytes = 0;
        var writtenSha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
        try
        {
            var reservation = await stages.ReserveAsync("original-read:" + identity, path, sizeBytes, ct);
            ticket = reservation.Ticket;
            if (ticket is null) return null;
            await using var read = await openRead(ct);
            if (read.Status != StorageOperationStatus.Succeeded || read.Locator != locator || read.Content is null) return null;
            StorageStageFileReceipt completed;
            try
            {
                completed = await stages.WriteCheckpointedFileAsync(ticket, async (output, initial, token) =>
                {
                    // The callback is reached only after the owned empty file and
                    // its native identity have been durably confirmed by the writer.
                    created = true;
                    fileIdentity = initial.FileIdentity;
                    var buffer = ArrayPool<byte>.Shared.Rent(81920);
                    try
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long size = 0;
                        int count;
                        while ((count = await read.Content.ReadAsync(buffer.AsMemory(0, 81920), token)) > 0)
                        {
                            if (count > sizeBytes - size) throw new OriginalVerificationException();
                            size += count;
                            hash.AppendData(buffer, 0, count);
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                            writtenBytes = size;
                            writtenSha256 = Convert.ToHexString(hash.GetCurrentHash());
                        }
                        if (size != sizeBytes || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
                            throw new OriginalVerificationException();
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
                }, ct);
            }
            catch (OriginalVerificationException) { return null; }
            writtenBytes = completed.PlannedBytes;
            writtenSha256 = completed.PlannedSha256;
            await stages.SettlePhysicalAsync(ticket, ct);
            verified = await VerifiedOriginalReadFile.OpenVerifiedAsync(path, fileIdentity, sizeBytes, sha256,
                () => logger.LogWarning("Verified original read file retained after disposal for {ReservationKey}", ticket.Key), ct);
            if (verified is null) return null;
            handedOff = true;
            return verified;
        }
        finally
        {
            try
            {
                if (!handedOff)
                {
                    if (verified is not null) await verified.DisposeAsync();
                    try
                    {
                        if (created && !await VerifiedOriginalReadFile.TryDeleteAsync(path, fileIdentity, writtenBytes, writtenSha256))
                            // Small writes may remain entirely in the coalescing
                            // buffer. Only the separately confirmed empty boundary
                            // of this same native file can authorize that cleanup.
                            await VerifiedOriginalReadFile.TryDeleteAsync(path, fileIdentity, 0, StorageStageFileReceipt.EmptySha256);
                        if (ticket is not null)
                        {
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            await stages.ReleaseAsync(ticket, cleanup.Token);
                        }
                    }
                    catch (Exception error)
                    {
                        // Preserve the provider/cancellation error; pending debt is
                        // retained for the existing bounded stage reconciler.
                        logger.LogWarning(error, "Verified original staging cleanup failed for {ReservationKey}", ticket?.Key);
                    }
                }
            }
            finally
            {
                if (ticket is not null) await ticket.CloseWriterAsync();
            }
        }
    }

    private sealed class OriginalVerificationException : Exception;
}
