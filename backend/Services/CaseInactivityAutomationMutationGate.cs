namespace PayPlanner.Api.Services;

/// <summary>
/// Serializes task-creating mutations for case inactivity automation inside one application instance.
/// The same singleton is shared by manual endpoints and the background scheduler so their
/// eligibility check and write cannot overlap and create duplicate active control cycles.
/// </summary>
public sealed class CaseInactivityAutomationMutationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(_semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
