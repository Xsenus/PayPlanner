using System;

namespace PayPlanner.Api.Models;

public class RelatedDocumentLink
{
    public int Id { get; set; }
    public string Label { get; set; } = string.Empty;
    public string? SecondaryLabel { get; set; }
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public string? Type { get; set; }
    public DateTime? Date { get; set; }
    public decimal? Amount { get; set; }
}
