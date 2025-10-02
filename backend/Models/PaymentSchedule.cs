using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

/// <summary>
/// Payment Schedule for tracking partial payments and remainders
/// Tracks planned payments, due dates, and status (Planned/Overdue/Paid)
/// </summary>
[Table("payment_schedule")]
public class PaymentSchedule
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("case_id")]
    public int CaseId { get; set; }

    [Column("client_id")]
    public int? ClientId { get; set; }

    [Column("related_payment_id")]
    public int? RelatedPaymentId { get; set; } // Link to Payment that created this schedule item

    [Required]
    [Column("scheduled_amount")]
    [Column(TypeName = "decimal(18,2)")]
    public decimal ScheduledAmount { get; set; }

    [Required]
    [Column("due_date")]
    public DateTime DueDate { get; set; }

    [Required]
    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "Planned"; // Planned, Overdue, Paid, Cancelled

    [Column("paid_amount")]
    [Column(TypeName = "decimal(18,2)")]
    public decimal PaidAmount { get; set; } = 0;

    [Column("paid_date")]
    public DateTime? PaidDate { get; set; }

    [Column("notes")]
    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("CaseId")]
    public virtual ClientCase Case { get; set; } = null!;

    [ForeignKey("ClientId")]
    public virtual Client? Client { get; set; }

    [ForeignKey("RelatedPaymentId")]
    public virtual Payment? RelatedPayment { get; set; }
}

public static class PaymentScheduleStatus
{
    public const string Planned = "Planned";
    public const string Overdue = "Overdue";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";
}
