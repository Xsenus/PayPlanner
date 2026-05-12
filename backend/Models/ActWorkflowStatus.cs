namespace PayPlanner.Api.Models;

public static class ActWorkflowStatus
{
    public const string Draft = DocumentWorkflowStatus.Draft;
    public const string NotSigned = DocumentWorkflowStatus.NotSigned;
    public const string RequiresClientSignature = DocumentWorkflowStatus.RequiresClientSignature;
    public const string OnApproval = DocumentWorkflowStatus.OnApproval;
    public const string Approved = DocumentWorkflowStatus.Approved;
    public const string ApprovalRejected = DocumentWorkflowStatus.ApprovalRejected;
    public const string NeedsCorrection = DocumentWorkflowStatus.NeedsCorrection;
    public const string OnSigning = DocumentWorkflowStatus.OnSigning;
    public const string Signed = DocumentWorkflowStatus.Signed;
    public const string Cancelled = DocumentWorkflowStatus.Cancelled;

    public static readonly IReadOnlySet<string> All = DocumentWorkflowStatus.ActCodes;

    public static bool IsValid(string? value) => Normalize(value) is not null;

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return All.FirstOrDefault(status => string.Equals(status, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static string FromActStatus(ActStatus status)
        => status switch
        {
            ActStatus.Transferred => OnSigning,
            ActStatus.Signed => Signed,
            ActStatus.Terminated => Cancelled,
            _ => Draft,
        };
}
