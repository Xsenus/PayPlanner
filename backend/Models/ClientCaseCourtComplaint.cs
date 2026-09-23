using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseCourtComplaint
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    public int CourtInstanceId { get; set; }
    public ClientCaseCourtInstance CourtInstance { get; set; } = null!;

    [Required, MaxLength(64)]
    public string ComplaintType { get; set; } = "appealComplaint";

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string TargetInstance { get; set; } = "appeal";

    public DateTime? FiledAt { get; set; }

    [Required, MaxLength(16)]
    public string FiledBy { get; set; } = "unknown";

    [Required, MaxLength(64)]
    public string Status { get; set; } = "draft";

    public int? ClientCaseDocumentId { get; set; }
    public ClientCaseDocument? ClientCaseDocument { get; set; }

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;
}
