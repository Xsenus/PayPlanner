using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "publish")]
    [InlineData(true, "publish")]
    [InlineData(false, "bounded")]
    [InlineData(true, "bounded")]
    [InlineData(false, "future")]
    [InlineData(true, "future")]
    [InlineData(false, "liveLease")]
    [InlineData(true, "liveLease")]
    [InlineData(false, "forbidden")]
    [InlineData(true, "forbidden")]
    [InlineData(false, "corrupt")]
    [InlineData(true, "corrupt")]
    [InlineData(false, "copyPending")]
    [InlineData(true, "copyPending")]
    [InlineData(false, "providerFailure")]
    [InlineData(true, "providerFailure")]
    [InlineData(false, "deadLetter")]
    [InlineData(true, "deadLetter")]
    [InlineData(false, "lostAck")]
    [InlineData(true, "lostAck")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "beforeCommitFailure")]
    [InlineData(true, "beforeCommitFailure")]
    [InlineData(false, "versionExhausted")]
    [InlineData(true, "versionExhausted")]
    [InlineData(false, "forbiddenRestored")]
    [InlineData(true, "forbiddenRestored")]
    public async Task ChatSendRecoveryBatchBoundsDueClaimsAndRecordsOutcomes(bool postgres, string scenario)
    {
        var ack = new ChatPublicationCommitAckFailure();
        var beforeCommit = new ChatPublicationBeforeCommitFailure();
        await using var f = await CreateMigratedFixtureAsync(postgres, false, ack, beforeCommit);
        var actor = await f.AddUserAsync("chat-batch@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var request = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        settings.WorkerEnabled = true;
        settings.WorkerBatchSize = 1;
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policies = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policies, monitor, environment, config);
        if (scenario is "bounded" or "forbidden" or "forbiddenRestored" or "corrupt")
        {
            var registry = new InternalChatSendAttemptRegistry(f.Db);
            Assert.Equal("recorded", (await registry.CaptureAsync(request, default)).Status);
            if (scenario == "bounded") Assert.Equal("recorded", (await registry.CaptureAsync(request with { ClientRequestId = Guid.NewGuid() }, default)).Status);
        }
        else Assert.Equal("accepted", (await core.Writer.WriteAsync(request, ChatOriginalInputs(request), default)).Status);
        var future = DateTime.UtcNow.AddMinutes(5);
        var successor = Guid.NewGuid();
        if (scenario == "future") await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryNextAttemptAt, future));
        if (scenario == "liveLease") await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor).SetProperty(item => item.RecoveryLeaseExpiresAt, future));
        if (scenario is "forbidden" or "forbiddenRestored") await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (scenario == "corrupt") await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Text, "Corrupt owned frozen proof"));
        if (scenario == "copyPending") await f.Db.StorageReplicas.ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
        if (scenario == "disabled") settings.WorkerEnabled = false;
        if (scenario == "providerFailure") local.BeforeReadReturn = () => throw new IOException("Owned provider read failure.");
        if (scenario == "deadLetter")
        {
            settings.WorkerMaxAttempts = 1;
            settings.UploadMinimumFreeBytes = long.MaxValue;
        }
        ack.Armed = scenario == "lostAck";
        beforeCommit.Armed = scenario == "beforeCommitFailure";
        if (scenario == "versionExhausted") await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Version, long.MaxValue));
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancel") local.BeforeReadReturn = () => { cancellation.Cancel(); return Task.CompletedTask; };
        var before = await f.Db.InternalChatSendAttempts.AsNoTracking().Where(item => item.ClientRequestId == request.ClientRequestId).SingleAsync();
        var puts = local.PutCalls;
        InternalChatSendRecoveryBatchResult? result = null;
        if (scenario == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, cancellation.Token));
        else result = await core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, default);
        Assert.Equal(puts, local.PutCalls);
        Assert.False(f.Db.ChangeTracker.HasChanges());
        var after = await f.Db.InternalChatSendAttempts.AsNoTracking().Where(item => item.ClientRequestId == request.ClientRequestId).SingleAsync();
        if (scenario is "disabled" or "future" or "liveLease" or "versionExhausted")
        {
            Assert.Equal(new InternalChatSendRecoveryBatchResult(0, 0, 0, 0, 0), result);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.RecoveryLeaseToken, after.RecoveryLeaseToken);
        }
        else if (scenario == "bounded")
        {
            Assert.Equal(1, result!.Inspected);
            Assert.Equal(1, result.Claimed);
            Assert.Equal(1, result.Deferred);
            Assert.Equal(1, await f.Db.InternalChatSendAttempts.CountAsync(item => item.RecoveryLastStatus == "needsInput"));
            Assert.Equal(1, (await core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, default)).Claimed);
            Assert.Equal(0, (await core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, default)).Inspected);
        }
        else if (scenario is "publish" or "lostAck")
        {
            Assert.Equal(1, result!.Published);
            Assert.NotNull(after.PublishedMessageId);
            Assert.Equal("published", after.RecoveryLastStatus);
            Assert.Null(after.RecoveryLeaseToken);
            Assert.Single(await f.Db.InternalChatMessages.Where(item => item.ClientRequestId == request.ClientRequestId).ToArrayAsync());
            Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
            Assert.Equal(0, (await core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, default)).Inspected);
            if (scenario == "lostAck") Assert.True(ack.Triggered);
        }
        else if (scenario == "cancel")
        {
            Assert.NotNull(after.RecoveryLeaseToken);
            Assert.Equal(before.RecoveryAttemptCount, after.RecoveryAttemptCount);
        }
        else
        {
            var expected = scenario switch { "forbidden" or "forbiddenRestored" => "forbidden", "corrupt" => "conflict", "copyPending" => "notReady", "deadLetter" => "deadLetter", _ => "unavailable" };
            Assert.Equal(expected, after.RecoveryLastStatus);
            Assert.Equal(before.RecoveryAttemptCount + 1, after.RecoveryAttemptCount);
            Assert.Null(after.PublishedMessageId);
            if (scenario == "beforeCommitFailure")
            {
                Assert.NotNull(after.RecoveryLeaseToken);
                Assert.True(after.RecoveryNextAttemptAt >= after.RecoveryLeaseExpiresAt);
            }
            else Assert.Null(after.RecoveryLeaseToken);
            Assert.Equal(0, (await core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, default)).Inspected);
            if (scenario == "deadLetter") Assert.Equal(1, result!.DeadLettered);
            if (scenario == "forbiddenRestored")
            {
                Assert.True(after.RecoveryNextAttemptAt >= DateTime.UtcNow.AddMinutes(14));
                await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, true));
                var due = DateTime.UtcNow.AddMinutes(-1);
                await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryNextAttemptAt, due));
                Assert.Equal(1, (await core.Writer.ProcessRecoveryBatchAsync(policies, core.Bindings, default)).Claimed);
                Assert.Equal("needsInput", await f.Db.InternalChatSendAttempts.Select(item => item.RecoveryLastStatus).SingleAsync());
            }
        }
        if (scenario is not ("publish" or "lostAck"))
        {
            Assert.Empty(await f.Db.InternalChatMessages.ToArrayAsync());
            Assert.Empty(await f.Db.InternalChatAttachments.ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        }
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await f.Db.StorageCapacityReservations.AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending && item.IdempotencyKey.StartsWith("stage:")));
    }
}
