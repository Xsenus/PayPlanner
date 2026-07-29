namespace PayPlanner.Api.Services.Notifications;

/// <summary>
/// Provider-neutral contract implemented by a concrete SMS gateway adapter.
/// </summary>
public interface ISmsProvider
{
    string Key { get; }

    Task<SmsProviderSendResult> SendAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken = default);

    Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default);

    Task<SmsProviderBalanceResult> GetBalanceAsync(CancellationToken cancellationToken = default);

    Task<SmsProviderHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public sealed record SmsProviderSendRequest(
    string PhoneNumber,
    string Text,
    string SenderName,
    string IdempotencyKey);

public sealed record SmsProviderSendResult(
    bool Accepted,
    string? ProviderMessageId,
    string? ProviderStatus = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    TimeSpan? RetryAfter = null);

public sealed record SmsProviderMessageStatusResult(
    string ProviderMessageId,
    string ProviderStatus,
    DateTime CheckedAtUtc,
    DateTime? DeliveredAtUtc = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record SmsProviderBalanceResult(
    bool IsAvailable,
    decimal? Amount,
    string? Currency,
    DateTime CheckedAtUtc,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record SmsProviderHealthResult(
    bool IsHealthy,
    DateTime CheckedAtUtc,
    TimeSpan? Latency = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
