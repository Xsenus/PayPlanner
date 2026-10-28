using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(false, "copyPending")]
    [InlineData(true, "copyPending")]
    [InlineData(false, "corruptSecond")]
    [InlineData(true, "corruptSecond")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "revokedDuringRead")]
    [InlineData(true, "revokedDuringRead")]
    [InlineData(false, "bindingFailure")]
    [InlineData(true, "bindingFailure")]
    [InlineData(false, "lostCommitAck")]
    [InlineData(true, "lostCommitAck")]
    [InlineData(false, "deletedReceipt")]
    [InlineData(true, "deletedReceipt")]
    [InlineData(false, "corruptReceipt")]
    [InlineData(true, "corruptReceipt")]
    [InlineData(false, "replyMention")]
    [InlineData(true, "replyMention")]
    [InlineData(false, "replySystem")]
    [InlineData(true, "replySystem")]
    [InlineData(false, "replySelf")]
    [InlineData(true, "replySelf")]
    [InlineData(false, "replyHidden")]
    [InlineData(true, "replyHidden")]
    [InlineData(false, "replyRestored")]
    [InlineData(true, "replyRestored")]
    [InlineData(false, "replyHiddenAfterPublish")]
    [InlineData(true, "replyHiddenAfterPublish")]
    [InlineData(false, "revokedAfterPublish")]
    [InlineData(true, "revokedAfterPublish")]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "staleLease")]
    [InlineData(true, "staleLease")]
    public async Task ChatSendPublicationCommitsTheWholeFrozenGraph(bool postgres, string scenario)
    {
        var ack = new ChatPublicationCommitAckFailure();
        await using var f = await CreateMigratedFixtureAsync(postgres, false, ack);
        var actor = await f.AddUserAsync("chat-publish@example.test", "Автор");
        var other = await f.AddUserAsync("chat-publish-other@example.test", "Участник");
        var thread = await CaptureChannelAsync(f, actor, other);
        var request = CaptureSnapshot(thread.Id, actor.Id);
        int? replyId = null;
        if (scenario is "replyMention" or "replySystem" or "replySelf" or "replyHidden" or "replyRestored" or "replyHiddenAfterPublish")
        {
            var reply = new InternalChatMessage
            {
                ThreadId = thread.Id,
                AuthorUserId = scenario == "replySystem" ? null : scenario == "replySelf" ? actor.Id : other.Id,
                Kind = scenario == "replySystem" ? InternalChatMessageKinds.System : InternalChatMessageKinds.User,
                Text = "Вопрос",
                NextNoReplyReminderAt = DateTime.UtcNow.AddDays(1)
            };
            f.Db.InternalChatMessages.Add(reply);
            await f.Db.SaveChangesAsync();
            replyId = reply.Id;
            request = request with { ReplyToMessageId = reply.Id, MentionUserIds = [other.Id] };
        }
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [LocalStoragePaths.RootConfigurationKey] = root.Path,
            [ApplicationFileStoragePaths.RootPathConfigurationKey] = root.Path,
            ["Email:Enabled"] = "false",
            ["BackgroundServices:Enabled"] = "false"
        }).Build();
        var settings = new StorageOrchestrationOptions
        {
            Enabled = true,
            UploadCapacityChecksEnabled = true,
            UploadMinimumFreeBytes = 0,
            ProductWriteEnabledClasses = ["chat-attachment"]
        };
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policies = new StoragePolicyService(monitor, providers);
        var stages = new StorageStageCapacityService(f.Db, providers, monitor, environment, config);
        var readStages = new StorageOriginalReadStagingService(stages, environment, config, monitor,
            NullLogger<StorageOriginalReadStagingService>.Instance);
        var objects = new StorageObjectService(f.Db, policies, providers, monitor, originalReadStages: readStages);
        var writer = new InternalChatOriginalWriter(f.Db, objects, stages, environment, config, monitor);
        Assert.Equal("accepted", (await writer.WriteAsync(request, ChatOriginalInputs(request), default)).Status);
        var before = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        var identities = await f.Db.InternalChatSendAttemptFiles.AsNoTracking().OrderBy(item => item.Index).ToArrayAsync();
        if (scenario is "replyHidden" or "replyRestored")
        {
            var member = f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id);
            await member.ExecuteUpdateAsync(set => set.SetProperty(item => item.HistoryHiddenThroughMessageId, replyId));
        }
        if (scenario == "copyPending") await f.Db.StorageReplicas.ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
        if (scenario == "corruptSecond")
        {
            var secondId = identities[1].StorageObjectId;
            await f.Db.StorageObjects.Where(item => item.Id == secondId).ExecuteUpdateAsync(set => set.SetProperty(item => item.LogicalName, "foreign.txt"));
        }
        if (scenario == "revoked") await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (scenario == "revokedDuringRead") local.BeforeReadReturn = async () =>
            await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        if (scenario == "disabled") settings.ProductWriteEnabledClasses.Clear();
        var successor = Guid.NewGuid();
        if (scenario == "staleLease")
        {
            var expiry = DateTime.UtcNow.AddMinutes(5);
            await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, successor)
                .SetProperty(item => item.RecoveryLeaseExpiresAt, expiry));
        }
        var bindings = new ChatPublicationBindings(new ProductStorageBindingService(f.Db, policies), scenario == "bindingFailure");
        if (scenario == "replyRestored")
        {
            Assert.Equal("conflict", (await writer.PublishAsync(thread.Id, actor.Id, request.ClientRequestId, policies, bindings, default)).Status);
            Assert.False(await f.Db.InternalChatMessages.AnyAsync(item => item.ClientRequestId == request.ClientRequestId));
            await f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.HistoryHiddenThroughMessageId, (int?)null));
        }
        ack.Armed = scenario == "lostCommitAck";
        var puts = local.PutCalls;
        InternalChatSendPublicationResult result;
        if (ack.Armed)
        {
            await Assert.ThrowsAsync<IOException>(() => writer.PublishAsync(thread.Id, actor.Id, request.ClientRequestId, policies, bindings, default));
            Assert.True(ack.Triggered);
            result = await writer.PublishAsync(thread.Id, actor.Id, request.ClientRequestId, policies, bindings, default);
        }
        else result = await writer.PublishAsync(thread.Id, actor.Id, request.ClientRequestId, policies, bindings, default);
        var expected = scenario switch
        {
            "copyPending" => "notReady",
            "corruptSecond" or "replyHidden" => "conflict",
            "revoked" or "revokedDuringRead" => "forbidden",
            "bindingFailure" => "notReady",
            "disabled" => "disabled",
            "staleLease" => "busy",
            _ => "published"
        };
        Assert.Equal(expected, result.Status);
        Assert.Equal(puts, local.PutCalls);
        Assert.False(f.Db.ChangeTracker.HasChanges());
        var after = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(before.AcceptedAt, after.AcceptedAt);
        Assert.Equal(before.RequestSha256, after.RequestSha256);
        if (expected != "published")
        {
            Assert.Null(after.PublishedMessageId);
            Assert.Empty(await f.Db.InternalChatAttachments.ToArrayAsync());
            Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
            Assert.False(await f.Db.InternalChatMessages.AnyAsync(item => item.ClientRequestId == request.ClientRequestId));
            Assert.Null(await f.Db.InternalChatThreads.AsNoTracking().Select(item => item.LastMessageAt).SingleAsync());
            foreach (var identity in identities)
                Assert.Equal($"chat-send:{before.Id:N}:{identity.Index}", await f.Db.StorageObjects.Where(item => item.Id == identity.StorageObjectId).Select(item => item.OwnerId).SingleAsync());
            if (scenario is "copyPending" or "corruptSecond" or "revoked" or "disabled" or "staleLease") Assert.Equal(0, local.ReadCalls);
            if (scenario == "staleLease") Assert.Equal(successor, after.RecoveryLeaseToken);
        }
        else
        {
            Assert.Equal(result.MessageId, after.PublishedMessageId);
            var message = await f.Db.InternalChatMessages.AsNoTracking().Include(item => item.Attachments).Include(item => item.Mentions)
                .AsSplitQuery().SingleAsync(item => item.Id == result.MessageId);
            Assert.Equal(request.Text, message.Text);
            Assert.Equal(request.ReplyToMessageId, message.ReplyToMessageId);
            Assert.Equal(request.MentionUserIds, message.Mentions.Select(item => item.UserId).Order().ToArray());
            Assert.True(message.NotificationDispatchPending);
            Assert.Equal(2, message.Attachments.Count);
            Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
            Assert.NotNull(await f.Db.InternalChatThreads.AsNoTracking().Select(item => item.LastMessageAt).SingleAsync());
            if (replyId.HasValue)
            {
                var reminder = await f.Db.InternalChatMessages.AsNoTracking().Where(item => item.Id == replyId).Select(item => item.NextNoReplyReminderAt).SingleAsync();
                if (scenario is "replyMention" or "replyRestored" or "replyHiddenAfterPublish") Assert.Null(reminder);
                else Assert.NotNull(reminder);
            }
            foreach (var identity in identities)
            {
                var attachment = message.Attachments.Single(item => item.StoredFileName == $"{before.Id:N}-{identity.Index}.bin");
                var path = Path.Combine(root.Path, "internal-chat-files", thread.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), attachment.StoredFileName);
                Assert.Equal(identity.SizeBytes, new FileInfo(path).Length);
                Assert.Equal(identity.StorageObjectId, await f.Db.StorageProductBindings.Where(item => item.SourceId == attachment.Id).Select(item => item.StorageObjectId).SingleAsync());
            }
            var reads = local.ReadCalls;
            if (scenario == "replyHiddenAfterPublish") await f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.HistoryHiddenThroughMessageId, replyId));
            if (scenario == "revokedAfterPublish") await f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
            if (scenario == "deletedReceipt")
            {
                var deletedAt = DateTime.UtcNow;
                await f.Db.InternalChatMessages.Where(item => item.Id == result.MessageId).ExecuteUpdateAsync(set => set.SetProperty(item => item.DeletedAt, deletedAt));
            }
            if (scenario == "corruptReceipt") await f.Db.StorageProductBindings.ExecuteUpdateAsync(set => set.SetProperty(item => item.Version, 0L));
            var replay = await writer.PublishAsync(thread.Id, actor.Id, request.ClientRequestId, policies, bindings, default);
            Assert.Equal(scenario switch { "deletedReceipt" => "deleted", "corruptReceipt" => "conflict", "revokedAfterPublish" => "forbidden", _ => "published" }, replay.Status);
            if (scenario == "revokedAfterPublish") Assert.Null(replay.MessageId);
            if (scenario == "replyHiddenAfterPublish") Assert.Equal(result.MessageId, replay.MessageId);
            Assert.Equal(reads, local.ReadCalls);
            Assert.Equal(puts, local.PutCalls);
            Assert.Single(await f.Db.InternalChatMessages.Where(item => item.ClientRequestId == request.ClientRequestId).ToArrayAsync());
        }
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await f.Db.StorageCapacityReservations.AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending
            && item.IdempotencyKey.StartsWith("stage:")));
    }

    private sealed class ChatPublicationBindings(IProductStorageBindingService inner, bool failSecond) : IProductStorageBindingService
    {
        private int calls;
        public Task<ProductStorageBindingResult> PublishAsync(ProductStorageBindingRequest request, CancellationToken ct)
            => failSecond && ++calls == 2 ? Task.FromResult(new ProductStorageBindingResult(false, null, "notReady")) : inner.PublishAsync(request, ct);
    }

    private sealed class ChatPublicationCommitAckFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Armed && !Triggered && eventData.Context!.ChangeTracker.Entries<InternalChatMessage>().Any(item => item.Entity.ClientRequestId.HasValue))
            { Triggered = true; throw new IOException("Owned lost chat publication acknowledgement."); }
            return Task.CompletedTask;
        }
    }
}
