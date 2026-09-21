using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TaskRecurrencePolicyTests
{
    [Theory]
    [InlineData("weekly")]
    [InlineData("monthly")]
    [InlineData("yearly")]
    public void Validate_RejectsMissingFrequencySpecificFields(string frequency)
    {
        var rule = new TaskRecurrenceRule(frequency, 1, null, null, null, 9 * 60, false, "UTC");

        Assert.NotNull(TaskRecurrencePolicy.Validate(rule));
    }

    [Fact]
    public void CalculateNextUtc_ClampsMonthlyDayToLastDay()
    {
        var rule = new TaskRecurrenceRule("monthly", 1, null, 31, null, 9 * 60, false, "UTC");

        var next = TaskRecurrencePolicy.CalculateNextUtc(
            rule,
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 2, 28, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void CalculateNextUtc_MovesWeekendOccurrenceToMonday()
    {
        var rule = new TaskRecurrenceRule("monthly", 1, null, 21, null, 9 * 60, true, "UTC");

        var next = TaskRecurrencePolicy.CalculateNextUtc(
            rule,
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void CalculateNextUtc_UsesConfiguredTimeZone()
    {
        var rule = new TaskRecurrenceRule("daily", 1, null, null, null, 9 * 60, false, "Asia/Novosibirsk");

        var next = TaskRecurrencePolicy.CalculateNextUtc(
            rule,
            new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 9, 21, 2, 0, 0, DateTimeKind.Utc), next);
    }
}
