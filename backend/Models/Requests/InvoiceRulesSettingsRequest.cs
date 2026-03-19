namespace PayPlanner.Api.Models.Requests;

public class InvoiceRulesSettingsRequest
{
    public int MaxClientCasesPerInvoice { get; set; } = 1;
    public int MaxActsPerInvoice { get; set; } = 1;
}
