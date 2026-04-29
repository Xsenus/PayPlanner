using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class BankStatementImportResultDto
{
    public int RowsRead { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<BankStatementImportCreatedOperationDto> Operations { get; set; } = new();
}

public sealed class BankStatementImportCreatedOperationDto
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public MoneyOperationDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string? CounterpartyName { get; set; }
    public string? PaymentOrderNumber { get; set; }
}
