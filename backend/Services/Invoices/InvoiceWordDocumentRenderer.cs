using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Services.Invoices;

/// <summary>Сохраняет специальную Word-вёрстку только для неизменённых встроенных шаблонов.</summary>
public static class InvoiceWordDocumentRenderer
{
    private static readonly Dictionary<string, (string Source, Func<string, byte[]> Render)> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        [InvoiceDocumentTemplateEngine.DefaultTemplateKey] = (Normalize(InvoiceDocumentTemplateEngine.DefaultTemplateHtml), InvoiceStandardWordDocumentRenderer.Render),
        [InvoiceDocumentTemplateEngine.OfferTemplateKey] = (Normalize(InvoiceDocumentTemplateEngine.OfferTemplateHtml), InvoiceOfferWordDocumentRenderer.Render),
        [InvoiceDocumentTemplateEngine.OrderTemplateKey] = (Normalize(InvoiceDocumentTemplateEngine.OrderTemplateHtml), InvoiceOrderWordDocumentRenderer.Render),
    };

    public static byte[] Render(string html, string? templateKey, string? templateSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        if (templateKey is not null && templateSource is not null
            && BuiltIns.TryGetValue(templateKey, out var builtIn)
            && string.Equals(Normalize(templateSource), builtIn.Source, StringComparison.Ordinal))
            return builtIn.Render(html);

        // User HTML owns the flow and image positions. Fixed overlays would remove
        // inline images and move only the first matching image to standard coordinates.
        return HtmlWordDocumentBuilder.Build(html);
    }

    private static string Normalize(string html) => html.ReplaceLineEndings("\n").Trim();
}
