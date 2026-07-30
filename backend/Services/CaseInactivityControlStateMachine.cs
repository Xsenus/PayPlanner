namespace PayPlanner.Api.Services;

public enum CaseInactivityControlTransition
{
    Waiting,
    FirstControlDue,
    RepeatDue,
    CriticalEscalationDue,
    Reset,
    SuppressedByActiveTask,
}

public readonly record struct CaseInactivityControlStateInput(
    DateTime Now,
    DateTime LastMeaningfulActivityAt,
    DateTime? ControlTaskCreatedAt,
    int InactiveDays,
    int EscalationDays,
    bool HasOtherActiveTask = false,
    bool FirstRepeatCompleted = false,
    bool SecondRepeatCompleted = false);

public readonly record struct CaseInactivityControlStateDecision(
    CaseInactivityControlTransition Transition,
    int DaysWithoutActivity,
    int RepeatLevel = 0,
    int? RepeatOffsetDays = null,
    bool IsCritical = false);

public static class CaseInactivityControlStateMachine
{
    public const int FirstRepeatOffsetDays = 2;
    public const int SecondRepeatOffsetDays = 4;

    public static CaseInactivityControlStateDecision Evaluate(CaseInactivityControlStateInput input)
    {
        var daysWithoutActivity = ElapsedWholeDays(input.Now, input.LastMeaningfulActivityAt);
        if (input.ControlTaskCreatedAt is null)
        {
            if (input.HasOtherActiveTask)
            {
                return Decision(CaseInactivityControlTransition.SuppressedByActiveTask, daysWithoutActivity);
            }

            return daysWithoutActivity >= input.InactiveDays
                ? Decision(
                    CaseInactivityControlTransition.FirstControlDue,
                    daysWithoutActivity,
                    isCritical: daysWithoutActivity >= input.EscalationDays)
                : Decision(CaseInactivityControlTransition.Waiting, daysWithoutActivity);
        }

        var controlTaskCreatedAt = input.ControlTaskCreatedAt.Value;
        if (input.LastMeaningfulActivityAt > controlTaskCreatedAt)
        {
            return Decision(CaseInactivityControlTransition.Reset, daysWithoutActivity);
        }

        if (input.HasOtherActiveTask)
        {
            return Decision(CaseInactivityControlTransition.SuppressedByActiveTask, daysWithoutActivity);
        }

        var daysSinceControlCreated = ElapsedWholeDays(input.Now, controlTaskCreatedAt);
        if (!input.FirstRepeatCompleted && daysSinceControlCreated >= FirstRepeatOffsetDays)
        {
            var isCritical = daysWithoutActivity >= input.EscalationDays;
            return Decision(
                isCritical
                    ? CaseInactivityControlTransition.CriticalEscalationDue
                    : CaseInactivityControlTransition.RepeatDue,
                daysWithoutActivity,
                repeatLevel: 1,
                repeatOffsetDays: FirstRepeatOffsetDays,
                isCritical: isCritical);
        }

        if (!input.SecondRepeatCompleted && daysSinceControlCreated >= SecondRepeatOffsetDays)
        {
            return Decision(
                CaseInactivityControlTransition.CriticalEscalationDue,
                daysWithoutActivity,
                repeatLevel: 2,
                repeatOffsetDays: SecondRepeatOffsetDays,
                isCritical: true);
        }

        return Decision(CaseInactivityControlTransition.Waiting, daysWithoutActivity);
    }

    private static int ElapsedWholeDays(DateTime later, DateTime earlier)
        => Math.Max(0, (int)Math.Floor((later - earlier).TotalDays));

    private static CaseInactivityControlStateDecision Decision(
        CaseInactivityControlTransition transition,
        int daysWithoutActivity,
        int repeatLevel = 0,
        int? repeatOffsetDays = null,
        bool isCritical = false)
        => new(transition, daysWithoutActivity, repeatLevel, repeatOffsetDays, isCritical);
}
