namespace PayPlanner.Api.Models.Responses;

public class ClientCaseFinanceSummaryDto
{
    public int CaseId { get; set; }

    public int? ClientId { get; set; }

    public string? ClientName { get; set; }

    public string? CaseTitle { get; set; }

    public string? CaseStatus { get; set; }

    public DateTime? CaseCreatedAt { get; set; }

    public bool HasCompletePaymentVisibility { get; set; } = true;

    public decimal IncomeTotal { get; set; }

    public decimal IncomePaid { get; set; }

    public decimal IncomeOutstanding { get; set; }

    public int IncomeCount { get; set; }

    public decimal ExpenseTotal { get; set; }

    public decimal ExpensePaid { get; set; }

    public decimal ExpenseOutstanding { get; set; }

    public int ExpenseCount { get; set; }

    public decimal PlannedMargin => IncomeTotal - ExpenseTotal;

    public decimal PaidMargin => IncomePaid - ExpensePaid;

    public decimal OutstandingBalance => IncomeOutstanding - ExpenseOutstanding;
}
