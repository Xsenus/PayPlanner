using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task SharedStageBudgetRechecksPolicyAndRootAfterSqlAwait(bool postgres, bool changeRoot)
    {
        var fault = new SharedBudgetConfigurationFault();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, fault);
        var root = Path.Combine(f.Root, "objects");
        var replacementRoot = Path.Combine(f.Root, "changed-root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(replacementRoot);
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
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        async Task Run()
        {
            await using var result = await service.VerifyAsync("budget-config:" + Guid.NewGuid().ToString("N"), "owned-original", 4, sha,
                _ =>
                {
                    fault.Action = () =>
                    {
                        if (changeRoot) configuration[LocalStoragePaths.RootConfigurationKey] = replacementRoot;
                        else policy.UploadMinimumFreeBytes = long.MaxValue;
                    };
                    return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "owned-original", new MemoryStream(bytes), 4));
                }, default);
        }
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.Equal(1, fault.Interceptions);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Null(row.StageFileReceiptJson);
            Assert.False(File.Exists(Path.Combine(root, row.LocalLocator)));
            Assert.False(File.Exists(Path.Combine(replacementRoot, row.LocalLocator)));
            Assert.Equal(changeRoot ? StorageCapacityReservationStatuses.Pending : StorageCapacityReservationStatuses.Released, row.Status);
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { fault.Action = null; configuration[LocalStoragePaths.RootConfigurationKey] = root; }
    }

    private sealed class SharedBudgetConfigurationFault : DbCommandInterceptor
    {
        internal Action? Action;
        internal int Interceptions;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("storage-stage-pending-reservation-budget", StringComparison.Ordinal)
                && Interlocked.Exchange(ref Action, null) is { } action)
            {
                Interceptions++;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }
}
