using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed class SudrfCourtRequestPolicyOptions
{
    public const string SectionName = "LegalIntegrations:CourtProviders:Sudrf";

    public int TimeoutSeconds { get; set; } = 25;
    public int MaxRetries { get; set; } = 2;
    public int RetryBaseDelayMilliseconds { get; set; } = 250;
    public int MaxConcurrency { get; set; } = 2;
    public int MinimumRequestIntervalMilliseconds { get; set; } = 100;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitBreakSeconds { get; set; } = 60;
}

public sealed class SudrfCourtRequestPolicy
{
    private readonly TimeSpan _timeout;
    private readonly int _maxRetries;
    private readonly TimeSpan _retryBaseDelay;
    private readonly TimeSpan _minimumRequestInterval;
    private readonly int _circuitFailureThreshold;
    private readonly TimeSpan _circuitBreakDuration;
    private readonly SemaphoreSlim _concurrency;
    private readonly CourtSyncMetrics? _metrics;
    private readonly object _stateLock = new();
    private DateTime _nextRequestAtUtc;
    private DateTime _circuitOpenUntilUtc;
    private int _consecutiveFailures;

    public SudrfCourtRequestPolicy(
        IOptions<SudrfCourtRequestPolicyOptions> options,
        CourtSyncMetrics metrics)
        : this(options.Value, metrics)
    {
    }

    public SudrfCourtRequestPolicy(
        SudrfCourtRequestPolicyOptions? options = null,
        CourtSyncMetrics? metrics = null)
    {
        options ??= new SudrfCourtRequestPolicyOptions();
        _metrics = metrics;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 120));
        _maxRetries = Math.Clamp(options.MaxRetries, 0, 5);
        _retryBaseDelay = TimeSpan.FromMilliseconds(Math.Clamp(options.RetryBaseDelayMilliseconds, 0, 10_000));
        _minimumRequestInterval = TimeSpan.FromMilliseconds(Math.Clamp(options.MinimumRequestIntervalMilliseconds, 0, 5_000));
        _circuitFailureThreshold = Math.Clamp(options.CircuitFailureThreshold, 1, 50);
        _circuitBreakDuration = TimeSpan.FromSeconds(Math.Clamp(options.CircuitBreakSeconds, 1, 900));
        _concurrency = new SemaphoreSlim(Math.Clamp(options.MaxConcurrency, 1, 20));
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ThrowIfCircuitOpen();
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                ThrowIfCircuitOpen();
                await WaitForRateSlotAsync(cancellationToken);
                using var request = requestFactory();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_timeout);
                var requestStartedAt = Stopwatch.GetTimestamp();

                try
                {
                    var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token);
                    _metrics?.RecordRequest(
                        Stopwatch.GetElapsedTime(requestStartedAt),
                        !response.IsSuccessStatusCode);
                    if (!IsTransient(response.StatusCode))
                    {
                        RegisterSuccess();
                        return response;
                    }

                    if (attempt >= _maxRetries)
                    {
                        RegisterFailure();
                        return response;
                    }

                    var retryDelay = ResolveRetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(retryDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _metrics?.RecordRequest(Stopwatch.GetElapsedTime(requestStartedAt), failed: false);
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    _metrics?.RecordRequest(Stopwatch.GetElapsedTime(requestStartedAt), failed: true);
                    if (attempt >= _maxRetries)
                    {
                        RegisterFailure();
                        throw new TimeoutException("SUDRF request timed out.", ex);
                    }

                    await Task.Delay(GetBackoff(attempt), cancellationToken);
                }
                catch (HttpRequestException)
                {
                    _metrics?.RecordRequest(Stopwatch.GetElapsedTime(requestStartedAt), failed: true);
                    if (attempt >= _maxRetries)
                    {
                        RegisterFailure();
                        throw;
                    }

                    await Task.Delay(GetBackoff(attempt), cancellationToken);
                }
                catch
                {
                    _metrics?.RecordRequest(Stopwatch.GetElapsedTime(requestStartedAt), failed: true);
                    throw;
                }
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task WaitForRateSlotAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            var requestAt = _nextRequestAtUtc > now ? _nextRequestAtUtc : now;
            _nextRequestAtUtc = requestAt + _minimumRequestInterval;
            delay = requestAt - now;
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private void ThrowIfCircuitOpen()
    {
        lock (_stateLock)
        {
            if (_circuitOpenUntilUtc > DateTime.UtcNow)
            {
                throw new CourtSyncCircuitOpenException(_circuitOpenUntilUtc);
            }

            if (_circuitOpenUntilUtc != default)
            {
                _circuitOpenUntilUtc = default;
                _consecutiveFailures = 0;
            }
        }
    }

    private void RegisterSuccess()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntilUtc = default;
        }
    }

    private void RegisterFailure()
    {
        lock (_stateLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _circuitFailureThreshold)
            {
                _circuitOpenUntilUtc = DateTime.UtcNow + _circuitBreakDuration;
            }
        }
    }

    private TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
        => response.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Min(retryAfter.TotalMilliseconds, 30_000))
            : GetBackoff(attempt);

    private TimeSpan GetBackoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Min(
            _retryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt),
            30_000));

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
        => statusCode == System.Net.HttpStatusCode.RequestTimeout
            || statusCode == System.Net.HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;
}

public sealed class CourtSyncCircuitOpenException(DateTime retryAtUtc)
    : Exception($"Судебный источник временно отключён после серии ошибок. Повторите после {retryAtUtc:O}.")
{
    public DateTime RetryAtUtc { get; } = retryAtUtc;
}
