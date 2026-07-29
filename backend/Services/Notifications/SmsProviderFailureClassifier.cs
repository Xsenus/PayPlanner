using System.Net;

namespace PayPlanner.Api.Services.Notifications;

public static class SmsProviderFailureCodes
{
    public const string RateLimited = "rate-limited";
    public const string ProviderUnavailable = "provider-unavailable";
    public const string AuthenticationFailed = "authentication-failed";
    public const string InsufficientBalance = "insufficient-balance";
    public const string SenderForbidden = "sender-forbidden";
    public const string InvalidRecipient = "invalid-recipient";
    public const string RequestRejected = "request-rejected";
    public const string ProviderBusinessError = "provider-business-error";
}

public static class SmsProviderFailureClassifier
{
    private static readonly IReadOnlyDictionary<string, SmsFailureClassification> BusinessCodeClassifications =
        BuildBusinessCodeClassifications();

    public static SmsProviderSendResult Classify(
        HttpStatusCode? httpStatusCode,
        string? providerCode = null,
        TimeSpan? retryAfter = null)
    {
        var normalizedProviderCode = NormalizeProviderCode(providerCode);
        var classification = normalizedProviderCode.Length > 0
            && BusinessCodeClassifications.TryGetValue(normalizedProviderCode, out var businessClassification)
                ? businessClassification
                : ClassifyHttpStatus(httpStatusCode, normalizedProviderCode.Length > 0);

        return new SmsProviderSendResult(
            false,
            null,
            classification.Status,
            ErrorCode: classification.ErrorCode,
            ErrorMessage: classification.SafeMessage,
            RetryAfter: classification.IsTransient ? NormalizeRetryAfter(retryAfter) : null,
            IsTransient: classification.IsTransient);
    }

    private static SmsFailureClassification ClassifyHttpStatus(
        HttpStatusCode? httpStatusCode,
        bool hasProviderCode)
    {
        if (httpStatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return httpStatusCode == HttpStatusCode.TooManyRequests
                ? RateLimited()
                : ProviderUnavailable();
        }

        var numericStatus = httpStatusCode.HasValue ? (int)httpStatusCode.Value : 0;
        if (numericStatus is 425 or >= 500 and <= 599)
        {
            return ProviderUnavailable();
        }

        if (httpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Rejected(
                SmsProviderFailureCodes.AuthenticationFailed,
                "SMS-провайдер отклонил авторизацию.");
        }

        if (httpStatusCode == HttpStatusCode.PaymentRequired)
        {
            return Rejected(
                SmsProviderFailureCodes.InsufficientBalance,
                "Недостаточно средств на балансе SMS-провайдера.");
        }

        if (hasProviderCode)
        {
            return Rejected(
                SmsProviderFailureCodes.ProviderBusinessError,
                "SMS-провайдер отклонил запрос по бизнес-правилу.");
        }

        return Rejected(
            SmsProviderFailureCodes.RequestRejected,
            "SMS-провайдер отклонил запрос.");
    }

    private static IReadOnlyDictionary<string, SmsFailureClassification> BuildBusinessCodeClassifications()
    {
        var classifications = new Dictionary<string, SmsFailureClassification>(StringComparer.OrdinalIgnoreCase);
        AddAliases(
            classifications,
            Rejected(
                SmsProviderFailureCodes.InsufficientBalance,
                "Недостаточно средств на балансе SMS-провайдера."),
            "insufficient_balance",
            "not_enough_balance",
            "balance_low",
            "no_funds");
        AddAliases(
            classifications,
            Rejected(
                SmsProviderFailureCodes.SenderForbidden,
                "SMS-провайдер отклонил имя отправителя."),
            "sender_forbidden",
            "invalid_sender",
            "sender_not_allowed",
            "sender_not_registered");
        AddAliases(
            classifications,
            Rejected(
                SmsProviderFailureCodes.InvalidRecipient,
                "SMS-провайдер отклонил номер получателя."),
            "invalid_phone",
            "invalid_number",
            "recipient_invalid",
            "phone_format");
        AddAliases(
            classifications,
            Rejected(
                SmsProviderFailureCodes.AuthenticationFailed,
                "SMS-провайдер отклонил авторизацию."),
            "unauthorized",
            "invalid_token",
            "authentication_failed");
        AddAliases(
            classifications,
            RateLimited(),
            "rate_limit",
            "rate_limited",
            "too_many_requests",
            "throttled");
        AddAliases(
            classifications,
            ProviderUnavailable(),
            "temporary_unavailable",
            "service_unavailable",
            "internal_error",
            "gateway_timeout");
        return classifications;
    }

    private static void AddAliases(
        IDictionary<string, SmsFailureClassification> target,
        SmsFailureClassification classification,
        params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            target[alias] = classification;
        }
    }

    private static SmsFailureClassification RateLimited()
        => new(
            SmsProviderMessageStatuses.Failed,
            SmsProviderFailureCodes.RateLimited,
            "SMS-провайдер временно ограничил частоту запросов.",
            true);

    private static SmsFailureClassification ProviderUnavailable()
        => new(
            SmsProviderMessageStatuses.Failed,
            SmsProviderFailureCodes.ProviderUnavailable,
            "SMS-провайдер временно недоступен.",
            true);

    private static SmsFailureClassification Rejected(string errorCode, string safeMessage)
        => new(
            SmsProviderMessageStatuses.Rejected,
            errorCode,
            safeMessage,
            false);

    private static string NormalizeProviderCode(string? providerCode)
    {
        if (string.IsNullOrWhiteSpace(providerCode))
        {
            return string.Empty;
        }

        var normalized = providerCode.Trim();
        if (normalized.Length > 128)
        {
            normalized = normalized[..128];
        }

        return normalized
            .Replace('-', '_')
            .Replace(' ', '_')
            .Replace('.', '_');
    }

    private static TimeSpan? NormalizeRetryAfter(TimeSpan? retryAfter)
        => retryAfter > TimeSpan.Zero ? retryAfter : null;

    private sealed record SmsFailureClassification(
        string Status,
        string ErrorCode,
        string SafeMessage,
        bool IsTransient);
}
