namespace PayPlanner.Api.Models.Responses;

public class InvoiceRulesSettingsDto
{
    public int MaxClientCasesPerInvoice { get; set; } = 1;
    public int MaxActsPerInvoice { get; set; } = 1;
}
