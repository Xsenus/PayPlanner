using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class InvoiceDocumentTemplateSettingsRequest
{
    [Required]
    public string ActiveTemplateKey { get; set; } = string.Empty;

    public bool PrintWithSignatureAndSealByDefault { get; set; }

    [Required]
    public List<InvoiceDocumentTemplateRequest> Templates { get; set; } = new();
}

public class InvoiceDocumentTemplateRequest
{
    [Required]
    public string Key { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    public string Html { get; set; } = string.Empty;
}
