namespace PayPlanner.Api.Models;

public static class InvoiceWorkflowStatus
{
    public const string Draft = DocumentWorkflowStatus.Draft;
    public const string NotSigned = DocumentWorkflowStatus.NotSigned;
    public const string RequiresClientSignature = DocumentWorkflowStatus.RequiresClientSignature;
    public const string OnApproval = DocumentWorkflowStatus.OnApproval;
    public const string Approved = DocumentWorkflowStatus.Approved;
    public const string ApprovalRejected = DocumentWorkflowStatus.ApprovalRejected;
    public const string NeedsCorrection = DocumentWorkflowStatus.NeedsCorrection;
    public const string OnSigning = DocumentWorkflowStatus.OnSigning;
    public const string SignedByClient = DocumentWorkflowStatus.SignedByClient;
    public const string Paid = DocumentWorkflowStatus.Paid;
    public const string PartiallyPaid = DocumentWorkflowStatus.PartiallyPaid;
    public const string Overdue = DocumentWorkflowStatus.Overdue;
    public const string Cancelled = DocumentWorkflowStatus.Cancelled;

    public static readonly IReadOnlySet<string> All = DocumentWorkflowStatus.InvoiceCodes;

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

    public static bool IsDocumentSigningStatus(string? value)
        => Normalize(value) is NotSigned or RequiresClientSignature or OnApproval or Approved or ApprovalRejected or NeedsCorrection or OnSigning or SignedByClient;

    public static string FromPaymentStatus(PaymentStatus status, decimal paidAmount = 0m, decimal amount = 0m)
    {
        return status switch
        {
            PaymentStatus.Cancelled => Cancelled,
            _ => Draft,
        };
    }
}
