using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseInactivityAutomationSchedulePolicyTests
{
    private static readonly TimeZoneInfo Moscow =
        CalendarTimeZoneHelper.ResolveTimeZone("Europe/Moscow");

    [Fact]
    public void Evaluate_RunsInsideConfiguredLocalWindow()
    {
        var nowUtc = new DateTime(2026, 7, 30, 6, 5, 0, DateTimeKind.Utc);

        var decision = CaseInactivityAutomationSchedulePolicy.Evaluate(
            nowUtc,
            new TimeOnly(9, 0),
            Moscow,
            lastRunLocalDate: null);

        Assert.True(decision.ShouldRun);
        Assert.Equal(new DateOnly(2026, 7, 30), decision.LocalDate);
    }

    [Fact]
    public void Evaluate_DoesNotRepeatWithinSameLocalDay()
    {
        var nowUtc = new DateTime(2026, 7, 30, 6, 10, 0, DateTimeKind.Utc);

        var decision = CaseInactivityAutomationSchedulePolicy.Evaluate(
            nowUtc,
            new TimeOnly(9, 0),
            Moscow,
            new DateOnly(2026, 7, 30));

        Assert.False(decision.ShouldRun);
    }

    [Fact]
    public void Evaluate_DoesNotCatchUpMissedWindowAfterRestart()
    {
        var nowUtc = new DateTime(2026, 7, 30, 8, 0, 0, DateTimeKind.Utc);

        var decision = CaseInactivityAutomationSchedulePolicy.Evaluate(
            nowUtc,
            new TimeOnly(9, 0),
            Moscow,
            lastRunLocalDate: null);

        Assert.False(decision.ShouldRun);
        Assert.Equal(
            new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc),
            decision.NextRunAtUtc);
    }

    [Fact]
    public void Evaluate_UsesConfiguredTimeZone()
    {
        var novosibirsk = CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk");
        var nowUtc = new DateTime(2026, 7, 30, 2, 5, 0, DateTimeKind.Utc);

        var novosibirskDecision = CaseInactivityAutomationSchedulePolicy.Evaluate(
            nowUtc,
            new TimeOnly(9, 0),
            novosibirsk,
            lastRunLocalDate: null);
        var moscowDecision = CaseInactivityAutomationSchedulePolicy.Evaluate(
            nowUtc,
            new TimeOnly(9, 0),
            Moscow,
            lastRunLocalDate: null);

        Assert.True(novosibirskDecision.ShouldRun);
        Assert.False(moscowDecision.ShouldRun);
    }

    [Fact]
    public void ResolveLastRunLocalDate_UsesConfiguredLocalDateAcrossUtcBoundary()
    {
        var timeZone = CalendarTimeZoneHelper.ResolveTimeZone("Asia/Novosibirsk");
        var lastSuccessAtUtc = new DateTime(2026, 7, 29, 18, 30, 0, DateTimeKind.Utc);

        var localDate = CaseInactivityAutomationSchedulePolicy.ResolveLastRunLocalDate(
            lastSuccessAtUtc,
            timeZone);

        Assert.Equal(new DateOnly(2026, 7, 30), localDate);
    }
}
