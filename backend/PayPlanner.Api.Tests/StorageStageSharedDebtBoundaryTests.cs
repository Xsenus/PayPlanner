using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using System.Security.Cryptography;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedStageBudgetCreditsOnlyItsConfirmedPhysicalPrefix(bool postgres)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var (stages, service, policy, root) = CreateSharedBudgetServices(f);
        const long otherUpper = 8L * 1024 * 1024;
        var other = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("credit-other:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "credit-other.tmp"), otherUpper, default)).Ticket);
        try
        {
            var bytes = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
            bytes[0] = 1; bytes[^1] = 2;
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var raised = 0;
            await using var result = await service.VerifyAsync("credit-original:" + Guid.NewGuid().ToString("N"),
                "owned-original", bytes.LongLength, sha, _ => Task.FromResult(new StorageReadResult(
                    StorageOperationStatus.Succeeded, "owned-original", new SharedBudgetPrefixSource(bytes, async () =>
                    {
                        var own = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ObjectId != other.ObjectId);
                        Assert.True(StorageStageFileReceipt.TryParse(own.StageFileReceiptJson, out var proof));
                        Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, proof!.PlannedBytes);
                        var current = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(root, "owned-observation", 0).AvailableBytes);
                        policy.UploadMinimumFreeBytes = current - (otherUpper + 4 + 1024 * 1024);
                        raised++;
                    }), bytes.LongLength)), default);
            Assert.NotNull(result);
            Assert.Equal(bytes.LongLength, result.Length);
            Assert.Equal(1, result.ReadByte());
            result.Seek(-1, SeekOrigin.End);
            Assert.Equal(2, result.ReadByte());
            Assert.Equal(1, raised);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(other.Key));
            Assert.Equal(StorageCapacityReservationStatuses.Released, await f.Db.StorageCapacityReservations
                .Where(r => r.ObjectId != other.ObjectId).Select(r => r.Status).SingleAsync());
        }
        finally { await other.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedStageBudgetCompletionDoesNotChargeUnusedUpperBound(bool postgres)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var (stages, _, policy, root) = CreateSharedBudgetServices(f);
        const long upper = 8L * 1024 * 1024;
        var other = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("unused-other:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "unused-other.tmp"), upper, default)).Ticket);
        var own = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("unused-own:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "unused-own.tmp"), upper, default)).Ticket);
        try
        {
            await using (var writer = await stages.OpenCheckpointWriterAsync(own, default))
            {
                await writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default);
                var current = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(root, "owned-observation", 0).AvailableBytes);
                policy.UploadMinimumFreeBytes = current - (upper + 1024 * 1024);
                var receipt = await writer.CompleteAsync(default);
                Assert.Equal(4, receipt.PlannedBytes);
            }
            var confirmed = await stages.ConfirmPhysicalSizeAsync(own, 4, default);
            await stages.SettlePhysicalAsync(confirmed, default);
            Assert.Equal(StorageCapacityReservationStatuses.Released, await f.StatusAsync(own.Key));
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(other.Key));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(own.AbsolutePath));
        }
        finally { await own.CloseWriterAsync(); await other.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharedStageBudgetRejectsInvalidOrOverflowingDebtBeforeCreateNew(bool postgres, bool overflow)
    {
        var probe = new SharedBudgetSqlProbe();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, probe);
        var (stages, _, _, root) = CreateSharedBudgetServices(f);
        var other = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("invalid-other:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "invalid-other.tmp"), 4, default)).Ticket);
        var own = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync("invalid-own:" + Guid.NewGuid().ToString("N"),
            Path.Combine(root, "invalid-own.tmp"), 4, default)).Ticket);
        try
        {
            var rows = f.Db.StorageCapacityReservations.Where(r => r.ObjectId == other.ObjectId);
            if (overflow) await rows.ExecuteUpdateAsync(s => s.SetProperty(r => r.Bytes, long.MaxValue));
            else await rows.ExecuteUpdateAsync(s => s.SetProperty(r => r.BindingHash, ""));
            async Task Run() { await using var writer = await stages.OpenCheckpointWriterAsync(own, default); }
            await Assert.ThrowsAsync<InvalidOperationException>(Run);
            Assert.False(File.Exists(own.AbsolutePath));
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(own.Key));
            var sql = Assert.Single(probe.Statements);
            Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StageFileReceiptJson", sql, StringComparison.Ordinal);
            Assert.Equal(0, await f.Db.StorageObjects.CountAsync());
        }
        finally { await own.CloseWriterAsync(); await other.CloseWriterAsync(); }
    }

    private static (StorageStageCapacityService Stages, StorageOriginalReadStagingService Reader,
        StorageOrchestrationOptions Policy, string Root) CreateSharedBudgetServices(WriterProcessFixture f)
    {
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
        return (stages, new(stages, environment, configuration, monitor, NullLogger<StorageOriginalReadStagingService>.Instance), policy, root);
    }

    private sealed class SharedBudgetPrefixSource(byte[] bytes, Func<Task> callback) : MemoryStream(bytes, writable: false)
    {
        private bool _called;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_called && Position >= StorageStageFileReceipt.MaximumBlockBytes) { _called = true; await callback(); }
            return await base.ReadAsync(buffer, ct);
        }
    }

    private sealed class SharedBudgetSqlProbe : DbCommandInterceptor
    {
        internal List<string> Statements { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("storage-stage-pending-reservation-budget", StringComparison.Ordinal))
                Statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
