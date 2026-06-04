using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ContractDomainService
{
    public static void Normalize(Contract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        contract.Date = NormalizeLocalDate(contract.Date);
        contract.StartDate = NormalizeLocalDate(contract.StartDate);
        contract.ValidUntil = NormalizeLocalDate(contract.ValidUntil);
    }

    public static DateTime NormalizeLocalDate(DateTime value)
        => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    public static DateTime? NormalizeLocalDate(DateTime? value)
        => value.HasValue ? NormalizeLocalDate(value.Value) : null;
}
