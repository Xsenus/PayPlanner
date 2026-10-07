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
    public async Task OriginalReadProducerHonorsMinimumFreeRaisedAfterAdmission(bool postgres, bool afterInitialFile)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = Path.Combine(f.Root, "objects"),
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var policy = new StorageOrchestrationOptions { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0 };
        var monitor = new StaticOptions(policy);
        var environment = new TestEnvironment(f.Root);
        var stages = new StorageStageCapacityService(f.Db,
            new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]), monitor, environment, configuration);
        var service = new StorageOriginalReadStagingService(stages, environment, configuration, monitor,
            NullLogger<StorageOriginalReadStagingService>.Instance);
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        async Task Run()
        {
            await using var verified = await service.VerifyAsync("original-fresh-policy:" + Guid.NewGuid().ToString("N"),
                "owned-original", bytes.Length, sha, _ =>
                {
                    if (!afterInitialFile) policy.UploadMinimumFreeBytes = long.MaxValue;
                    var source = new OriginalBoundarySource(bytes, () =>
                    {
                        if (afterInitialFile) policy.UploadMinimumFreeBytes = long.MaxValue;
                    });
                    return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", source, bytes.Length));
                }, default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(Run);
        var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
        if (afterInitialFile)
        {
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(0, proof!.Sequence);
            Assert.Equal(0, proof.PlannedBytes);
        }
        else Assert.Null(row.StageFileReceiptJson);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(f.Root, "objects", ".original-read-staging"), "*.tmp"));
        Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
    }
}
