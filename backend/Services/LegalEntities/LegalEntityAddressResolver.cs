using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.LegalEntities;

public static class LegalEntityAddressResolver
{
    public static string ResolvePreferredAddress(LegalEntity? legalEntity)
        => legalEntity is null
            ? string.Empty
            : ResolvePreferredAddress(legalEntity.Addresses, legalEntity.Address);

    public static string ResolvePreferredAddress(IEnumerable<AddressRecord>? addresses, string? legacyAddress = null)
    {
        if (addresses is not null)
        {
            var preferredAddress = addresses
                .OrderByDescending(address => address.IsPrimary)
                .ThenBy(address => GetTypePriority(address.Type))
                .ThenByDescending(address => address.UpdatedAt ?? address.CreatedAt)
                .ThenBy(address => address.Id)
                .Select(ResolvePreferredAddress)
                .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address));

            if (!string.IsNullOrWhiteSpace(preferredAddress))
            {
                return preferredAddress;
            }
        }

        return Normalize(legacyAddress);
    }

    public static string ResolvePreferredAddress(AddressRecord? address)
        => address is null
            ? string.Empty
            : FirstNotEmpty(address.UnrestrictedValue, address.Value);

    private static int GetTypePriority(AddressType type)
        => type switch
        {
            AddressType.Legal => 0,
            AddressType.Actual => 1,
            AddressType.Registration => 2,
            _ => 3,
        };

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
