using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ProceduralDeadlineCalculatorTests
{
    [Fact]
    public void CalculateByMonths_RollsFixedHolidayForward()
    {
        var result = ProceduralDeadlineCalculator.CalculateByMonths(
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            monthsToAdd: 1);

        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), result);
    }

    [Fact]
    public void Calculate_WorkingDays_SkipsWeekendAndFixedHolidays()
    {
        var result = ProceduralDeadlineCalculator.Calculate(new ProceduralDeadlineCalculationRequest
        {
            StartAt = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            Rule = "workingDays",
            DaysToAdd = 2,
            RollForwardToWorkingDay = true,
        });

        Assert.Null(result.Error);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), result.DeadlineAt);
        Assert.Equal(new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc), result.StartAt);
        Assert.Equal(new DateTime(2026, 5, 5, 0, 0, 0, DateTimeKind.Utc), result.RawDeadlineAt);
        Assert.Equal(3, result.SkippedNonWorkingDays);
        Assert.Equal(0, result.RollForwardDays);
        Assert.Contains("пропущено нерабочих дней: 3", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Calculate_CalendarMonths_ExplainsRollForward()
    {
        var result = ProceduralDeadlineCalculator.Calculate(new ProceduralDeadlineCalculationRequest
        {
            StartAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Rule = "calendarMonths",
            MonthsToAdd = 1,
            RollForwardToWorkingDay = true,
        });

        Assert.Null(result.Error);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), result.RawDeadlineAt);
        Assert.Equal(new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), result.DeadlineAt);
        Assert.Equal(3, result.RollForwardDays);
        Assert.Contains("Дата перенесена на ближайший рабочий день: 04.05.2026", result.Explanation, StringComparison.Ordinal);
    }
}
