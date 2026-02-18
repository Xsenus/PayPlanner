namespace PayPlanner.Api.Services.LegalEntities
{
    public interface ILegalEntityEnrichmentService
    {
        /// <summary>

        Task<IReadOnlyList<DadataAddressSuggestion>> SuggestAddressesAsync(string query, int limit,
            CancellationToken cancellationToken);
    public sealed record DadataAddressSuggestion(string Value, string? UnrestrictedValue, string? PostalCode,
        string? FiasId, string? KladrId, string? GeoLat, string? GeoLon);

        /// Ñòàíäàðòèçàöèÿ ïî÷òîâîãî àäðåñà (clean/address).
        /// </summary>
        Task<CleanedAddress?> CleanAddressAsync(string rawAddress, CancellationToken cancellationToken);

        /// <summary>
        /// Ñòàíäàðòèçàöèÿ email (clean/email).
        /// </summary>
        Task<CleanedEmail?> CleanEmailAsync(string rawEmail, CancellationToken cancellationToken);

        /// <summary>
        /// Ñòàíäàðòèçàöèÿ òåëåôîíà (clean/phone).
        /// </summary>
        Task<CleanedPhone?> CleanPhoneAsync(string rawPhone, CancellationToken cancellationToken);

        /// <summary>
        /// Èùåò îðãàíèçàöèþ ïî ÈÍÍ èëè ÎÃÐÍ (îïöèîíàëüíî ôèëèàë ïî ÊÏÏ) è âîçâðàùàåò ðàñøèðåííûå äàííûå.
        /// </summary>
        Task<LegalEntityDetails?> FindByInnOrOgrnAsync(string query, string? kpp, CancellationToken cancellationToken);

        Task<IReadOnlyList<LegalEntitySuggestion>> SuggestAsync(string? query, string? inn, int limit,
            CancellationToken cancellationToken);
    }

    public sealed record LegalEntitySuggestion(string ShortName, string? FullName, string? Inn, string? Kpp,
        string? Ogrn, string? Address, string? Phone, string? Email, string? ManagementName);

    public sealed record LegalEntityDetails(string ShortName, string? FullName, string? Inn, string? Kpp, string? Ogrn,
        string? OpfShort, string? OpfFull, string? Status, DateTimeOffset? RegistrationDate,
        DateTimeOffset? LiquidationDate, string? Okved, string? OkvedType, int? BranchCount, string? ManagementName,
        string? ManagementPost, string? AddressValue, string? AddressUnrestrictedValue);

    public sealed record CleanedAddress(string Result, string? PostalCode, string? Country, string? RegionWithType,
        string? CityWithType, string? StreetWithType, string? House, string? Flat, string? FiasId, string? KladrId,
        string? GeoLat, string? GeoLon);

    public sealed record CleanedPhone(string Phone, string? Country, string? City, string? Provider, int? Qc);

    public sealed record CleanedEmail(string Email, string? Type, int? Qc);
}
