using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class ClientCaseExpenseDetailDto
{
    public int PaymentId { get; set; }

    public int? InvoiceId { get; set; }

    public string? InvoiceNumber { get; set; }

    public DateTime? InvoiceDate { get; set; }

    public DateTime PaymentDate { get; set; }

    public DateTime PlannedDate { get; set; }

    public int? SupplierId { get; set; }

    public string? SupplierName { get; set; }

    public int? ExpenseClientId { get; set; }

    public string? ExpenseClientName { get; set; }

    public int CaseId { get; set; }

    public string? CaseTitle { get; set; }

    public int? LineItemId { get; set; }

    public string? LineItemName { get; set; }

    public int? ServiceId { get; set; }

    public string? ServiceName { get; set; }

    public decimal? Quantity { get; set; }

    public string? Unit { get; set; }

    public decimal? Price { get; set; }

    public decimal Amount { get; set; }

    public decimal PaidAmount { get; set; }

    public decimal OutstandingAmount { get; set; }

    public PaymentStatus Status { get; set; }

    public string? PaymentOrderNumber { get; set; }

    public DateTime? PaymentDocumentDate { get; set; }

    public string? Description { get; set; }
}
