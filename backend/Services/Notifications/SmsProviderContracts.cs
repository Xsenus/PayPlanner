using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    string IdempotencyKey,
    string Account = "",
    string ApiToken = "");

public sealed record SmsProviderSendResult(
    bool Accepted,
    string? ProviderMessageId,
    string Status,
    string? RawProviderStatus = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    TimeSpan? RetryAfter = null,
    bool IsTransient = false);

public sealed record SmsProviderMessageStatusResult(
    string ProviderMessageId,
    string Status,
    DateTime CheckedAtUtc,
    string? RawProviderStatus = null,
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

public static class SmsProviderIdempotencyKey
{
    public const string Prefix = "pp-sms-v1-";
    public const int MaxLength = 64;

    public static string Create(string eventKey, string recipientAddress, int? senderId)
    {
        var normalizedEventKey = NormalizeRequired(eventKey, nameof(eventKey));
        var normalizedRecipientAddress = NormalizeRequired(recipientAddress, nameof(recipientAddress));
        var normalizedSenderId = senderId?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var payload = string.Concat(
            normalizedEventKey.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            normalizedEventKey,
            "|",
            normalizedRecipientAddress.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            normalizedRecipientAddress,
            "|",
            normalizedSenderId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var encodedHash = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var key = Prefix + encodedHash;

        if (key.Length > MaxLength)
        {
            throw new InvalidOperationException("Generated SMS idempotency key exceeds the supported length.");
        }

        return key;
    }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }

        return normalized;
    }
}

public static class SmsProviderMessageStatuses
{
    public const string Accepted = "accepted";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Rejected = "rejected";
    public const string Unknown = "unknown";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Accepted,
        Delivered,
        Failed,
        Expired,
        Rejected,
        Unknown,
    };

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Delivered,
        Failed,
        Expired,
        Rejected,
    };
}

public static class SmsProviderStatusNormalizer
{
    public const int MaxProviderMessageIdLength = 256;

    private static readonly IReadOnlyDictionary<string, string> StatusAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SmsProviderMessageStatuses.Accepted] = SmsProviderMessageStatuses.Accepted,
            ["accept"] = SmsProviderMessageStatuses.Accepted,
            ["queued"] = SmsProviderMessageStatuses.Accepted,
            ["pending"] = SmsProviderMessageStatuses.Accepted,
            ["processing"] = SmsProviderMessageStatuses.Accepted,
            ["submitted"] = SmsProviderMessageStatuses.Accepted,
            ["sent"] = SmsProviderMessageStatuses.Accepted,
            [SmsProviderMessageStatuses.Delivered] = SmsProviderMessageStatuses.Delivered,
            ["delivrd"] = SmsProviderMessageStatuses.Delivered,
            ["delivered_to_handset"] = SmsProviderMessageStatuses.Delivered,
            [SmsProviderMessageStatuses.Failed] = SmsProviderMessageStatuses.Failed,
            ["error"] = SmsProviderMessageStatuses.Failed,
            ["undelivered"] = SmsProviderMessageStatuses.Failed,
            [SmsProviderMessageStatuses.Expired] = SmsProviderMessageStatuses.Expired,
            ["ttl_expired"] = SmsProviderMessageStatuses.Expired,
            [SmsProviderMessageStatuses.Rejected] = SmsProviderMessageStatuses.Rejected,
            ["blocked"] = SmsProviderMessageStatuses.Rejected,
            ["invalid"] = SmsProviderMessageStatuses.Rejected,
            ["rejectd"] = SmsProviderMessageStatuses.Rejected,
        };

    public static string NormalizeStatus(string? providerStatus)
    {
        var normalized = NormalizeStatusKey(providerStatus);
        return normalized.Length > 0 && StatusAliases.TryGetValue(normalized, out var status)
            ? status
            : SmsProviderMessageStatuses.Unknown;
    }

    public static string? NormalizeMessageId(string? providerMessageId)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
        {
            return null;
        }

        var normalized = providerMessageId.Trim();
        if (normalized.Length > MaxProviderMessageIdLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerMessageId),
                $"Provider message id must not exceed {MaxProviderMessageIdLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Provider message id must not contain control characters.",
                nameof(providerMessageId));
        }

        return normalized;
    }

    private static string NormalizeStatusKey(string? providerStatus)
    {
        if (string.IsNullOrWhiteSpace(providerStatus))
        {
            return string.Empty;
        }

        return providerStatus
            .Trim()
            .Replace('-', '_')
            .Replace(' ', '_');
    }
}
