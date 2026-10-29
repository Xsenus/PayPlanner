using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Models.Requests;

public sealed class CompanyDocumentImagePrintDefaultsRequest
{
    public DocumentImagePrintDefaultsRequest? Invoice { get; set; }
    public DocumentImagePrintDefaultsRequest? Act { get; set; }
    public DocumentImagePrintDefaultsRequest? Contract { get; set; }

    public bool TryToValue(out CompanyDocumentImagePrintDefaults? value)
    {
        value = null;
        if (!TryRead(Invoice, out var invoice) || !TryRead(Act, out var act) || !TryRead(Contract, out var contract)) return false;
        value = new(invoice, act, contract);
        return true;
    }

    private static bool TryRead(DocumentImagePrintDefaultsRequest? request, out DocumentImagePrintOptions? value)
    {
        value = null;
        if (request is null) return true;
        if (request.IncludeLogo is not bool logo || request.IncludeSeal is not bool seal || request.IncludeSignature is not bool signature) return false;
        value = new(logo, seal, signature);
        return true;
    }
}

public sealed class DocumentImagePrintDefaultsRequest
{
    [Required] public bool? IncludeLogo { get; set; }
    [Required] public bool? IncludeSeal { get; set; }
    [Required] public bool? IncludeSignature { get; set; }
}
