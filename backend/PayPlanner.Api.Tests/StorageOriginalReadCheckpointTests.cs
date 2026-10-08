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
    [InlineData(false, "large")]
    [InlineData(true, "large")]
    [InlineData(false, "short")]
    [InlineData(true, "short")]
    [InlineData(false, "long")]
    [InlineData(true, "long")]
    [InlineData(false, "sha")]
    [InlineData(true, "sha")]
    [InlineData(false, "provider")]
    [InlineData(true, "provider")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task OriginalReadProducerCoalescesAndPreservesSourceFailures(bool postgres, string scenario)
    {
        var commits = new CheckpointCommitFault();
        await using var f = await CreateWriterFixtureAsync(postgres, commits);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = Path.Combine(f.Root, "objects"),
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var service = new StorageOriginalReadStagingService(f.Stages, new TestEnvironment(f.Root), configuration,
            new StaticOptions(new() { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 }),
            NullLogger<StorageOriginalReadStagingService>.Instance);
        var expected = scenario == "large" ? new byte[StorageStageFileReceipt.MaximumBlockBytes + 4] : new byte[] { 1, 2, 3, 4 };
        expected[0] = 1; expected[^1] = 4;
        var bytes = scenario switch
        {
            "short" => new byte[] { 1, 2, 3 },
            "long" => new byte[] { 1, 2, 3, 4, 5 },
            "sha" => new byte[] { 4, 3, 2, 1 },
            _ => expected
        };
        var sha = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        OriginalCheckpointMeasuredSource? measuredSource = null;
        Task<FileStream?> Run() => service.VerifyAsync("checkpoint-original-boundary:" + Guid.NewGuid().ToString("N"),
            "owned-original", expected.Length, sha, _ =>
            {
                reads++;
                commits.Commits = 0; // exclude the two admission transactions
                Stream source = scenario is "provider" or "cancel"
                    ? new OriginalCheckpointFailingSource(bytes, scenario == "cancel" ? cancellation : null)
                    : measuredSource = new OriginalCheckpointMeasuredSource(bytes);
                return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", source, bytes.Length));
            }, cancellation.Token);
        FileStream? verified = null;
        var measurement = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (scenario == "provider")
                Assert.Equal("Owned original source failed.", (await Assert.ThrowsAsync<IOException>(Run)).Message);
            else if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
            else verified = await Run();
            measurement.Stop();
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(1, reads);
            if (scenario == "large")
            {
                Assert.NotNull(verified);
                Assert.False(verified.CanWrite);
                using var output = new MemoryStream();
                await verified.CopyToAsync(output);
                Assert.Equal(expected, output.ToArray());
                Assert.Equal(2, proof!.Sequence);
                Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, proof.PreviousBytes);
                Assert.Equal(expected.Length, proof.PlannedBytes);
                Assert.Equal(sha, proof.PlannedSha256);
                Assert.Equal(4, commits.Commits); // initial + two bounded blocks + settlement
                Assert.NotNull(measuredSource);
                Assert.Equal(expected.Length, measuredSource.BytesRead);
                Assert.Equal((expected.Length + 81919) / 81920 + 1, measuredSource.ReadCalls);
                Console.WriteLine($"Owned original-read metrics: database={(postgres ? "PostgreSQL" : "SQLite")}; bytes={expected.Length}; sourceReads={measuredSource.ReadCalls}; checkpointCommits={proof.Sequence + 1}; commitsAfterAdmission={commits.Commits}; elapsedMs={measurement.ElapsedMilliseconds}");
            }
            else
            {
                Assert.Null(verified);
                Assert.Equal(0, proof!.Sequence);
                Assert.Equal(0, proof.PlannedBytes); // failed callback never flushes the buffered tail
            }
        }
        finally { if (verified is not null) await verified.DisposeAsync(); }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root, "objects", ".original-read-staging"), "*.tmp"));
    }

    private sealed class OriginalCheckpointFailingSource(byte[] bytes, CancellationTokenSource? cancellation) : MemoryStream(bytes)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_read)
            {
                _read = true;
                return base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
            }
            if (cancellation is not null)
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("Owned original source failed.");
        }
    }

    private sealed class OriginalCheckpointMeasuredSource(byte[] bytes) : MemoryStream(bytes)
    {
        public long BytesRead { get; private set; }
        public int ReadCalls { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ReadCalls++;
            var count = await base.ReadAsync(buffer, ct);
            BytesRead += count;
            return count;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalReadProducerPersistsCheckpointBeforeReadonlyHandoff(bool postgres)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = Path.Combine(f.Root, "objects"),
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var service = new StorageOriginalReadStagingService(f.Stages, new TestEnvironment(f.Root), configuration,
            new StaticOptions(new() { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 }),
            NullLogger<StorageOriginalReadStagingService>.Instance);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reads = 0;
        var verified = await service.VerifyAsync("checkpoint-original:" + Guid.NewGuid().ToString("N"), "owned-original", bytes.Length, sha,
            async ct =>
            {
                reads++;
                var admitted = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(ct);
                Assert.Equal(StorageCapacityReservationStatuses.Pending, admitted.Status);
                Assert.False(admitted.StageWriterAdmissionPending);
                return new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", new MemoryStream(bytes), bytes.Length);
            }, default);
        Assert.NotNull(verified);
        var path = verified.Name;
        try
        {
            Assert.False(verified.CanWrite);
            Assert.True(verified.CanRead);
            using var output = new MemoryStream();
            await verified.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
            Assert.Equal(1, reads);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof), "Actual original-read producer must persist a checkpoint receipt.");
            Assert.Equal(row.ObjectId, proof!.ObjectId);
            Assert.Equal(bytes.Length, proof.PlannedBytes);
            Assert.Equal(sha, proof.PlannedSha256);
            Assert.Equal(1, proof.Sequence);
        }
        finally { await verified.DisposeAsync(); }
        Assert.False(File.Exists(path));
    }
}
