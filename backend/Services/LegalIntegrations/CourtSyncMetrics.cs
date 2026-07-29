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
    private long _pendingRequestCount;
    private long _pendingFailedRequestCount;
    private long _pendingTotalLatencyTicks;
    private long _pendingMaxLatencyTicks;
    private long _pendingDuplicateSkipCount;
    private long _pendingDocumentCount;
    private long _pendingDocumentBytes;

    public void RecordRequest(TimeSpan latency, bool failed)
    {
        var safeTicks = Math.Max(0, latency.Ticks);
        Interlocked.Increment(ref _requestCount);
        Interlocked.Add(ref _totalLatencyTicks, safeTicks);
        UpdateMaximum(ref _maxLatencyTicks, safeTicks);
        Interlocked.Increment(ref _pendingRequestCount);
        Interlocked.Add(ref _pendingTotalLatencyTicks, safeTicks);
        UpdateMaximum(ref _pendingMaxLatencyTicks, safeTicks);
        if (failed)
        {
            Interlocked.Increment(ref _failedRequestCount);
            Interlocked.Increment(ref _pendingFailedRequestCount);
        }
    }

    public void RecordDuplicateSkipped()
    {
        Interlocked.Increment(ref _duplicateSkipCount);
        Interlocked.Increment(ref _pendingDuplicateSkipCount);
    }

    public void RecordDocument(long sizeBytes)
    {
        Interlocked.Increment(ref _documentCount);
        Interlocked.Add(ref _documentBytes, Math.Max(0, sizeBytes));
        Interlocked.Increment(ref _pendingDocumentCount);
        Interlocked.Add(ref _pendingDocumentBytes, Math.Max(0, sizeBytes));
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

    public CourtSyncMetricsDelta DrainPending()
        => new(
            Interlocked.Exchange(ref _pendingRequestCount, 0),
            Interlocked.Exchange(ref _pendingFailedRequestCount, 0),
            Interlocked.Exchange(ref _pendingTotalLatencyTicks, 0),
            Interlocked.Exchange(ref _pendingMaxLatencyTicks, 0),
            Interlocked.Exchange(ref _pendingDuplicateSkipCount, 0),
            Interlocked.Exchange(ref _pendingDocumentCount, 0),
            Interlocked.Exchange(ref _pendingDocumentBytes, 0));

    public void RestorePending(CourtSyncMetricsDelta delta)
    {
        Interlocked.Add(ref _pendingRequestCount, delta.RequestCount);
        Interlocked.Add(ref _pendingFailedRequestCount, delta.FailedRequestCount);
        Interlocked.Add(ref _pendingTotalLatencyTicks, delta.TotalLatencyTicks);
        UpdateMaximum(ref _pendingMaxLatencyTicks, delta.MaxLatencyTicks);
        Interlocked.Add(ref _pendingDuplicateSkipCount, delta.DuplicateSkipCount);
        Interlocked.Add(ref _pendingDocumentCount, delta.DocumentCount);
        Interlocked.Add(ref _pendingDocumentBytes, delta.DocumentBytes);
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

public sealed record CourtSyncMetricsDelta(
    long RequestCount,
    long FailedRequestCount,
    long TotalLatencyTicks,
    long MaxLatencyTicks,
    long DuplicateSkipCount,
    long DocumentCount,
    long DocumentBytes)
{
    public bool HasValues =>
        RequestCount != 0
        || FailedRequestCount != 0
        || TotalLatencyTicks != 0
        || MaxLatencyTicks != 0
        || DuplicateSkipCount != 0
        || DocumentCount != 0
        || DocumentBytes != 0;
}
