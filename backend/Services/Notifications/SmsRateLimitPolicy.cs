using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public static class SmsRateLimitReasonCodes
{
    public const string DailyLimitReached = "sms-daily-limit-reached";
    public const string RecipientDailyLimitReached = "sms-recipient-daily-limit-reached";
    public const string BurstLimitReached = "sms-burst-limit-reached";
}

public sealed record SmsRateLimitPolicy(
    int DailyLimit,
    int RecipientDailyLimit,
    int BurstLimitPerMinute)
{
    public static SmsRateLimitPolicy FromSettings(SmsNotificationSettingsDto? settings)
        => new(
            PositiveOrDefault(settings?.DailyLimit, 100),
            PositiveOrDefault(settings?.RecipientDailyLimit, 5),
            PositiveOrDefault(settings?.BurstLimitPerMinute, 20));

    private static int PositiveOrDefault(int? value, int fallback)
        => value is > 0 ? value.Value : fallback;
}

public sealed record SmsRateLimitSnapshot(
    int DailyCount,
    int RecipientDailyCount,
    int BurstCount);

public sealed record SmsRateLimitDecision(
    bool IsAllowed,
    string? ReasonCode);

public static class SmsRateLimitEvaluator
{
    public static SmsRateLimitDecision Evaluate(
        SmsRateLimitPolicy policy,
        SmsRateLimitSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.DailyCount >= policy.DailyLimit)
        {
            return Denied(SmsRateLimitReasonCodes.DailyLimitReached);
        }

        if (snapshot.RecipientDailyCount >= policy.RecipientDailyLimit)
        {
            return Denied(SmsRateLimitReasonCodes.RecipientDailyLimitReached);
        }

        return snapshot.BurstCount >= policy.BurstLimitPerMinute
            ? Denied(SmsRateLimitReasonCodes.BurstLimitReached)
            : new SmsRateLimitDecision(true, null);
    }

    private static SmsRateLimitDecision Denied(string reasonCode)
        => new(false, reasonCode);
}
