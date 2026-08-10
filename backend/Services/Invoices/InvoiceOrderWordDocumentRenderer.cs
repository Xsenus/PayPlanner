using PayPlanner.Api.Services.Documents;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceOrderWordDocumentRenderer
{
    public static byte[] Render(string html)
        => HtmlWordDocumentBuilder.Build(ApplyFixedTableWidths(html));

    private static string ApplyFixedTableWidths(string html)
    {
        var normalized = html
            .Replace("margin-top: 5.6mm;", "margin-top: 3.2mm;", StringComparison.Ordinal)
            .Replace("font-size: 8.8pt; line-height: 1.14;", "font-size: 8pt; line-height: 9pt; mso-line-height-rule: exactly;", StringComparison.Ordinal)
            .Replace(".signature-block { margin-top: 5mm; padding-top: 2mm;", ".signature-block { margin-top: 2mm; padding-top: 1mm;", StringComparison.Ordinal)
            .Replace("padding: 1.8mm 1mm; mso-padding-alt: 5.1pt 2.8pt 5.1pt 2.8pt;", "padding: 0; mso-padding-alt: 0;", StringComparison.Ordinal)
            .Replace(".totals { width: 48%;", ".totals { width: 252.5pt;", StringComparison.Ordinal)
            .Replace(".totals td { padding: .45mm 0 .45mm 2mm; }", ".totals td { height: 12pt; padding: 0 0 0 5.7pt; mso-padding-alt: 0 0 0 5.7pt; line-height: 10pt; mso-line-height-rule: exactly; }", StringComparison.Ordinal)
            .Replace("<table class=\"totals\">", "<table class=\"totals\" width=\"337\" style=\"width:252.5pt;margin-left:273.5pt\">", StringComparison.Ordinal)
            .Replace("<div class=\"signature-block signature-new-page\">", "<p style=\"page-break-before:always;font-size:1pt;line-height:1pt;margin:0\">&nbsp;</p><div class=\"signature-block signature-new-page\">", StringComparison.Ordinal)
            .Replace("</style>", ".signature-new-page { margin-top: -8mm !important; padding-top: 0 !important; } .signature-new-page .line { min-height: 0 !important; } </style>", StringComparison.Ordinal)
            .Replace("<table class=\"items\" width=\"100%\">", "<table class=\"items\" width=\"702\" style=\"width:526pt;table-layout:fixed\">", StringComparison.Ordinal)
            .Replace("width=\"5%\" style=\"width:5%\"", "width=\"35\" style=\"width:26.3pt\"", StringComparison.Ordinal)
            .Replace("width=\"52%\" style=\"width:52%\"", "width=\"365\" style=\"width:273.5pt\"", StringComparison.Ordinal)
            .Replace("width=\"8%\" style=\"width:8%\"", "width=\"56\" style=\"width:42.1pt\"", StringComparison.Ordinal)
            .Replace("width=\"9%\" style=\"width:9%\"", "width=\"63\" style=\"width:47.3pt\"", StringComparison.Ordinal)
            .Replace("width=\"13%\" style=\"width:13%\"", "width=\"91\" style=\"width:68.4pt\"", StringComparison.Ordinal);

        return Regex.Replace(
            normalized,
            "<(td|th)([^>]*class=\"(?:index|name|quantity|unit|price|sum)\"[^>]*)>(.*?)</\\1>",
            "<$1$2><p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;line-height:9pt;mso-line-height-rule:exactly\">$3</p></$1>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}
