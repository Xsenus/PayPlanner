namespace PayPlanner.Api.Models;

public static class InvoiceWorkflowStatus
{
    public const string Draft = "Draft";
    public const string NotSigned = "NotSigned";
    public const string RequiresClientSignature = "RequiresClientSignature";
    public const string OnSigning = "OnSigning";
    public const string SignedByClient = "SignedByClient";
    public const string Paid = "Paid";
    public const string PartiallyPaid = "PartiallyPaid";
    public const string Overdue = "Overdue";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft,
        NotSigned,
        RequiresClientSignature,
        OnSigning,
        SignedByClient,
        Paid,
        PartiallyPaid,
        Overdue,
        Cancelled,
    };

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
        => Normalize(value) is NotSigned or RequiresClientSignature or OnSigning or SignedByClient;

    public static string FromPaymentStatus(PaymentStatus status, decimal paidAmount = 0m, decimal amount = 0m)
    {
        if (paidAmount > 0m && paidAmount < amount)
        {
            return PartiallyPaid;
        }

        return status switch
        {
            PaymentStatus.Completed => Paid,
            PaymentStatus.Overdue => Overdue,
            PaymentStatus.Cancelled => Cancelled,
            PaymentStatus.Processing => OnSigning,
            _ => Draft,
        };
    }
}
