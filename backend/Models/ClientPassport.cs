using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models;

public class ClientPassport
{
    [Key]
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(20)]
    public string? Series { get; set; }

    [JsonIgnore]
    public string? SeriesEncrypted { get; set; }

    [MaxLength(64)]
    [JsonIgnore]
    public string? SeriesBlindIndex { get; set; }

    [MaxLength(20)]
    public string? Number { get; set; }

    [JsonIgnore]
    public string? NumberEncrypted { get; set; }

    [MaxLength(64)]
    [JsonIgnore]
    public string? NumberBlindIndex { get; set; }

    [MaxLength(500)]
    public string? IssuedBy { get; set; }

    [JsonIgnore]
    public string? IssuedByEncrypted { get; set; }

    public DateTime? IssuedAt { get; set; }

    [JsonIgnore]
    public string? IssuedAtEncrypted { get; set; }

    [MaxLength(20)]
    public string? DepartmentCode { get; set; }

    [JsonIgnore]
    public string? DepartmentCodeEncrypted { get; set; }

    [MaxLength(500)]
    public string? BirthPlacePassport { get; set; }

    [JsonIgnore]
    public string? BirthPlacePassportEncrypted { get; set; }

    [MaxLength(120)]
    public string? BirthPlaceFias { get; set; }

    [JsonIgnore]
    public string? BirthPlaceFiasEncrypted { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
