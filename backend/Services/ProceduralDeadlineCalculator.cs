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
        var skippedNonWorkingDays = 0;
        var calculated = rule switch
        {
            "workingDays" => AddWorkingDays(start, Math.Max(0, request.DaysToAdd ?? 0), out skippedNonWorkingDays),
            "calendarDays" => start.AddDays(Math.Max(0, request.DaysToAdd ?? 0)),
            _ => start.AddMonths(Math.Max(0, request.MonthsToAdd ?? 0)),
        };

        var rawDeadline = calculated;
        var rollForwardDays = 0;
        if (request.RollForwardToWorkingDay)
        {
            calculated = RollForwardToWorkingDay(calculated);
            rollForwardDays = (calculated.Date - rawDeadline.Date).Days;
        }

        return new ProceduralDeadlineCalculationResult(
            DateTime.SpecifyKind(calculated.Date, request.StartAt.Value.Kind),
            null,
            rule,
            request.MonthsToAdd ?? 0,
            request.DaysToAdd ?? 0,
            request.RollForwardToWorkingDay,
            DateTime.SpecifyKind(start.Date, request.StartAt.Value.Kind),
            DateTime.SpecifyKind(rawDeadline.Date, request.StartAt.Value.Kind),
            skippedNonWorkingDays,
            rollForwardDays,
            BuildExplanation(
                start,
                rawDeadline,
                calculated,
                rule,
                request.MonthsToAdd ?? 0,
                request.DaysToAdd ?? 0,
                skippedNonWorkingDays,
                rollForwardDays));
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

    private static DateTime AddWorkingDays(DateTime start, int daysToAdd, out int skippedNonWorkingDays)
    {
        var current = start;
        var remaining = daysToAdd;
        skippedNonWorkingDays = 0;
        while (remaining > 0)
        {
            current = current.AddDays(1);
            if (!IsRussianNonWorkingDay(current))
            {
                remaining--;
                continue;
            }

            skippedNonWorkingDays++;
        }

        return current;
    }

    private static string BuildExplanation(
        DateTime start,
        DateTime rawDeadline,
        DateTime deadline,
        string rule,
        int monthsToAdd,
        int daysToAdd,
        int skippedNonWorkingDays,
        int rollForwardDays)
    {
        var period = rule switch
        {
            "workingDays" => $"{daysToAdd} рабочих дней",
            "calendarDays" => $"{daysToAdd} календарных дней",
            _ => $"{monthsToAdd} месяцев",
        };
        var parts = new List<string>
        {
            $"Старт: {start:dd.MM.yyyy}. Правило: {period}. Базовая дата: {rawDeadline:dd.MM.yyyy}.",
        };

        if (skippedNonWorkingDays > 0)
        {
            parts.Add($"При расчете рабочих дней пропущено нерабочих дней: {skippedNonWorkingDays}.");
        }

        if (rollForwardDays > 0)
        {
            parts.Add($"Дата перенесена на ближайший рабочий день: {deadline:dd.MM.yyyy}.");
        }

        return string.Join(" ", parts);
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
        bool rollForwardToWorkingDay = true,
        DateTime? startAt = null,
        DateTime? rawDeadlineAt = null,
        int skippedNonWorkingDays = 0,
        int rollForwardDays = 0,
        string explanation = "")
    {
        DeadlineAt = deadlineAt;
        Error = error;
        Rule = rule;
        MonthsToAdd = monthsToAdd;
        DaysToAdd = daysToAdd;
        RollForwardToWorkingDay = rollForwardToWorkingDay;
        StartAt = startAt;
        RawDeadlineAt = rawDeadlineAt;
        SkippedNonWorkingDays = skippedNonWorkingDays;
        RollForwardDays = rollForwardDays;
        Explanation = explanation;
    }

    public DateTime? DeadlineAt { get; }
    public string? Error { get; }
    public string Rule { get; }
    public int MonthsToAdd { get; }
    public int DaysToAdd { get; }
    public bool RollForwardToWorkingDay { get; }
    public DateTime? StartAt { get; }
    public DateTime? RawDeadlineAt { get; }
    public int SkippedNonWorkingDays { get; }
    public int RollForwardDays { get; }
    public string Explanation { get; }
}
