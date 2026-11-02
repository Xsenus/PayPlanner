namespace PayPlanner.TestShards;

/// <summary>A failed log sink must not leave its owned child blocked on a full redirected pipe.</summary>
public static class OwnedOutputDrain
{
    public static async Task WaitAsync(Task exited, Task stdout, Task stderr, Action stopOwnedProcess,
        TimeSpan? outputCloseTimeout = null)
    {
        var closeTimeout = outputCloseTimeout ?? TimeSpan.FromSeconds(10);
        if (closeTimeout <= TimeSpan.Zero || closeTimeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(outputCloseTimeout));
        var pending = new List<Task> { exited, stdout, stderr };
        while (!exited.IsCompleted)
        {
            var completed = await Task.WhenAny(pending);
            if (completed == exited) break;
            pending.Remove(completed);
            if (completed.IsFaulted || completed.IsCanceled)
            {
                stopOwnedProcess();
                break;
            }
        }
        await exited;
        // An inherited console/pipe handle can survive the direct child. Never
        // turn that incomplete output into success or wait for the whole job timeout.
        await Task.WhenAll(stdout, stderr).WaitAsync(closeTimeout);
    }
}
