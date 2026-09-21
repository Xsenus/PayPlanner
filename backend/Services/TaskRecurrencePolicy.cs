namespace PayPlanner.Api.Services;

public sealed record TaskRecurrenceRule(
    string Frequency,
    int Interval,
    int? DayOfWeek,
    int? DayOfMonth,
    int? Month,
    int TimeMinutes,
    bool SkipWeekends,
    string TimeZoneId);

public static class TaskRecurrencePolicy
{
    private static readonly HashSet<string> AllowedFrequencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "daily",
        "weekly",
        "monthly",
        "yearly",
    };

    public static string? Validate(TaskRecurrenceRule? rule)
    {
        if (rule is null)
        {
            return "Укажите параметры повторения задачи.";
        }

        if (!AllowedFrequencies.Contains(rule.Frequency))
        {
            return "Выберите корректный интервал повторения задачи.";
        }

        if (rule.Interval is < 1 or > 365)
        {
            return "Интервал повторения должен быть от 1 до 365.";
        }

        if (rule.TimeMinutes is < 0 or >= 24 * 60)
        {
            return "Укажите корректное время повторения задачи.";
        }

        if (string.IsNullOrWhiteSpace(rule.TimeZoneId) || rule.TimeZoneId.Trim().Length > 100)
        {
            return "Укажите корректный часовой пояс повторения задачи.";
        }

        if (string.Equals(rule.Frequency, "weekly", StringComparison.OrdinalIgnoreCase)
            && (rule.DayOfWeek is null || rule.DayOfWeek is < 0 or > 6))
        {
            return "Выберите день недели для повторения задачи.";
        }

        if ((string.Equals(rule.Frequency, "monthly", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rule.Frequency, "yearly", StringComparison.OrdinalIgnoreCase))
            && (rule.DayOfMonth is null || rule.DayOfMonth is < 1 or > 31))
        {
            return "Число месяца для повторения должно быть от 1 до 31.";
        }

        if (string.Equals(rule.Frequency, "yearly", StringComparison.OrdinalIgnoreCase)
            && (rule.Month is null || rule.Month is < 1 or > 12))
        {
            return "Выберите месяц ежегодного повторения задачи.";
        }

        return null;
    }

    public static DateTime CalculateNextUtc(TaskRecurrenceRule rule, DateTime afterUtc)
    {
        var validation = Validate(rule);
        if (validation is not null)
        {
            throw new ArgumentException(validation, nameof(rule));
        }

        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(rule.TimeZoneId);
        var normalizedAfterUtc = CalendarTimeZoneHelper.NormalizeUtcInstant(afterUtc);
        var localAfter = CalendarTimeZoneHelper.ToUserLocal(normalizedAfterUtc, timeZone);
        var localDate = FindNextLocalDate(rule, localAfter);
        var localCandidate = localDate.AddMinutes(rule.TimeMinutes);
        return CalendarTimeZoneHelper.FromUserLocalToUtc(localCandidate, timeZone);
    }

    private static DateTime FindNextLocalDate(TaskRecurrenceRule rule, DateTime localAfter)
    {
        var frequency = rule.Frequency.ToLowerInvariant();
        var searchDate = localAfter.Date;
        for (var attempt = 0; attempt < 3700; attempt++)
        {
            var candidate = frequency switch
            {
                "daily" => searchDate.AddDays(attempt * rule.Interval),
                "weekly" => NextWeeklyDate(searchDate, rule, attempt),
                "monthly" => NextMonthlyDate(searchDate, rule, attempt),
                "yearly" => NextYearlyDate(searchDate, rule, attempt),
                _ => throw new ArgumentOutOfRangeException(nameof(rule)),
            };

            var adjusted = rule.SkipWeekends ? MoveToMonday(candidate) : candidate;
            var localCandidate = adjusted.AddMinutes(rule.TimeMinutes);
            if (localCandidate > localAfter)
            {
                return adjusted;
            }

        }

        throw new InvalidOperationException("Не удалось вычислить следующую дату повторения задачи.");
    }

    private static DateTime NextWeeklyDate(DateTime start, TaskRecurrenceRule rule, int attempt)
    {
        var targetDay = rule.DayOfWeek ?? (int)start.DayOfWeek;
        var delta = (targetDay - (int)start.DayOfWeek + 7) % 7;
        return start.AddDays(delta + attempt * 7 * rule.Interval);
    }

    private static DateTime NextMonthlyDate(DateTime start, TaskRecurrenceRule rule, int attempt)
    {
        var monthOffset = attempt * rule.Interval;
        var month = new DateTime(start.Year, start.Month, 1).AddMonths(monthOffset);
        var day = Math.Min(rule.DayOfMonth ?? start.Day, DateTime.DaysInMonth(month.Year, month.Month));
        return new DateTime(month.Year, month.Month, day);
    }

    private static DateTime NextYearlyDate(DateTime start, TaskRecurrenceRule rule, int attempt)
    {
        var year = start.Year + attempt * rule.Interval;
        var month = rule.Month ?? start.Month;
        var day = Math.Min(rule.DayOfMonth ?? start.Day, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day);
    }

    private static DateTime MoveToMonday(DateTime date)
        => date.DayOfWeek switch
        {
            DayOfWeek.Saturday => date.AddDays(2),
            DayOfWeek.Sunday => date.AddDays(1),
            _ => date,
        };
}
