using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class InvoiceDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public DateTime? DueDate { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidDate { get; set; }
    public PaymentType Type { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? ClientCompany { get; set; }
    public int? ClientStatusId { get; set; }
    public ClientStatusDto? ClientStatus { get; set; }
    public int? ClientCaseId { get; set; }
    public string? ClientCaseTitle { get; set; }
    public string? Description { get; set; }
    public string? ActReference { get; set; }
    public int? ActId { get; set; }
    public string? ActNumber { get; set; }
    public string? ActTitle { get; set; }
    public ActStatus? ActStatus { get; set; }
    public string? ResponsibleName { get; set; }
    public int? ResponsibleId { get; set; }
    public string? CounterpartyInn { get; set; }
    public string? PaymentStatusName { get; set; }
    public int? PaymentSourceId { get; set; }
    public string? PaymentSourceName { get; set; }
    public string? PaymentSourceColor { get; set; }
    public PaymentType? PaymentSourceType { get; set; }
    public int? IncomeTypeId { get; set; }
    public string? IncomeTypeName { get; set; }
    public string? IncomeTypeColor { get; set; }
    public DateTime CreatedAt { get; set; }
}
