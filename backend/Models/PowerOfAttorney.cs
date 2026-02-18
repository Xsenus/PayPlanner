using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class PowerOfAttorney
{
    [Key]
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(100)]
    public string Number { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; }

    public DateTime? ValidUntil { get; set; }

    [MaxLength(300)]
    public string? Issuer { get; set; }

    [MaxLength(1500)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
