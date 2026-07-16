namespace PayPlanner.Api.Models;

public static class TaskClientBillingClassification
{
    public const string Legacy = "legacy";
    public const string Billable = "billable";
    public const string Internal = "internal";

    public static bool IsDecision(string? value)
        => string.Equals(value, Billable, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, Internal, StringComparison.OrdinalIgnoreCase);

    public static string NormalizeDecision(string? value)
        => string.Equals(value, Billable, StringComparison.OrdinalIgnoreCase)
            ? Billable
            : Internal;
}
