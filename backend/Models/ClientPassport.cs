using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientPassport
{
    [Key]
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(20)]
    public string? Series { get; set; }

    [MaxLength(20)]
    public string? Number { get; set; }

    [MaxLength(500)]
    public string? IssuedBy { get; set; }

    public DateTime? IssuedAt { get; set; }

    [MaxLength(20)]
    public string? DepartmentCode { get; set; }

    [MaxLength(500)]
    public string? BirthPlacePassport { get; set; }

    [MaxLength(120)]
    public string? BirthPlaceFias { get; set; }

    public DateTime? ValidFrom { get; set; }

    public DateTime? ValidTo { get; set; }

    public bool IsPrimary { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
