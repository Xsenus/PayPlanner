using System;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class ActDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string? InvoiceNumber { get; set; }
    public int? InvoiceId { get; set; }
    public int? ContractId { get; set; }
    public string? CounterpartyInn { get; set; }
    public ActStatus Status { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int? ResponsibleId { get; set; }
    public string? ResponsibleName { get; set; }
    public string? Comment { get; set; }
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
}
