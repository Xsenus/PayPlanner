using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using System.Reflection;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Cases;

// A transport-only model; it is never attached to the database context.
public sealed class CaseEditRequest : ClientCase
{
    [ValidateNever]
    public ClientCase? ExpectedSource { get; init; }

    public new DateTime? CourtRegisteredAt
    {
        get => base.CourtRegisteredAt;
        set { base.CourtRegisteredAt = value; HasCourtRegisteredAt = true; }
    }
    public new DateTime? CourtDecisionAt
    {
        get => base.CourtDecisionAt;
        set { base.CourtDecisionAt = value; HasCourtDecisionAt = true; }
    }
    public new DateTime? CassationDeadlineAt
    {
        get => base.CassationDeadlineAt;
        set { base.CassationDeadlineAt = value; HasCassationDeadlineAt = true; }
    }
    internal bool HasCourtRegisteredAt { get; private set; }
    internal bool HasCourtDecisionAt { get; private set; }
    internal bool HasCassationDeadlineAt { get; private set; }

    internal static bool IncludesLegacyDate(ClientCase model, string field) => model is CaseEditRequest request
        ? field switch
        {
            nameof(ClientCase.CourtRegisteredAt) => request.HasCourtRegisteredAt || request.ClearCourtRegisteredAt,
            nameof(ClientCase.CourtDecisionAt) => request.HasCourtDecisionAt,
            nameof(ClientCase.CassationDeadlineAt) => request.HasCassationDeadlineAt,
            _ => false,
        }
        : false;
}

internal static class CaseEditSnapshot
{
    // Only fields ordinary editing can overwrite. Activity/view stamps and other
    // automatic metadata must not make an otherwise current editor stale.
    private static readonly string[] Fields =
    [
        nameof(ClientCase.Id),
        nameof(ClientCase.Title),
        nameof(ClientCase.Description),
        nameof(ClientCase.Status),
        nameof(ClientCase.ClientId),
        nameof(ClientCase.LegalEntityId),
        nameof(ClientCase.IsUnderControl),
        nameof(ClientCase.CaseNumber),
        nameof(ClientCase.CaseType),
        nameof(ClientCase.Stage),
        nameof(ClientCase.ResponsibleUserId),
        nameof(ClientCase.StartDate),
        nameof(ClientCase.DueDate),
        nameof(ClientCase.FeeAmount),
        nameof(ClientCase.Priority),
        nameof(ClientCase.CourtName),
        nameof(ClientCase.OurCourtRole),
        nameof(ClientCase.CourtClaimAmount),
        nameof(ClientCase.CourtAwardedAmount),
        nameof(ClientCase.CourtStateFeeAmount),
        nameof(ClientCase.CourtCaseNumber),
        nameof(ClientCase.JudicialUid),
        nameof(ClientCase.CourtUrl),
        nameof(ClientCase.NextHearingAt),
        nameof(ClientCase.Claimant),
        nameof(ClientCase.ClaimantInn),
        nameof(ClientCase.ClaimantOgrn),
        nameof(ClientCase.Defendant),
        nameof(ClientCase.DefendantInn),
        nameof(ClientCase.DefendantOgrn),
        nameof(ClientCase.ThirdParties),
        nameof(ClientCase.Representatives),
        nameof(ClientCase.JudgeName),
        nameof(ClientCase.Courtroom),
        nameof(ClientCase.CourtSecretaryContacts),
        nameof(ClientCase.ClaimFiledAt),
        nameof(ClientCase.CourtRegisteredAt),
        nameof(ClientCase.CourtDecisionAt),
        nameof(ClientCase.CourtDecisionEffectiveAt),
        nameof(ClientCase.ExecutiveWritReceivedAt),
        nameof(ClientCase.ExecutiveWritSubmittedAt),
        nameof(ClientCase.ExecutiveWritSubmittedTo),
        nameof(ClientCase.AppealDeadlineAt),
        nameof(ClientCase.CassationDeadlineAt),
        nameof(ClientCase.MotionDeadlineAt),
        nameof(ClientCase.ObjectionDeadlineAt),
        nameof(ClientCase.DocumentsSummary),
        nameof(ClientCase.EventHistory),
        nameof(ClientCase.Strategy),
        nameof(ClientCase.InactivityRemindersEnabled),
        nameof(ClientCase.InactivityRemindersDisabledReason),
        nameof(ClientCase.IsHiddenFromExecutor),
    ];

    private static readonly PropertyInfo[] Properties = Fields.Where(name => name != nameof(ClientCase.Id))
        .Select(name => typeof(ClientCase).GetProperty(name)!).ToArray();
    private static readonly HashSet<string> LegacyFields =
    [
        nameof(ClientCase.AppealDeadlineAt),
        nameof(ClientCase.CaseNumber),
        nameof(ClientCase.CaseType),
        nameof(ClientCase.ClaimFiledAt),
        nameof(ClientCase.Claimant),
        nameof(ClientCase.ClaimantInn),
        nameof(ClientCase.ClaimantOgrn),
        nameof(ClientCase.ClientId),
        nameof(ClientCase.CourtAwardedAmount),
        nameof(ClientCase.CourtCaseNumber),
        nameof(ClientCase.CourtClaimAmount),
        nameof(ClientCase.CourtDecisionEffectiveAt),
        nameof(ClientCase.CourtName),
        nameof(ClientCase.CourtSecretaryContacts),
        nameof(ClientCase.CourtStateFeeAmount),
        nameof(ClientCase.CourtUrl),
        nameof(ClientCase.Courtroom),
        nameof(ClientCase.Defendant),
        nameof(ClientCase.DefendantInn),
        nameof(ClientCase.DefendantOgrn),
        nameof(ClientCase.Description),
        nameof(ClientCase.DocumentsSummary),
        nameof(ClientCase.DueDate),
        nameof(ClientCase.EventHistory),
        nameof(ClientCase.ExecutiveWritReceivedAt),
        nameof(ClientCase.ExecutiveWritSubmittedAt),
        nameof(ClientCase.ExecutiveWritSubmittedTo),
        nameof(ClientCase.FeeAmount),
        nameof(ClientCase.JudgeName),
        nameof(ClientCase.JudicialUid),
        nameof(ClientCase.LegalEntityId),
        nameof(ClientCase.MotionDeadlineAt),
        nameof(ClientCase.NextHearingAt),
        nameof(ClientCase.ObjectionDeadlineAt),
        nameof(ClientCase.OurCourtRole),
        nameof(ClientCase.Priority),
        nameof(ClientCase.Representatives),
        nameof(ClientCase.ResponsibleUserId),
        nameof(ClientCase.Stage),
        nameof(ClientCase.StartDate),
        nameof(ClientCase.Status),
        nameof(ClientCase.Strategy),
        nameof(ClientCase.ThirdParties),
        nameof(ClientCase.Title),
        nameof(ClientCase.CourtRegisteredAt),
        nameof(ClientCase.CourtDecisionAt),
        nameof(ClientCase.CassationDeadlineAt),
    ];

    public static bool TryMerge(ClientCase current, ClientCase requested, ClientCase original, bool compareFeeAmount, bool legacy)
    {
        if (original.Id != current.Id) return false;
        if (current.ClientId != original.ClientId && requested.ClientId != current.ClientId) return false;
        if (current.LegalEntityId != original.LegalEntityId && requested.LegalEntityId != current.LegalEntityId) return false;
        var replacements = new List<(PropertyInfo Property, object? Value)>();
        var redundant = ClearGroups.Where(group => (bool)group.Flag.GetValue(requested)!
            && group.Fields.All(field => Empty(field.GetValue(original)))).ToArray();
        var redundantFields = redundant.SelectMany(group => group.Fields).Select(field => field.Name).ToHashSet();
        foreach (var property in Properties)
        {
            if (legacy && !LegacyFields.Contains(property.Name)) continue;
            var actual = property.GetValue(current);
            if (legacy && (property.Name is nameof(ClientCase.CourtRegisteredAt) or nameof(ClientCase.CourtDecisionAt) or nameof(ClientCase.CassationDeadlineAt))
                && !CaseEditRequest.IncludesLegacyDate(requested, property.Name))
            {
                replacements.Add((property, actual));
                continue;
            }
            var wanted = property.GetValue(requested);
            var before = property.GetValue(original);
            if ((!compareFeeAmount && property.Name == nameof(ClientCase.FeeAmount))
                || ((!ClearsField(requested, property.Name) || redundantFields.Contains(property.Name))
                    && (Same(wanted, before) || (redundantFields.Contains(property.Name) && Empty(wanted) && Empty(before)))))
                replacements.Add((property, actual));
            else if (!Same(actual, before) && !Same(actual, ClearsField(requested, property.Name) ? null : wanted)) return false;
        }
        foreach (var (property, value) in replacements) property.SetValue(requested, value);
        foreach (var group in redundant) group.Flag.SetValue(requested, false);
        return true;
    }

    private static bool Empty(object? value) => value is null or "";
    private static readonly (PropertyInfo Flag, PropertyInfo[] Fields)[] ClearGroups =
    [
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearCourtClaimAmount))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.CourtClaimAmount))!]),
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearCourtAwardedAmount))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.CourtAwardedAmount))!]),
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearCourtStateFeeAmount))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.CourtStateFeeAmount))!]),
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearCourtRegisteredAt))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.CourtRegisteredAt))!]),
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearCourtDecisionEffectiveAt))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.CourtDecisionEffectiveAt))!]),
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearExecutiveWritReceivedAt))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.ExecutiveWritReceivedAt))!]),
        (typeof(ClientCase).GetProperty(nameof(ClientCase.ClearExecutiveWritSubmission))!, [typeof(ClientCase).GetProperty(nameof(ClientCase.ExecutiveWritSubmittedAt))!, typeof(ClientCase).GetProperty(nameof(ClientCase.ExecutiveWritSubmittedTo))!]),
    ];

    private static bool Same(object? first, object? second)
        => first is DateTime actual && second is DateTime original
            ? CalendarTimeZoneHelper.NormalizeUtcInstant(actual) == CalendarTimeZoneHelper.NormalizeUtcInstant(original)
            : Equals(first, second);

    private static bool ClearsField(ClientCase model, string name) => name switch
    {
        nameof(ClientCase.CourtClaimAmount) => model.ClearCourtClaimAmount,
        nameof(ClientCase.CourtAwardedAmount) => model.ClearCourtAwardedAmount,
        nameof(ClientCase.CourtStateFeeAmount) => model.ClearCourtStateFeeAmount,
        nameof(ClientCase.CourtRegisteredAt) => model.ClearCourtRegisteredAt,
        nameof(ClientCase.CourtDecisionEffectiveAt) => model.ClearCourtDecisionEffectiveAt,
        nameof(ClientCase.ExecutiveWritReceivedAt) => model.ClearExecutiveWritReceivedAt,
        nameof(ClientCase.ExecutiveWritSubmittedAt) or nameof(ClientCase.ExecutiveWritSubmittedTo) => model.ClearExecutiveWritSubmission,
        _ => false,
    };
}
