namespace PayPlanner.Api.Services;

public static class CaseMeaningfulActivitySources
{
    public const string CaseCreated = "caseCreated";
    public const string Task = "task";
    public const string TaskComment = "taskComment";
    public const string Document = "document";
    public const string CourtEvent = "courtEvent";
    public const string ResponsibleView = "responsibleView";
    public const string ActivityConfirmation = "activityConfirmation";
}

public readonly record struct CaseMeaningfulActivitySignals(
    DateTime CaseCreatedAt,
    DateTime? LastTaskAt = null,
    DateTime? LastTaskCommentAt = null,
    DateTime? LastDocumentAt = null,
    DateTime? LastCourtEventAt = null,
    DateTime? LastResponsibleViewAt = null,
    DateTime? LastActivityConfirmationAt = null);

public readonly record struct CaseMeaningfulActivityResult(
    DateTime At,
    string Source);

public static class CaseMeaningfulActivityResolver
{
    public static CaseMeaningfulActivityResult Resolve(CaseMeaningfulActivitySignals signals)
    {
        var result = new CaseMeaningfulActivityResult(
            signals.CaseCreatedAt,
            CaseMeaningfulActivitySources.CaseCreated);

        result = SelectLater(
            result,
            signals.LastTaskAt,
            CaseMeaningfulActivitySources.Task);
        result = SelectLater(
            result,
            signals.LastTaskCommentAt,
            CaseMeaningfulActivitySources.TaskComment);
        result = SelectLater(
            result,
            signals.LastDocumentAt,
            CaseMeaningfulActivitySources.Document);
        result = SelectLater(
            result,
            signals.LastCourtEventAt,
            CaseMeaningfulActivitySources.CourtEvent);
        result = SelectLater(
            result,
            signals.LastResponsibleViewAt,
            CaseMeaningfulActivitySources.ResponsibleView);
        return SelectLater(
            result,
            signals.LastActivityConfirmationAt,
            CaseMeaningfulActivitySources.ActivityConfirmation);
    }

    private static CaseMeaningfulActivityResult SelectLater(
        CaseMeaningfulActivityResult current,
        DateTime? candidateAt,
        string candidateSource)
        => candidateAt.HasValue && candidateAt.Value > current.At
            ? new CaseMeaningfulActivityResult(candidateAt.Value, candidateSource)
            : current;
}
