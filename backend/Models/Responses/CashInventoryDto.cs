namespace PayPlanner.Api.Models.Responses;

public sealed class CashInventoryDto
{
    public int Id { get; init; }
    public string Number { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public decimal AccountingBalance { get; init; }
    public decimal ActualBalance { get; init; }
    public decimal Difference { get; init; }
    public int? CorrectionCashOrderId { get; init; }
    public string? CorrectionCashOrderNumber { get; init; }
    public string? Comment { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public sealed class CashInventoryCreateRequest
{
    public DateTime Date { get; init; }
    public string? AccountName { get; init; }
    public decimal ActualBalance { get; init; }
    public string? Comment { get; init; }
    public bool CreateCorrection { get; init; }
}
