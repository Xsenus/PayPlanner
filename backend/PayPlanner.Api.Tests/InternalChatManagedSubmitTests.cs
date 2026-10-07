using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatManagedSubmitPublishesOrKeepsOnePendingFrozenAttempt(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var actor = await f.AddUserAsync("chat-managed-submit@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        settings.WorkerEnabled = true;
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var happy = CaptureSnapshot(thread.Id, actor.Id);
        var core = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        var controller = f.Controller(actor, configuration: config, chatWriter: core.Writer, chatPolicies: policy,
            chatBindings: core.Bindings, storageWorkerAvailability: new(true));
        var missingKey = ChatSubmitRequest(happy);
        missingKey.ClientRequestId = null;
        Assert.IsType<BadRequestObjectResult>((await controller.SendMessage(thread.Id, missingKey, default)).Result);
        Assert.False(await f.Db.InternalChatSendAttempts.AnyAsync());
        var sent = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.SendMessage(thread.Id, ChatSubmitRequest(happy), default)).Result).Value);
        Assert.Equal(2, sent.Attachments.Count);
        var puts = local.PutCalls;
        var replay = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await controller.SendMessage(thread.Id, ChatSubmitRequest(happy), default)).Result).Value);
        Assert.Equal(sent.Id, replay.Id);
        Assert.Equal(puts, local.PutCalls);
        var changed = happy with { Files = happy.Files.ToArray() };
        var changedInputs = ChatOriginalInputs(changed).ToArray();
        changedInputs[1] = new FormFile(new MemoryStream([6, 5, 4]), 0, 3, "files", changed.Files[1].FileName)
        { Headers = new HeaderDictionary(), ContentType = changed.Files[1].ContentType };
        var mismatch = ChatSubmitRequest(changed);
        mismatch.Files = changedInputs.ToList();
        Assert.IsType<ConflictObjectResult>((await controller.SendMessage(thread.Id, mismatch, default)).Result);
        Assert.Equal(puts, local.PutCalls);

        var pending = CaptureSnapshot(thread.Id, actor.Id);
        ChatOriginalStorage? intercepted = null;
        var delayed = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config, objects =>
        {
            intercepted = new ChatOriginalStorage(objects);
            intercepted.AfterWrite = async (result, call) =>
            {
                if (call == 2) await f.Db.StorageReplicas.Where(item => item.StorageObjectGeneration.StorageObject.OwnerId.StartsWith("chat-send:"))
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
                return result;
            };
            return intercepted;
        });
        var delayedController = f.Controller(actor, configuration: config, chatWriter: delayed.Writer, chatPolicies: policy,
            chatBindings: delayed.Bindings, storageWorkerAvailability: new(true));
        var waiting = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<AcceptedAtActionResult>(
            (await delayedController.SendMessage(thread.Id, ChatSubmitRequest(pending), default)).Result).Value);
        Assert.Equal("waiting", waiting.Status);
        Assert.Null(waiting.Message);
        Assert.Equal(pending.ClientRequestId, waiting.ClientRequestId);
        Assert.Equal(1, await f.Db.InternalChatMessages.CountAsync());
        var before = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync(item => item.ClientRequestId == pending.ClientRequestId);
        puts = local.PutCalls;
        settings.ProductWriteEnabledClasses.Clear();
        var paused = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<AcceptedAtActionResult>(
            (await delayedController.SendMessage(thread.Id, ChatSubmitRequest(pending), default)).Result).Value);
        Assert.Equal("paused", paused.Status);
        Assert.Null(paused.Message);
        Assert.Equal(puts, local.PutCalls);
        Assert.Equal(1, await f.Db.InternalChatMessages.CountAsync());
        var after = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync(item => item.Id == before.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.RequestSha256, after.RequestSha256);
        settings.ProductWriteEnabledClasses.Add("chat-attachment");
        intercepted!.AfterWrite = null;
        await f.Db.StorageReplicas.ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Available));
        var completed = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await delayedController.SendMessage(thread.Id, ChatSubmitRequest(pending), default)).Result).Value);
        Assert.NotEqual(sent.Id, completed.Id);
        Assert.Equal(puts, local.PutCalls);
        Assert.Equal(2, await f.Db.InternalChatSendAttempts.CountAsync());
        Assert.Equal(2, await f.Db.InternalChatMessages.CountAsync());
        Assert.Equal(4, await f.Db.StorageProductBindings.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChatManagedSubmitLegacyCommitRechecksConcurrentIntentAndAccess(bool postgres, bool revokeAccess)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres, true);
        var actor = await f.AddUserAsync("chat-legacy-race@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var config = ChatPublicationConfiguration(root.Path);
        var first = ChatOriginalInputs(snapshot)[0];
        var callback = new ChatLegacyCaptureFile(first, async () =>
        {
            await using var external = CaptureFreshDb(f, true);
            if (revokeAccess) await external.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
            else Assert.Equal("recorded", (await new InternalChatSendAttemptRegistry(external).CaptureAsync(snapshot, default)).Status);
        });
        var request = ChatSubmitRequest(snapshot);
        request.Files[0] = callback;
        var result = (await f.Controller(actor, configuration: config).SendMessage(thread.Id, request, default)).Result;
        if (revokeAccess) Assert.IsType<ForbidResult>(result);
        else Assert.IsType<ConflictObjectResult>(result);
        Assert.False(await f.Db.InternalChatMessages.AnyAsync());
        Assert.False(await f.Db.InternalChatAttachments.AnyAsync());
        Assert.False(await f.Db.StorageProductBindings.AnyAsync());
        Assert.Null(await f.Db.InternalChatThreads.Where(item => item.Id == thread.Id).Select(item => item.LastMessageAt).SingleAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories));
    }

    private static SendInternalChatMessageRequest ChatSubmitRequest(InternalChatSendSnapshot snapshot)
        => new()
        {
            Text = snapshot.Text,
            ClientRequestId = snapshot.ClientRequestId,
            ReplyToMessageId = snapshot.ReplyToMessageId,
            MentionUserIds = snapshot.MentionUserIds.ToList(),
            Files = ChatOriginalInputs(snapshot).ToList()
        };

    private sealed class ChatLegacyCaptureFile(IFormFile inner, Func<Task> afterCopy) : IFormFile
    {
        public string ContentType => inner.ContentType;
        public string ContentDisposition => inner.ContentDisposition;
        public IHeaderDictionary Headers => inner.Headers;
        public long Length => inner.Length;
        public string Name => inner.Name;
        public string FileName => inner.FileName;
        public Stream OpenReadStream() => inner.OpenReadStream();
        public void CopyTo(Stream target) => throw new NotSupportedException();
        public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        { await inner.CopyToAsync(target, cancellationToken); await afterCopy(); }
    }
}
