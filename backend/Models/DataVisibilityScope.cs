namespace PayPlanner.Api.Models;

public static class DataVisibilityScope
{
    public const string Own = "own";
    public const string Department = "department";
    public const string All = "all";
    public const string Specific = "specific";

    public static readonly string[] AllValues =
    [
        Own,
        Department,
        All,
        Specific
    ];

    public static bool IsSupported(string? value, bool includeSpecific = false)
    {
        if (!includeSpecific && string.Equals(value, Specific, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return AllValues.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string? value, string fallback = Own, bool includeSpecific = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return IsSupported(normalized, includeSpecific) ? normalized : fallback;
    }
}
