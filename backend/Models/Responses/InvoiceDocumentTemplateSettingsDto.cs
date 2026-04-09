namespace PayPlanner.Api.Models.Responses;

public class InvoiceDocumentTemplateSettingsDto
{
    public string ActiveTemplateKey { get; set; } = string.Empty;
    public List<InvoiceDocumentTemplateDto> Templates { get; set; } = new();
    public List<InvoiceDocumentTemplateTokenDto> Tokens { get; set; } = new();
    public string DefaultTemplateHtml { get; set; } = string.Empty;
}

public class InvoiceDocumentTemplateDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Html { get; set; } = string.Empty;
}

public class InvoiceDocumentTemplateTokenDto
{
    public string Key { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsRepeating { get; set; }
}
