using System.Buffers;
using System.Security.Cryptography;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed partial class StorageStageCapacityService
{
    private sealed record StageReceiptSnapshot(string? FileJson, string? SizeJson, bool AdmissionPending);

    private static bool Matches(StorageCapacityReservation row, StorageStageCapacityTicket expected)
        => row.IdempotencyKey == expected.Key && row.ResourceKey == expected.ResourceKey
            && row.GroupId == expected.GroupId && row.BindingHash == expected.BindingHash
            && row.Bytes == expected.SizeBytes && row.ObjectId == expected.ObjectId
            && row.LocalLocator == expected.RelativePath
            && row.StageWriterMarkerIdentity == expected.WriterMarkerIdentity && row.StageRootIdentity == expected.RootIdentity;

    // The caller keeps this descriptor open through the exact receipt CAS. A
    // legacy row has no invented identity; its existing checks still apply.
    private async Task VerifyCheckpointFileAsync(FileStream file, StorageCapacityReservation row,
        StorageStageCapacityTicket ticket, long expectedBytes, CancellationToken ct)
    {
        if (row.StageFileReceiptJson is null) return;
        if (row.StageWriterAdmissionPending || !StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof)
            || !TryReadPhysicalSizeReceipt(row, out var sizeProof)
            || proof!.ObjectId != row.ObjectId || proof.ObjectId != ticket.ObjectId
            || proof.UpperBound != (sizeProof?.UpperBound ?? row.Bytes)
            || proof.AdmissionBindingHash != (sizeProof?.InitialBindingHash ?? row.BindingHash)
            || proof.PlannedBytes != expectedBytes || expectedBytes <= 0
            || !TryResolvePendingPath(row, out var path, out var root))
            throw new InvalidOperationException("Stage file receipt does not match its reservation.");
        var physical = LocalStorageCapacityProbe.Observe(root, row.ResourceKey, 0);
        if (physical.GroupId != row.GroupId || physical.AvailableBytes is null)
            throw new InvalidOperationException("Stage physical group changed before file verification.");
        void RequireDescriptor()
        {
            var observed = StorageStageFileObservation.Observe(file.SafeFileHandle);
            if (observed is null || observed.FileIdentity != proof.FileIdentity || observed.GroupId != row.GroupId
                || !string.Equals(observed.AbsolutePath, path, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !string.Equals(path, Path.GetFullPath(ticket.AbsolutePath), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || file.Length != expectedBytes)
                throw new InvalidOperationException("Stage file descriptor changed before settlement.");
        }
        RequireDescriptor();
        file.Position = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long readBytes = 0;
            int count;
            while ((count = await file.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(65536, expectedBytes - readBytes >= 65536 ? 65536 : expectedBytes - readBytes + 1)), ct).ConfigureAwait(false)) > 0)
            {
                if (count > expectedBytes - readBytes)
                    throw new InvalidOperationException("Stage file exceeded its confirmed boundary.");
                readBytes += count;
                hash.AppendData(buffer, 0, count);
            }
            if (readBytes != expectedBytes || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != proof.PlannedSha256)
                throw new InvalidOperationException("Stage file bytes changed before settlement.");
            RequireDescriptor();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}
