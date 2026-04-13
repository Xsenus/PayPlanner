using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class Bank
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(16)]
    public string? Bik { get; set; }

    [MaxLength(34)]
    public string? CorrespondentAccount { get; set; }

    [MaxLength(120)]
    public string? City { get; set; }

    public bool IsForeign { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}
