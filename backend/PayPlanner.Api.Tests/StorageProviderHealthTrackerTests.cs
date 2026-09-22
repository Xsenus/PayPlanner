using Microsoft.Extensions.Options;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageProviderHealthTrackerTests
{
    [Fact]
    public void Circuit_OpensAfterThresholdAndSuccessClosesIt()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-09-22T10:00:00Z"));
        var tracker = new StorageProviderHealthTracker(
            new StaticOptionsMonitor<StorageOrchestrationOptions>(new StorageOrchestrationOptions
            {
                ProviderFailureThreshold = 2,
                ProviderCircuitBreakSeconds = 30,
            }), clock);

        tracker.Record("s3", StorageOperationStatus.Unavailable, "timeout 1");
        Assert.True(tracker.CanAttempt("s3"));
        tracker.Record("s3", StorageOperationStatus.Unknown, "timeout 2");
        Assert.False(tracker.CanAttempt("s3"));
        Assert.Equal("open", Assert.Single(tracker.Snapshot(["s3"])).State);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.CanAttempt("s3"));
        Assert.Equal("halfOpen", Assert.Single(tracker.Snapshot(["s3"])).State);
        tracker.Record("s3", StorageOperationStatus.Succeeded);
        var healthy = Assert.Single(tracker.Snapshot(["s3"]));
        Assert.Equal("healthy", healthy.State);
        Assert.Equal(0, healthy.ConsecutiveFailures);
    }

    [Fact]
    public void BusinessOutcomes_DoNotOpenCircuit()
    {
        var tracker = new StorageProviderHealthTracker(
            new StaticOptionsMonitor<StorageOrchestrationOptions>(new StorageOrchestrationOptions { ProviderFailureThreshold = 1 }),
            TimeProvider.System);
        tracker.Record("local", StorageOperationStatus.NotFound, "not found");
        tracker.Record("local", StorageOperationStatus.Conflict, "conflict");

        Assert.True(tracker.CanAttempt("local"));
        Assert.Equal("healthy", Assert.Single(tracker.Snapshot(["local"])).State);
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
