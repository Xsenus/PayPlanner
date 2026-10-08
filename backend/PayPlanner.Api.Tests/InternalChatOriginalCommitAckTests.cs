using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false, "pointer")]
    [InlineData(true, "pointer")]
    [InlineData(false, "acceptance")]
    [InlineData(true, "acceptance")]
    public async Task ChatOriginalCommittedPointersAndAcceptanceSurviveLostDatabaseAcknowledgement(bool postgres, string boundary)
    {
        var fault = new ChatOriginalAckState(boundary);
        await using var f = await CreateMigratedFixtureAsync(postgres, true, new ChatOriginalAckCommands(fault), new ChatOriginalAckCommit(fault));
        var actor = await f.AddUserAsync("chat-original-ack@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var monitor = new ChatOriginalOptions(ChatPublicationSettings());
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var initial = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => initial.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default));
        Assert.True(fault.Triggered);
        var puts = local.PutCalls;
        Assert.Equal(2, puts);
        await using var recovered = CaptureFreshDb(f, true);
        var attempt = await recovered.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(boundary == "acceptance", attempt.AcceptedAt.HasValue);
        Assert.NotNull(attempt.RecoveryLeaseToken);
        var pointers = await recovered.InternalChatSendAttemptFiles.AsNoTracking().OrderBy(item => item.Index).ToArrayAsync();
        Assert.Equal(2, pointers.Length);
        Assert.All(pointers, item => Assert.NotNull(item.StorageObjectId));
        Assert.Empty(await recovered.InternalChatMessages.ToArrayAsync());
        Assert.Empty(await recovered.InternalChatAttachments.ToArrayAsync());
        Assert.Empty(await recovered.StorageProductBindings.ToArrayAsync());
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        var next = ChatPublicationCore(recovered, providers, policy, monitor, environment, config);
        Assert.Equal("busy", (await next.Writer.ResumeAsync(thread.Id, actor.Id, snapshot.ClientRequestId, default)).Status);
        Assert.Equal(puts, local.PutCalls);
        var expired = DateTime.UtcNow.AddSeconds(-1);
        await recovered.InternalChatSendAttempts.Where(item => item.Id == attempt.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired));
        var resumed = await next.Writer.ResumeAsync(thread.Id, actor.Id, snapshot.ClientRequestId, default);
        Assert.Equal("accepted", resumed.Status);
        Assert.True(resumed.Accepted);
        Assert.Equal(puts, local.PutCalls);
        var published = await next.Writer.PublishAsync(thread.Id, actor.Id, snapshot.ClientRequestId, policy, next.Bindings, default);
        Assert.Equal("published", published.Status);
        Assert.Equal(puts, local.PutCalls);
        Assert.Equal(2, await recovered.StorageObjects.CountAsync());
        Assert.Equal(2, await recovered.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await recovered.StorageProductBindings.CountAsync());
        Assert.Equal(published.MessageId, Assert.Single(await recovered.InternalChatMessages.ToArrayAsync()).Id);
        var after = await recovered.InternalChatSendAttemptFiles.AsNoTracking().OrderBy(item => item.Index).ToArrayAsync();
        Assert.Equal(pointers.Select(item => (item.StorageObjectId, item.StorageGeneration)), after.Select(item => (item.StorageObjectId, item.StorageGeneration)));
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private sealed class ChatOriginalAckState(string boundary)
    {
        public bool Armed { get; set; }
        public bool Triggered { get; set; }
        public DbTransaction? Target { get; private set; }
        private int pointers;
        public void Observe(DbCommand command)
        {
            if (!Armed || Triggered || command.Transaction is null) return;
            if (boundary == "pointer" && command.CommandText.Contains("UPDATE \"InternalChatSendAttemptFiles\"", StringComparison.Ordinal)
                && command.CommandText.Contains("\"StorageObjectId\"", StringComparison.Ordinal) && ++pointers == 2)
                Target = command.Transaction;
            if (boundary == "acceptance" && command.CommandText.Contains("UPDATE \"InternalChatSendAttempts\"", StringComparison.Ordinal)
                && command.CommandText.Contains("SET \"AcceptedAt\"", StringComparison.Ordinal)) Target = command.Transaction;
        }
    }

    private sealed class ChatOriginalAckCommands(ChatOriginalAckState state) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { state.Observe(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { state.Observe(command); return ValueTask.FromResult(result); }
    }

    private sealed class ChatOriginalAckCommit(ChatOriginalAckState state) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (state.Armed && !state.Triggered && ReferenceEquals(state.Target, transaction))
            { state.Triggered = true; throw new IOException("Owned lost original database commit acknowledgement."); }
            return Task.CompletedTask;
        }
    }
}
