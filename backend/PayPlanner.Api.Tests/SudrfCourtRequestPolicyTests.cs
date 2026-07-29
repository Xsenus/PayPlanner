using System.Net;
using PayPlanner.Api.Services.LegalIntegrations;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SudrfCourtRequestPolicyTests
{
    [Fact]
    public async Task SendAsync_RetriesTransientResponsesAndReturnsSuccess()
    {
        using var handler = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var metrics = new CourtSyncMetrics();
        var policy = CreatePolicy(maxRetries: 2, metrics: metrics);

        using var response = await policy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/case"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.RequestCount);
        var snapshot = metrics.GetSnapshot();
        Assert.Equal(3, snapshot.RequestCount);
        Assert.Equal(2, snapshot.FailedRequestCount);
        Assert.True(snapshot.AverageLatencyMs >= 0);
        Assert.True(snapshot.MaxLatencyMs >= snapshot.AverageLatencyMs);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryPermanentClientError()
    {
        using var handler = new SequenceHandler(HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);
        var policy = CreatePolicy(maxRetries: 3);

        using var response = await policy.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/case"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_OpensCircuitAfterConfiguredFailures()
    {
        using var handler = new SequenceHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var policy = CreatePolicy(maxRetries: 0, failureThreshold: 2);

        using (var first = await policy.SendAsync(
                   client,
                   () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/one"),
                   CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        }

        using (var second = await policy.SendAsync(
                   client,
                   () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/two"),
                   CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        }

        await Assert.ThrowsAsync<CourtSyncCircuitOpenException>(() =>
            policy.SendAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/three"),
                CancellationToken.None));
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SendAsync_PreservesCallerCancellationWithoutRetry()
    {
        using var handler = new CancellingHandler();
        using var client = new HttpClient(handler);
        var policy = CreatePolicy(maxRetries: 3);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.SendAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Get, "https://example.test/case"),
                cancellation.Token));
        Assert.Equal(0, handler.RequestCount);
    }

    private static SudrfCourtRequestPolicy CreatePolicy(
        int maxRetries,
        int failureThreshold = 5,
        CourtSyncMetrics? metrics = null)
        => new(new SudrfCourtRequestPolicyOptions
        {
            TimeoutSeconds = 2,
            MaxRetries = maxRetries,
            RetryBaseDelayMilliseconds = 1,
            MinimumRequestIntervalMilliseconds = 0,
            MaxConcurrency = 2,
            CircuitFailureThreshold = failureThreshold,
            CircuitBreakSeconds = 30,
        }, metrics);

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _index;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var status = statuses[Math.Min(_index, statuses.Length - 1)];
            _index++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }
}
