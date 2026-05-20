using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

public class ClientCaseWorkOrder
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Status { get; set; } = "draft";

    [MaxLength(3)]
    public string Currency { get; set; } = "RUB";

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;

    public DateTime? ApprovalRequestedAt { get; set; }
    public int? ApprovalRequestedByUserId { get; set; }

    [MaxLength(256)]
    public string ApprovalRequestedByUserName { get; set; } = string.Empty;

    public DateTime? ApprovalDecisionAt { get; set; }
    public int? ApprovalDecisionByUserId { get; set; }

    [MaxLength(256)]
    public string ApprovalDecisionByUserName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string ApprovalComment { get; set; } = string.Empty;

    public ICollection<ClientCaseWorkOrderItem> Items { get; set; } = new List<ClientCaseWorkOrderItem>();
}
