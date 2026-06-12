using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class PaymentAdvanceReviewCandidateReportDto
{
    public int Total { get; init; }
    public decimal TotalAmount { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<PaymentAdvanceReviewCandidateDto> Items { get; init; } = [];
}

public sealed class PaymentAdvanceReviewBackfillResponseDto
{
    public int Eligible { get; init; }
    public int Updated { get; init; }
    public decimal TotalAmount { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<PaymentAdvanceReviewCandidateDto> Items { get; init; } = [];
}

public sealed class PaymentAdvanceReviewBackfillRollbackResponseDto
{
    public int Eligible { get; init; }
    public int Updated { get; init; }
    public decimal TotalAmount { get; init; }
    public int Limit { get; init; }
    public IReadOnlyList<PaymentAdvanceReviewCandidateDto> Items { get; init; } = [];
}

public sealed class PaymentAdvanceReviewCandidateDto
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal AdvanceAmount { get; init; }
    public string Description { get; init; } = string.Empty;
    public int? ClientId { get; init; }
    public int? LegalEntityId { get; init; }
    public string? CounterpartyName { get; init; }
    public int? ClientCaseId { get; init; }
    public int? ContractId { get; init; }
    public PaymentStatus Status { get; init; }
    public PaymentAdvanceReviewStatus AdvanceReviewStatus { get; init; }
}
