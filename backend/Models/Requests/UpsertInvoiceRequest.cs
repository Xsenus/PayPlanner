using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public class UpsertInvoiceRequest
{
    [Required]
    [MaxLength(120)]
    public string Number { get; set; } = string.Empty;

    [Required]
    public DateTime Date { get; set; }

    public DateTime? DueDate { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Amount { get; set; }

    [Required]
    public PaymentType Type { get; set; } = PaymentType.Income;

    [Required]
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public DateTime? PaidDate { get; set; }

    [Required]
    public int ClientId { get; set; }

    public int? ClientCaseId { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [MaxLength(300)]
    public string? ActReference { get; set; }

    [MaxLength(200)]
    public string? CounterpartyName { get; set; }

    public int? PaymentSourceId { get; set; }

    public int? IncomeTypeId { get; set; }

    public int? DealTypeId { get; set; }

    public int? PaymentStatusEntityId { get; set; }
}
