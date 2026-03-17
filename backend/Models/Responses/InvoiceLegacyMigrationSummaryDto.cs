namespace PayPlanner.Api.Models.Responses;

public class InvoiceLegacyMigrationSummaryDto
{
    public int ScannedPayments { get; set; }
    public int InvoicePayments { get; set; }
    public int ConvertedToInvoiceKind { get; set; }
    public int AlreadyLinked { get; set; }
    public int MissingNumberSkipped { get; set; }
    public int MissingClientSkipped { get; set; }
    public int LinkedToExistingInvoice { get; set; }
    public int CreatedInvoices { get; set; }
    public int LinkedPayments { get; set; }
    public int RelinkedPayments { get; set; }
    public int DistinctLegacyInvoiceKeys { get; set; }
    public int DistinctLegacyGroups { get; set; }
    public int GroupsWithCase { get; set; }
    public int GroupsWithoutCase { get; set; }
    public int ExistingDuplicateInvoiceKeys { get; set; }
    public int SharedInvoiceEntities { get; set; }
    public int SplitSharedInvoiceGroups { get; set; }
    public int RepresentativePaymentsUpdated { get; set; }
}
