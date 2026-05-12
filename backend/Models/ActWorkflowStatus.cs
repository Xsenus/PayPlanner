namespace PayPlanner.Api.Models;

public static class ActWorkflowStatus
{
    public const string Draft = "Draft";
    public const string NotSigned = "NotSigned";
    public const string OnSigning = "OnSigning";
    public const string Signed = "Signed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Draft,
        NotSigned,
        OnSigning,
        Signed,
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

    public static string FromActStatus(ActStatus status)
        => status switch
        {
            ActStatus.Transferred => OnSigning,
            ActStatus.Signed => Signed,
            ActStatus.Terminated => Cancelled,
            _ => Draft,
        };
}
