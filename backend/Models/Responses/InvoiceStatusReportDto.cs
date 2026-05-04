namespace PayPlanner.Api.Models.Responses;

public sealed class InvoiceStatusReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int InvoiceCount { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public int PaidCount { get; init; }
    public int OverdueCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public IReadOnlyList<InvoiceStatusReportItemDto> Statuses { get; init; } = [];
}

public sealed class InvoiceStatusReportItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int InvoiceCount { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public int PaidCount { get; init; }
    public int OverdueCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public decimal Share { get; init; }
}
