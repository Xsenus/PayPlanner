using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CourtSyncMetricsTests
{
    [Fact]
    public void Snapshot_AggregatesRequestsDuplicatesAndDocuments()
    {
        var metrics = new CourtSyncMetrics();

        metrics.RecordRequest(TimeSpan.FromMilliseconds(100), failed: false);
        metrics.RecordRequest(TimeSpan.FromMilliseconds(300), failed: true);
        metrics.RecordDuplicateSkipped();
        metrics.RecordDocument(1_024);
        metrics.RecordDocument(2_048);

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(2, snapshot.RequestCount);
        Assert.Equal(1, snapshot.FailedRequestCount);
        Assert.Equal(200, snapshot.AverageLatencyMs);
        Assert.Equal(300, snapshot.MaxLatencyMs);
        Assert.Equal(1, snapshot.DuplicateSkipCount);
        Assert.Equal(2, snapshot.DocumentCount);
        Assert.Equal(3_072, snapshot.DocumentBytes);
    }

    [Fact]
    public void Snapshot_IsThreadSafeUnderConcurrentUpdates()
    {
        var metrics = new CourtSyncMetrics();

        Parallel.For(0, 1_000, index =>
        {
            metrics.RecordRequest(TimeSpan.FromMilliseconds(index % 10), failed: index % 4 == 0);
            metrics.RecordDuplicateSkipped();
            metrics.RecordDocument(index);
        });

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1_000, snapshot.RequestCount);
        Assert.Equal(250, snapshot.FailedRequestCount);
        Assert.Equal(1_000, snapshot.DuplicateSkipCount);
        Assert.Equal(1_000, snapshot.DocumentCount);
        Assert.Equal(499_500, snapshot.DocumentBytes);
        Assert.Equal(9, snapshot.MaxLatencyMs);
    }
}
