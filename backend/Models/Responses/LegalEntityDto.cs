using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models.Responses;

public class LegalEntityClientDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
}

public class LegalEntityListItemDto
{
    public int Id { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Ogrn { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Director { get; set; }
    public string? Country { get; set; }
    public string? BaseCurrency { get; set; }
    public string? ServiceLanguage { get; set; }
    public string? OrganizationForm { get; set; }
    public string? Timezone { get; set; }
    public string? TaxSystem { get; set; }
    public string? Notes { get; set; }
    public int ClientsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<LegalEntityClientDto> Clients { get; set; } = new();
    public List<AddressRecordDto> Addresses { get; set; } = new();
}

public class LegalEntityDetailDto : LegalEntityListItemDto
{
}

public class LegalEntitySuggestionDto
{
    public string ShortName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Ogrn { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Director { get; set; }
}

public class AddressRecordDto
{
    public int Id { get; set; }
    public int Type { get; set; }
    public string Value { get; set; } = string.Empty;
    public string? UnrestrictedValue { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? RegionWithType { get; set; }
    public string? CityWithType { get; set; }
    public string? StreetWithType { get; set; }
    public string? House { get; set; }
    public string? Flat { get; set; }
    public string? FiasId { get; set; }
    public string? KladrId { get; set; }
    public string? GeoLat { get; set; }
    public string? GeoLon { get; set; }
    public bool IsPrimary { get; set; }
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
