using System.Data.Common;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class InternalChatControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendRecoveryConcurrentBatchesPublishOneGraph(bool postgres)
    {
        await using var f = await CreateMigratedFixtureAsync(postgres, true);
        var actor = await f.AddUserAsync("chat-batch-concurrent@example.test", "Автор");
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
        var first = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await first.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        await using var secondDb = CaptureFreshDb(f, true);
        var second = ChatPublicationCore(secondDb, providers, policy, monitor, environment, config);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        local.BeforeReadReturn = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var processing = first.Writer.ProcessRecoveryBatchAsync(policy, first.Bindings, default);
        InternalChatSendRecoveryBatchResult winner;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var receipt = await secondDb.InternalChatSendAttempts.AsNoTracking().SingleAsync();
            Assert.NotNull(receipt.RecoveryLeaseToken);
            var reads = local.ReadCalls;
            Assert.Equal(new InternalChatSendRecoveryBatchResult(0, 0, 0, 0, 0),
                await second.Writer.ProcessRecoveryBatchAsync(policy, second.Bindings, default));
            var after = await secondDb.InternalChatSendAttempts.AsNoTracking().SingleAsync();
            Assert.Equal(receipt.RecoveryLeaseToken, after.RecoveryLeaseToken);
            Assert.Equal(receipt.Version, after.Version);
            Assert.Equal(0, after.RecoveryAttemptCount);
            Assert.Equal(reads, local.ReadCalls);
            Assert.False(await secondDb.InternalChatMessages.AnyAsync());
            Assert.False(await secondDb.InternalChatAttachments.AnyAsync());
            Assert.False(await secondDb.StorageProductBindings.AnyAsync());
        }
        finally
        {
            local.BeforeReadReturn = null;
            release.TrySetResult();
            winner = await processing;
        }
        Assert.Equal(new InternalChatSendRecoveryBatchResult(1, 1, 1, 0, 0), winner);
        var completedReads = local.ReadCalls;
        Assert.Equal(new InternalChatSendRecoveryBatchResult(0, 0, 0, 0, 0),
            await second.Writer.ProcessRecoveryBatchAsync(policy, second.Bindings, default));
        Assert.Equal(completedReads, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        var message = Assert.Single(await secondDb.InternalChatMessages.ToArrayAsync());
        Assert.Equal(snapshot.ClientRequestId, message.ClientRequestId);
        Assert.True(message.NotificationDispatchPending);
        Assert.Equal(2, await secondDb.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await secondDb.StorageProductBindings.CountAsync());
        var completed = await secondDb.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        Assert.Null(completed.RecoveryLeaseToken);
        Assert.Equal("published", completed.RecoveryLastStatus);
        Assert.Equal(1, completed.RecoveryAttemptCount);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
        Assert.False(await secondDb.StorageCapacityReservations.AnyAsync(item => item.Status == StorageCapacityReservationStatuses.Pending
            && item.IdempotencyKey.StartsWith("stage:")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendRecoveryHostedWorkerUsesFreshScopeAfterCopyProcessing(bool postgres)
    {
        var workerDiagnostics = new ChatRecoveryWorkerDiagnostics();
        workerDiagnostics.Mark("fixture-start");
        await using var f = await CreateMigratedFixtureAsync(postgres, true);
        workerDiagnostics.ObserveSeed(f.Db);
        workerDiagnostics.Mark("fixture-ready");
        var actor = await f.AddUserAsync("chat-worker@example.test", "Автор");
        var thread = await CaptureChannelAsync(f, actor);
        var snapshot = CaptureSnapshot(thread.Id, actor.Id);
        using var root = new ChatOriginalRoot();
        var environment = new ChatOriginalEnvironment(root.Path);
        var config = ChatPublicationConfiguration(root.Path);
        var settings = ChatPublicationSettings();
        settings.WorkerEnabled = true;
        settings.WorkerPollSeconds = 300;
        var monitor = new ChatOriginalOptions(settings);
        var local = new ChatRecoveryLocal(new LocalStorageProvider(environment, config));
        var providers = new StorageProviderRegistry([local, new LegacyLocalStorageProvider(environment, config)]);
        var policy = new StoragePolicyService(monitor, providers);
        var seed = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await seed.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        workerDiagnostics.Mark("seed-accepted");
        var signal = new ChatRecoveryPublicationSignal();
        var services = new ServiceCollection();
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IOptionsMonitor<StorageOrchestrationOptions>>(monitor);
        services.AddSingleton<IStorageProviderRegistry>(providers);
        services.AddSingleton<IStoragePolicyService>(policy);
        services.AddSingleton<ILogger<InternalChatSendRecovery>>(signal);
        services.AddSingleton<ILogger<StorageOriginalReadStagingService>>(NullLogger<StorageOriginalReadStagingService>.Instance);
        services.AddScoped(_ =>
        {
            var context = CaptureFreshDb(f, true);
            workerDiagnostics.ObserveScope(context);
            return context;
        });
        services.AddScoped<IStorageStageCapacityService, StorageStageCapacityService>();
        services.AddScoped<IStorageStageWriterMarkerRecoveryService, StorageStageCapacityService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<StorageStageMarkerMaintenance>();
        services.AddScoped<IStorageOriginalReadStagingService, StorageOriginalReadStagingService>();
        services.AddScoped<IStorageObjectService, StorageObjectService>();
        services.AddScoped<IProductStorageBindingService, ProductStorageBindingService>();
        services.AddInternalChatStorageRecovery();
        services.AddScoped<IStorageOperationProcessor>(sp => new ChatDirtyCopyProcessor(sp.GetRequiredService<PaymentContext>(), thread.Id, actor.Id,
            () => workerDiagnostics.MarkCopy(entered: true), () => workerDiagnostics.MarkCopy(entered: false)));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var worker = new StorageOperationWorker(provider.GetRequiredService<IServiceScopeFactory>(), monitor,
            workerDiagnostics, provider.GetRequiredService<StorageStageMarkerMaintenance>());
        workerDiagnostics.Mark("start-enter");
        await worker.StartAsync(default);
        workerDiagnostics.Mark("start-return");
        string? deadline = null;
        var afterStop = "not-observed";
        ExceptionDispatchInfo? stopFailure = null;
        try
        {
            await signal.Published.Task.WaitAsync(TimeSpan.FromSeconds(15));
            workerDiagnostics.Mark("published-signal");
        }
        catch (TimeoutException)
        {
            workerDiagnostics.Mark("publication-deadline");
            deadline = workerDiagnostics.Summary + "; " + signal.Summary
                + $"; Reads={local.ReadCalls}; Puts={local.PutCalls}; ExecuteState={worker.ExecuteTask?.Status}";
        }
        finally
        {
            workerDiagnostics.Mark("stop-enter");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await worker.StopAsync(stop.Token); }
            catch (Exception error)
            {
                stopFailure = ExceptionDispatchInfo.Capture(error);
                workerDiagnostics.ObserveDiagnosticError(error);
            }
            workerDiagnostics.Mark("stop-return");
            // Never race this observation against an unfinished owned worker.
            var joined = worker.ExecuteTask?.IsCompleted == true;
            var metadata = deadline is not null && joined
                ? await ReadStoppedChatMetadataAsync(f, workerDiagnostics)
                : deadline is null ? "not-requested" : "skipped-unfinished-worker";
            afterStop = $"Joined={joined}; ExecuteState={worker.ExecuteTask?.Status}; Metadata={metadata}; "
                + $"Reads={local.ReadCalls}; Puts={local.PutCalls}; " + signal.Summary;
            workerDiagnostics.Report(afterStop);
        }
        // A later publication/cleanup cannot turn the original deadline into success.
        if (deadline is not null)
            Assert.Fail("Chat publication signal timed out at the original 15-second boundary. AtDeadline: " + deadline
                + "; AfterStop: " + workerDiagnostics.Summary + "; " + afterStop);
        stopFailure?.Throw();
        Assert.Empty(workerDiagnostics.Errors);
        await using var verify = CaptureFreshDb(f, true);
        var message = Assert.Single(await verify.InternalChatMessages.ToArrayAsync());
        Assert.Equal(snapshot.ClientRequestId, message.ClientRequestId);
        Assert.Equal(snapshot.Text, message.Text);
        Assert.True(message.NotificationDispatchPending);
        Assert.Equal(2, await verify.InternalChatAttachments.CountAsync());
        Assert.Equal(2, await verify.StorageProductBindings.CountAsync());
        Assert.Null(await verify.InternalChatSendAttempts.Select(item => item.RecoveryLeaseToken).SingleAsync());
        Assert.Equal(2, local.PutCalls);
        Assert.Empty(Directory.EnumerateFiles(root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatSendRecoveryRestartsAfterPreCommitFailureWithoutReadingOriginalsAgain(bool postgres)
    {
        var failure = new ChatPublicationBeforeCommitFailure();
        await using var f = await CreateMigratedFixtureAsync(postgres, true, failure);
        var actor = await f.AddUserAsync("chat-restart@example.test", "Автор");
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
        var first = ChatPublicationCore(f.Db, providers, policy, monitor, environment, config);
        Assert.Equal("accepted", (await first.Writer.WriteAsync(snapshot, ChatOriginalInputs(snapshot), default)).Status);
        failure.Armed = true;
        var interrupted = await first.Writer.ProcessRecoveryBatchAsync(policy, first.Bindings, default);
        Assert.Equal(1, interrupted.Deferred);
        Assert.True(failure.Triggered);
        Assert.False(await f.Db.InternalChatMessages.AnyAsync());
        var receipt = await f.Db.InternalChatSendAttempts.AsNoTracking().SingleAsync();
        Assert.NotNull(receipt.RecoveryLeaseToken);
        var reads = local.ReadCalls;
        var expired = DateTime.UtcNow.AddMinutes(-1);
        await f.Db.InternalChatSendAttempts.ExecuteUpdateAsync(set => set.SetProperty(item => item.RecoveryLeaseExpiresAt, expired)
            .SetProperty(item => item.RecoveryNextAttemptAt, expired));
        await using var fresh = CaptureFreshDb(f, true);
        var restarted = ChatPublicationCore(fresh, providers, policy, monitor, environment, config);
        Assert.Equal(1, (await restarted.Writer.ProcessRecoveryBatchAsync(policy, restarted.Bindings, default)).Published);
        Assert.Equal(reads, local.ReadCalls);
        Assert.Equal(2, local.PutCalls);
        Assert.Single(await fresh.InternalChatMessages.ToArrayAsync());
        Assert.Equal(2, await fresh.StorageProductBindings.CountAsync());
        Assert.Null(await fresh.InternalChatSendAttempts.Select(item => item.RecoveryLeaseToken).SingleAsync());
    }

    private sealed class ChatDirtyCopyProcessor(PaymentContext context, int threadId, int actorId,
        Action? entered = null, Action? exited = null) : IStorageOperationProcessor
    {
        public Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
        {
            entered?.Invoke();
            context.InternalChatMessages.Add(new InternalChatMessage { ThreadId = threadId, AuthorUserId = actorId, Text = "Unsaved copy-scope sentinel" });
            Assert.True(context.ChangeTracker.HasChanges());
            exited?.Invoke();
            return Task.FromResult(new StorageOperationBatchResult(0, 0, 0, 0, 0));
        }
    }

    private static async Task<string> ReadStoppedChatMetadataAsync(Fixture fixture, ChatRecoveryWorkerDiagnostics diagnostics)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var db = CaptureFreshDb(fixture, true);
            db.Database.SetCommandTimeout(2);
            var now = DateTime.UtcNow;
            var rows = await db.InternalChatSendAttempts.AsNoTracking().Select(item => new
            {
                Accepted = item.AcceptedAt.HasValue,
                Published = item.PublishedMessageId.HasValue,
                Due = item.RecoveryNextAttemptAt <= now,
                LeasePresent = item.RecoveryLeaseToken.HasValue,
                LeaseExpired = item.RecoveryLeaseExpiresAt <= now,
                item.RecoveryLastStatus,
                item.RecoveryAttemptCount,
            }).Take(2).ToArrayAsync(timeout.Token);
            if (rows.Length != 1) return $"rowCount={rows.Length}";
            var row = rows[0];
            var status = row.RecoveryLastStatus switch
            {
                null => "none",
                "published" or "busy" or "unavailable" or "forbidden" or "conflict" or "invalid"
                    or "deadLetter" or "needsInput" or "accepted" or "disabled" => row.RecoveryLastStatus,
                _ => "other",
            };
            return $"rowCount=1,accepted={row.Accepted},published={row.Published},due={row.Due},leasePresent={row.LeasePresent},"
                + $"leaseExpired={row.LeaseExpired},status={status},attempts={Math.Clamp(row.RecoveryAttemptCount, -1, 1000)}";
        }
        catch (Exception error)
        {
            diagnostics.ObserveDiagnosticError(error);
            return "observation-failed";
        }
    }

    private sealed class ChatRecoveryWorkerDiagnostics : ILogger<StorageOperationWorker>
    {
        private readonly object _sync = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly HashSet<string> _errors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _diagnosticErrors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _stages = new(StringComparer.Ordinal);
        private readonly List<PaymentContext> _contexts = [];
        private readonly List<DbConnection> _connections = [];
        private readonly List<string> _scopeIdentities = [];
        private PaymentContext? _seedContext;
        private DbConnection? _seedConnection;
        private string? _target;
        private bool _copyEntered, _copyExited;
        private int _scopeCount;
        public string[] Errors { get { lock (_sync) return _errors.ToArray(); } }
        public string Summary
        {
            get
            {
                lock (_sync)
                    return $"CopyProcessorEntered={_copyEntered}; CopyProcessorExited={_copyExited}; ScopeCount={_scopeCount}; "
                        + $"StagesMs={string.Join(",", _stages.Select(item => item.Key + ":" + item.Value))}; "
                        + $"ScopeIdentity={string.Join("|", _scopeIdentities)}; ErrorTypes={string.Join(",", _errors)}; "
                        + $"DiagnosticErrorTypes={string.Join(",", _diagnosticErrors)}";
            }
        }
        public void Mark(string stage)
        {
            lock (_sync)
                if (_stages.Count < 16) _stages.TryAdd(stage, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
        }
        public void MarkCopy(bool entered)
        {
            lock (_sync)
            {
                if (entered) _copyEntered = true; else _copyExited = true;
                Mark(entered ? "copy-enter" : "copy-exit");
            }
        }
        public void ObserveSeed(PaymentContext context)
        {
            try
            {
                _seedContext = context;
                _seedConnection = context.Database.GetDbConnection();
                _target = context.Database.GetConnectionString();
            }
            catch (Exception error) { ObserveDiagnosticError(error); }
        }
        public void ObserveScope(PaymentContext context)
        {
            try
            {
                lock (_sync)
                {
                    _scopeCount = Math.Min(1000, _scopeCount + 1);
                    if (_scopeIdentities.Count >= 4) return;
                    var connection = context.Database.GetDbConnection();
                    _scopeIdentities.Add($"scope{_scopeCount}:seedContext={ReferenceEquals(context, _seedContext)},"
                        + $"seedConnection={ReferenceEquals(connection, _seedConnection)},"
                        + $"reusedContext={_contexts.Any(item => ReferenceEquals(item, context))},"
                        + $"reusedConnection={_connections.Any(item => ReferenceEquals(item, connection))},"
                        + $"sameTarget={context.Database.GetConnectionString() == _target}");
                    _contexts.Add(context);
                    _connections.Add(connection);
                    Mark($"scope{_scopeCount}-created");
                }
            }
            catch (Exception error) { ObserveDiagnosticError(error); }
        }
        public void ObserveDiagnosticError(Exception error)
        {
            lock (_sync) if (_diagnosticErrors.Count < 4) _diagnosticErrors.Add(SafeType(error));
        }
        public void Report(string details)
        {
            try { Console.WriteLine("Owned chat worker diagnostic: " + Summary + "; " + details); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        private static string SafeType(Exception? error)
        {
            var name = error?.GetType().FullName ?? "unknown";
            return name.Length <= 192 && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.+`<>]*$")
                ? name : "unknown";
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // Test diagnostic only: never serialize formatter/state/message/Data.
            lock (_sync) if (_errors.Count < 4) _errors.Add(SafeType(exception));
        }
    }

    private sealed class ChatRecoveryPublicationSignal : ILogger<InternalChatSendRecovery>
    {
        private readonly object _sync = new();
        private int _batchLogs;
        private int _claimed = -1, _published = -1, _deferred = -1, _deadLettered = -1;
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Summary
        {
            get
            {
                lock (_sync)
                    return $"RecoveryBatchLogs={_batchLogs}; Claimed={_claimed}; Published={_published}; Deferred={_deferred}; DeadLettered={_deadLettered}";
            }
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var publish = false;
            int claimed = -1, published = -1, deferred = -1, deadLettered = -1;
            // Capture only bounded numeric fields, never formatter/state text,
            // identifiers, payloads, credentials or exception details.
            foreach (var item in values)
            {
                if (item.Key == "Published" && item.Value is int count && count == 1) publish = true;
                if (item.Value is not int number || number is < 0 or > 20) continue;
                switch (item.Key)
                {
                    case "Claimed": claimed = number; break;
                    case "Published": published = number; break;
                    case "Deferred": deferred = number; break;
                    case "DeadLettered": deadLettered = number; break;
                }
            }
            lock (_sync)
            {
                if (_batchLogs < 1000) _batchLogs++;
                _claimed = claimed;
                _published = published;
                _deferred = deferred;
                _deadLettered = deadLettered;
            }
            if (publish) Published.TrySetResult();
        }
    }
}
