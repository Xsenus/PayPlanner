namespace PayPlanner.TestShards;

public sealed record ExecutionResources(ulong AvailableBytes, int Processors);
public sealed record SchedulingEvent(string Kind, string Lane, int Workers, ulong AvailableBytes, int Processors, int Active);

/// <summary>
/// Owns the complete lifetime of each dispatched batch. Resource probes and actions are injectable
/// for deterministic concurrency tests; the executable supplies only its real Windows host probe.
/// </summary>
public static class BoundedBatchScheduler
{
    public static async Task RunAsync(IReadOnlyCollection<ExecutionBatch> batches, string requestedWorkers, int initialBudget,
        Func<ExecutionResources> readResources, Func<ExecutionBatch, Task> executeBatch, Action<SchedulingEvent>? observe = null)
    {
        ShardPlan.Require(initialBudget is 1 or 2 or 4, "Invalid initial worker budget.");
        ShardPlan.Require(batches.All(batch => batch.Lane is "fast" or "heavy" or "exclusive")
            && batches.Select(batch => batch.Id).Distinct(StringComparer.Ordinal).Count() == batches.Count,
            "Execution batches have invalid lanes or duplicate identities.");
        var failures = new List<Exception>();
        foreach (var lane in new[] { "fast", "heavy", "exclusive" })
        {
            var queue = new Queue<ExecutionBatch>(batches.Where(batch => batch.Lane == lane));
            if (queue.Count == 0) continue;
            var initial = readResources();
            var budget = lane == "exclusive" ? ExecutionScheduling.WorkerBudget("1", initial.AvailableBytes, initial.Processors)
                : Math.Min(initialBudget, ExecutionScheduling.WorkerBudget(requestedWorkers, initial.AvailableBytes, initial.Processors));
            observe?.Invoke(new("lane-budget", lane, budget, initial.AvailableBytes, initial.Processors, 0));
            var active = new List<Task>();
            var resourceFailure = false;
            try
            {
                while (queue.Count > 0 || active.Count > 0)
                {
                    var resource = readResources();
                    if (resource.AvailableBytes < 1024UL * 1024 * 1024 && !resourceFailure)
                    {
                        resourceFailure = true;
                        failures.Add(new InvalidDataException("Available RAM fell below 1 GiB; pending batches were not started."));
                        observe?.Invoke(new("memory-floor", lane, budget, resource.AvailableBytes, resource.Processors, active.Count));
                    }
                    while (!resourceFailure && queue.Count > 0 && ExecutionScheduling.CanLaunchAdditional(active.Count, budget, resource.AvailableBytes))
                    {
                        observe?.Invoke(new("launch-budget", lane, budget, resource.AvailableBytes, resource.Processors, active.Count));
                        var batch = queue.Dequeue();
                        try { active.Add(executeBatch(batch)); }
                        catch (Exception error) { active.Add(Task.FromException(error)); }
                        resource = readResources();
                    }
                    if (active.Count == 0) break;
                    var completed = await Task.WhenAny(active);
                    active.Remove(completed);
                    try { await completed; }
                    catch (Exception error) { failures.Add(error); }
                }
            }
            finally
            {
                // A failed probe/observer must still await every child already dispatched.
                foreach (var remaining in active)
                    try { await remaining; }
                    catch (Exception error) { failures.Add(error); }
            }
            if (resourceFailure) break;
        }
        ShardPlan.Require(failures.Count == 0, $"{failures.Count} execution batch or resource check(s) failed; inspect the retained batch logs.");
    }
}
