namespace PayPlanner.Api.Services.Notifications;

public sealed record SmsProviderResilienceOptions(
    TimeSpan OperationTimeout,
    int MaxAttempts,
    TimeSpan RetryDelay,
    TimeSpan MaxRetryDelay,
    int CircuitFailureThreshold,
    TimeSpan CircuitBreakDuration)
{
    public static SmsProviderResilienceOptions Default { get; } = new(
        TimeSpan.FromSeconds(10),
        3,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(5),
        5,
        TimeSpan.FromMinutes(1));
}

public static class SmsProviderCircuitStates
{
    public const string Closed = "closed";
    public const string Open = "open";
    public const string HalfOpen = "half-open";
}

public sealed record SmsProviderCircuitSnapshot(
    string State,
    int ConsecutiveTransientFailures,
    DateTimeOffset? OpenUntilUtc);

public sealed class SmsProviderResilienceExecutor
{
    private readonly ISmsProvider _provider;
    private readonly SmsProviderResilienceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly object _circuitLock = new();
    private string _circuitState = SmsProviderCircuitStates.Closed;
    private int _consecutiveTransientFailures;
    private DateTimeOffset? _openUntilUtc;
    private bool _halfOpenProbeInProgress;

    public SmsProviderResilienceExecutor(
        ISmsProvider provider,
        SmsProviderResilienceOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? SmsProviderResilienceOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ValidateOptions(_options);
    }

    public SmsProviderCircuitSnapshot GetCircuitSnapshot()
    {
        lock (_circuitLock)
        {
            return new SmsProviderCircuitSnapshot(
                _circuitState,
                _consecutiveTransientFailures,
                _openUntilUtc);
        }
    }

    public async Task<SmsProviderSendResult> SendAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryAcquireCircuitPermit(out var retryAfter))
        {
            return CircuitOpenResult(retryAfter);
        }

        try
        {
            for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
            {
                var result = await SendAttemptAsync(request, cancellationToken);
                if (result.Accepted || !result.IsTransient)
                {
                    RecordProviderAvailable();
                    return result;
                }

                var circuitOpened = RecordTransientFailure();
                if (attempt >= _options.MaxAttempts || circuitOpened)
                {
                    return result;
                }

                var delay = CalculateRetryDelay(attempt, result.RetryAfter);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch
        {
            ReopenAbortedHalfOpenProbe();
            throw;
        }

        throw new InvalidOperationException("SMS provider resilience loop ended unexpectedly.");
    }

    private async Task<SmsProviderSendResult> SendAttemptAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken)
    {
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var sendTask = _provider.SendAsync(request, providerCancellation.Token);
            return await sendTask.WaitAsync(_options.OperationTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            await providerCancellation.CancelAsync();
            return new SmsProviderSendResult(
                false,
                null,
                SmsProviderMessageStatuses.Failed,
                ErrorCode: "provider-timeout",
                ErrorMessage: "SMS provider request timed out.",
                IsTransient: true);
        }
        catch (HttpRequestException)
        {
            return new SmsProviderSendResult(
                false,
                null,
                SmsProviderMessageStatuses.Failed,
                ErrorCode: "provider-http-error",
                ErrorMessage: "SMS provider request failed.",
                IsTransient: true);
        }
    }

    private bool TryAcquireCircuitPermit(out TimeSpan retryAfter)
    {
        lock (_circuitLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (_circuitState == SmsProviderCircuitStates.Open)
            {
                if (_openUntilUtc > now)
                {
                    retryAfter = _openUntilUtc.Value - now;
                    return false;
                }

                _circuitState = SmsProviderCircuitStates.HalfOpen;
                _halfOpenProbeInProgress = false;
            }

            if (_circuitState == SmsProviderCircuitStates.HalfOpen)
            {
                if (_halfOpenProbeInProgress)
                {
                    retryAfter = _options.CircuitBreakDuration;
                    return false;
                }

                _halfOpenProbeInProgress = true;
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private bool RecordTransientFailure()
    {
        lock (_circuitLock)
        {
            _consecutiveTransientFailures++;
            if (_circuitState == SmsProviderCircuitStates.HalfOpen
                || _consecutiveTransientFailures >= _options.CircuitFailureThreshold)
            {
                _circuitState = SmsProviderCircuitStates.Open;
                _openUntilUtc = _timeProvider.GetUtcNow() + _options.CircuitBreakDuration;
                _halfOpenProbeInProgress = false;
                return true;
            }

            return false;
        }
    }

    private void RecordProviderAvailable()
    {
        lock (_circuitLock)
        {
            _circuitState = SmsProviderCircuitStates.Closed;
            _consecutiveTransientFailures = 0;
            _openUntilUtc = null;
            _halfOpenProbeInProgress = false;
        }
    }

    private void ReopenAbortedHalfOpenProbe()
    {
        lock (_circuitLock)
        {
            if (_circuitState != SmsProviderCircuitStates.HalfOpen || !_halfOpenProbeInProgress)
            {
                return;
            }

            _circuitState = SmsProviderCircuitStates.Open;
            _openUntilUtc = _timeProvider.GetUtcNow() + _options.CircuitBreakDuration;
            _halfOpenProbeInProgress = false;
        }
    }

    private TimeSpan CalculateRetryDelay(int failedAttempt, TimeSpan? providerRetryAfter)
    {
        var exponentialFactor = 1 << Math.Min(failedAttempt - 1, 9);
        var configuredDelay = TimeSpan.FromTicks(
            Math.Min(
                _options.RetryDelay.Ticks * exponentialFactor,
                _options.MaxRetryDelay.Ticks));
        var requestedDelay = providerRetryAfter.GetValueOrDefault();
        if (requestedDelay <= TimeSpan.Zero)
        {
            return configuredDelay;
        }

        return TimeSpan.FromTicks(
            Math.Min(
                Math.Max(configuredDelay.Ticks, requestedDelay.Ticks),
                _options.MaxRetryDelay.Ticks));
    }

    private static SmsProviderSendResult CircuitOpenResult(TimeSpan retryAfter)
        => new(
            false,
            null,
            SmsProviderMessageStatuses.Failed,
            ErrorCode: "circuit-open",
            ErrorMessage: "SMS provider is temporarily unavailable.",
            RetryAfter: retryAfter,
            IsTransient: true);

    private static void ValidateOptions(SmsProviderResilienceOptions options)
    {
        if (options.OperationTimeout <= TimeSpan.Zero || options.OperationTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Operation timeout must be between 0 and 2 minutes.");
        }

        if (options.MaxAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max attempts must be between 1 and 10.");
        }

        if (options.RetryDelay < TimeSpan.Zero
            || options.MaxRetryDelay < options.RetryDelay
            || options.MaxRetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delays are outside the supported range.");
        }

        if (options.CircuitFailureThreshold is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Circuit failure threshold must be between 1 and 100.");
        }

        if (options.CircuitBreakDuration <= TimeSpan.Zero || options.CircuitBreakDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Circuit break duration must be between 0 and 1 hour.");
        }
    }
}
