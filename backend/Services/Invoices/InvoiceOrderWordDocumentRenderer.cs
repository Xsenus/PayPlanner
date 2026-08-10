using PayPlanner.Api.Services.Documents;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceOrderWordDocumentRenderer
{
    public static byte[] Render(string html)
        => HtmlWordDocumentBuilder.Build(ApplyFixedTableWidths(html));

    private static string ApplyFixedTableWidths(string html)
    {
        var itemCount = Regex.Matches(
            html,
            "<td class=\"index\"",
            RegexOptions.CultureInvariant).Count;
        var normalized = html
            .Replace(".order { width: 100%; }", ".order { width: 506pt; margin: 4.5pt 0 0 3pt; }", StringComparison.Ordinal)
            .Replace("table { width: 100%; border-collapse: collapse; }", "table { border-collapse: collapse; }", StringComparison.Ordinal)
            .Replace("h1 { margin: 0 0 1.5mm; padding: 1.3mm 0 3.4mm;", "h1 { margin: -3.7mm 0 0; padding: 0 0 2mm;", StringComparison.Ordinal)
            .Replace(".parties { table-layout: fixed; margin: 0 0 2.5mm;", ".parties { table-layout: fixed; margin: 0 0 1.8mm;", StringComparison.Ordinal)
            .Replace(".parties tr:first-child td { padding-bottom: 1.7mm; }", ".parties tr:first-child td { padding-bottom: 0; }", StringComparison.Ordinal)
            .Replace("<tr style=\"height:14.7pt;mso-height-source:exactly\">", "<tr style=\"height:9.5pt;mso-height-source:exactly\">", StringComparison.Ordinal)
            .Replace("<tr style=\"height:24.9pt;mso-height-source:exactly\">", "<tr style=\"height:13.5pt;mso-height-source:exactly\">", StringComparison.Ordinal)
            .Replace("<tr style=\"height:36.3pt;mso-height-source:exactly\">", "<tr style=\"height:25.2pt;mso-height-source:exactly\">", StringComparison.Ordinal)
            .Replace("font-size: 8.8pt; line-height: 1.14;", "font-size: 8pt; line-height: 9pt; mso-line-height-rule: exactly;", StringComparison.Ordinal)
            .Replace(".signature-block { margin-top: -4.8mm; padding-top: 6.5mm;", ".signature-block { margin-top: 0; padding-top: 1mm;", StringComparison.Ordinal)
            .Replace("padding: .8mm 1mm; mso-padding-alt: 2.3pt 2.8pt 2.3pt 2.8pt;", "padding: 0; mso-padding-alt: 0;", StringComparison.Ordinal)
            .Replace(".totals { width: 48%;", ".totals { width: 252.5pt;", StringComparison.Ordinal)
            .Replace(".totals td { padding: .45mm 0 .45mm 2mm; }", ".totals td { height: 12pt; padding: 0 0 0 5.7pt; mso-padding-alt: 0 0 0 5.7pt; line-height: 10pt; mso-line-height-rule: exactly; }", StringComparison.Ordinal)
            .Replace("<table class=\"bank\" width=\"100%\">", "<table class=\"bank\" width=\"675\" style=\"width:506pt;table-layout:fixed\">", StringComparison.Ordinal)
            .Replace("<table class=\"parties\">", "<table class=\"parties\" width=\"675\" style=\"width:506pt;table-layout:fixed\">", StringComparison.Ordinal)
            .Replace("<table class=\"signature\">", "<table class=\"signature\" width=\"675\" style=\"width:506pt;table-layout:fixed\">", StringComparison.Ordinal)
            .Replace("<table class=\"items\" width=\"100%\">", "<table class=\"items\" width=\"675\" style=\"width:506pt;table-layout:fixed\">", StringComparison.Ordinal)
            .Replace("<table class=\"items compact-rows\" width=\"100%\">", "<table class=\"items compact-rows\" width=\"675\" style=\"width:506pt;table-layout:fixed\">", StringComparison.Ordinal)
            .Replace("width=\"5%\" style=\"width:5%\"", "width=\"34\" style=\"width:25.5pt\"", StringComparison.Ordinal)
            .Replace("width=\"50%\" style=\"width:50%\"", "width=\"338\" style=\"width:253.5pt\"", StringComparison.Ordinal)
            .Replace("width=\"10%\" style=\"width:10%\"", "width=\"67\" style=\"width:50.25pt\"", StringComparison.Ordinal)
            .Replace("width=\"7%\" style=\"width:7%\"", "width=\"47\" style=\"width:35.25pt\"", StringComparison.Ordinal)
            .Replace("width=\"13%\" style=\"width:13%\"", "width=\"88\" style=\"width:66pt\"", StringComparison.Ordinal)
            .Replace("width=\"15%\" style=\"width:15%\"", "width=\"101\" style=\"width:75.75pt\"", StringComparison.Ordinal);

        normalized = Regex.Replace(
            normalized,
            "<table class=\"totals\">(.*?)</table>",
            "<table width=\"675\" style=\"width:506pt;table-layout:fixed\"><tr><td width=\"447\" style=\"width:335pt\"></td><td width=\"228\" style=\"width:171pt\"><table class=\"totals\" width=\"228\" style=\"width:171pt\">$1</table></td></tr></table>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        normalized = itemCount > 12
            ? normalized
                .Replace(".items.compact-rows tbody tr { height: 8mm; }", ".items.compact-rows tbody tr { height: 8mm; }", StringComparison.Ordinal)
                .Replace(
                    "<div class=\"signature-block signature-new-page\">",
                    "<p style=\"page-break-before:always;font-size:1pt;line-height:1pt;margin:0\">&nbsp;</p><div class=\"signature-block\" style=\"border-top:0;margin-top:0\">",
                    StringComparison.Ordinal)
            : normalized.Replace(" signature-new-page", string.Empty, StringComparison.Ordinal);

        if (itemCount > 12)
        {
            normalized = Regex.Replace(
                normalized,
                "(<table class=\"items compact-rows\"[^>]*>.*?<tbody>)(.*?)(</tbody>)",
                match => match.Groups[1].Value
                    + match.Groups[2].Value.Replace(
                        "<tr>",
                        "<tr style=\"height:21.5pt;mso-height-source:exactly\">",
                        StringComparison.Ordinal)
                    + match.Groups[3].Value,
                RegexOptions.Singleline | RegexOptions.CultureInvariant);
        }

        return Regex.Replace(
            normalized,
            "<(td|th)([^>]*class=\"(?:index|name|quantity|unit|price|sum)\"[^>]*)>(.*?)</\\1>",
            "<$1$2><p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;line-height:9pt;mso-line-height-rule:exactly\">$3</p></$1>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}
