using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class AccountsSummaryBucketDto
{
    public decimal Amount { get; init; }
    public int Count { get; init; }
}

public sealed class AccountsSummaryDto
{
    public AccountsSummaryBucketDto Total { get; init; } = new();
    public AccountsSummaryBucketDto Pending { get; init; } = new();
    public AccountsSummaryBucketDto Paid { get; init; } = new();
    public AccountsSummaryBucketDto Overdue { get; init; } = new();
    public AccountsSummaryBucketDto Cancelled { get; init; } = new();
}

public sealed class AccountRegistryRowDto
{
    public string GroupKey { get; init; } = string.Empty;
    public string AccountNumber { get; init; } = string.Empty;
    public InvoiceDto? Invoice { get; init; }
    public IReadOnlyList<Payment> Payments { get; init; } = Array.Empty<Payment>();
    public IReadOnlyList<ActDto> Acts { get; init; } = Array.Empty<ActDto>();
    public decimal TotalAmount { get; init; }
    public DateTime? DueDate { get; init; }
    public DateTime? PaidDate { get; init; }
    public string Status { get; init; } = "created";
    public PaymentType Type { get; init; }
    public bool IsOverdue { get; init; }
}

public sealed class AccountsPageResponse : PagedResponse<AccountRegistryRowDto>
{
    public AccountsSummaryDto Summary { get; init; } = new();
}
