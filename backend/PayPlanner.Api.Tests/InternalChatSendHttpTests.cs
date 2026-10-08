using System.Reflection;
using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "thread")]
    [InlineData(true, "thread")]
    [InlineData(false, "author")]
    [InlineData(true, "author")]
    [InlineData(false, "key")]
    [InlineData(true, "key")]
    public async Task ChatSendHttpDoesNotReturnDtoWhoseScopeChangedAfterReceipt(bool postgres, string changed)
    {
        var mutation = new ChatHttpDtoScopeChange();
        await using var f = await CreateMigratedFixtureAsync(postgres, true, mutation);
        var actor = await f.AddUserAsync("chat-http-scope@example.test", "Автор");
        var other = await f.AddUserAsync("chat-http-scope-other@example.test", "Другой");
        var thread = await CaptureChannelAsync(f, actor, other);
        var otherThread = await CaptureChannelAsync(f, other);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var monitor = new ChatOriginalOptions(ChatPublicationSettings());
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        Assert.Equal("published", (await core.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policy, core.Bindings, default)).Status);
        var reads = local.ReadCalls;
        mutation.OnRead = async () =>
        {
            await using var external = CaptureFreshDb(f, true);
            var messages = external.InternalChatMessages.Where(item => item.ClientRequestId == snapshot.ClientRequestId);
            if (changed == "thread") await messages.ExecuteUpdateAsync(set => set.SetProperty(item => item.ThreadId, otherThread.Id));
            else if (changed == "author") await messages.ExecuteUpdateAsync(set => set.SetProperty(item => item.AuthorUserId, other.Id));
            else
            {
                var anotherKey = Guid.NewGuid();
                await messages.ExecuteUpdateAsync(set => set.SetProperty(item => item.ClientRequestId, anotherKey));
            }
        };
        mutation.Armed = true;
        var controller = f.Controller(actor, configuration: config, chatWriter: core.Writer, chatPolicies: policy,
            chatBindings: core.Bindings, storageWorkerAvailability: new(false));
        var result = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetSendStatus(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.True(mutation.Triggered);
        Assert.Equal("conflict", result.Status);
        Assert.Null(result.Message);
        Assert.False(result.AutomaticRecovery);
        Assert.Null(result.RetryAfter);
        Assert.Equal(reads, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.Single(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.True(await f.Db.InternalChatMessages.Select(item => item.NotificationDispatchPending).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendHttpCatalogRoutesKeepActorThreadAndCurrentAccess(bool postgres)
    {
        await using var f = await CreateMigratedFixtureAsync(postgres);
        var actor = await f.AddUserAsync("chat-http@example.test", "Автор");
        var other = await f.AddUserAsync("chat-http-other@example.test", "Другой");
        var outsider = await f.AddUserAsync("chat-http-outsider@example.test", "Посторонний");
        var thread = await CaptureChannelAsync(f, actor, other);
        var another = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        settings.WorkerEnabled = true;
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        InternalChatController Controller(PayPlanner.Api.Models.User user) => f.Controller(user, configuration: config,
            chatWriter: core.Writer, chatPolicies: policy, chatBindings: core.Bindings, storageWorkerAvailability: new(true));
        var controller = Controller(actor);
        var state = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetSendStatus(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.Equal("waiting", state.Status);
        Assert.True(state.AutomaticRecovery);
        Assert.Null(state.Message);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        var page = Assert.IsType<InternalChatSendDiscoveryResult>(Assert.IsType<OkObjectResult>(
            (await controller.GetPendingSends(thread.Id, default)).Result).Value);
        Assert.Equal(snapshot.ClientRequestId, Assert.Single(page.Items).ClientRequestId);
        Assert.Null(page.NextAfterAttemptId);
        var foreign = Assert.IsType<InternalChatSendDiscoveryResult>(Assert.IsType<OkObjectResult>(
            (await Controller(other).GetPendingSends(thread.Id, default)).Result).Value);
        Assert.Empty(foreign.Items);
        Assert.IsType<NotFoundObjectResult>((await Controller(other).GetSendStatus(thread.Id, snapshot.ClientRequestId, default)).Result);
        Assert.IsType<NotFoundObjectResult>((await controller.GetSendStatus(another.Id, snapshot.ClientRequestId, default)).Result);
        Assert.IsType<ForbidResult>((await Controller(outsider).GetSendStatus(thread.Id, snapshot.ClientRequestId, default)).Result);
        Assert.IsType<ForbidResult>((await Controller(outsider).ResumeSend(thread.Id, snapshot.ClientRequestId, default)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetSendStatus(thread.Id, Guid.Empty, default)).Result);
        foreach (var limit in new[] { 0, 51 }) Assert.IsType<BadRequestObjectResult>((await controller.GetPendingSends(thread.Id, default, limit)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetPendingSends(thread.Id, default, afterAttemptId: Guid.NewGuid())).Result);
        await f.Db.InternalChatMembers.Where(item => item.UserId == actor.Id && item.ThreadId == thread.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        Assert.IsType<ForbidResult>((await controller.GetSendStatus(thread.Id, snapshot.ClientRequestId, default)).Result);
        Assert.IsType<ForbidResult>((await controller.GetPendingSends(thread.Id, default)).Result);
        Assert.IsType<ForbidResult>((await controller.ResumeSend(thread.Id, snapshot.ClientRequestId, default)).Result);
        Assert.Equal(0, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.False(await f.Db.InternalChatMessages.AnyAsync());
        Assert.False(await f.Db.StorageProductBindings.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendHttpExplicitResumePublishesWithoutHostedWorkerAndDispatchesOnce(bool postgres)
    {
        await using var f = await CreateMigratedFixtureAsync(postgres);
        var actor = await f.AddUserAsync("chat-http-resume@example.test", "Автор");
        var other = await f.AddUserAsync("chat-http-recipient@example.test", "Получатель");
        var thread = await CaptureChannelAsync(f, actor, other);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id) with { MentionUserIds = [other.Id] };
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var monitor = new ChatOriginalOptions(ChatPublicationSettings());
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        var notifications = DispatchProxy.Create<INotificationService, NotificationProxy>();
        var events = ((NotificationProxy)(object)notifications).Events;
        var controller = f.Controller(actor, configuration: config, notifications: notifications,
            chatWriter: core.Writer, chatPolicies: policy, chatBindings: core.Bindings, storageWorkerAvailability: new(false));
        var paused = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.GetSendStatus(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.Equal("paused", paused.Status);
        Assert.False(paused.AutomaticRecovery);
        Assert.Empty(events);
        var published = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.ResumeSend(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.Equal("published", published.Status);
        Assert.Equal(2, Assert.IsType<InternalChatMessageDto>(published.Message).Attachments.Count);
        Assert.NotEmpty(events);
        var deliveries = events.Count;
        var reads = local.ReadCalls;
        var replay = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.ResumeSend(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.Equal(published.Message!.Id, replay.Message!.Id);
        await controller.GetSendStatus(thread.Id, snapshot.ClientRequestId, default);
        Assert.Equal(deliveries, events.Count);
        Assert.Equal(reads, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.Single(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.False(await f.Db.InternalChatMessages.Select(item => item.NotificationDispatchPending).SingleAsync());
        Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendHttpResumePreservesPendingOriginalsWhenCopiesOrFlagsBlockPublication(bool postgres)
    {
        await using var f = await CreateMigratedFixtureAsync(postgres);
        var actor = await f.AddUserAsync("chat-http-pending@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        settings.WorkerEnabled = true;
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var core = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        await f.Db.StorageReplicas.ExecuteUpdateAsync(set => set.SetProperty(item => item.Status, StorageReplicaStatuses.Pending));
        var controller = f.Controller(actor, configuration: config, chatWriter: core.Writer, chatPolicies: policy,
            chatBindings: core.Bindings, storageWorkerAvailability: new(true));
        var waiting = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.ResumeSend(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.Equal("waiting", waiting.Status);
        Assert.Null(waiting.Message);
        var before = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        settings.ProductWriteEnabledClasses.Clear();
        var paused = Assert.IsType<InternalChatSendStateDto>(Assert.IsType<OkObjectResult>(
            (await controller.ResumeSend(thread.Id, snapshot.ClientRequestId, default)).Result).Value);
        Assert.Equal("paused", paused.Status);
        Assert.False(paused.AutomaticRecovery);
        var after = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.RequestSha256, after.RequestSha256);
        Assert.Equal(before.RecoveryAttemptCount, after.RecoveryAttemptCount);
        Assert.Equal(0, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.False(await f.Db.InternalChatMessages.AnyAsync());
        Assert.False(await f.Db.StorageProductBindings.AnyAsync());
    }

    private sealed class ChatHttpDtoScopeChange : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public Func<Task>? OnRead { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("InternalChatReactions", StringComparison.Ordinal)
                && command.CommandText.Contains("InternalChatMessages", StringComparison.Ordinal))
            {
                Armed = false;
                Triggered = true;
                await OnRead!();
            }
            return result;
        }
    }
}
