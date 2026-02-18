using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public enum AddressType
{
    Registration = 1,
    Actual = 2,
    Legal = 3,
    Other = 9,
}

public class AddressRecord
{
    [Key]
    public int Id { get; set; }

    public AddressType Type { get; set; } = AddressType.Other;

    [MaxLength(600)]
    public string Value { get; set; } = string.Empty;

    [MaxLength(600)]
    public string? UnrestrictedValue { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [MaxLength(120)]
    public string? Country { get; set; }

    [MaxLength(200)]
    public string? RegionWithType { get; set; }

    [MaxLength(200)]
    public string? CityWithType { get; set; }

    [MaxLength(200)]
    public string? StreetWithType { get; set; }

    [MaxLength(50)]
    public string? House { get; set; }

    [MaxLength(50)]
    public string? Flat { get; set; }

    [MaxLength(60)]
    public string? FiasId { get; set; }

    [MaxLength(60)]
    public string? KladrId { get; set; }

    [MaxLength(32)]
    public string? GeoLat { get; set; }

    [MaxLength(32)]
    public string? GeoLon { get; set; }

    public bool IsPrimary { get; set; }

    public int? ClientId { get; set; }
    public Client? Client { get; set; }

    public int? LegalEntityId { get; set; }
    public LegalEntity? LegalEntity { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
