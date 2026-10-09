using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, "pending")]
    [InlineData(false, "running")]
    [InlineData(false, "unknown")]
    [InlineData(false, "failed")]
    [InlineData(false, "succeeded")]
    [InlineData(false, "deadLetter")]
    [InlineData(true, "pending")]
    [InlineData(true, "running")]
    [InlineData(true, "unknown")]
    [InlineData(true, "failed")]
    [InlineData(true, "succeeded")]
    [InlineData(true, "deadLetter")]
    public async Task CheckpointCopyBudgetChargesOnlyUnsettledRepairOperations(bool postgres, string status)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        const long availableBound = 64L * 1024 * 1024;
        var (stages, policy, root) = CreateCopyBudgetServices(f,
            local => new OriginalCopyBudgetNativeProvider(local, availableBound));
        const long copyBytes = 8L * 1024 * 1024;
        var operation = await AddOwnedCopyBudgetOperationAsync(f, copyBytes, status, "local");
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync(
            "copy-state:" + Guid.NewGuid().ToString("N"), Path.Combine(root, "state.tmp"), 4, default)).Ticket);
        try
        {
            var native = LocalStorageCapacityProbe.Observe(root, "owned-native", 0);
            var spool = LocalStorageCapacityProbe.Observe(Path.GetTempPath(), "owned-spool", 0);
            Assert.False(string.IsNullOrWhiteSpace(native.GroupId));
            Assert.Equal(native.GroupId, spool.GroupId);
            Assert.True(native.AvailableBytes >= availableBound && spool.AvailableBytes >= availableBound,
                $"Copy-state fixture requires at least {availableBound} native bytes; root={native.AvailableBytes}, spool={spool.AvailableBytes}.");
            // Keep the copy-debt boundary fixed while native root/spool probes stay real.
            // Incorrectly including a settled copy still activates the capped provider.
            policy.UploadMinimumFreeBytes = availableBound - (4 + copyBytes / 2);
            var settled = status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter;
            if (settled)
            {
                await using (var writer = await stages.OpenCheckpointWriterAsync(ticket, default))
                {
                    await writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default);
                    Assert.Equal(4, (await writer.CompleteAsync(default)).PlannedBytes);
                }
                await stages.SettlePhysicalAsync(ticket, default);
                Assert.Equal(StorageCapacityReservationStatuses.Released, await f.StatusAsync(ticket.Key));
            }
            else
            {
                async Task Open() { await using var writer = await stages.OpenCheckpointWriterAsync(ticket, default); }
                var error = await Assert.ThrowsAsync<InvalidOperationException>(Open);
                Assert.Equal("Stage pending copy, spool and reservation debt exceeds fresh physical headroom.", error.Message);
                Assert.False(File.Exists(ticket.AbsolutePath));
                Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
            }
            Assert.Equal(status, await f.Db.StorageOperations.Where(o => o.Id == operation.Id).Select(o => o.Status).SingleAsync());
            Assert.Equal(1, await f.Db.StorageObjects.CountAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointCopyBudgetRemoteDestinationChargesSpoolWithoutCloudQuotaIo(bool postgres, bool deny)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var remote = new OwnedRemoteBudgetProvider();
        var (stages, policy, root) = CreateCopyBudgetServices(f, remote);
        // This is queued metadata, not a physical allocation; the writer still emits four bytes.
        // Exercise both sides of the copy debt with a gap that tolerates unrelated disk traffic.
        const long copyBytes = 256L * 1024 * 1024;
        var operation = await AddOwnedCopyBudgetOperationAsync(f, copyBytes, StorageOperationStatuses.Pending, remote.Key);
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync(
            "remote-copy:" + Guid.NewGuid().ToString("N"), Path.Combine(root, "remote.tmp"), 4, default)).Ticket);
        Assert.True(remote.Observations > 0); // Scheduling still checks remote quota.
        remote.Observations = 0;
        try
        {
            var free = Assert.IsType<long>(LocalStorageCapacityProbe.Observe(root, "owned", 0).AvailableBytes);
            Assert.True(free >= copyBytes * 2,
                $"Remote copy fixture requires at least {copyBytes * 2} native bytes; available={free}.");
            policy.UploadMinimumFreeBytes = free - (4 + (deny ? copyBytes / 2 : copyBytes + copyBytes / 2));
            if (deny)
            {
                async Task Open() { await using var writer = await stages.OpenCheckpointWriterAsync(ticket, default); }
                var error = await Assert.ThrowsAsync<InvalidOperationException>(Open);
                Assert.Equal("Stage pending copy, spool and reservation debt exceeds fresh physical headroom.", error.Message);
                Assert.False(File.Exists(ticket.AbsolutePath));
                Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
            }
            else
            {
                await using (var writer = await stages.OpenCheckpointWriterAsync(ticket, default))
                {
                    await writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default);
                    Assert.Equal(4, (await writer.CompleteAsync(default)).PlannedBytes);
                }
                await stages.SettlePhysicalAsync(ticket, default);
                Assert.Equal(StorageCapacityReservationStatuses.Released, await f.StatusAsync(ticket.Key));
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(ticket.AbsolutePath));
            }
            Assert.Equal(0, remote.Observations);
            Assert.Equal(StorageOperationStatuses.Pending, await f.Db.StorageOperations.Where(o => o.Id == operation.Id).Select(o => o.Status).SingleAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, "missingGeneration")]
    [InlineData(false, "negative")]
    [InlineData(false, "blankKey")]
    [InlineData(false, "overflow")]
    [InlineData(false, "unknownDomain")]
    [InlineData(false, "missingProvider")]
    [InlineData(true, "missingGeneration")]
    [InlineData(true, "negative")]
    [InlineData(true, "blankKey")]
    [InlineData(true, "overflow")]
    [InlineData(true, "unknownDomain")]
    [InlineData(true, "missingProvider")]
    public async Task CheckpointCopyBudgetRejectsInvalidOrUnknownQueueBeforeCreateNew(bool postgres, string damage)
    {
        var probe = new CopyBudgetSqlProbe();
        await using var f = await CreateWriterFixtureAsync(postgres, probe);
        var unknown = new OwnedUnknownBudgetProvider();
        var (stages, _, root) = CreateCopyBudgetServices(f, unknown);
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync(
            "invalid-copy:" + Guid.NewGuid().ToString("N"), Path.Combine(root, "invalid.tmp"), 4, default)).Ticket);
        try
        {
            var bytes = damage == "overflow" ? long.MaxValue : damage == "negative" ? -1 : 8L * 1024 * 1024;
            await AddOwnedCopyBudgetOperationAsync(f, bytes, StorageOperationStatuses.Pending,
                damage == "unknownDomain" ? unknown.Key : damage == "missingProvider" ? "owned-missing" : "local", operation =>
                {
                    if (damage == "missingGeneration") operation.StorageObjectGeneration = null;
                    if (damage == "blankKey") operation.IdempotencyKey = " ";
                });
            probe.Statements.Clear();
            async Task Open() { await using var writer = await stages.OpenCheckpointWriterAsync(ticket, default); }
            await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.False(File.Exists(ticket.AbsolutePath));
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
            Assert.Null(await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
            var sql = Assert.Single(probe.Statements);
            Assert.Contains("SUM", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FrozenSourceJson", sql, StringComparison.Ordinal);
            Assert.Equal(1, await f.Db.StorageOperations.CountAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointCopyBudgetRechecksPolicyAfterCopySqlAwait(bool postgres)
    {
        var probe = new CopyBudgetSqlProbe();
        await using var f = await CreateWriterFixtureAsync(postgres, probe);
        var (stages, policy, root) = CreateCopyBudgetServices(f);
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync(
            "copy-await:" + Guid.NewGuid().ToString("N"), Path.Combine(root, "await.tmp"), 4, default)).Ticket);
        try
        {
            probe.AfterRead = () => policy.UploadMinimumFreeBytes = long.MaxValue;
            async Task Open() { await using var writer = await stages.OpenCheckpointWriterAsync(ticket, default); }
            await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.Equal(1, probe.Interceptions);
            Assert.False(File.Exists(ticket.AbsolutePath));
            Assert.Null(await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
        }
        finally { probe.AfterRead = null; await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointCopyBudgetRejectsExcessiveDestinationGraphBeforeCreateNew(bool postgres)
    {
        await using var f = await CreateWriterFixtureAsync(postgres);
        var (stages, _, root) = CreateCopyBudgetServices(f);
        var ticket = Assert.IsType<StorageStageCapacityTicket>((await stages.ReserveAsync(
            "copy-graph:" + Guid.NewGuid().ToString("N"), Path.Combine(root, "graph.tmp"), 4, default)).Ticket);
        try
        {
            for (var i = 0; i < 41; i++)
                await AddOwnedCopyBudgetOperationAsync(f, 4, StorageOperationStatuses.Pending, "local",
                    operation => operation.ConnectionId = "owned-group-" + i);
            async Task Open() { await using var writer = await stages.OpenCheckpointWriterAsync(ticket, default); }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(Open);
            Assert.Contains("resource graph", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(ticket.AbsolutePath));
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.StatusAsync(ticket.Key));
            Assert.Equal(41, await f.Db.StorageOperations.CountAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    private static (StorageStageCapacityService Stages, StorageOrchestrationOptions Policy, string Root)
        CreateCopyBudgetServices(WriterProcessFixture f, params IStorageProvider[] additional)
        => CreateCopyBudgetServices(f, local => local, additional);

    private static (StorageStageCapacityService Stages, StorageOrchestrationOptions Policy, string Root)
        CreateCopyBudgetServices(WriterProcessFixture f, Func<LocalStorageProvider, IStorageProvider> createLocal,
            params IStorageProvider[] additional)
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
        return (new(f.Db, new StorageProviderRegistry(new IStorageProvider[] { createLocal(new LocalStorageProvider(environment, configuration)) }.Concat(additional)),
            new StaticOptions(policy), environment, configuration), policy, root);
    }

    private static async Task<StorageOperation> AddOwnedCopyBudgetOperationAsync(WriterProcessFixture f,
        long bytes, string status, string provider, Action<StorageOperation>? configure = null)
    {
        var item = new StorageObject
        {
            DataClass = "owned-budget",
            OwnerType = "owned-test",
            OwnerId = Guid.NewGuid().ToString("N"),
            LogicalName = "owned-copy.bin",
            CurrentGeneration = 1,
            Version = 1
        };
        var generation = new StorageObjectGeneration { StorageObject = item, Generation = 1, SizeBytes = bytes, Sha256 = new string('a', 64) };
        var operation = new StorageOperation
        {
            StorageObject = item,
            StorageObjectGeneration = generation,
            ProviderKey = provider,
            OperationType = StorageOperationTypes.Repair,
            Status = status,
            IdempotencyKey = "owned-copy:" + Guid.NewGuid().ToString("N")
        };
        configure?.Invoke(operation);
        f.Db.StorageOperations.Add(operation);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        return operation;
    }

    private class OwnedUnknownBudgetProvider : IStorageProvider
    {
        public virtual string Key => "owned-unknown";
        public StorageProviderCapabilities Capabilities { get; } = new(true, false, true, false, false, false, false, false, false);
        public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => throw new InvalidOperationException("No provider writes in budget fixture.");
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("No provider reads in budget fixture.");
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("No provider reads in budget fixture.");
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct) => throw new InvalidOperationException("No provider deletes in budget fixture.");
    }

    private sealed class OwnedRemoteBudgetProvider : OwnedUnknownBudgetProvider, IStorageProvider, IStorageCapacityProvider
    {
        public override string Key => "owned-remote";
        public StorageCapacityDomain CapacityDomain => StorageCapacityDomain.Remote;
        internal int Observations;
        public Task<StorageCapacityResource> ObserveCapacityAsync(string key, string? connection, long minimum, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Observations++;
            return Task.FromResult(new StorageCapacityResource(key, "owned-remote-account", 128L * 1024 * 1024, minimum, DateTimeOffset.UtcNow));
        }
    }

    private sealed class CopyBudgetSqlProbe : DbCommandInterceptor
    {
        internal readonly List<string> Statements = [];
        internal Action? AfterRead;
        internal int Interceptions;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("storage-capacity-pending-budget", StringComparison.Ordinal)) Statements.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("storage-capacity-pending-budget", StringComparison.Ordinal)
                && Interlocked.Exchange(ref AfterRead, null) is { } action)
            {
                Interceptions++;
                action();
            }
            return ValueTask.FromResult(result);
        }
    }
}
