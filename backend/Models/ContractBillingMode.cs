using System;

namespace PayPlanner.Api.Models;

public static class ContractBillingMode
{
    public const string Fixed = "Fixed";
    public const string Actual = "Actual";

    public static bool IsValid(string? value)
        => string.Equals(value, Fixed, StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, Actual, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value)
        => string.Equals(value, Actual, StringComparison.OrdinalIgnoreCase)
            ? Actual
            : Fixed;
}
