using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models.Responses;

public class ContractDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Date { get; set; }
    public DateTime? StartDate { get; set; }
    public string? Description { get; set; }
    public string BillingMode { get; set; } = ContractBillingMode.Fixed;
    public string Status { get; set; } = ContractStatus.Draft;
    public DateTime? SentForSigningAt { get; set; }
    public DateTime? ConcludedAt { get; set; }
    public bool IsOpenEnded { get; set; }
    public decimal? Amount { get; set; }
    public DateTime? ValidUntil { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }
    public string? TerminationTerms { get; set; }
    public string? TerminationFinancialTerms { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? AuditCreatedByUserId { get; set; }
    public string? AuditCreatedByUserName { get; set; }
    public DateTime? AuditUpdatedAt { get; set; }
    public int? AuditUpdatedByUserId { get; set; }
    public string? AuditUpdatedByUserName { get; set; }
    public bool AuditIsDeleted { get; set; }
    public DateTime? AuditDeletedAt { get; set; }
    public int? AuditDeletedByUserId { get; set; }
    public string? AuditDeletedByUserName { get; set; }
    public decimal InvoiceAmountTotal { get; set; }
    public decimal ActAmountTotal { get; set; }
    public decimal PaymentAmountTotal { get; set; }
    public IReadOnlyCollection<ContractClientDto> Clients { get; set; } = Array.Empty<ContractClientDto>();
    public IReadOnlyCollection<int> ActIds { get; set; } = Array.Empty<int>();
    public IReadOnlyCollection<int> InvoiceIds { get; set; } = Array.Empty<int>();
    public IReadOnlyCollection<int> PaymentIds { get; set; } = Array.Empty<int>();
}

public class ContractClientDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string? Comment { get; set; }
}
