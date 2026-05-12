namespace PayPlanner.Api.Models;

public static class ContractStatus
{
    public const string Draft = DocumentWorkflowStatus.Draft;
    public const string NotSigned = DocumentWorkflowStatus.NotSigned;
    public const string RequiresClientSignature = DocumentWorkflowStatus.RequiresClientSignature;
    public const string OnApproval = DocumentWorkflowStatus.OnApproval;
    public const string Approved = DocumentWorkflowStatus.Approved;
    public const string ApprovalRejected = DocumentWorkflowStatus.ApprovalRejected;
    public const string NeedsCorrection = DocumentWorkflowStatus.NeedsCorrection;
    public const string OnSigning = DocumentWorkflowStatus.OnSigning;
    public const string Concluded = DocumentWorkflowStatus.Concluded;
    public const string Terminated = DocumentWorkflowStatus.Terminated;
    public const string Cancelled = DocumentWorkflowStatus.Cancelled;

    public static readonly IReadOnlySet<string> All = DocumentWorkflowStatus.ContractCodes;

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
}
