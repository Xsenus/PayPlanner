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
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalReadProducerRequiresUnwrittenHeadroomEvenWhenMinimumIsStillMet(bool postgres)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = Path.Combine(f.Root, "objects"),
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var environment = new TestEnvironment(f.Root);
        var policy = new StorageOrchestrationOptions { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 };
        var monitor = new StaticOptions(policy);
        var provider = new LocalStorageProvider(environment, configuration);
        var stages = new StorageStageCapacityService(f.Db, new StorageProviderRegistry([provider]), monitor, environment, configuration);
        var service = new StorageOriginalReadStagingService(stages, environment, configuration, monitor,
            NullLogger<StorageOriginalReadStagingService>.Instance);
        var storageRoot = Path.Combine(f.Root, "objects");
        var available = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(storageRoot, "owned-observation", 0).AvailableBytes);
        Assert.True(available > 64L * 1024 * 1024, "Owned capacity fixture needs enough free space for its database and bounded block.");
        var upperBound = available / 2;
        var bytes = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
        bytes[0] = 1; bytes[^1] = 2;
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sourceReads = 0;
        async Task Run()
        {
            await using var verified = await service.VerifyAsync("original-remaining-capacity:" + Guid.NewGuid().ToString("N"),
                "owned-original", upperBound, sha, async ct =>
                {
                    var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(ct);
                    Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
                    Assert.False(row.StageWriterAdmissionPending);
                    return new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original",
                        new OriginalBoundarySource(bytes, () =>
                        {
                            sourceReads++;
                            var current = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(storageRoot, "owned-observation", 0).AvailableBytes);
                            var headroom = Math.Min(upperBound / 2, current / 4);
                            Assert.True(headroom > 0);
                            policy.UploadMinimumFreeBytes = current - headroom;
                            var observed = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(storageRoot, "owned-observation", 0).AvailableBytes);
                            Assert.True(observed >= policy.UploadMinimumFreeBytes, "The new minimum itself is still met.");
                            Assert.True(observed - policy.UploadMinimumFreeBytes < upperBound, "The unused admitted upper bound no longer fits.");
                        }), bytes.Length);
                }, default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(Run);
        var saved = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, saved.Status);
        Assert.True(StorageStageFileReceipt.TryParse(saved.StageFileReceiptJson, out var proof));
        Assert.Equal(0, proof!.Sequence);
        Assert.Equal(0, proof.PlannedBytes);
        Assert.Equal(1, sourceReads);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root, "objects", ".original-read-staging"), "*.tmp"));
        Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
    }
}
