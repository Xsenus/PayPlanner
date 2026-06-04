using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ActDomainService
{
    public static void Normalize(Act act)
    {
        ArgumentNullException.ThrowIfNull(act);

        act.Date = NormalizeLocalDate(act.Date);
    }

    public static DateTime NormalizeLocalDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    public static DateTime? NormalizeLocalDate(DateTime? value)
        => value.HasValue ? NormalizeLocalDate(value.Value) : null;

    public static DateTime NormalizeUtcInstant(DateTime value)
        => CalendarTimeZoneHelper.NormalizeUtcInstant(value);

    public static DateTime? NormalizeUtcInstant(DateTime? value)
        => value.HasValue ? NormalizeUtcInstant(value.Value) : null;
}
