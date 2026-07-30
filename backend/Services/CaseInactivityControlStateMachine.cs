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
    int CompletedRepeatLevel = 0,
    DateTime? NextCheckAt = null);

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
    public static readonly TimeSpan MaximumTimelyEscalationDelay = TimeSpan.FromHours(36);

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

        if (!input.NextCheckAt.HasValue || input.Now < input.NextCheckAt.Value)
        {
            return Decision(CaseInactivityControlTransition.Waiting, daysWithoutActivity);
        }

        var nextRepeatLevel = Math.Clamp(input.CompletedRepeatLevel + 1, 1, 3);
        if (nextRepeatLevel == 1)
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

        if (nextRepeatLevel == 2)
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

    public static DateTime? ResolveRecoveryNextCheckAt(DateTime now, DateTime? nextCheckAt)
    {
        if (!nextCheckAt.HasValue ||
            now <= nextCheckAt.Value ||
            now - nextCheckAt.Value <= MaximumTimelyEscalationDelay)
        {
            return null;
        }

        return now.AddDays(FirstRepeatOffsetDays);
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
