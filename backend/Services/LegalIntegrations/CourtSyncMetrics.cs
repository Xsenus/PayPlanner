namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed class CourtSyncMetrics
{
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private long _requestCount;
    private long _failedRequestCount;
    private long _totalLatencyTicks;
    private long _maxLatencyTicks;
    private long _duplicateSkipCount;
    private long _documentCount;
    private long _documentBytes;

    public void RecordRequest(TimeSpan latency, bool failed)
    {
        var safeTicks = Math.Max(0, latency.Ticks);
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _totalLatencyTicks, safeTicks);
        UpdateMaximum(ref _maxLatencyTicks, safeTicks);
        if (failed)
        {
            Interlocked.Increment(ref _failedRequestCount);
        }
    }

    public void RecordDuplicateSkipped()
        => Interlocked.Increment(ref _duplicateSkipCount);

    public void RecordDocument(long sizeBytes)
    {
        Interlocked.Increment(ref _documentCount);
        Interlocked.Add(ref _documentBytes, Math.Max(0, sizeBytes));
    }

    public CourtSyncMetricsSnapshot GetSnapshot()
    {
        var requestCount = Interlocked.Read(ref _requestCount);
        var totalLatencyTicks = Interlocked.Read(ref _totalLatencyTicks);
        return new CourtSyncMetricsSnapshot(
            _startedAt,
            requestCount,
            Interlocked.Read(ref _failedRequestCount),
            requestCount == 0
                ? 0
                : TimeSpan.FromTicks(totalLatencyTicks).TotalMilliseconds / requestCount,
            TimeSpan.FromTicks(Interlocked.Read(ref _maxLatencyTicks)).TotalMilliseconds,
            Interlocked.Read(ref _duplicateSkipCount),
            Interlocked.Read(ref _documentCount),
            Interlocked.Read(ref _documentBytes));
    }

    private static void UpdateMaximum(ref long target, long candidate)
    {
        var current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}

public sealed record CourtSyncMetricsSnapshot(
    DateTime StartedAt,
    long RequestCount,
    long FailedRequestCount,
    double AverageLatencyMs,
    double MaxLatencyMs,
    long DuplicateSkipCount,
    long DocumentCount,
    long DocumentBytes);
