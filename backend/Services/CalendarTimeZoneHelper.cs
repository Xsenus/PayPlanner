using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

public static class CalendarTimeZoneHelper
{
    public const string UserTimeZoneSettingKey = "profile.timeZoneId";
    public const string DefaultTimeZoneId = "Europe/Moscow";

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

    public static async Task<IReadOnlyDictionary<int, TimeZoneInfo>> ResolveUserTimeZonesAsync(
        PaymentContext db,
        IEnumerable<int> userIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedUserIds = userIds
            .Where(userId => userId > 0)
            .Distinct()
            .ToArray();
        if (normalizedUserIds.Length == 0)
        {
            return new Dictionary<int, TimeZoneInfo>();
        }

        var timeZoneSettings = await db.UserSettings
            .AsNoTracking()
            .Where(item => normalizedUserIds.Contains(item.UserId) && item.Key == UserTimeZoneSettingKey)
            .Select(item => new { item.UserId, item.Value })
            .ToDictionaryAsync(item => item.UserId, item => item.Value, cancellationToken);

        return normalizedUserIds.ToDictionary(
            userId => userId,
            userId => ResolveTimeZone(timeZoneSettings.GetValueOrDefault(userId)));
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        var normalized = NormalizeTimeZoneId(timeZoneId);
        if (TryFindSystemTimeZone(normalized, out var timeZone))
        {
            return timeZone;
        }

        return TryFindSystemTimeZone(DefaultTimeZoneId, out var defaultTimeZone)
            ? defaultTimeZone
            : TimeZoneInfo.Utc;
    }

    public static bool TryResolveTimeZone(string? timeZoneId, out TimeZoneInfo timeZone)
        => TryFindSystemTimeZone(NormalizeTimeZoneId(timeZoneId), out timeZone);

    public static DateTime NormalizeUtcInstant(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    public static DateTime ToExternalUtc(DateTime value, TimeZoneInfo timeZone)
        => NormalizeUtcInstant(value);

    public static DateTime ToUserLocal(DateTime value, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTimeFromUtc(NormalizeUtcInstant(value), timeZone);

    public static DateTime FromUserLocalToUtc(DateTime value, TimeZoneInfo timeZone)
        => ConvertUserLocalToUtc(value, timeZone);

    public static string FormatUserDateTime(
        DateTime value,
        TimeZoneInfo timeZone,
        string format = "dd.MM.yyyy HH:mm",
        CultureInfo? culture = null)
        => ToUserLocal(value, timeZone).ToString(format, culture ?? CultureInfo.InvariantCulture);

    public static string FormatOffsetDateTime(DateTime value, TimeZoneInfo timeZone)
    {
        var instant = NormalizeUtcInstant(value);
        var local = ToUserLocal(instant, timeZone);
        // During a repeated local hour, the UTC instant identifies which offset applies.
        var offset = timeZone.GetUtcOffset(instant);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
    }

    public static string FormatUtcIcsDateTime(DateTime value, TimeZoneInfo timeZone)
        => NormalizeUtcInstant(value).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

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
            if (TryFindById(timeZoneId, out timeZone))
            {
                return true;
            }

            if (!IsWindowsTimeZoneId(timeZoneId) &&
                TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out var windowsId) &&
                TryFindById(windowsId, out timeZone))
            {
                return true;
            }

            if (IsWindowsTimeZoneId(timeZoneId) &&
                TimeZoneInfo.TryConvertWindowsIdToIanaId(timeZoneId, out var ianaId) &&
                TryFindById(ianaId, out timeZone))
            {
                return true;
            }
        }

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    private static bool TryFindById(string timeZoneId, out TimeZoneInfo timeZone)
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

        timeZone = TimeZoneInfo.Utc;
        return false;
    }

    private static DateTime ConvertUserLocalToUtc(DateTime value, TimeZoneInfo timeZone)
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
