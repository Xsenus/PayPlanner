using System;

namespace PayPlanner.Api.Models;

public static class ActCreationMode
{
    public const string Manual = "Manual";
    public const string Full = "Full";
    public const string Partial = "Partial";

    public static bool IsValid(string? value)
        => string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, Full, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, Partial, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value)
        => string.Equals(value, Full, StringComparison.OrdinalIgnoreCase)
            ? Full
            : string.Equals(value, Partial, StringComparison.OrdinalIgnoreCase)
                ? Partial
                : Manual;
}
