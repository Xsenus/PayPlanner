namespace PayPlanner.Api.Services;

public readonly record struct CaseInactivityAutomationScheduleDecision(
    bool ShouldRun,
    DateOnly LocalDate,
    DateTime NextRunAtUtc);

public static class CaseInactivityAutomationSchedulePolicy
{
    public static readonly TimeOnly DefaultRunLocalTime = new(9, 0);
    public static readonly TimeSpan DefaultRunWindow = TimeSpan.FromMinutes(20);

    public static CaseInactivityAutomationScheduleDecision Evaluate(
        DateTime nowUtc,
        TimeOnly runLocalTime,
        TimeZoneInfo timeZone,
        DateOnly? lastRunLocalDate,
        TimeSpan? runWindow = null)
    {
        var normalizedNow = CalendarTimeZoneHelper.NormalizeUtcInstant(nowUtc);
        var localNow = CalendarTimeZoneHelper.ToUserLocal(normalizedNow, timeZone);
        var localDate = DateOnly.FromDateTime(localNow);
        var scheduledLocal = localDate.ToDateTime(runLocalTime, DateTimeKind.Unspecified);
        var effectiveWindow = runWindow.GetValueOrDefault(DefaultRunWindow);
        if (effectiveWindow <= TimeSpan.Zero)
        {
            effectiveWindow = DefaultRunWindow;
        }

        var shouldRun =
            lastRunLocalDate != localDate &&
            localNow >= scheduledLocal &&
            localNow < scheduledLocal.Add(effectiveWindow);
        var nextLocal = localNow < scheduledLocal
            ? scheduledLocal
            : localDate.AddDays(1).ToDateTime(runLocalTime, DateTimeKind.Unspecified);
        var nextRunAtUtc = CalendarTimeZoneHelper.FromUserLocalToUtc(nextLocal, timeZone);

        return new CaseInactivityAutomationScheduleDecision(
            shouldRun,
            localDate,
            nextRunAtUtc);
    }

    public static DateOnly? ResolveLastRunLocalDate(
        DateTime? lastSuccessAtUtc,
        TimeZoneInfo timeZone)
    {
        if (!lastSuccessAtUtc.HasValue)
        {
            return null;
        }

        var localLastSuccess = CalendarTimeZoneHelper.ToUserLocal(
            CalendarTimeZoneHelper.NormalizeUtcInstant(lastSuccessAtUtc.Value),
            timeZone);
        return DateOnly.FromDateTime(localLastSuccess);
    }
}
