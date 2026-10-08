using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task OriginalReadProducerResolvesPlannedCommitAck(bool postgres, bool afterCommit)
    {
        var fault = new CheckpointCommitFault { AfterCommit = afterCommit };
        await using var f = await CreateWriterFixtureAsync(postgres, fault);
        var service = CreateOriginalBoundaryReader(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Task<FileStream?> Run() => service.VerifyAsync("original-ack:" + Guid.NewGuid().ToString("N"), "owned-original", bytes.Length, sha,
            _ => Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original",
                new OriginalBoundarySource(bytes, () => fault.Armed = true), bytes.Length)), default);
        FileStream? verified = null;
        try
        {
            if (afterCommit)
            {
                verified = await Run();
                Assert.NotNull(verified);
                var output = new byte[bytes.Length];
                Assert.Equal(bytes.Length, await verified.ReadAsync(output));
                Assert.Equal(bytes, output);
                Assert.False(verified.CanWrite);
            }
            else Assert.Equal("owned checkpoint before commit", (await Assert.ThrowsAsync<IOException>(Run)).Message);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(afterCommit ? 1 : 0, proof!.Sequence);
            Assert.Equal(afterCommit ? bytes.Length : 0, proof.PlannedBytes);
            Assert.False(fault.Armed);
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { if (verified is not null) await verified.DisposeAsync(); }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root, "objects", ".original-read-staging"), "*.tmp"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OriginalReadProducerRetainsDebtAfterSourceFailureBeyondPhysicalBlock(bool postgres, bool cancel)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var service = CreateOriginalBoundaryReader(f);
        var prefix = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
        prefix[0] = 1; prefix[StorageStageFileReceipt.MaximumBlockBytes - 1] = 2; prefix[^1] = 3;
        var expected = new byte[prefix.Length + 5];
        prefix.CopyTo(expected, 0);
        var sha = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        using var cancellation = new CancellationTokenSource();
        Task<FileStream?> Run() => service.VerifyAsync("original-source-fault:" + Guid.NewGuid().ToString("N"), "owned-original",
            expected.Length, sha, _ => Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original",
                new OriginalBoundarySource(prefix, failure: () =>
                {
                    if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                    throw new IOException("Owned original source failed after physical block.");
                }), prefix.Length)), cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else Assert.Equal("Owned original source failed after physical block.", (await Assert.ThrowsAsync<IOException>(Run)).Message);
        var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
        Assert.Equal(1, proof!.Sequence);
        Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, proof.PlannedBytes);
        var path = Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Root, "objects", ".original-read-staging"), "*.tmp"));
        var physical = await File.ReadAllBytesAsync(path);
        Assert.Equal(prefix[..StorageStageFileReceipt.MaximumBlockBytes], physical);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(physical)).ToLowerInvariant(), proof.PlannedSha256);
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(proof.FileIdentity, StorageStageFileIdentity.Observe(file.SafeFileHandle));
        var json = row.StageFileReceiptJson;
        var old = DateTime.UtcNow.AddMinutes(-30);
        await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, old));
        await using var fresh = f.NewContext();
        var recovery = f.CreateStages(fresh);
        foreach (var apply in new[] { false, true })
        {
            var report = await recovery.ReconcilePendingAsync(apply, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("incompleteFile", Assert.Single(report.Items).Status);
            Assert.Equal(0, report.Settled);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(row.IdempotencyKey));
        }
        Assert.Equal(json, await fresh.StorageCapacityReservations.AsNoTracking().Select(r => r.StageFileReceiptJson).SingleAsync());
        Assert.Equal(physical, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
    }

    private static StorageOriginalReadStagingService CreateOriginalBoundaryReader(WriterProcessFixture f)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = Path.Combine(f.Root, "objects"),
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        return new(f.Stages, new TestEnvironment(f.Root), configuration,
            new StaticOptions(new() { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 }),
            NullLogger<StorageOriginalReadStagingService>.Instance);
    }

    private sealed class OriginalBoundarySource(byte[] bytes, Action? beforeRead = null, Action? failure = null) : MemoryStream(bytes)
    {
        private bool _started;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_started) { _started = true; beforeRead?.Invoke(); }
            if (Position == Length) failure?.Invoke();
            return base.ReadAsync(buffer, ct);
        }
    }
}
