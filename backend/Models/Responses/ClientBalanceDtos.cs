using System;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class ClientBalanceSummaryDto
{
    public int ClientId { get; set; }
    public decimal Balance { get; set; }
}

public sealed class ClientBalanceTransactionDto
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public int? InvoiceId { get; set; }
    public int? PaymentId { get; set; }
    public decimal Amount { get; set; }
    public ClientBalanceTransactionType Type { get; set; }
    public ClientBalanceDirection Direction { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUserName { get; set; }
}

public sealed class ApplyClientBalanceRequest
{
    public int PaymentId { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}
