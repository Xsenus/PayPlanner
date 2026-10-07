using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageStageMarkerMaintenanceTests
{
    private const string Cursor = "stage:writer-v1:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static StorageStageWriterMarkerRecoveryReport End() => new(0, 0, 0, false, []);
    private static StorageStageWriterMarkerRecoveryReport Page() => new(1, 0, 0, true,
        [new(Cursor, "markerNotProvenStopped")])
    { NextAfterKey = Cursor };

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(201, 200)]
    [InlineData(int.MaxValue, 200)]
    public async Task PagesAreBoundedThrottledAndRecycleOnlyAfterTheLastPage(int configured, int expected)
    {
        await using var h = new Harness();
        h.Settings.WorkerBatchSize = configured;
        h.State.Handle = (_, _) => Task.FromResult(h.State.Calls.Count == 1 ? Page() : End());
        Assert.NotNull(await h.Maintenance.ProcessIfDueAsync(default));
        Assert.Null(await h.Maintenance.ProcessIfDueAsync(default));
        h.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(await h.Maintenance.ProcessIfDueAsync(default));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await h.Maintenance.ProcessIfDueAsync(default));
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await h.Maintenance.ProcessIfDueAsync(default));
        var calls = h.State.Calls.ToArray();
        Assert.Equal(new string?[] { null, Cursor, null }, calls.Select(item => item.Cursor));
        Assert.All(calls, item => { Assert.True(item.Apply); Assert.Equal(expected, item.Limit); Assert.Equal(TimeSpan.FromMinutes(10), item.Age); });
        Assert.Equal(3, calls.Select(item => item.ScopeId).Distinct().Count());
        Assert.Equal(3, h.State.Disposed.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledStorageOrWorkerDoesNotOpenRecoveryScope(bool enabled, bool worker)
    {
        await using var h = new Harness();
        h.Settings.Enabled = enabled;
        h.Settings.WorkerEnabled = worker;
        Assert.Null(await h.Maintenance.ProcessIfDueAsync(default));
        Assert.Empty(h.State.Calls);
        Assert.Empty(h.State.Created);
    }

    [Fact]
    public async Task DisablingAndReenablingKeepsTheContinuationWithoutDelayingItsDuePage()
    {
        await using var h = new Harness();
        h.State.Handle = (_, _) => Task.FromResult(Page());
        await h.Maintenance.ProcessIfDueAsync(default);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.Settings.Enabled = false;
        Assert.Null(await h.Maintenance.ProcessIfDueAsync(default));
        Assert.Single(h.State.Calls);
        h.Settings.Enabled = true;
        await h.Maintenance.ProcessIfDueAsync(default);
        Assert.Equal(Cursor, h.State.Calls.Last().Cursor);
        Assert.Equal(2, h.State.Disposed.Count);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("missingCursor")]
    public async Task FailedOrCancelledPagePreservesCursorAndWaitsBeforeRetry(string failure)
    {
        await using var h = new Harness();
        h.State.Handle = (_, _) => Task.FromResult(Page());
        await h.Maintenance.ProcessIfDueAsync(default);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource();
        h.State.Handle = (_, ct) =>
        {
            if (failure == "io") throw new IOException("Owned cleanup failure.");
            if (failure == "cancel") { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(new StorageStageWriterMarkerRecoveryReport(1, 0, 0, true, []));
        };
        if (failure == "io") await Assert.ThrowsAsync<IOException>(() => h.Maintenance.ProcessIfDueAsync(cancel.Token));
        else if (failure == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Maintenance.ProcessIfDueAsync(cancel.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => h.Maintenance.ProcessIfDueAsync(cancel.Token));
        Assert.Null(await h.Maintenance.ProcessIfDueAsync(default));
        Assert.Equal(2, h.State.Calls.Count);
        Assert.Equal(2, h.State.Disposed.Count);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.State.Handle = (_, _) => Task.FromResult(End());
        await h.Maintenance.ProcessIfDueAsync(default);
        Assert.Equal(Cursor, h.State.Calls.Last().Cursor);
        Assert.Equal(3, h.State.Disposed.Count);
    }

    [Fact]
    public async Task ConcurrentInvocationDoesNotStartAnotherPageOrScope()
    {
        await using var h = new Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Handle = async (_, ct) => { entered.SetResult(); await finish.Task.WaitAsync(ct); return End(); };
        var first = h.Maintenance.ProcessIfDueAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await h.Maintenance.ProcessIfDueAsync(default));
            Assert.Single(h.State.Calls);
            Assert.Single(h.State.Created);
        }
        finally { finish.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Single(h.State.Disposed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HostedWorkerSeparatesDirtyCopyScopeAndBothFailureBoundaries(bool copyFailure, bool cleanupFailure)
    {
        await using var h = new Harness();
        h.State.CopyFailure = copyFailure;
        var cleanupSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Handle = (_, _) =>
        {
            cleanupSeen.TrySetResult();
            if (cleanupFailure) throw new IOException("Owned cleanup failure.");
            return Task.FromResult(End());
        };
        var worker = Assert.IsType<StorageOperationWorker>(Assert.Single(h.Provider.GetServices<IHostedService>()));
        await worker.StartAsync(default);
        try
        {
            await cleanupSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await h.State.SecondCopy.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stop.Token);
        }
        var call = Assert.Single(h.State.Calls);
        Assert.DoesNotContain(call.ScopeId, h.State.CopyScopes);
        Assert.Contains(call.ScopeId, h.State.Disposed);
        Assert.True(h.State.CopyCalls >= 2);
        Assert.All(h.State.CopyScopes, id => Assert.Contains(id, h.State.Disposed));
    }

    private sealed record Call(Guid ScopeId, bool Apply, int Limit, TimeSpan Age, string? Cursor);
    private sealed class State
    {
        public ConcurrentQueue<Call> Calls { get; } = new();
        public ConcurrentBag<Guid> Created { get; } = new();
        public ConcurrentBag<Guid> Disposed { get; } = new();
        public ConcurrentBag<Guid> CopyScopes { get; } = new();
        public Func<Call, CancellationToken, Task<StorageStageWriterMarkerRecoveryReport>> Handle { get; set; } = (_, _) => Task.FromResult(End());
        public bool CopyFailure { get; set; }
        public int CopyCalls;
        public TaskCompletionSource SecondCopy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ScopeProbe(State state) : IDisposable, IAsyncDisposable
    {
        public Guid Id { get; } = Create(state);
        public bool Dirty { get; set; }
        private int _disposed;
        private static Guid Create(State state) { var id = Guid.NewGuid(); state.Created.Add(id); return id; }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) state.Disposed.Add(Id); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Reader(State state, ScopeProbe scope) : IStorageStageWriterMarkerRecoveryService
    {
        public Task<StorageStageWriterMarkerRecoveryReport> ReconcileReleasedWriterMarkersAsync(bool apply, int limit,
            TimeSpan minimumAge, CancellationToken ct, string? afterKey = null)
        {
            Assert.False(scope.Dirty);
            var call = new Call(scope.Id, apply, limit, minimumAge, afterKey);
            state.Calls.Enqueue(call);
            return state.Handle(call, ct);
        }
    }
    private sealed class CopyProcessor(State state, ScopeProbe scope) : IStorageOperationProcessor
    {
        public Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
        {
            scope.Dirty = true;
            state.CopyScopes.Add(scope.Id);
            var count = Interlocked.Increment(ref state.CopyCalls);
            if (count >= 2) state.SecondCopy.TrySetResult();
            if (state.CopyFailure && count == 1) throw new IOException("Owned copy failure.");
            return Task.FromResult(new StorageOperationBatchResult(0, 0, 0, 0, 0));
        }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
    private sealed class Monitor(StorageOrchestrationOptions settings) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => settings;
        public StorageOrchestrationOptions Get(string? name) => settings;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class Harness : IAsyncDisposable
    {
        public State State { get; } = new();
        public Clock Clock { get; } = new();
        public StorageOrchestrationOptions Settings { get; } = new() { Enabled = true, WorkerEnabled = true, WorkerPollSeconds = 1 };
        public ServiceProvider Provider { get; }
        public StorageStageMarkerMaintenance Maintenance => Provider.GetRequiredService<StorageStageMarkerMaintenance>();
        public Harness()
        {
            var services = new ServiceCollection();
            services.AddSingleton(State);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IOptionsMonitor<StorageOrchestrationOptions>>(new Monitor(Settings));
            services.AddSingleton<ILogger<StorageOperationWorker>>(NullLogger<StorageOperationWorker>.Instance);
            services.AddScoped<ScopeProbe>();
            services.AddScoped<IStorageStageWriterMarkerRecoveryService, Reader>();
            services.AddScoped<IStorageOperationProcessor, CopyProcessor>();
            services.AddSingleton<StorageStageMarkerMaintenance>();
            services.AddHostedService<StorageOperationWorker>();
            Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
