using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class ActStatusReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int ActCount { get; init; }
    public int CreatedCount { get; init; }
    public int TransferredCount { get; init; }
    public int SignedCount { get; init; }
    public int TerminatedCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal SignedAmount { get; init; }
    public decimal PendingAmount { get; init; }
    public IReadOnlyList<ActStatusReportItemDto> Statuses { get; init; } = [];
}

public sealed class ActStatusReportItemDto
{
    public string WorkflowStatus { get; init; } = string.Empty;
    public string WorkflowStatusLabel { get; init; } = string.Empty;
    public int ActCount { get; init; }
    public int CreatedCount { get; init; }
    public int TransferredCount { get; init; }
    public int SignedCount { get; init; }
    public int TerminatedCount { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal SignedAmount { get; init; }
    public decimal PendingAmount { get; init; }
    public decimal Share { get; init; }
}
