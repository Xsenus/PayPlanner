using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed record CaseInactivityResponsibleAvailabilitySnapshot(
    HashSet<int> UsersOnVacation,
    IReadOnlyDictionary<int, string> TimeZoneIds,
    IReadOnlyDictionary<int, string> VacationUntilDates);

public static class CaseInactivityResponsibleAvailabilityService
{
    public static async Task<HashSet<int>> GetUsersOnVacationAsync(
        PaymentContext context,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => (await GetAvailabilityAsync(context, nowUtc, cancellationToken)).UsersOnVacation;

    public static async Task<HashSet<int>> GetUsersOnVacationAsync(
        PaymentContext context,
        DateTime nowUtc,
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken)
        => userIds.Count == 0
            ? []
            : (await GetAvailabilityAsync(context, nowUtc, userIds, cancellationToken)).UsersOnVacation;

    public static async Task<CaseInactivityResponsibleAvailabilitySnapshot> GetAvailabilityAsync(
        PaymentContext context,
        DateTime nowUtc,
        CancellationToken cancellationToken)
        => await GetAvailabilityAsync(context, nowUtc, userIds: null, cancellationToken);

    private static async Task<CaseInactivityResponsibleAvailabilitySnapshot> GetAvailabilityAsync(
        PaymentContext context,
        DateTime nowUtc,
        IReadOnlyCollection<int>? userIds,
        CancellationToken cancellationToken)
    {
        var settingsQuery = context.UserSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == UserNotificationDoNotDisturbSettings.SettingKey ||
                item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey);
        if (userIds is { Count: > 0 })
        {
            settingsQuery = settingsQuery.Where(item => userIds.Contains(item.UserId));
        }

        var settings = await settingsQuery
            .Select(item => new { item.UserId, item.Key, item.Value })
            .ToListAsync(cancellationToken);

        if (settings.Count == 0)
        {
            return new CaseInactivityResponsibleAvailabilitySnapshot(
                [],
                new Dictionary<int, string>(),
                new Dictionary<int, string>());
        }

        var result = new HashSet<int>();
        var vacationUntilDates = new Dictionary<int, string>();
        var timeZoneIds = settings
            .Where(item => item.Key == CalendarTimeZoneHelper.UserTimeZoneSettingKey)
            .GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => group.First().Value);
        foreach (var userSettings in settings.GroupBy(item => item.UserId))
        {
            var doNotDisturbRaw = userSettings
                .FirstOrDefault(item => item.Key == UserNotificationDoNotDisturbSettings.SettingKey)
                ?.Value;
            var doNotDisturb = UserNotificationDoNotDisturbSettings.Parse(doNotDisturbRaw);
            if (!doNotDisturb.Enabled || string.IsNullOrWhiteSpace(doNotDisturb.VacationUntil))
            {
                continue;
            }

            var timeZoneId = timeZoneIds.GetValueOrDefault(userSettings.Key);
            var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(timeZoneId);
            if (UserNotificationDoNotDisturbSettings.IsVacationActive(doNotDisturb, nowUtc, timeZone))
            {
                result.Add(userSettings.Key);
                if (DateOnly.TryParseExact(
                        doNotDisturb.VacationUntil,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var vacationUntil))
                {
                    vacationUntilDates[userSettings.Key] = vacationUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
            }
        }

        return new CaseInactivityResponsibleAvailabilitySnapshot(result, timeZoneIds, vacationUntilDates);
    }
}
