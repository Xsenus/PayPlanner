using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

/// <summary>Renews only this execution's lease through independent, short-lived contexts.</summary>
public sealed class StorageOperationLeaseScope : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource lost = new();
    private readonly CancellationTokenSource linked;
    private readonly Task loop;
    private int leaseLost;
    public bool LeaseLost => Volatile.Read(ref leaseLost) != 0;
    public CancellationToken Token => linked.Token;

    public StorageOperationLeaseScope(IDbContextFactory<PaymentContext> factory, StorageOperation operation,
        TimeProvider clock, TimeSpan duration, CancellationToken ct)
    {
        if (duration < TimeSpan.FromMilliseconds(30)) throw new ArgumentOutOfRangeException(nameof(duration));
        linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lost.Token);
        // Capture immutable execution identity, never read tracked mutable values on the timer.
        var id = operation.Id; var owner = operation.LeaseOwner; var kind = operation.OperationType;
        var objectId = operation.StorageObjectId; var generationId = operation.StorageObjectGenerationId;
        var provider = operation.ProviderKey; var connection = operation.ConnectionId;
        loop = RunAsync();

        async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromTicks(duration.Ticks / 3), clock, stop.Token);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromTicks(duration.Ticks / 3), clock);
                    using var renewalToken = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, deadline.Token);
                    await using var context = await factory.CreateDbContextAsync(renewalToken.Token);
                    context.Database.SetCommandTimeout(Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds / 3)));
                    var now = clock.GetUtcNow().UtcDateTime;
                    var rows = await context.StorageOperations.Where(o => o.Id == id && o.Status == StorageOperationStatuses.Running
                        && o.LeaseOwner == owner && o.LeaseExpiresAt > now && o.OperationType == kind
                        && o.StorageObjectId == objectId && o.StorageObjectGenerationId == generationId
                        && o.ProviderKey == provider && o.ConnectionId == connection)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseExpiresAt, now.Add(duration)), renewalToken.Token);
                    if (rows != 1) { LoseLease(); return; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch { LoseLease(); } // A failed renewal cannot authorize continued provider I/O.
        }
    }

    private void LoseLease()
    {
        Interlocked.Exchange(ref leaseLost, 1);
        lost.Cancel();
    }

    public async Task StopAsync()
    {
        await stop.CancelAsync();
        await loop;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        linked.Dispose(); lost.Dispose(); stop.Dispose();
    }
}
