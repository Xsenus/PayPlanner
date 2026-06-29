namespace PayPlanner.Api.Models.Responses;

public sealed class ContractorBalanceDto
{
    public decimal OwedToMe { get; set; }
    public decimal OwedByMe { get; set; }
    public decimal PaidToMe { get; set; }
    public decimal PaidByMe { get; set; }
    public IReadOnlyList<ContractorBalanceItemDto> Queue { get; set; } = Array.Empty<ContractorBalanceItemDto>();
    public IReadOnlyList<ContractorBalanceItemDto> Paid { get; set; } = Array.Empty<ContractorBalanceItemDto>();
}

public sealed class ContractorBalanceItemDto
{
    public int TaskId { get; set; }
    public string TaskTitle { get; set; } = string.Empty;
    public int? PaymentId { get; set; }
    public string Direction { get; set; } = "toMe";
    public string AssigneeName { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int? LegalEntityId { get; set; }
    public string? LegalEntityName { get; set; }
    public int? ClientCaseId { get; set; }
    public string? ClientCaseTitle { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public string PaymentStatus { get; set; } = string.Empty;
    public string TaskStatus { get; set; } = string.Empty;
    public string? PaymentDueDate { get; set; }
    public string? PaidDate { get; set; }
}
