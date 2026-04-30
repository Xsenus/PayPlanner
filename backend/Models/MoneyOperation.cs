namespace PayPlanner.Api.Models;

public class MoneyOperation
{
    public int Id { get; set; }
    public MoneyOperationSource Source { get; set; }
    public MoneyOperationDirection Direction { get; set; }
    public MoneyOperationAccountingType AccountingType { get; set; }
    public int? MoneyAccountId { get; set; }
    public MoneyAccount? MoneyAccount { get; set; }
    public string? AccountName { get; set; }
    public string? CounterpartyName { get; set; }
    public int? IncomeTypeId { get; set; }
    public IncomeType? IncomeType { get; set; }
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
    public int? LinkedInvoiceId { get; set; }
    public Invoice? LinkedInvoice { get; set; }
    public MoneyOperationMatchStatus MatchStatus { get; set; } = MoneyOperationMatchStatus.None;
    public DateTime? MatchedAt { get; set; }
    public string? MatchComment { get; set; }
    public int? BankStatementImportBatchId { get; set; }
    public BankStatementImportBatch? BankStatementImportBatch { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public ICollection<CashOrder> CashOrders { get; set; } = new List<CashOrder>();
}
