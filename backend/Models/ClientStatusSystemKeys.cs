namespace PayPlanner.Api.Models;

public static class ClientStatusSystemKeys
{
    public const string Client = "client";
    public const string Supplier = "supplier";
    public const string Creditor = "creditor";

    public static bool IsSupplierLike(string? systemKey)
        => string.Equals(systemKey, Supplier, StringComparison.OrdinalIgnoreCase)
           || string.Equals(systemKey, Creditor, StringComparison.OrdinalIgnoreCase);

    public static bool IsClientLike(string? systemKey)
        => string.Equals(systemKey, Client, StringComparison.OrdinalIgnoreCase);
}
