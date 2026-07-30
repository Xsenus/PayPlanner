using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public readonly record struct CaseInactivityNextCycleDecision(
    bool IsEligible,
    DateTime CooldownAnchorAt,
    DateTime EligibleAt,
    int DaysWithoutMeaningfulActivity,
    bool WaitsAfterPreviousCycle);

public static class CaseInactivityControlCyclePolicy
{
    public static readonly ClientCaseStatus[] ExcludedCaseStatuses =
    [
        ClientCaseStatus.OnHold,
        ClientCaseStatus.Closed,
        ClientCaseStatus.Lost,
    ];

    public static CaseInactivityNextCycleDecision EvaluateNextCycle(
        DateTime now,
        DateTime lastMeaningfulActivityAt,
        DateTime? lastControlCycleEndedAt,
        int inactiveDays)
    {
        var normalizedInactiveDays = Math.Max(1, inactiveDays);
        var waitsAfterPreviousCycle =
            lastControlCycleEndedAt.HasValue &&
            lastControlCycleEndedAt.Value > lastMeaningfulActivityAt;
        var cooldownAnchorAt = waitsAfterPreviousCycle
            ? lastControlCycleEndedAt!.Value
            : lastMeaningfulActivityAt;
        var eligibleAt = cooldownAnchorAt.AddDays(normalizedInactiveDays);
        var daysWithoutMeaningfulActivity = Math.Max(
            0,
            (int)Math.Floor((now - lastMeaningfulActivityAt).TotalDays));

        return new CaseInactivityNextCycleDecision(
            now >= eligibleAt,
            cooldownAnchorAt,
            eligibleAt,
            daysWithoutMeaningfulActivity,
            waitsAfterPreviousCycle);
    }
}
