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
            controlTaskCreatedAt: Now.AddDays(-2));

        Assert.Equal(CaseInactivityControlTransition.RepeatDue, decision.Transition);
        Assert.Equal(1, decision.RepeatLevel);
        Assert.Equal(2, decision.RepeatOffsetDays);
    }

    [Fact]
    public void Evaluate_MakesFirstRepeatCriticalAtEscalationThreshold()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-30),
            controlTaskCreatedAt: Now.AddDays(-2));

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
            firstRepeatCompleted: true);

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
    public void Evaluate_WaitsAfterAllRepeatLevelsAreCompleted()
    {
        var decision = Evaluate(
            lastActivityAt: Now.AddDays(-20),
            controlTaskCreatedAt: Now.AddDays(-10),
            firstRepeatCompleted: true,
            secondRepeatCompleted: true);

        Assert.Equal(CaseInactivityControlTransition.Waiting, decision.Transition);
    }

    private static CaseInactivityControlStateDecision Evaluate(
        DateTime lastActivityAt,
        DateTime? controlTaskCreatedAt = null,
        bool hasOtherActiveTask = false,
        bool firstRepeatCompleted = false,
        bool secondRepeatCompleted = false)
        => CaseInactivityControlStateMachine.Evaluate(
            new CaseInactivityControlStateInput(
                Now,
                lastActivityAt,
                controlTaskCreatedAt,
                InactiveDays: 14,
                EscalationDays: 30,
                HasOtherActiveTask: hasOtherActiveTask,
                FirstRepeatCompleted: firstRepeatCompleted,
                SecondRepeatCompleted: secondRepeatCompleted));
}
