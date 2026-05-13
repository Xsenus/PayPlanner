namespace PayPlanner.Api.Models;

public static class DataVisibilityScope
{
    public const string Own = "own";
    public const string Department = "department";
    public const string All = "all";

    public static readonly string[] AllValues =
    [
        Own,
        Department,
        All
    ];

    public static bool IsSupported(string? value)
        => AllValues.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? value, string fallback = Own)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return IsSupported(normalized) ? normalized : fallback;
    }
}
