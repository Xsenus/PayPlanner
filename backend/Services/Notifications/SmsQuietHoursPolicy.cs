using System.Globalization;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed record SmsQuietHoursDecision(
    bool IsAllowed,
    bool IsQuietHours,
    string? ReasonCode = null);

public static class SmsQuietHoursPolicy
{
    public const string QuietHoursReasonCode = "sms_quiet_hours";

    public static SmsQuietHoursDecision Evaluate(
        SmsNotificationSettingsDto? settings,
        DateTimeOffset nowUtc,
        bool isApprovedCriticalEvent)
    {
        if (settings is not { Enabled: true, QuietHoursEnabled: true })
        {
            return new SmsQuietHoursDecision(true, false);
        }

        var start = ParseTime(settings.QuietHoursStart, SystemSmsNotificationService.DefaultQuietHoursStart);
        var end = ParseTime(settings.QuietHoursEnd, SystemSmsNotificationService.DefaultQuietHoursEnd);
        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(settings.QuietHoursTimeZone);
        var localTime = CalendarTimeZoneHelper.ToUserLocal(nowUtc.UtcDateTime, timeZone).TimeOfDay;
        var isQuietHours = start == end
            || (start < end
                ? localTime >= start && localTime < end
                : localTime >= start || localTime < end);

        if (!isQuietHours || isApprovedCriticalEvent)
        {
            return new SmsQuietHoursDecision(true, isQuietHours);
        }

        return new SmsQuietHoursDecision(false, true, QuietHoursReasonCode);
    }

    private static TimeSpan ParseTime(string? value, string fallback)
        => TimeSpan.TryParseExact(
            value?.Trim(),
            @"hh\:mm",
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : TimeSpan.ParseExact(fallback, @"hh\:mm", CultureInfo.InvariantCulture);
}
