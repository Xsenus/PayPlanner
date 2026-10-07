using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendStatusAcceptedOriginalsReenterRecoveryAfterNeedsInput(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var actor = await f.AddUserAsync("chat-status-input@example.test", "Автор");
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
        Assert.Equal("recorded", (await new InternalChatSendAttemptRegistry(f.Db).CaptureAsync(snapshot, default)).Status);
        Assert.Equal(1, (await core.Writer.ProcessRecoveryBatchAsync(policy, core.Bindings, default)).Deferred);
        var missing = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, new(true), default);
        Assert.Equal("needsInput", missing.Status);
        Assert.False(missing.AutomaticRecovery);
        Assert.Equal(0, local.ReadCalls);
        Assert.Equal(0, local.PutCalls);
        Assert.Equal(0, (await core.Writer.ProcessRecoveryBatchAsync(policy, core.Bindings, default)).Inspected);
        Assert.Equal("accepted", (await core.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        Assert.Equal("needsInput", await f.Db.InternalChatSendAttempts.Select(item => item.RecoveryLastStatus).SingleAsync());
        var accepted = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, new(true), default);
        Assert.Equal("waiting", accepted.Status);
        Assert.True(accepted.AutomaticRecovery);
        Assert.Equal(missing.AttemptId, accepted.AttemptId);
        var due = DateTime.UtcNow.AddMinutes(-1);
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryNextAttemptAt, due));
        Assert.Equal(1, (await core.Writer.ProcessRecoveryBatchAsync(policy, core.Bindings, default)).Published);
        var published = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, new(true), default);
        Assert.Equal("published", published.Status);
        Assert.Equal(2, local.PutCalls);
        Assert.Single(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.Equal(2, await f.Db.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendStatusHidesReceiptWhenAccessIsRemovedDuringCatalogRead(bool postgres)
    {
        var revoke = new ChatStatusAccessRevocation();
        await using var f = await Fixture.CreateMigratedAsync(postgres, true, revoke);
        var actor = await f.AddUserAsync("chat-status-revoked@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
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
        revoke.OnRead = async () =>
        {
            await using var external = CaptureFreshDb(f, true);
            await external.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        };
        revoke.Armed = true;
        var status = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, new(true), default);
        Assert.True(revoke.Triggered);
        Assert.Equal("forbidden", status.Status);
        Assert.Null(status.AttemptId);
        Assert.Null(status.MessageId);
        Assert.False(status.AutomaticRecovery);
        Assert.Null(status.RetryAfter);
        Assert.Equal(reads, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.Single(await f.Db.InternalChatMessages.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendStatusReadsOnlyOwnedCatalogWithoutChangingRecovery(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var actor = await f.AddUserAsync("chat-status@example.test", "Автор");
        var other = await f.AddUserAsync("chat-status-other@example.test", "Другой");
        var thread = await CaptureChannelAsync(f, actor, other);
        var otherThread = await CaptureChannelAsync(f, actor);
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
        var id = await f.Db.InternalChatSendAttempts.Select(item => item.Id).SingleAsync();
        var availability = new StorageWorkerAvailability(true);
        var clock = DateTime.UtcNow.AddMinutes(5);
        var next = new DateTime(clock.Ticks - clock.Ticks % 10, DateTimeKind.Utc);
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryNextAttemptAt, next));

        async Task<InternalChatSendStatusResult> Read(string expected, bool automatic, StorageWorkerAvailability? worker = null)
        {
            var before = await f.Db.InternalChatSendAttempts.AsNoTracking().Select(item => new
            { item.Version, item.RecoveryLeaseToken, item.RecoveryLeaseExpiresAt, item.RecoveryAttemptCount, item.RecoveryNextAttemptAt, item.RecoveryLastStatus }).SingleAsync();
            var result = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, worker ?? availability, default);
            Assert.Equal(expected, result.Status);
            Assert.Equal(id, result.AttemptId);
            Assert.Equal(automatic, result.AutomaticRecovery);
            Assert.Null(result.MessageId);
            if (!automatic) Assert.Null(result.RetryAfter);
            var after = await f.Db.InternalChatSendAttempts.AsNoTracking().Select(item => new
            { item.Version, item.RecoveryLeaseToken, item.RecoveryLeaseExpiresAt, item.RecoveryAttemptCount, item.RecoveryNextAttemptAt, item.RecoveryLastStatus }).SingleAsync();
            Assert.Equal(before, after);
            Assert.Equal(0, local.ReadCalls);
            Assert.Equal(2, local.PutCalls);
            Assert.False(await f.Db.InternalChatMessages.AnyAsync());
            Assert.False(await f.Db.StorageProductBindings.AnyAsync());
            return result;
        }
        Assert.Equal(next, (await Read("waiting", true)).RetryAfter);
        foreach (var (stored, expected, automatic) in new[]
        {
            ("unavailable", "retrying", true), ("needsInput", "waiting", true),
            ("deadLetter", "failed", false), ("conflict", "conflict", false), ("invalid", "conflict", false)
        })
        {
            await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLastStatus, stored));
            await Read(expected, automatic);
        }
        var activeLease = Guid.NewGuid();
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLastStatus, (string?)null)
            .SetProperty(item => item.RecoveryLeaseToken, activeLease).SetProperty(item => item.RecoveryLeaseExpiresAt, next));
        await Read("processing", true);
        await Read("processing", false, new(false)); // Explicit continuation may run without a hosted worker.
        settings.Enabled = false;
        await Read("paused", false);
        settings.Enabled = true;
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseToken, (Guid?)null)
            .SetProperty(item => item.RecoveryLeaseExpiresAt, (DateTime?)null));
        settings.WorkerEnabled = false;
        await Read("paused", false);
        settings.WorkerEnabled = true;
        await Read("paused", false, new(false));
        settings.UploadCapacityChecksEnabled = false;
        await Read("paused", false);
        settings.UploadCapacityChecksEnabled = true;
        settings.ProductWriteEnabledClasses.Clear();
        await Read("paused", false);
        settings.ProductWriteEnabledClasses.Add("chat-attachment");
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Text, "Changed frozen payload"));
        await Read("conflict", false);
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Text, snapshot.Text));
        await f.Db.StorageObjects.ExecuteUpdateAsync(set => set.SetProperty(item => item.IsDeleted, true));
        await Read("conflict", false);
        await f.Db.StorageObjects.ExecuteUpdateAsync(set => set.SetProperty(item => item.IsDeleted, false));
        var usableVersion = await f.Db.InternalChatSendAttempts.Select(item => item.Version).SingleAsync();
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Version, long.MaxValue));
        await Read("failed", false);
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Version, usableVersion));

        foreach (var (scope, owner, key, expected) in new[]
        {
            (thread.Id, other.Id, snapshot.ClientRequestId, "notFound"),
            (otherThread.Id, actor.Id, snapshot.ClientRequestId, "notFound"),
            (thread.Id, actor.Id, Guid.NewGuid(), "notFound"),
            (thread.Id, actor.Id, Guid.Empty, "invalid")
        })
        {
            var result = await core.Writer.ReadStatusAsync(scope, owner, key, availability, default);
            Assert.Equal(expected, result.Status);
            Assert.Null(result.AttemptId);
            Assert.Null(result.MessageId);
        }
        await f.Db.InternalChatMembers.Where(item => item.ThreadId == thread.Id && item.UserId == actor.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.IsActive, false));
        var forbidden = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, availability, default);
        Assert.Equal("forbidden", forbidden.Status);
        Assert.Null(forbidden.AttemptId);
        Assert.Null(forbidden.MessageId);
        Assert.Equal(0, local.ReadCalls);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendStatusValidatesHistoricalPublicationWithWritesDisabled(bool postgres)
    {
        await using var f = await Fixture.CreateMigratedAsync(postgres);
        var actor = await f.AddUserAsync("chat-status-published@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
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
        var published = await core.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policy, core.Bindings, default);
        Assert.Equal("published", published.Status);
        monitor.CurrentValue.Enabled = false;
        monitor.CurrentValue.UploadCapacityChecksEnabled = false;
        monitor.CurrentValue.ProductWriteEnabledClasses.Clear();
        var reads = local.ReadCalls;
        var version = await f.Db.InternalChatSendAttempts.Select(item => item.Version).SingleAsync();
        async Task<InternalChatSendStatusResult> Read(string expected)
        {
            var result = await core.Writer.ReadStatusAsync(thread.Id, actor.Id, snapshot.ClientRequestId, new(false), default);
            Assert.Equal(expected, result.Status);
            Assert.Equal(published.AttemptId, result.AttemptId);
            Assert.Equal(expected == "published" ? published.MessageId : null, result.MessageId);
            Assert.False(result.AutomaticRecovery);
            Assert.Null(result.RetryAfter);
            Assert.Equal(reads, local.ReadCalls);
            Assert.Equal(2, local.PutCalls);
            Assert.Equal(version, await f.Db.InternalChatSendAttempts.Select(item => item.Version).SingleAsync());
            return result;
        }
        await Read("published");
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.Version, long.MaxValue));
        version = long.MaxValue;
        await Read("published");
        var changedAt = DateTime.UtcNow;
        await f.Db.InternalChatMessages.ExecuteUpdateAsync(set => set.SetProperty(item => item.Text, "Допустимая правка")
            .SetProperty(item => item.EditedAt, changedAt));
        await Read("published");
        await f.Db.InternalChatMessages.ExecuteUpdateAsync(set => set.SetProperty(item => item.DeletedAt, changedAt));
        await Read("deleted");
        await f.Db.InternalChatMessages.ExecuteUpdateAsync(set => set.SetProperty(item => item.DeletedAt, (DateTime?)null));
        await f.Db.InternalChatAttachments.ExecuteUpdateAsync(set => set.SetProperty(item => item.Size, item => item.Size + 1));
        await Read("conflict");
        await f.Db.InternalChatAttachments.ExecuteUpdateAsync(set => set.SetProperty(item => item.Size, item => item.Size - 1));
        var binding = await f.Db.StorageProductBindings.AsNoTracking().OrderBy(item => item.SourceId).FirstAsync();
        var wrongObject = await f.Db.StorageProductBindings.Where(item => item.SourceId != binding.SourceId).Select(item => item.StorageObjectId).SingleAsync();
        await f.Db.StorageProductBindings.Where(item => item.SourceId == binding.SourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.StorageObjectId, wrongObject));
        await Read("conflict");
        await f.Db.StorageProductBindings.Where(item => item.SourceId == binding.SourceId)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.StorageObjectId, binding.StorageObjectId));
        await Read("published");
        Assert.Single(await f.Db.InternalChatMessages.ToArrayAsync());
        Assert.Equal(2, await f.Db.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class ChatStatusAccessRevocation : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Triggered { get; private set; }
        public Func<Task>? OnRead { get; set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("StorageProductBindings", StringComparison.Ordinal))
            {
                Armed = false;
                Triggered = true;
                await OnRead!();
            }
            return result;
        }
    }
}
