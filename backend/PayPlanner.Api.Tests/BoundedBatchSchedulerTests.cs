using System.Collections.Concurrent;
using PayPlanner.TestShards;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BoundedBatchSchedulerTests
{
    private const ulong Gib = 1024UL * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task RealSchedulerLoopRunsExactlyTheAdmittedConcurrentWorkers(int budget)
    {
        var started = Signal();
        var release = Signal();
        var observationGate = new object();
        var active = 0;
        var maximum = 0;
        var completed = new ConcurrentBag<string>();
        var batches = Batches("heavy", budget * 3);
        var run = BoundedBatchScheduler.RunAsync(batches, budget.ToString(), budget, AmpleResources, async batch =>
        {
            lock (observationGate)
            {
                active++;
                maximum = Math.Max(maximum, active);
                if (active == budget) started.TrySetResult();
            }
            try { await release.Task; }
            finally
            {
                lock (observationGate) active--;
                completed.Add(batch.Id);
            }
        });
        try
        {
            await started.Task.WaitAsync(Deadline);
            Assert.False(run.IsCompleted);
            lock (observationGate) Assert.Equal(budget, active);
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(Deadline);
        Assert.Equal(budget, maximum);
        Assert.Equal(0, active);
        Assert.Equal(batches.Select(batch => batch.Id).Order(), completed.Order());
    }

    [Fact]
    public async Task ExclusiveLaneWaitsForBothOrdinaryLanesAndRunsOneBatchAtATime()
    {
        var fastStarted = Signal();
        var releaseFast = Signal();
        var heavyStarted = Signal();
        var releaseHeavy = Signal();
        var exclusiveStarted = Signal();
        var releaseExclusive = Signal();
        var started = new ConcurrentDictionary<string, int>();
        var completed = new ConcurrentDictionary<string, int>();
        var active = 0;
        var batches = Batches("fast", 2).Concat(Batches("heavy", 2)).Concat(Batches("exclusive", 2)).ToArray();
        var run = BoundedBatchScheduler.RunAsync(batches, "4", 4, AmpleResources, async batch =>
        {
            var concurrency = Interlocked.Increment(ref active);
            var count = started.AddOrUpdate(batch.Lane, 1, (_, previous) => previous + 1);
            try
            {
                if (batch.Lane == "fast")
                {
                    if (count == 2) fastStarted.TrySetResult();
                    await releaseFast.Task;
                }
                else if (batch.Lane == "heavy")
                {
                    Assert.Equal(2, completed.GetValueOrDefault("fast"));
                    if (count == 2) heavyStarted.TrySetResult();
                    await releaseHeavy.Task;
                }
                else
                {
                    Assert.Equal(1, concurrency);
                    Assert.Equal(2, completed.GetValueOrDefault("heavy"));
                    exclusiveStarted.TrySetResult();
                    await releaseExclusive.Task;
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
                completed.AddOrUpdate(batch.Lane, 1, (_, previous) => previous + 1);
            }
        });
        try
        {
            await fastStarted.Task.WaitAsync(Deadline);
            Assert.False(started.ContainsKey("heavy"));
            Assert.False(started.ContainsKey("exclusive"));
            releaseFast.TrySetResult();
            await heavyStarted.Task.WaitAsync(Deadline);
            Assert.False(started.ContainsKey("exclusive"));
            releaseHeavy.TrySetResult();
            await exclusiveStarted.Task.WaitAsync(Deadline);
            Assert.Equal(1, started["exclusive"]);
        }
        finally
        {
            releaseFast.TrySetResult();
            releaseHeavy.TrySetResult();
            releaseExclusive.TrySetResult();
        }
        await run.WaitAsync(Deadline);
        Assert.Equal(2, completed["exclusive"]);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task FailedWorkerDoesNotAbandonRunningSiblingsOrRemainingBatches()
    {
        var siblingStarted = Signal();
        var release = Signal();
        var completed = new ConcurrentBag<string>();
        var batches = Batches("heavy", 4).Concat(Batches("exclusive", 1)).ToArray();
        var run = BoundedBatchScheduler.RunAsync(batches, "2", 2, AmpleResources, async batch =>
        {
            if (batch.Id == "heavy-0") throw new IOException("An owned batch failed.");
            if (batch.Lane == "heavy")
            {
                siblingStarted.TrySetResult();
                await release.Task;
            }
            completed.Add(batch.Id);
        });
        try
        {
            await siblingStarted.Task.WaitAsync(Deadline);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(Deadline));
        Assert.Equal(batches.Where(batch => batch.Id != "heavy-0").Select(batch => batch.Id).Order(), completed.Order());
    }

    [Fact]
    public async Task SynchronousDispatchFailureStillPreservesOtherBatchResults()
    {
        var completed = new List<string>();
        var batches = Batches("heavy", 4);
        await Assert.ThrowsAsync<InvalidDataException>(() => BoundedBatchScheduler.RunAsync(batches, "2", 2, AmpleResources, batch =>
        {
            if (batch.Id == "heavy-0") throw new IOException("The native process could not start.");
            completed.Add(batch.Id);
            return Task.CompletedTask;
        }).WaitAsync(Deadline));
        Assert.Equal(["heavy-1", "heavy-2", "heavy-3"], completed);
    }

    [Fact]
    public async Task AProbeFailureWaitsForEveryAlreadyDispatchedBatchAndPreservesTheOriginalError()
    {
        var started = Signal();
        var release = Signal();
        var failure = new IOException("The real resource probe became unavailable.");
        var probes = 0;
        var starts = 0;
        var completions = 0;
        ExecutionResources Probe() => ++probes == 4 ? throw failure : AmpleResources();
        var run = BoundedBatchScheduler.RunAsync(Batches("heavy", 4), "2", 2, Probe, async _ =>
        {
            if (Interlocked.Increment(ref starts) == 2) started.TrySetResult();
            await release.Task;
            Interlocked.Increment(ref completions);
        });
        try
        {
            await started.Task.WaitAsync(Deadline);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, completions);
        }
        finally { release.TrySetResult(); }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Deadline)));
        Assert.Equal(2, starts);
        Assert.Equal(2, completions);
    }

    [Fact]
    public async Task AnObserverFailureAlsoDrainsItsRunningChildBeforeReturning()
    {
        var started = Signal();
        var release = Signal();
        var failure = new IOException("The execution telemetry file cannot be written.");
        var completions = 0;
        var run = BoundedBatchScheduler.RunAsync(Batches("heavy", 4), "2", 2, AmpleResources, async _ =>
        {
            started.TrySetResult();
            await release.Task;
            Interlocked.Increment(ref completions);
        }, value =>
        {
            if (value.Kind == "launch-budget" && value.Active == 1) throw failure;
        });
        try
        {
            await started.Task.WaitAsync(Deadline);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(Deadline)));
        Assert.Equal(1, completions);
    }

    [Fact]
    public async Task MemoryBelowTheFloorDrainsActiveWorkAndLeavesPendingBatchesUnstarted()
    {
        var started = Signal();
        var release = Signal();
        var starts = 0;
        var completions = 0;
        var floorRecorded = false;
        var available = 20 * Gib;
        var run = BoundedBatchScheduler.RunAsync(Batches("heavy", 4), "2", 2,
            () => new ExecutionResources(Volatile.Read(ref available), 8), async _ =>
            {
                if (Interlocked.Increment(ref starts) == 2)
                {
                    Volatile.Write(ref available, Gib / 2);
                    started.TrySetResult();
                }
                await release.Task;
                Interlocked.Increment(ref completions);
            }, value => floorRecorded |= value.Kind == "memory-floor");
        try { await started.Task.WaitAsync(Deadline); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => run.WaitAsync(Deadline));
        Assert.Equal(2, starts);
        Assert.Equal(2, completions);
        Assert.True(floorRecorded);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ExecutionResources AmpleResources() => new(20 * Gib, 8);
    private static ExecutionBatch[] Batches(string lane, int count) => Enumerable.Range(0, count)
        .Select(index => new ExecutionBatch($"{lane}-{index}", lane, [], 1)).ToArray();
}
