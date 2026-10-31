using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class PartialPaymentRemainderCandidateDto
{
    public Payment Payment { get; set; } = new();
    public decimal OriginalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainderAmount { get; set; }
    public int DaysPastDue { get; set; }
    public DateTime SuggestedRemainderDate { get; set; }
    public bool NeedsAmountConfirmation { get; set; }
    public string ConfirmationToken { get; set; } = string.Empty;
}

public sealed class PartialPaymentRemainderCandidatesResponse
{
    public List<PartialPaymentRemainderCandidateDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public decimal TotalOriginalAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
    public decimal TotalRemainderAmount { get; set; }
    public bool IncludeFuture { get; set; }
    public DateTime CheckedAt { get; set; }
}
