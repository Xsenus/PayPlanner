namespace PayPlanner.Api.Models;

public class MoneyBalanceReconciliationResult
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public MoneyOperationSource Source { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public decimal OpeningBalance { get; set; }
    public decimal Income { get; set; }
    public decimal Expense { get; set; }
    public decimal ClosingBalance { get; set; }
    public int IncomeCount { get; set; }
    public int ExpenseCount { get; set; }
    public bool IsBalanced { get; set; } = true;
    public int IssueCount { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public int? CheckedByUserId { get; set; }
}
