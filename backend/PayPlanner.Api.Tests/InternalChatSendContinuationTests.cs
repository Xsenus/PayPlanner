using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "accepted")]
    [InlineData(true, "accepted")]
    [InlineData(false, "copyPending")]
    [InlineData(true, "copyPending")]
    [InlineData(false, "metadataOnly")]
    [InlineData(true, "metadataOnly")]
    [InlineData(false, "reservationGap")]
    [InlineData(true, "reservationGap")]
    [InlineData(false, "catalogGap")]
    [InlineData(true, "catalogGap")]
    [InlineData(false, "published")]
    [InlineData(true, "published")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "corruptRoot")]
    [InlineData(true, "corruptRoot")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "busy")]
    [InlineData(true, "busy")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "successor")]
    [InlineData(true, "successor")]
    public async Task ChatSendContinuationUsesOneLeaseAcrossOriginalsAndPublication(bool postgres, string scenario)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres, true);
        var actor = await f.AddUserAsync("chat-continue@example.test", "Автор");
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
        var stages = new StorageStageCapacityService(f.Db, providers, monitor, environment, config);
        var reads = new StorageOriginalReadStagingService(stages, environment, config, monitor, NullLogger<StorageOriginalReadStagingService>.Instance);
        var objects = new StorageObjectService(f.Db, policies, providers, monitor, originalReadStages: reads);
        var interrupted = new ChatOriginalStorage(objects);
        if (scenario == "catalogGap") interrupted.AfterWrite = (result, count) => Task.FromResult(count == 2 ? result with { ObjectId = Guid.Empty } : result);
        if (scenario == "reservationGap") local.ThrowPutAckAt = 2;
        var writer = new InternalChatOriginalWriter(f.Db, interrupted, stages, environment, config, monitor);
        if (scenario == "metadataOnly") Assert.Equal("recorded", (await new InternalChatSendAttemptRegistry(f.Db).CaptureAsync(snapshot, default)).Status);
        else if (scenario == "reservationGap")
        {
            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default));
            var expired = DateTime.UtcNow.AddMinutes(-1);
            await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired));
        }
        else Assert.Equal(scenario == "catalogGap" ? "unavailable" : "accepted", (await writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        var bindings = new ProductStorageBindingService(f.Db, policies);
        if (scenario is "published" or "deleted")
        {
            Assert.Equal("published", (await writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, bindings, default)).Status);
            if (scenario == "deleted")
            {
                var deleted = DateTime.UtcNow;
                await f.Db.InternalChatMessages.Where(item => item.ClientRequestId == snapshot.ClientRequestId)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.DeletedAt, deleted));
            }
        }
        if (scenario == "copyPending") await f.Db.StorageReplicas.ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
        if (scenario == "corruptRoot") await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Text, "Изменён frozen text"));
        if (scenario == "revoked") await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        var successor = Guid.NewGuid();
        if (scenario == "busy")
        {
            var future = DateTime.UtcNow.AddMinutes(5);
            await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor)
                .SetProperty(item => item.RecoveryLeaseExpiresAt, future));
        }
        var before = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        await using var fresh = CaptureFreshDb(f, true);
        var continuation = ChatPublicationCore(fresh, providers, policies, monitor, environment, config);
        using var cancellation = new CancellationTokenSource();
        local.ReadCalls = 0;
        local.BeforeReadReturn = async () =>
        {
            if (scenario == "cancel") cancellation.Cancel();
            if (scenario == "successor") await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor));
            if (scenario is "reservationGap" or "catalogGap" && local.ReadCalls == 3)
                Assert.Empty(Directory.EnumerateFiles(Path.Combine(root.Path, ".original-read-staging"), "*.tmp"));
        };
        var puts = local.PutCalls;
        InternalChatSendPublicationResult? result = null;
        if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => continuation.Writer.ContinueAsync(thread.Id, actor.Id,
            snapshot.ClientRequestId, policies, continuation.Bindings, cancellation.Token));
        else result = await continuation.Writer.ContinueAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, continuation.Bindings, default);
        Assert.Equal(puts, local.PutCalls);
        Assert.False(fresh.ChangeTracker.HasChanges());
        var after = await fresh.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        var claimed = scenario is "accepted" or "copyPending" or "metadataOnly" or "reservationGap" or "catalogGap" or "cancel" or "successor";
        Assert.Equal(before.Version + (claimed ? 1 : 0), after.Version);
        Assert.Equal(before.RequestSha256, after.RequestSha256);
        if (result is not null)
        {
            var expected = scenario switch
            {
                "accepted" or "reservationGap" or "catalogGap" or "published" => "published",
                "copyPending" => "notReady",
                "metadataOnly" => "needsInput",
                "deleted" => "deleted",
                "revoked" => "forbidden",
                "busy" or "successor" => "busy",
                _ => "conflict"
            };
            Assert.Equal(expected, result.Status);
            if (claimed && scenario != "successor")
            {
                Assert.Equal(expected, after.RecoveryLastStatus);
                Assert.Equal(before.RecoveryAttemptCount + 1, after.RecoveryAttemptCount);
                Assert.True(after.RecoveryNextAttemptAt > before.RecoveryNextAttemptAt);
                Assert.Null(after.RecoveryLeaseToken);
                Assert.Null(after.RecoveryLeaseExpiresAt);
            }
            else
            {
                Assert.Equal(before.RecoveryAttemptCount, after.RecoveryAttemptCount);
                Assert.Equal(before.RecoveryLastStatus, after.RecoveryLastStatus);
            }
            if (expected == "published")
            {
                Assert.Equal(result.MessageId, after.PublishedMessageId);
                Assert.Single(await fresh.InternalChatMessages.Where(item => item.ClientRequestId == snapshot.ClientRequestId).ToArrayAsync());
                Assert.Equal(2, await fresh.InternalChatAttachments.CountAsync());
                Assert.Equal(2, await fresh.StorageProductBindings.CountAsync());
                var beforeReplay = local.ReadCalls;
                Assert.Equal(result.MessageId, (await continuation.Writer.ContinueAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, continuation.Bindings, default)).MessageId);
                Assert.Equal(beforeReplay, local.ReadCalls);
                Assert.Equal(after.RecoveryAttemptCount, await fresh.InternalChatSendAttempts.Select(item => item.RecoveryAttemptCount).SingleAsync());
            }
        }
        if (scenario is not ("published" or "deleted" or "accepted" or "reservationGap" or "catalogGap"))
        {
            Assert.Null(after.PublishedMessageId);
            Assert.False(await fresh.InternalChatMessages.AnyAsync(item => item.ClientRequestId == snapshot.ClientRequestId));
            Assert.Empty(await fresh.InternalChatAttachments.ToArrayAsync());
            Assert.Empty(await fresh.StorageProductBindings.ToArrayAsync());
        }
        if (scenario is "copyPending" or "metadataOnly" or "published" or "deleted" or "corruptRoot" or "revoked" or "busy") Assert.Equal(0, local.ReadCalls);
        if (scenario == "accepted") Assert.Equal(2, local.ReadCalls);
        if (scenario is "reservationGap" or "catalogGap") Assert.Equal(4, local.ReadCalls);
        if (scenario is "busy" or "successor") Assert.Equal(successor, after.RecoveryLeaseToken);
        if (scenario == "cancel") Assert.NotNull(after.RecoveryLeaseToken);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await fresh.StorageCapacityReservations.AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending && item.IdempotencyKey.StartsWith("stage:")));
    }
}
