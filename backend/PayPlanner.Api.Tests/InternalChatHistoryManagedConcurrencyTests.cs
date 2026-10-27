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
    public async Task PersonalHistory_HiddenAttachmentCannotBeReadByItsOwnerButRemainsAvailableToOthers(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var owner = await fixture.AddUserAsync("history-attachment-owner@example.test", "Владелец");
        var colleague = await fixture.AddUserAsync("history-attachment-colleague@example.test", "Коллега");
        var thread = await CaptureChannelAsync(fixture, owner, colleague);
        var message = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(owner).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "Файл" }, default)).Result).Value);
        var attachment = new InternalChatAttachment
        {
            MessageId = message.Id,
            FileName = "managed.txt",
            StoredFileName = "managed.txt",
            ContentType = "text/plain",
            Size = 3,
            UploadedByUserId = owner.Id
        };
        fixture.Db.InternalChatAttachments.Add(attachment);
        await fixture.Db.SaveChangesAsync();
        var hidden = State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(message.Id, 0), default));
        var reader = new RecordingChatProductReader(ProductStorageReadStatus.Available);
        Assert.IsType<NotFoundResult>(await fixture.Controller(owner, productStorageReader: reader).DownloadAttachment(attachment.Id, default));
        Assert.Equal(0, reader.Calls);
        var colleagueDownload = Assert.IsType<FileStreamResult>(await fixture.Controller(colleague, productStorageReader: reader).DownloadAttachment(attachment.Id, default));
        await colleagueDownload.FileStream.DisposeAsync();
        Assert.Equal(1, reader.Calls);
        State(await fixture.Controller(owner).SetHistoryVisibility(thread.Id, Body(null, hidden.Revision), default));
        var restoredDownload = Assert.IsType<FileStreamResult>(await fixture.Controller(owner, productStorageReader: reader).DownloadAttachment(attachment.Id, default));
        await restoredDownload.FileStream.DisposeAsync();
        Assert.Equal(2, reader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersonalHistory_HideDuringManagedPublicationPreservesTheNewMessageAndAttachments(bool postgres)
    {
        await using var fixture = postgres ? await Fixture.CreatePostgresModelAsync() : await Fixture.CreateSharedModelAsync();
        var actor = await fixture.AddUserAsync("history-managed@example.test", "Автор");
        var thread = await CaptureChannelAsync(fixture, actor);
        var old = Assert.IsType<InternalChatMessageDto>(Assert.IsType<CreatedAtActionResult>(
            (await fixture.Controller(actor).SendMessage(thread.Id, new SendInternalChatMessageRequest { Text = "Старая история" }, default)).Result).Value);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policies = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(fixture.Db, providers, policies, monitor, environment, config);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        await using var observer = CaptureFreshDb(fixture, independentSqlite: true);
        var captured = State(await fixture.Controller(actor, db: observer).GetHistoryVisibility(thread.Id, default));
        Assert.Equal(old.Id, captured.LatestMessageId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        local.BeforeReadReturn = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var publish = core.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policies, core.Bindings, default);
        InternalChatSendPublicationResult result;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var hidden = State(await fixture.Controller(actor, db: observer).SetHistoryVisibility(thread.Id,
                Body(captured.LatestMessageId, captured.Revision), default));
            Assert.Equal(old.Id, hidden.HiddenThroughMessageId);
        }
        finally
        {
            local.BeforeReadReturn = null;
            release.TrySetResult();
            result = await publish;
        }
        Assert.Equal("published", result.Status);
        Assert.True(result.MessageId > old.Id);
        observer.ChangeTracker.Clear();
        var visible = Assert.Single((await Page(fixture.Controller(actor, db: observer), thread.Id)).Items);
        Assert.Equal(result.MessageId, visible.Id);
        Assert.Equal(snapshot.Text, visible.Text);
        Assert.Equal(2, visible.Attachments.Count);
        Assert.Equal(2, await observer.InternalChatMessages.CountAsync());
    }
}
