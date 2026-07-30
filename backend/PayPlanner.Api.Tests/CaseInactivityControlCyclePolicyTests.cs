using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseInactivityControlCyclePolicyTests
{
    private static readonly DateTime Now =
        new(2026, 7, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EvaluateNextCycle_WaitsFullIntervalAfterPreviousCycleEnds()
    {
        var decision = CaseInactivityControlCyclePolicy.EvaluateNextCycle(
            Now,
            Now.AddDays(-40),
            Now.AddDays(-13),
            inactiveDays: 14);

        Assert.False(decision.IsEligible);
        Assert.True(decision.WaitsAfterPreviousCycle);
        Assert.Equal(Now.AddDays(1), decision.EligibleAt);
        Assert.Equal(40, decision.DaysWithoutMeaningfulActivity);
    }

    [Fact]
    public void EvaluateNextCycle_AllowsNewCycleAfterCooldown()
    {
        var decision = CaseInactivityControlCyclePolicy.EvaluateNextCycle(
            Now,
            Now.AddDays(-40),
            Now.AddDays(-14),
            inactiveDays: 14);

        Assert.True(decision.IsEligible);
        Assert.Equal(Now, decision.EligibleAt);
        Assert.Equal(40, decision.DaysWithoutMeaningfulActivity);
    }

    [Fact]
    public void EvaluateNextCycle_CountsWeekendInCooldown()
    {
        var friday = new DateTime(2026, 7, 24, 8, 0, 0, DateTimeKind.Utc);
        var sunday = friday.AddDays(2);

        var decision = CaseInactivityControlCyclePolicy.EvaluateNextCycle(
            sunday,
            friday.AddDays(-20),
            friday,
            inactiveDays: 2);

        Assert.True(decision.IsEligible);
        Assert.Equal(sunday, decision.EligibleAt);
    }

    [Fact]
    public void EvaluateNextCycle_UsesLaterMeaningfulActivityInsteadOfOldCycleEnd()
    {
        var decision = CaseInactivityControlCyclePolicy.EvaluateNextCycle(
            Now,
            Now.AddDays(-3),
            Now.AddDays(-20),
            inactiveDays: 14);

        Assert.False(decision.IsEligible);
        Assert.False(decision.WaitsAfterPreviousCycle);
        Assert.Equal(Now.AddDays(11), decision.EligibleAt);
        Assert.Equal(3, decision.DaysWithoutMeaningfulActivity);
    }
}
