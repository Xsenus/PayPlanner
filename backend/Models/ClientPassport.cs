using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientPassport
{
    [Key]
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(10)]
    public string? Series { get; set; }

    [MaxLength(20)]
    public string? Number { get; set; }

    [MaxLength(500)]
    public string? IssuedBy { get; set; }

    public DateTime? IssuedAt { get; set; }

    [MaxLength(20)]
    public string? DepartmentCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
