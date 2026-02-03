namespace PayPlanner.Api.Models;

public class MoneyOperation
{
    public int Id { get; set; }
    public MoneyOperationSource Source { get; set; }
    public MoneyOperationDirection Direction { get; set; }
    public MoneyOperationAccountingType AccountingType { get; set; }
    public string? AccountName { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CategoryName { get; set; }
    public decimal Amount { get; set; }
    public string? Currency { get; set; }
    public string? PaymentOrderNumber { get; set; }
    public DateTime Date { get; set; }
    public DateTime? RecognitionDate { get; set; }
    public bool IsAdvancePayment { get; set; }
    public string? Purpose { get; set; }
    public string? PaidInvoiceNumber { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? ProjectName { get; set; }
    public string? DirectionName { get; set; }
    public string? SalesPointName { get; set; }
    public int? PaymentPriority { get; set; }
    public string? PaidAccountName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
