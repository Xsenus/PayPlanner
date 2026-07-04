using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class PaymentDisplayNameService
{
    public static void FillCounterpartyNames(IEnumerable<Payment> payments)
    {
        foreach (var payment in payments)
        {
            payment.CounterpartyName = ResolveCounterpartyName(payment);
        }
    }

    public static string? ResolveCounterpartyName(Payment payment)
    {
        var storedName = Normalize(payment.CounterpartyName);
        if (storedName is not null)
        {
            return storedName;
        }

        if (payment.Type == PaymentType.Expense)
        {
            return Normalize(payment.Client?.Name)
                ?? ResolveLegalEntityName(payment.LegalEntity)
                ?? Normalize(payment.ExpenseClient?.Name)
                ?? ResolveLegalEntityName(payment.ExpenseLegalEntity);
        }

        return Normalize(payment.Client?.Name)
            ?? ResolveLegalEntityName(payment.LegalEntity);
    }

    private static string? ResolveLegalEntityName(LegalEntity? legalEntity)
        => legalEntity is null
            ? null
            : Normalize(legalEntity.ShortName) ?? Normalize(legalEntity.FullName);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
