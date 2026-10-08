using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

// Pending: apply to tests only after the frozen full gate has terminated.
public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task ChatOriginalCreateConflictPreservesForeignBytesAndPendingDebt(bool postgres, int collisionCall)
    {
        await using var fixture = await CreateMigratedFixtureAsync(postgres);
        var actor = await fixture.AddUserAsync("chat-ownership@example.test", "Автор");
        var thread = await CaptureChannelAsync(fixture, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = root.Path,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var options = new ChatOriginalOptions(new()
        {
            Enabled = true,
            ProductWriteEnabledClasses = ["chat-attachment"],
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0
        });
        var providers = new StorageProviderRegistry([new LocalStorageProvider(environment, configuration)]);
        var storage = new ChatOriginalStorage(new StorageObjectService(fixture.Db,
            new StoragePolicyService(options, providers), providers, options));
        var admission = new ForeignChatOriginalAdmission(
            new StorageStageCapacityService(fixture.Db, providers, options, environment, configuration), collisionCall);
        var writer = new InternalChatOriginalWriter(fixture.Db, storage, admission, environment, configuration, options);

        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default));

        var foreign = Assert.IsType<StorageStageCapacityTicket>(admission.ForeignTicket);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(foreign.AbsolutePath));
        var pending = Assert.Single(await fixture.Db.StorageCapacityReservations.AsNoTracking()
            .Where(row => row.Status == StorageCapacityReservationStatuses.Pending).ToArrayAsync());
        Assert.Equal(foreign.ObjectId, pending.ObjectId);
        Assert.Equal(foreign.Key, pending.IdempotencyKey);
        Assert.Equal(0, storage.Calls);
        Assert.False(await fixture.Db.InternalChatSendAttempts.AnyAsync());
        Assert.False(await fixture.Db.InternalChatMessages.AnyAsync());
        Assert.False(await fixture.Db.StorageObjects.AnyAsync());
        Assert.Single(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class ForeignChatOriginalAdmission(IStorageStageCapacityService inner, int collisionCall)
        : IStorageStageCapacityService
    {
        private int calls;
        public StorageStageCapacityTicket? ForeignTicket { get; private set; }
        public async Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveAsync(
            string identity, string path, long size, CancellationToken ct)
        {
            var result = await inner.ReserveAsync(identity, path, size, ct);
            if (++calls == collisionCall && result.Ticket is { } ticket)
            {
                // Simulate another writer between quota admission and CreateNew.
                await File.WriteAllBytesAsync(path, new byte[] { 9, 8, 7 }, ct);
                ForeignTicket = ticket;
            }
            return result;
        }
        public Task<(StorageStageCapacityTicket? Ticket, string? Error)> ReserveCompatibilityAsync(
            string identity, string path, long size, string alias, CancellationToken ct)
            => inner.ReserveCompatibilityAsync(identity, path, size, alias, ct);
        public Task<StorageStageCapacityTicket> ConfirmPhysicalSizeAsync(StorageStageCapacityTicket ticket,
            long size, CancellationToken ct) => inner.ConfirmPhysicalSizeAsync(ticket, size, ct);
        public Task SettlePhysicalAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
            => inner.SettlePhysicalAsync(ticket, ct);
        public Task ReleaseAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
            => inner.ReleaseAsync(ticket, ct);
        public Task<StorageStageRecoveryReport> ReconcilePendingAsync(bool apply, int limit,
            TimeSpan age, CancellationToken ct, string? afterKey = null)
            => inner.ReconcilePendingAsync(apply, limit, age, ct, afterKey);
    }
}
