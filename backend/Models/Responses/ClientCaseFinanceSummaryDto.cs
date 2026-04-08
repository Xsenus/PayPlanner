namespace PayPlanner.Api.Models.Responses;

public class ClientCaseFinanceSummaryDto
{
    public int CaseId { get; set; }

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
