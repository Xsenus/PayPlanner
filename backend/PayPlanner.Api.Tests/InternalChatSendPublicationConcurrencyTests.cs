using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendPublicationConcurrentConnectionsPublishOneGraph(bool postgres)
    {
        await using var f = await CreateMigratedFixtureAsync(postgres, true);
        var actor = await f.AddUserAsync("chat-concurrent@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policies = new StoragePolicyService(monitor, providers);
        var first = ChatPublicationCore(f.Db, providers, policies, monitor, environment, config);
        Assert.Equal("accepted", (await first.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        await using var secondDb = CaptureFreshDb(f, true);
        var second = ChatPublicationCore(secondDb, providers, policies, monitor, environment, config);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        local.BeforeReadReturn = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var publish = first.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, first.Bindings, default);
        InternalChatSendPublicationResult winner;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var reads = local.ReadCalls;
            var competing = await second.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, second.Bindings, default);
            Assert.Equal("busy", competing.Status);
            Assert.Equal(reads, local.ReadCalls);
            Assert.False(await secondDb.InternalChatMessages.AnyAsync(item => item.ClientRequestId == snapshot.ClientRequestId));
            Assert.Empty(await secondDb.InternalChatAttachments.ToArrayAsync());
            Assert.Empty(await secondDb.StorageProductBindings.ToArrayAsync());
        }
        finally
        {
            local.BeforeReadReturn = null;
            release.TrySetResult();
            winner = await publish;
        }
        Assert.Equal("published", winner.Status);
        var afterReads = local.ReadCalls;
        var replay = await second.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, second.Bindings, default);
        Assert.Equal("published", replay.Status);
        Assert.Equal(winner.MessageId, replay.MessageId);
        Assert.Equal(afterReads, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.Single(await secondDb.InternalChatMessages.Where(item => item.ClientRequestId == snapshot.ClientRequestId).ToArrayAsync());
        Assert.Equal(2, await secondDb.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await secondDb.StorageProductBindings.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await secondDb.StorageCapacityReservations.AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending
            && item.IdempotencyKey.StartsWith("stage:")));
    }

    [Theory]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "expired")]
    [InlineData(true, "expired")]
    [InlineData(false, "successor")]
    [InlineData(true, "successor")]
    [InlineData(false, "tombstone")]
    [InlineData(true, "tombstone")]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "copyChanged")]
    [InlineData(true, "copyChanged")]
    [InlineData(false, "beforeCommit")]
    [InlineData(true, "beforeCommit")]
    [InlineData(false, "foreignCompatibility")]
    [InlineData(true, "foreignCompatibility")]
    public async Task ChatSendPublicationFencesInterruptionAndRetainsOriginals(bool postgres, string scenario)
    {
        var failure = new ChatPublicationBeforeCommitFailure();
        await using var f = await CreateMigratedFixtureAsync(postgres, true, failure);
        var actor = await f.AddUserAsync("chat-interruption@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policies = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policies, monitor, environment, config);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        var original = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        var identities = await f.Db.InternalChatSendAttemptFiles.AsNoTracking().OrderBy(item => item.Index).ToArrayAsync();
        await using var competingDb = CaptureFreshDb(f, true);
        using var cancellation = new CancellationTokenSource();
        var successor = Guid.NewGuid();
        var changed = false;
        local.BeforeReadReturn = async () =>
        {
            if (changed) return;
            changed = true;
            if (scenario == "cancel") cancellation.Cancel();
            if (scenario == "disabled") settings.ProductWriteEnabledClasses.Clear();
            if (scenario == "expired")
            {
                var expired = DateTime.UtcNow.AddMinutes(-1);
                await competingDb.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired));
            }
            if (scenario == "successor") await competingDb.InternalChatSendAttempts
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor));
            if (scenario == "tombstone")
            {
                competingDb.StorageTombstones.Add(new StorageTombstone
                {
                    StorageObjectId = identities[0].StorageObjectId!.Value,
                    Generation = identities[0].StorageGeneration!.Value,
                    Reason = "Owned chat publication tombstone boundary."
                });
                await competingDb.SaveChangesAsync();
            }
            if (scenario == "copyChanged")
            {
                var secondObjectId = identities[1].StorageObjectId;
                await competingDb.StorageReplicas.Where(item => item.StorageObjectGeneration.StorageObjectId == secondObjectId)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
            }
        };
        var directory = Path.Combine(root.Path, "internal-chat-files", thread.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var firstPath = Path.Combine(directory, $"{original.Id:N}-0.bin");
        if (scenario == "foreignCompatibility")
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(firstPath, [9, 8, 7]);
        }
        failure.Armed = scenario == "beforeCommit";
        if (scenario == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => core.Writer.PublishAsync(thread.Id, actor.Id,
                snapshot.ClientRequestId, policies, core.Bindings, cancellation.Token));
        else if (scenario == "beforeCommit")
        {
            await Assert.ThrowsAsync<IOException>(() => core.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, core.Bindings, default));
            Assert.True(failure.Triggered);
        }
        else
        {
            var result = await core.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, core.Bindings, default);
            Assert.Equal(scenario switch { "expired" or "successor" => "busy", "disabled" => "disabled", "copyChanged" => "notReady", _ => "unavailable" }, result.Status);
        }
        Assert.False(f.Db.ChangeTracker.HasChanges());
        var after = await competingDb.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        Assert.Null(after.PublishedMessageId);
        Assert.Equal(original.AcceptedAt, after.AcceptedAt);
        Assert.Equal(original.RequestSha256, after.RequestSha256);
        if (scenario == "successor") Assert.Equal(successor, after.RecoveryLeaseToken);
        if (scenario is "cancel" or "beforeCommit") Assert.NotNull(after.RecoveryLeaseToken);
        Assert.False(await competingDb.InternalChatMessages.AnyAsync(item => item.ClientRequestId == snapshot.ClientRequestId));
        Assert.Empty(await competingDb.InternalChatAttachments.ToArrayAsync());
        Assert.Empty(await competingDb.StorageProductBindings.ToArrayAsync());
        Assert.Null(await competingDb.InternalChatThreads.Select(item => item.LastMessageAt).SingleAsync());
        foreach (var identity in identities)
        {
            Assert.Equal($"chat-send:{original.Id:N}:{identity.Index}", await competingDb.StorageObjects.Where(item => item.Id == identity.StorageObjectId).Select(item => item.OwnerId).SingleAsync());
            var locator = await competingDb.StorageReplicas.Where(item => item.StorageObjectGeneration.StorageObjectId == identity.StorageObjectId)
                .Select(item => item.Locator).SingleAsync();
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(root.Path, locator))));
        }
        Assert.Equal(2, local.PutCalls);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await competingDb.StorageCapacityReservations.AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending
            && item.IdempotencyKey.StartsWith("stage:")));
        if (scenario == "foreignCompatibility") Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(firstPath));
        if (scenario == "beforeCommit")
        {
            Assert.Equal(2, Directory.EnumerateFiles(directory, "*.bin").Count());
            var expired = DateTime.UtcNow.AddMinutes(-1);
            await competingDb.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired));
            local.BeforeReadReturn = null;
            var reads = local.ReadCalls;
            var retry = ChatPublicationCore(competingDb, providers, policies, monitor, environment, config);
            Assert.Equal("published", (await retry.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, retry.Bindings, default)).Status);
            Assert.Equal(reads, local.ReadCalls);
            Assert.Single(await competingDb.InternalChatMessages.Where(item => item.ClientRequestId == snapshot.ClientRequestId).ToArrayAsync());
        }
    }

    private static IConfiguration ChatPublicationConfiguration(string root)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = root,
            [ApplicationFileStoragePaths.RootPathConfigurationKey] = root,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();

    private static StorageOrchestrationOptions ChatPublicationSettings()
        => new() { Enabled = true, UploadCapacityChecksEnabled = true, UploadMinimumFreeBytes = 0, ProductWriteEnabledClasses = ["chat-attachment"] };

    private static (InternalChatOriginalWriter Writer, IProductStorageBindingService Bindings) ChatPublicationCore(PaymentContext context,
        IStorageProviderRegistry providers, IStoragePolicyService policies, ChatOriginalOptions monitor, IWebHostEnvironment environment, IConfiguration config,
        Func<IStorageObjectService, IStorageObjectService>? decorate = null)
    {
        var stages = new StorageStageCapacityService(context, providers, monitor, environment, config);
        var reads = new StorageOriginalReadStagingService(stages, environment, config, monitor, NullLogger<StorageOriginalReadStagingService>.Instance);
        IStorageObjectService objects = new StorageObjectService(context, policies, providers, monitor, originalReadStages: reads);
        if (decorate is not null) objects = decorate(objects);
        return (new InternalChatOriginalWriter(context, objects, stages, environment, config, monitor), new ProductStorageBindingService(context, policies));
    }

    private sealed class ChatPublicationBeforeCommitFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered && eventData.Context!.ChangeTracker.Entries<InternalChatMessage>().Any(item => item.Entity.ClientRequestId.HasValue))
            { Triggered = true; throw new IOException("Owned chat publication failure before commit."); }
            return ValueTask.FromResult(result);
        }
    }
}
