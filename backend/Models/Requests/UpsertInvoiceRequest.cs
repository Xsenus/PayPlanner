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

    public int? ExpenseClientId { get; set; }

    public int? ContractId { get; set; }

    public int? ClientCaseId { get; set; }

    public List<int>? ClientCaseIds { get; set; }

    [MaxLength(300)]
    public string? ClientCaseReference { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public bool IsNotForAccounting { get; set; }

    [MaxLength(300)]
    public string? ActReference { get; set; }

    public int? ActId { get; set; }

    public List<int>? ActIds { get; set; }

    [MaxLength(200)]
    public string? CounterpartyName { get; set; }

    public int? PaymentSourceId { get; set; }

    public int? ServiceId { get; set; }

    public int? IncomeTypeId { get; set; }

    public int? DealTypeId { get; set; }

    public int? PaymentStatusEntityId { get; set; }

    /// <summary>
    /// Режим импорта: при конфликте номера счёта автоматически подбирается уникальный номер (суффикс),
    /// чтобы загрузка не прерывалась из-за исторических дублей.
    /// </summary>
    public bool AllowDuplicateNumberOnImport { get; set; }

    public List<int>? ResponsibleIds { get; set; }

    public List<InvoiceLineItemRequest>? LineItems { get; set; }

    public InvoiceInstallmentPlanRequest? InstallmentPlan { get; set; }
}

public class InvoiceLineItemRequest
{
    [MaxLength(2000)]
    public string? Name { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Quantity { get; set; }

    [MaxLength(32)]
    public string? Unit { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Price { get; set; }
}
