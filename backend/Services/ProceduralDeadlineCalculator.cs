namespace PayPlanner.Api.Services;

public static class ProceduralDeadlineCalculator
{
    public static ProceduralDeadlineCalculationResult Calculate(ProceduralDeadlineCalculationRequest request)
    {
        if (request.StartAt is null)
        {
            return new ProceduralDeadlineCalculationResult(null, "Укажите начальную дату расчета.");
        }

        var rule = NormalizeRule(request.Rule);
        var start = NormalizeDate(request.StartAt.Value);
        var calculated = rule switch
        {
            "workingDays" => AddWorkingDays(start, Math.Max(0, request.DaysToAdd ?? 0)),
            "calendarDays" => start.AddDays(Math.Max(0, request.DaysToAdd ?? 0)),
            _ => start.AddMonths(Math.Max(0, request.MonthsToAdd ?? 0)),
        };

        if (request.RollForwardToWorkingDay)
        {
            calculated = RollForwardToWorkingDay(calculated);
        }

        return new ProceduralDeadlineCalculationResult(
            DateTime.SpecifyKind(calculated.Date, request.StartAt.Value.Kind),
            null,
            rule,
            request.MonthsToAdd ?? 0,
            request.DaysToAdd ?? 0,
            request.RollForwardToWorkingDay);
    }

    public static DateTime CalculateByMonths(DateTime startAt, int monthsToAdd)
        => Calculate(new ProceduralDeadlineCalculationRequest
        {
            StartAt = startAt,
            Rule = "calendarMonths",
            MonthsToAdd = monthsToAdd,
            RollForwardToWorkingDay = true,
        }).DeadlineAt!.Value;

    public static DateTime RollForwardToWorkingDay(DateTime date)
    {
        var result = NormalizeDate(date);
        while (IsRussianNonWorkingDay(result))
        {
            result = result.AddDays(1);
        }

        return result;
    }

    public static bool IsRussianNonWorkingDay(DateTime date)
        => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || IsFixedRussianHoliday(date);

    public static bool IsFixedRussianHoliday(DateTime date)
        => (date.Month == 1 && date.Day is >= 1 and <= 8)
           || (date.Month == 2 && date.Day == 23)
           || (date.Month == 3 && date.Day == 8)
           || (date.Month == 5 && date.Day is 1 or 9)
           || (date.Month == 6 && date.Day == 12)
           || (date.Month == 11 && date.Day == 4);

    private static DateTime AddWorkingDays(DateTime start, int daysToAdd)
    {
        var current = start;
        var remaining = daysToAdd;
        while (remaining > 0)
        {
            current = current.AddDays(1);
            if (!IsRussianNonWorkingDay(current))
            {
                remaining--;
            }
        }

        return current;
    }

    private static DateTime NormalizeDate(DateTime date)
        => date.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
            : date.ToUniversalTime().Date;

    private static string NormalizeRule(string? rule)
        => rule?.Trim() switch
        {
            "workingDays" => "workingDays",
            "calendarDays" => "calendarDays",
            _ => "calendarMonths",
        };
}

public sealed class ProceduralDeadlineCalculationRequest
{
    public DateTime? StartAt { get; set; }
    public string? Rule { get; set; }
    public int? MonthsToAdd { get; set; }
    public int? DaysToAdd { get; set; }
    public bool RollForwardToWorkingDay { get; set; } = true;
}

public sealed class ProceduralDeadlineCalculationResult
{
    public ProceduralDeadlineCalculationResult(
        DateTime? deadlineAt,
        string? error,
        string rule = "",
        int monthsToAdd = 0,
        int daysToAdd = 0,
        bool rollForwardToWorkingDay = true)
    {
        DeadlineAt = deadlineAt;
        Error = error;
        Rule = rule;
        MonthsToAdd = monthsToAdd;
        DaysToAdd = daysToAdd;
        RollForwardToWorkingDay = rollForwardToWorkingDay;
    }

    public DateTime? DeadlineAt { get; }
    public string? Error { get; }
    public string Rule { get; }
    public int MonthsToAdd { get; }
    public int DaysToAdd { get; }
    public bool RollForwardToWorkingDay { get; }
}
