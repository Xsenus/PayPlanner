using System.Security.Cryptography;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OriginalReadCopyBudgetCreditsPhysicalPrefixAndMeasuresBoundedSql(bool postgres, bool denyTail)
    {
        var sql = new OriginalCopyBudgetMetrics();
        var commits = new CheckpointCommitFault();
        await using var f = await CreateWriterFixtureAsync(postgres, sql, commits);
        const long availableBound = 64L * 1024 * 1024;
        var (_, service, policy, root) = CreateSharedBudgetServices(f,
            local => new OriginalCopyBudgetNativeProvider(local, availableBound));
        RequireNativeCapacity();
        const long copyBytes = 8L * 1024 * 1024;
        var operation = await AddOwnedCopyBudgetOperationAsync(f, copyBytes, StorageOperationStatuses.Pending, "local");
        var bytes = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
        bytes[0] = 1; bytes[^1] = 2;
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var raised = 0;
        var source = new OriginalCopyBudgetMeasuredSource(bytes, async () =>
        {
            raised++;
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
            Assert.Equal(1, receipt!.Sequence);
            Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, receipt.PlannedBytes);
            Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, new FileInfo(Path.Combine(root, row.LocalLocator)).Length);
            RequireNativeCapacity();
            var headroom = copyBytes * 2 + 4 + (denyTail ? -1 : 1) * 1024 * 1024;
            policy.UploadMinimumFreeBytes = availableBound - headroom;
            Assert.True(headroom > 4); // Its own unwritten tail still fits.
            if (!denyTail) Assert.True(headroom < copyBytes * 2 + bytes.LongLength);
        });
        Task<FileStream?> Run() => service.VerifyAsync("copy-prefix:" + Guid.NewGuid().ToString("N"), "owned-original", bytes.LongLength, sha,
            _ =>
            {
                sql.CopyGroups = sql.ReservationGroups = commits.Commits = 0;
                return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", source, bytes.LongLength));
            }, default);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        FileStream? result = null;
        try
        {
            if (denyTail) await Assert.ThrowsAsync<InvalidOperationException>(Run);
            else result = await Run();
            timer.Stop();
            Assert.Equal(1, raised);
            Assert.Equal(bytes.LongLength, source.BytesRead);
            Assert.Equal((bytes.Length + 81919) / 81920 + 1, source.ReadCalls);
            var own = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(own.StageFileReceiptJson, out var proof));
            Assert.Equal(denyTail ? 1 : 2, proof!.Sequence);
            Assert.Equal(denyTail ? StorageStageFileReceipt.MaximumBlockBytes : bytes.LongLength, proof.PlannedBytes);
            Assert.Equal(denyTail ? StorageCapacityReservationStatuses.Pending : StorageCapacityReservationStatuses.Released, own.Status);
            Assert.Equal(denyTail ? 5 : 6, sql.CopyGroups);
            Assert.Equal(sql.CopyGroups, sql.ReservationGroups);
            Assert.Equal(denyTail ? 2 : 4, commits.Commits);
            if (denyTail)
            {
                Assert.Null(result);
                var retained = await File.ReadAllBytesAsync(Path.Combine(root, own.LocalLocator));
                Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, retained.Length);
                Assert.Equal(proof.PlannedSha256, Convert.ToHexString(SHA256.HashData(retained)).ToLowerInvariant());
            }
            else
            {
                Assert.NotNull(result);
                Assert.False(result.CanWrite);
                Assert.Equal(bytes.LongLength, result.Length);
                using var actual = new MemoryStream();
                await result.CopyToAsync(actual);
                Assert.Equal(bytes, actual.ToArray());
            }
            Assert.Equal(StorageOperationStatuses.Pending, await f.Db.StorageOperations.Where(o => o.Id == operation.Id).Select(o => o.Status).SingleAsync());
            Assert.Equal(1, await f.Db.StorageObjects.CountAsync());
            Console.WriteLine($"Owned copy-budget producer metrics: database={(postgres ? "PostgreSQL" : "SQLite")}; denyTail={denyTail}; bytes={source.BytesRead}; sourceReads={source.ReadCalls}; copyGroups={sql.CopyGroups}; reservationGroups={sql.ReservationGroups}; commitsAfterAdmission={commits.Commits}; elapsedMs={timer.ElapsedMilliseconds}");
        }
        finally { if (result is not null) await result.DisposeAsync(); }
        if (!denyTail) Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ".original-read-staging"), "*.tmp"));

        void RequireNativeCapacity()
        {
            var native = LocalStorageCapacityProbe.Observe(root, "owned-native", 0);
            var spool = LocalStorageCapacityProbe.Observe(Path.GetTempPath(), "owned-spool", 0);
            Assert.False(string.IsNullOrWhiteSpace(native.GroupId));
            Assert.Equal(native.GroupId, spool.GroupId);
            Assert.True(native.AvailableBytes >= availableBound && spool.AvailableBytes >= availableBound,
                $"Copy-budget fixture requires at least {availableBound} native bytes; root={native.AvailableBytes}, spool={spool.AvailableBytes}.");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OriginalReadProducerHonorsPendingLocalCopyAndSpoolDebtAfterMinimumChanges(bool postgres, bool beforeCreate)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var (stages, service, policy, root) = CreateSharedBudgetServices(f);
        var native = LocalStorageCapacityProbe.Observe(root, "owned-local", 0);
        var spool = LocalStorageCapacityProbe.Observe(Path.GetTempPath(), "owned-spool", 0);
        Assert.Equal(native.GroupId, spool.GroupId);
        Assert.True(native.AvailableBytes > 64L * 1024 * 1024);
        const long copyBytes = 8L * 1024 * 1024;
        var item = new StorageObject
        {
            DataClass = "owned-budget",
            OwnerType = "owned-test",
            OwnerId = Guid.NewGuid().ToString("N"),
            LogicalName = "owned-copy.bin",
            CurrentGeneration = 1,
            Version = 1
        };
        var generation = new StorageObjectGeneration
        {
            StorageObject = item,
            Generation = 1,
            SizeBytes = copyBytes,
            Sha256 = new string('a', 64)
        };
        var operation = new StorageOperation
        {
            StorageObject = item,
            StorageObjectGeneration = generation,
            ProviderKey = "local",
            OperationType = StorageOperationTypes.Replicate,
            Status = StorageOperationStatuses.Pending,
            IdempotencyKey = "owned-copy-budget:" + Guid.NewGuid().ToString("N")
        };
        f.Db.StorageOperations.Add(operation);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        var bytes = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
        bytes[0] = 1; bytes[^1] = 2;
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var raised = 0;
        void RaiseMinimum()
        {
            raised++;
            var current = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(root, "owned-local", 0).AvailableBytes);
            var headroom = bytes.LongLength + copyBytes;
            policy.UploadMinimumFreeBytes = current - headroom;
            Assert.True(headroom >= bytes.LongLength);
            Assert.True(headroom < bytes.LongLength + copyBytes * 2);
        }
        async Task Run()
        {
            await using var result = await service.VerifyAsync("copy-spool-original:" + Guid.NewGuid().ToString("N"),
                "owned-original", bytes.LongLength, sha, _ =>
                {
                    if (beforeCreate) RaiseMinimum();
                    Stream source = beforeCreate ? new MemoryStream(bytes, writable: false)
                        : new OriginalBoundarySource(bytes, RaiseMinimum);
                    return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", source, bytes.LongLength));
                }, default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(Run);
        Assert.Equal(1, raised);
        var own = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, own.Status);
        if (beforeCreate) Assert.Null(own.StageFileReceiptJson);
        else
        {
            Assert.True(StorageStageFileReceipt.TryParse(own.StageFileReceiptJson, out var proof));
            Assert.Equal(0, proof!.Sequence);
            Assert.Equal(0, proof.PlannedBytes);
        }
        Assert.Equal(StorageOperationStatuses.Pending, await f.Db.StorageOperations.Where(o => o.Id == operation.Id).Select(o => o.Status).SingleAsync());
        Assert.Equal(copyBytes, await f.Db.StorageObjectGenerations.Where(g => g.Id == generation.Id).Select(g => g.SizeBytes).SingleAsync());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ".original-read-staging"), "*.tmp"));
        Assert.Equal(1, await f.Db.StorageObjects.CountAsync());
    }

    // Cap only this fixture's destination budget; root/spool and native identity remain real.
    // A loss of actual capacity below the cap must still reach the production guard.
    private sealed class OriginalCopyBudgetNativeProvider(LocalStorageProvider inner, long availableBound)
        : IStorageProvider, IStorageCapacityProvider
    {
        public string Key => inner.Key;
        public StorageCapacityDomain CapacityDomain => inner.CapacityDomain;
        public StorageProviderCapabilities Capabilities => inner.Capabilities;
        public async Task<StorageCapacityResource> ObserveCapacityAsync(string key, string? connection,
            long minimum, CancellationToken ct)
        {
            var physical = await inner.ObserveCapacityAsync(key, connection, minimum, ct);
            return physical with
            {
                AvailableBytes = physical.AvailableBytes is { } actual ? Math.Min(actual, availableBound) : null
            };
        }
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => inner.PutAsync(request, ct);
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => inner.HeadAsync(locator, ct);
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => inner.OpenReadAsync(locator, ct);
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => inner.DeleteAsync(locator, ct);
    }

    private sealed class OriginalCopyBudgetMeasuredSource(byte[] bytes, Func<Task> afterPrefix) : MemoryStream(bytes, writable: false)
    {
        private bool _raised;
        internal long BytesRead;
        internal int ReadCalls;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_raised && Position >= StorageStageFileReceipt.MaximumBlockBytes) { _raised = true; await afterPrefix(); }
            var count = await base.ReadAsync(buffer, ct);
            ReadCalls++;
            BytesRead += count;
            return count;
        }
    }

    private sealed class OriginalCopyBudgetMetrics : DbCommandInterceptor
    {
        internal int CopyGroups, ReservationGroups;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("storage-capacity-pending-budget", StringComparison.Ordinal)) CopyGroups++;
            if (command.CommandText.Contains("storage-stage-pending-reservation-budget", StringComparison.Ordinal)) ReservationGroups++;
            return ValueTask.FromResult(result);
        }
    }
}
