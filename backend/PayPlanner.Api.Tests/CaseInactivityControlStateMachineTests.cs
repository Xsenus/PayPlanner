using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseInactivityControlStateMachineTests
{
    private static readonly DateTime Now =
        new(2026, 7, 30, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_WaitsBeforeInitialThreshold()
    {
        var decision = Evaluate(lastActivityAt: Now.AddDays(-13));

        Assert.Equal(CaseInactivityControlTransition.Waiting, decision.Transition);
        Assert.Equal(13, decision.DaysWithoutActivity);
    }

    [Fact]
    public void Evaluate_CreatesFirstControlAtConfiguredThreshold()
    {
        var decision = Evaluate(lastActivityAt: Now.AddDays(-14));

        Assert.Equal(CaseInactivityControlTransition.FirstControlDue, decision.Transition);
        Assert.False(decision.IsCritical);
    }

    [Theory]
    [InlineData(13, 23, 59, CaseInactivityControlTransition.Waiting)]
    [InlineData(14, 0, 0, CaseInactivityControlTransition.FirstControlDue)]
    public void Evaluate_UsesCompletedTwentyFourHourPeriodsAtInitialBoundary(
        int days,
        int hours,
        int minutes,
        CaseInactivityControlTransition expected)
    {
        var decision = Evaluate(lastActivityAt: Now.AddDays(-days).AddHours(-hours).AddMinutes(-minutes));

        Assert.Equal(expected, decision.Transition);
        Assert.Equal(days, decision.DaysWithoutActivity);
    }

    [Fact]
    public void Evaluate_ClampsFutureActivityToZeroDays()
    {
        var decision = Evaluate(lastActivityAt: Now.AddHours(1));

        Assert.Equal(CaseInactivityControlTransition.Waiting, decision.Transition);
        Assert.Equal(0, decision.DaysWithoutActivity);
    }

    [Fact]
    public void Evaluate_CountsWeekendAsCalendarDays()
    {
        var friday = new DateTime(2026, 7, 24, 8, 0, 0, DateTimeKind.Utc);
        var sunday = friday.AddDays(2);
        Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);

        var decision = CaseInactivityControlStateMachine.Evaluate(
            new CaseInactivityControlStateInput(
                sunday,
                friday,
                ControlTaskCreatedAt: null,
                InactiveDays: 2,
                EscalationDays: 30));

        Assert.Equal(CaseInactivityControlTransition.FirstControlDue, decision.Transition);
        Assert.Equal(2, decision.DaysWithoutActivity);
    }

    [Fact]
    public void Evaluate_MarksFirstControlCriticalAtEscalationThreshold()
    {
        var decision = Evaluate(lastActivityAt: Now.AddDays(-30));

        Assert.Equal(CaseInactivityControlTransition.FirstControlDue, decision.Transition);
        Assert.True(decision.IsCritical);
    }

    [Fact]
    public void Evaluate_ProducesFirstRepeatAfterTwoDays()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-20),
            controlTaskCreatedAt: Now.AddDays(-2),
            nextCheckAt: Now);

        Assert.Equal(CaseInactivityControlTransition.RepeatDue, decision.Transition);
        Assert.Equal(1, decision.RepeatLevel);
        Assert.Equal(2, decision.RepeatOffsetDays);
    }

    [Fact]
    public void Evaluate_DoesNotResetWhenActivityEqualsControlCreationTime()
    {
        var createdAt = Now.AddDays(-2);
        var decision = Evaluate(
            lastActivityAt: createdAt,
            controlTaskCreatedAt: createdAt,
            nextCheckAt: Now);

        Assert.Equal(CaseInactivityControlTransition.RepeatDue, decision.Transition);
        Assert.Equal(1, decision.RepeatLevel);
    }

    [Fact]
    public void Evaluate_MakesFirstRepeatCriticalAtEscalationThreshold()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-30),
            controlTaskCreatedAt: Now.AddDays(-2),
            nextCheckAt: Now);

        Assert.Equal(CaseInactivityControlTransition.CriticalEscalationDue, decision.Transition);
        Assert.Equal(1, decision.RepeatLevel);
        Assert.True(decision.IsCritical);
    }

    [Fact]
    public void Evaluate_ProducesCriticalSecondRepeatAfterFourDays()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-20),
            controlTaskCreatedAt: Now.AddDays(-4),
            completedRepeatLevel: 1,
            nextCheckAt: Now);

        Assert.Equal(CaseInactivityControlTransition.CriticalEscalationDue, decision.Transition);
        Assert.Equal(2, decision.RepeatLevel);
        Assert.Equal(4, decision.RepeatOffsetDays);
        Assert.True(decision.IsCritical);
    }

    [Fact]
    public void Evaluate_ResetsCycleAfterMeaningfulActivity()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddHours(-1),
            controlTaskCreatedAt: Now.AddDays(-2));

        Assert.Equal(CaseInactivityControlTransition.Reset, decision.Transition);
    }

    [Fact]
    public void Evaluate_SuppressesControlWhenAnotherActiveTaskExists()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-30),
            hasOtherActiveTask: true);

        Assert.Equal(CaseInactivityControlTransition.SuppressedByActiveTask, decision.Transition);
    }

    [Fact]
    public void Evaluate_SuppressesDueRepeatWhenAnotherActiveTaskExists()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-30),
            controlTaskCreatedAt: Now.AddDays(-4),
            hasOtherActiveTask: true,
            completedRepeatLevel: 1,
            nextCheckAt: Now);

        Assert.Equal(CaseInactivityControlTransition.SuppressedByActiveTask, decision.Transition);
    }

    [Fact]
    public void Evaluate_WaitsAfterAllRepeatLevelsAreCompleted()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-20),
            controlTaskCreatedAt: Now.AddDays(-10),
            completedRepeatLevel: 2,
            nextCheckAt: null);

        Assert.Equal(CaseInactivityControlTransition.Waiting, decision.Transition);
    }

    [Fact]
    public void Evaluate_WaitsUntilStoredNextCheckAt()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-20),
            controlTaskCreatedAt: Now.AddDays(-10),
            nextCheckAt: Now.AddMinutes(1));

        Assert.Equal(CaseInactivityControlTransition.Waiting, decision.Transition);
    }

    [Fact]
    public void Evaluate_WaitsAfterCompletedRepeatsEvenWithStaleStoredDate()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-40),
            controlTaskCreatedAt: Now.AddDays(-10),
            completedRepeatLevel: 2,
            nextCheckAt: Now.AddDays(-1));

        Assert.Equal(CaseInactivityControlTransition.Waiting, decision.Transition);
        Assert.Equal(0, decision.RepeatLevel);
    }

    [Fact]
    public void ResolveRecoveryNextCheckAt_KeepsRecentlyDueEscalation()
    {
        var nextCheckAt = Now.Subtract(
            CaseInactivityControlStateMachine.MaximumTimelyEscalationDelay);

        var recoveryNextCheckAt =
            CaseInactivityControlStateMachine.ResolveRecoveryNextCheckAt(Now, nextCheckAt);

        Assert.Null(recoveryNextCheckAt);
    }

    [Fact]
    public void ResolveRecoveryNextCheckAt_RebasesEscalationMissedDuringLongDowntime()
    {
        var nextCheckAt = Now
            .Subtract(CaseInactivityControlStateMachine.MaximumTimelyEscalationDelay)
            .AddMinutes(-1);

        var recoveryNextCheckAt =
            CaseInactivityControlStateMachine.ResolveRecoveryNextCheckAt(Now, nextCheckAt);

        Assert.Equal(Now.AddDays(2), recoveryNextCheckAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void ResolveRecoveryNextCheckAt_DoesNotRebaseMissingOrFutureDate(int? futureDays)
    {
        var nextCheckAt = futureDays.HasValue ? Now.AddDays(futureDays.Value) : (DateTime?)null;

        var recoveryNextCheckAt =
            CaseInactivityControlStateMachine.ResolveRecoveryNextCheckAt(Now, nextCheckAt);

        Assert.Null(recoveryNextCheckAt);
    }

    private static CaseInactivityControlStateDecision Evaluate(
        DateTime lastActivityAt,
        DateTime? controlTaskCreatedAt = null,
        bool hasOtherActiveTask = false,
        int completedRepeatLevel = 0,
        DateTime? nextCheckAt = null)
        => CaseInactivityControlStateMachine.Evaluate(
            new CaseInactivityControlStateInput(
                Now,
                lastActivityAt,
                controlTaskCreatedAt,
                InactiveDays: 14,
                EscalationDays: 30,
                HasOtherActiveTask: hasOtherActiveTask,
                CompletedRepeatLevel: completedRepeatLevel,
                NextCheckAt: nextCheckAt));
}
