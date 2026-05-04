using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class CashOrderReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public decimal PostedIncomingTotal { get; init; }
    public decimal PostedOutgoingTotal { get; init; }
    public decimal PostedNetTotal { get; init; }
    public int OrderCount { get; init; }
    public int DraftCount { get; init; }
    public int PostedCount { get; init; }
    public int CancelledCount { get; init; }
    public IReadOnlyList<CashOrderReportItemDto> Statuses { get; init; } = [];
}

public sealed class CashOrderReportItemDto
{
    public CashOrderStatus Status { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public decimal IncomingTotal { get; init; }
    public decimal OutgoingTotal { get; init; }
    public decimal NetTotal { get; init; }
    public decimal TotalAmount { get; init; }
    public int IncomingCount { get; init; }
    public int OutgoingCount { get; init; }
    public int OrderCount { get; init; }
    public decimal Share { get; init; }
}
