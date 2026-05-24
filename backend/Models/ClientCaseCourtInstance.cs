using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseCourtInstance
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [Required, MaxLength(64)]
    public string Instance { get; set; } = "first";

    public int? CourtId { get; set; }
    public ClientCaseCourt? Court { get; set; }

    [MaxLength(200)]
    public string CourtName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string CourtCaseNumber { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string CourtUrl { get; set; } = string.Empty;

    [MaxLength(200)]
    public string JudgeName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Courtroom { get; set; } = string.Empty;

    [MaxLength(200)]
    public string SecretaryName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;

    public ICollection<ClientCaseCourtComplaint> Complaints { get; set; } = new List<ClientCaseCourtComplaint>();
}
