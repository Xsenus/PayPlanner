namespace PayPlanner.TestShards;

/// <summary>A failed log sink must not leave its owned child blocked on a full redirected pipe.</summary>
public static class OwnedOutputDrain
{
    public static async Task WaitAsync(Task exited, Task stdout, Task stderr, Action stopOwnedProcess)
    {
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
        await Task.WhenAll(stdout, stderr);
    }
}
