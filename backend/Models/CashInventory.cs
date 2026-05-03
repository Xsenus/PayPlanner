namespace PayPlanner.Api.Models;

public class CashInventory
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public decimal AccountingBalance { get; set; }
    public decimal ActualBalance { get; set; }
    public decimal Difference { get; set; }
    public int? CorrectionCashOrderId { get; set; }
    public CashOrder? CorrectionCashOrder { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
