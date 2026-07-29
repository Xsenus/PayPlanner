using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SmsProviderResilienceExecutorTests
{
    private static readonly SmsProviderSendRequest Request = new(
        "+79990000000",
        "Тест",
        "PayPlanner",
        "pp-sms-v1-test");

    [Fact]
    public async Task SendAsync_RetriesTransientResultAndPreservesRequest()
    {
        var provider = new SequenceSmsProvider(
            TransientFailure("temporary-1"),
            TransientFailure("temporary-2"),
            Accepted());
        var executor = CreateExecutor(provider, maxAttempts: 3);

        var result = await executor.SendAsync(Request);

        Assert.True(result.Accepted);
        Assert.Equal(3, provider.Attempts.Count);
        Assert.All(provider.Attempts, attempt => Assert.Same(Request, attempt));
        Assert.Equal(SmsProviderCircuitStates.Closed, executor.GetCircuitSnapshot().State);
    }

    [Fact]
    public async Task SendAsync_DoesNotRetryPermanentFailure()
    {
        var provider = new SequenceSmsProvider(new SmsProviderSendResult(
            false,
            null,
            SmsProviderMessageStatuses.Rejected,
            ErrorCode: "invalid-recipient",
            ErrorMessage: "Recipient is invalid."));
        var executor = CreateExecutor(provider, maxAttempts: 3);

        var result = await executor.SendAsync(Request);

        Assert.False(result.Accepted);
        Assert.False(result.IsTransient);
        Assert.Equal("invalid-recipient", result.ErrorCode);
        Assert.Single(provider.Attempts);
        Assert.Equal(0, executor.GetCircuitSnapshot().ConsecutiveTransientFailures);
    }

    [Fact]
    public async Task SendAsync_ConvertsTimeoutToTransientFailureAndRetries()
    {
        var provider = new TimeoutSmsProvider();
        var executor = CreateExecutor(
            provider,
            maxAttempts: 2,
            operationTimeout: TimeSpan.FromMilliseconds(20));

        var result = await executor.SendAsync(Request);

        Assert.False(result.Accepted);
        Assert.True(result.IsTransient);
        Assert.Equal("provider-timeout", result.ErrorCode);
        Assert.Equal(2, provider.Attempts);
    }

    [Fact]
    public async Task SendAsync_EnforcesTimeoutWhenProviderIgnoresCancellation()
    {
        var provider = new NonCooperativeSmsProvider();
        var executor = CreateExecutor(
            provider,
            maxAttempts: 1,
            operationTimeout: TimeSpan.FromMilliseconds(20));

        var result = await executor.SendAsync(Request);

        Assert.Equal("provider-timeout", result.ErrorCode);
        Assert.True(result.IsTransient);
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task SendAsync_PropagatesCallerCancellationWithoutRetry()
    {
        var provider = new TimeoutSmsProvider();
        var executor = CreateExecutor(
            provider,
            maxAttempts: 3,
            operationTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.SendAsync(Request, cancellation.Token));
        Assert.Equal(0, provider.Attempts);
    }

    [Fact]
    public async Task SendAsync_OpensCircuitAndAllowsSingleSuccessfulProbeAfterBreak()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero));
        var provider = new SequenceSmsProvider(
            TransientFailure("temporary-1"),
            TransientFailure("temporary-2"),
            Accepted());
        var options = Options(
            maxAttempts: 1,
            circuitFailureThreshold: 2,
            circuitBreakDuration: TimeSpan.FromMinutes(1));
        var executor = new SmsProviderResilienceExecutor(provider, options, timeProvider);

        await executor.SendAsync(Request);
        await executor.SendAsync(Request);
        var blocked = await executor.SendAsync(Request);

        Assert.Equal("circuit-open", blocked.ErrorCode);
        Assert.True(blocked.IsTransient);
        Assert.Equal(2, provider.Attempts.Count);
        Assert.Equal(SmsProviderCircuitStates.Open, executor.GetCircuitSnapshot().State);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var probe = await executor.SendAsync(Request);

        Assert.True(probe.Accepted);
        Assert.Equal(3, provider.Attempts.Count);
        var snapshot = executor.GetCircuitSnapshot();
        Assert.Equal(SmsProviderCircuitStates.Closed, snapshot.State);
        Assert.Equal(0, snapshot.ConsecutiveTransientFailures);
        Assert.Null(snapshot.OpenUntilUtc);
    }

    [Fact]
    public async Task SendAsync_TreatsHttpRequestFailureAsTransient()
    {
        var provider = new ThrowingSmsProvider(new HttpRequestException("Sensitive upstream detail"));
        var executor = CreateExecutor(provider, maxAttempts: 1);

        var result = await executor.SendAsync(Request);

        Assert.True(result.IsTransient);
        Assert.Equal("provider-http-error", result.ErrorCode);
        Assert.DoesNotContain("Sensitive", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task SendAsync_ReopensCircuitWhenHalfOpenProbeIsCancelled()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero));
        var provider = new CancelledProbeSmsProvider();
        var executor = new SmsProviderResilienceExecutor(
            provider,
            Options(
                maxAttempts: 1,
                circuitFailureThreshold: 1,
                circuitBreakDuration: TimeSpan.FromMinutes(1)),
            timeProvider);

        await executor.SendAsync(Request);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.SendAsync(Request, cancellation.Token));

        Assert.Equal(SmsProviderCircuitStates.Open, executor.GetCircuitSnapshot().State);
        var blocked = await executor.SendAsync(Request);
        Assert.Equal("circuit-open", blocked.ErrorCode);
        Assert.Equal(2, provider.Attempts);
    }

    [Fact]
    public void Constructor_RejectsUnsafeOptions()
    {
        var provider = new SequenceSmsProvider(Accepted());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmsProviderResilienceExecutor(
                provider,
                Options(operationTimeout: TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmsProviderResilienceExecutor(
                provider,
                Options(maxAttempts: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SmsProviderResilienceExecutor(
                provider,
                Options(retryDelay: TimeSpan.FromSeconds(2), maxRetryDelay: TimeSpan.FromSeconds(1))));
    }

    private static SmsProviderResilienceExecutor CreateExecutor(
        ISmsProvider provider,
        int maxAttempts,
        TimeSpan? operationTimeout = null)
        => new(
            provider,
            Options(
                operationTimeout: operationTimeout,
                maxAttempts: maxAttempts));

    private static SmsProviderResilienceOptions Options(
        TimeSpan? operationTimeout = null,
        int maxAttempts = 3,
        TimeSpan? retryDelay = null,
        TimeSpan? maxRetryDelay = null,
        int circuitFailureThreshold = 5,
        TimeSpan? circuitBreakDuration = null)
        => new(
            operationTimeout ?? TimeSpan.FromSeconds(1),
            maxAttempts,
            retryDelay ?? TimeSpan.Zero,
            maxRetryDelay ?? TimeSpan.Zero,
            circuitFailureThreshold,
            circuitBreakDuration ?? TimeSpan.FromMinutes(1));

    private static SmsProviderSendResult Accepted()
        => new(
            true,
            "provider-message-1",
            SmsProviderMessageStatuses.Accepted);

    private static SmsProviderSendResult TransientFailure(string errorCode)
        => new(
            false,
            null,
            SmsProviderMessageStatuses.Failed,
            ErrorCode: errorCode,
            ErrorMessage: "Temporary failure.",
            IsTransient: true);

    private sealed class SequenceSmsProvider(params SmsProviderSendResult[] results) : TestSmsProvider
    {
        private readonly Queue<SmsProviderSendResult> _results = new(results);

        public List<SmsProviderSendRequest> Attempts { get; } = [];

        public override Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class TimeoutSmsProvider : TestSmsProvider
    {
        public int Attempts { get; private set; }

        public override async Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class NonCooperativeSmsProvider : TestSmsProvider
    {
        private readonly TaskCompletionSource<SmsProviderSendResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Attempts { get; private set; }

        public override Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return _completion.Task;
        }
    }

    private sealed class ThrowingSmsProvider(Exception exception) : TestSmsProvider
    {
        public int Attempts { get; private set; }

        public override Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            return Task.FromException<SmsProviderSendResult>(exception);
        }
    }

    private sealed class CancelledProbeSmsProvider : TestSmsProvider
    {
        public int Attempts { get; private set; }

        public override async Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1)
            {
                return TransientFailure("temporary");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private abstract class TestSmsProvider : ISmsProvider
    {
        public string Key => "test";

        public abstract Task<SmsProviderSendResult> SendAsync(
            SmsProviderSendRequest request,
            CancellationToken cancellationToken = default);

        public Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
            string providerMessageId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
