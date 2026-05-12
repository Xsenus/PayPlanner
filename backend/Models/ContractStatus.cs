namespace PayPlanner.Api.Models;

public static class ContractStatus
{
    public const string Draft = "Draft";
    public const string NotSigned = "NotSigned";
    public const string OnApproval = "OnApproval";
    public const string Approved = "Approved";
    public const string ApprovalRejected = "ApprovalRejected";
    public const string NeedsCorrection = "NeedsCorrection";
    public const string OnSigning = "OnSigning";
    public const string Concluded = "Concluded";
    public const string Terminated = "Terminated";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft,
        NotSigned,
        OnApproval,
        Approved,
        ApprovalRejected,
        NeedsCorrection,
        OnSigning,
        Concluded,
        Terminated,
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
}
