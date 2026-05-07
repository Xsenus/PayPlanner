using PayPlanner.Api.Services;

namespace PayPlanner.Api.Models.Responses;

public sealed class RepairInvoicePaymentStateResponse
{
    public bool DryRun { get; init; }

    public int Scanned { get; init; }

    public int Changed { get; init; }

    public int Applied { get; init; }

    public IReadOnlyList<RepairInvoicePaymentStateItemDto> Items { get; init; } = [];
}

public sealed class RepairInvoicePaymentStateItemDto
{
    public int? InvoiceId { get; init; }

    public int RepresentativePaymentId { get; init; }

    public string? Account { get; init; }

    public DateTime? AccountDate { get; init; }

    public int SourcePaymentCount { get; init; }

    public InvoicePaymentStateSnapshot Current { get; init; } = null!;

    public InvoicePaymentStateSnapshot Suggested { get; init; } = null!;
}
