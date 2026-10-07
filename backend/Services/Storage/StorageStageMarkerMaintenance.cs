using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services.Storage;

// One bounded page per minute, in a scope independent of copy/publication work.
// A failed or cancelled page keeps its cursor; already removed markers replay safely.
public sealed class StorageStageMarkerMaintenance(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextRunAt;
    private string? _afterKey;

    public async Task<StorageStageWriterMarkerRecoveryReport?> ProcessIfDueAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return null;
        try
        {
            var settings = options.CurrentValue;
            var now = timeProvider.GetUtcNow();
            if (!settings.Enabled || !settings.WorkerEnabled || now < _nextRunAt) return null;
            // Bound retries as well as successful pages; provider/copy work must
            // not turn a failing cleanup into a tight loop.
            _nextRunAt = now.AddMinutes(1);
            await using var scope = scopeFactory.CreateAsyncScope();
            var recovery = scope.ServiceProvider.GetRequiredService<IStorageStageWriterMarkerRecoveryService>();
            var report = await recovery.ReconcileReleasedWriterMarkersAsync(true,
                Math.Clamp(settings.WorkerBatchSize, 1, 200), TimeSpan.FromMinutes(10), ct, _afterKey);
            if (report.HasMore && string.IsNullOrWhiteSpace(report.NextAfterKey))
                throw new InvalidOperationException("Marker recovery omitted its continuation cursor.");
            _afterKey = report.HasMore ? report.NextAfterKey : null;
            return report;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
