using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class LegalEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string ShortName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Inn { get; set; }

    [MaxLength(20)]
    public string? Kpp { get; set; }

    [MaxLength(20)]
    public string? Ogrn { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Director { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Client> Clients { get; set; } = new List<Client>();
}
