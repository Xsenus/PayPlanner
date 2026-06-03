using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

public static class CalendarTimeZoneHelper
{
    public const string UserTimeZoneSettingKey = "profile.timeZoneId";

    public static async Task<TimeZoneInfo> ResolveUserTimeZoneAsync(
        PaymentContext db,
        int? userId,
        CancellationToken cancellationToken = default)
    {
        if (userId.HasValue)
        {
            var timeZoneId = await db.UserSettings
                .AsNoTracking()
                .Where(item => item.UserId == userId.Value && item.Key == UserTimeZoneSettingKey)
                .Select(item => item.Value)
                .FirstOrDefaultAsync(cancellationToken);

            return ResolveTimeZone(timeZoneId);
        }

        return ResolveTimeZone(null);
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var normalized = NormalizeTimeZoneId(timeZoneId);
        if (TryFindSystemTimeZone(normalized, out var timeZone))
        {
            return timeZone;
        }

        return TimeZoneInfo.Local;
    }

    public static DateTime ToExternalUtc(DateTime value, TimeZoneInfo timeZone)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => ConvertUnspecifiedToUtc(value, timeZone),
        };

    public static DateTime ToUserLocal(DateTime value, TimeZoneInfo timeZone)
        => value.Kind switch
        {
            DateTimeKind.Utc => TimeZoneInfo.ConvertTimeFromUtc(value, timeZone),
            DateTimeKind.Local => TimeZoneInfo.ConvertTime(value, timeZone),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
        };

    public static string FormatOffsetDateTime(DateTime value, TimeZoneInfo timeZone)
    {
        var local = ToUserLocal(value, timeZone);
        var offset = timeZone.GetUtcOffset(local);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
    }

    public static string FormatUtcIcsDateTime(DateTime value, TimeZoneInfo timeZone)
        => ToExternalUtc(value, timeZone).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string ResolveIanaTimeZoneId(TimeZoneInfo timeZone)
    {
        if (!IsWindowsTimeZoneId(timeZone.Id))
        {
            return timeZone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZone.Id, out var ianaId)
            ? ianaId
            : "UTC";
    }

    private static string? NormalizeTimeZoneId(string? timeZoneId)
    {
        var normalized = (timeZoneId ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if ((normalized.StartsWith('"') && normalized.EndsWith('"')) ||
            (normalized.StartsWith('\'') && normalized.EndsWith('\'')))
        {
            normalized = normalized[1..^1].Trim();
        }

        return normalized.Length == 0 ? null : normalized;
    }

    private static bool TryFindSystemTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }

            if (!IsWindowsTimeZoneId(timeZoneId) &&
                TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
                TryFindSystemTimeZone(windowsId, out timeZone))
            {
                return true;
            }

            if (IsWindowsTimeZoneId(timeZoneId) &&
                TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) &&
                TryFindSystemTimeZone(ianaId, out timeZone))
            {
                return true;
            }
        }

        timeZone = TimeZoneInfo.Local;
        return false;
    }

    private static DateTime ConvertUnspecifiedToUtc(DateTime value, TimeZoneInfo timeZone)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone);
        }
        catch (ArgumentException)
        {
            var offset = timeZone.GetUtcOffset(unspecified);
            return new DateTimeOffset(unspecified, offset).UtcDateTime;
        }
    }

    private static bool IsWindowsTimeZoneId(string timeZoneId)
        => !timeZoneId.Contains('/', StringComparison.Ordinal);
}
