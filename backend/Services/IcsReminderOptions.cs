using System.Globalization;

namespace PayPlanner.Api.Services;

public static class IcsReminderOptions
{
    public static readonly IReadOnlyList<int> DefaultReminderMinutes = [60, 15];

    public static IReadOnlyList<int> Normalize(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return DefaultReminderMinutes;
        }

        var result = rawValue
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                ? minutes
                : (int?)null)
            .Where(minutes => minutes is >= 1 and <= 10080)
            .Select(minutes => minutes!.Value)
            .Distinct()
            .OrderByDescending(static minutes => minutes)
            .Take(5)
            .ToList();

        return result.Count == 0 ? DefaultReminderMinutes : result;
    }

    public static string ToTriggerDuration(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 10080);
        return minutes % 60 == 0
            ? $"PT{minutes / 60}H"
            : $"PT{minutes}M";
    }
}
