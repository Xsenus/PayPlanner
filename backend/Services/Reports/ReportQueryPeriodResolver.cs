namespace PayPlanner.Api.Services.Reports;

public static class ReportQueryPeriodResolver
{
    public static (DateTime Start, DateTime End) ResolveReportPeriod(DateTime? from, DateTime? to, DateTime today)
    {
        var start = (from?.Date) ?? new DateTime(today.Year, today.Month, 1);
        var end = (to?.Date) ?? start.AddMonths(1).AddDays(-1);
        return end < start ? (end, start) : (start, end);
    }

    public static DateTime NormalizeReportInstant(DateTime value)
        => CalendarTimeZoneHelper.NormalizeUtcInstant(value);

    public static DateTime? NormalizeReportInstant(DateTime? value)
        => value.HasValue ? NormalizeReportInstant(value.Value) : null;

    public static DateTime NormalizeReportDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    public static DateTime? NormalizeReportDate(DateTime? value)
        => value.HasValue ? NormalizeReportDate(value.Value) : null;

    public static DateTime ResolveReportLocalDate(DateTime value, TimeZoneInfo timeZone)
        => NormalizeReportDate(CalendarTimeZoneHelper.ToUserLocal(value, timeZone));
}
