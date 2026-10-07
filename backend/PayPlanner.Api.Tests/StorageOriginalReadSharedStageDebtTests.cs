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
    public async Task OriginalReadProducerHonorsOtherPendingStageDebtAfterMinimumChanges(bool postgres, bool beforeCreate)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var root = Path.Combine(f.Root, "objects");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = root,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var environment = new TestEnvironment(f.Root);
        var policy = new StorageOrchestrationOptions { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 };
        var monitor = new StaticOptions(policy);
        var stages = new StorageStageCapacityService(f.Db,
            new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]), monitor, environment, configuration);
        var service = new StorageOriginalReadStagingService(stages, environment, configuration, monitor,
            NullLogger<StorageOriginalReadStagingService>.Instance);
        const long otherUpper = 8L * 1024 * 1024;
        var bytes = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
        bytes[0] = 1; bytes[^1] = 2;
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.True(LocalStorageCapacityProbe.Observe(root, "owned-observation", 0).AvailableBytes > 64L * 1024 * 1024);
        var other = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync(
            "shared-stage-other:" + Guid.NewGuid().ToString("N"), Path.Combine(root, "other-pending.tmp"), otherUpper, default)).Ticket);
        var raised = 0;
        Guid ownId = Guid.Empty;
        void RaiseMinimum()
        {
            raised++;
            var current = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(root, "owned-observation", 0).AvailableBytes);
            var headroom = bytes.LongLength + otherUpper / 2;
            policy.UploadMinimumFreeBytes = current - headroom;
            Assert.True(headroom >= bytes.LongLength);
            Assert.True(headroom < bytes.LongLength + otherUpper);
        }
        try
        {
            async Task Run()
            {
                await using var result = await service.VerifyAsync("shared-stage-original:" + Guid.NewGuid().ToString("N"),
                    "owned-original", bytes.LongLength, sha, async ct =>
                    {
                        var own = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ObjectId != other.ObjectId, ct);
                        ownId = own.ObjectId;
                        if (beforeCreate) RaiseMinimum();
                        Stream source = beforeCreate ? new MemoryStream(bytes, writable: false)
                            : new OriginalBoundarySource(bytes, RaiseMinimum);
                        return new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", source, bytes.LongLength);
                    }, default);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.Equal(1, raised);
            var own = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ObjectId == ownId);
            Assert.Equal(StorageCapacityReservationStatuses.Released, own.Status);
            if (beforeCreate) Assert.Null(own.StageFileReceiptJson);
            else
            {
                Assert.True(StorageStageFileReceipt.TryParse(own.StageFileReceiptJson, out var proof));
                Assert.Equal(0, proof!.Sequence);
                Assert.Equal(0, proof.PlannedBytes);
            }
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(other.Key));
            Assert.False(File.Exists(other.AbsolutePath));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, ".original-read-staging"), "*.tmp"));
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { await other.CloseWriterAsync(); }
    }
}
