namespace PayPlanner.Api.Models.Responses;

public sealed class ContractStatusReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int ContractCount { get; init; }
    public int DraftCount { get; init; }
    public int NotSignedCount { get; init; }
    public int OnSigningCount { get; init; }
    public int ConcludedCount { get; init; }
    public int TerminatedCount { get; init; }
    public int CancelledCount { get; init; }
    public int OpenEndedCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal ActiveAmount { get; init; }
    public decimal ClosedAmount { get; init; }
    public IReadOnlyList<ContractStatusReportItemDto> Statuses { get; init; } = [];
}

public sealed class ContractStatusReportItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int ContractCount { get; init; }
    public int OpenEndedCount { get; init; }
    public int ExpiringCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal Share { get; init; }
}
