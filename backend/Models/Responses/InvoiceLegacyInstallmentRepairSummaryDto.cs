namespace PayPlanner.Api.Models.Responses;

public sealed class InvoiceLegacyInstallmentRepairSummaryDto
{
    public int InvoiceEntityId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public int RepresentativePaymentId { get; set; }
    public int ScannedPayments { get; set; }
    public int ChildPayments { get; set; }
    public int UpdatedKinds { get; set; }
    public int AddedMarkers { get; set; }
    public int NormalizedLinks { get; set; }
    public int UpdatedCaseLinks { get; set; }
    public int CreatedPayments { get; set; }
    public int UpdatedInvoiceAmounts { get; set; }
    public int UpdatedInvoicePaidAmounts { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal ChildPaymentsTotal { get; set; }
    public decimal MissingInstallmentAmount { get; set; }
    public decimal ResultingInvoiceAmount { get; set; }
    public decimal ResultingChildPaymentsTotal { get; set; }
    public decimal RemainingUnscheduledAmount { get; set; }
    public bool LegacyAggregateDetected { get; set; }
    public bool ConvertedLegacyAggregateDetected { get; set; }
    public bool Repaired { get; set; }
    public bool DryRun { get; set; }
    public List<string> Actions { get; set; } = new();
}
