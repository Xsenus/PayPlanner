using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class UpsertPowerOfAttorneyRequest
{
    [Required]
    [MaxLength(100)]
    public string Number { get; set; } = string.Empty;

    [Required]
    public DateTime? IssuedAt { get; set; }

    public DateTime? ValidUntil { get; set; }

    [MaxLength(300)]
    public string? Issuer { get; set; }

    [MaxLength(1500)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
}
