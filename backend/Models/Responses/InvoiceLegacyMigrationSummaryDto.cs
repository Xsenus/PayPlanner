namespace PayPlanner.Api.Models.Responses;

public class InvoiceLegacyMigrationSummaryDto
{
    public int ScannedPayments { get; set; }
    public int InvoicePayments { get; set; }
    public int AlreadyLinked { get; set; }
    public int MissingNumberSkipped { get; set; }
    public int LinkedToExistingInvoice { get; set; }
    public int CreatedInvoices { get; set; }
    public int LinkedPayments { get; set; }
    public int DistinctLegacyInvoiceKeys { get; set; }
    public int ExistingDuplicateInvoiceKeys { get; set; }
}
