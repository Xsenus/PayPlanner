using PayPlanner.Api.Services.Documents;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceOfferWordDocumentRenderer
{
    public static byte[] Render(string html)
    {
        var overlays = InvoiceStandardWordDocumentRenderer.BuildImageOverlays(html, offer: true);
        var normalizedHtml = InvoiceStandardWordDocumentRenderer.RemoveEmbeddedImages(html);
        var firstPageHtml = InvoiceStandardWordDocumentRenderer.BuildWordHtml(normalizedHtml);
        if (string.Equals(firstPageHtml, html, StringComparison.Ordinal))
        {
            return HtmlWordDocumentBuilder.Build(normalizedHtml, overlays);
        }

        var contractMatch = Regex.Match(
            normalizedHtml,
            "(<div class=\"offer-contract-subject\">.*?)(?=<table class=\"signature-table offer-signature-table\">)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!contractMatch.Success)
        {
            return HtmlWordDocumentBuilder.Build(normalizedHtml, overlays);
        }

        const string offerStyles = """
    .offer-contract-subject { margin-top: -54pt; font-size: 9pt; line-height: 9.4pt; letter-spacing: -.8pt; }
    .offer-subject-label, .offer-heading, .offer-requisites-heading, .offer-requisites-heading-word { font-weight: 700; }
    .offer-section { margin-top: 0; font-size: 9pt; line-height: 9.4pt; letter-spacing: -.8pt; }
    .offer-title, .offer-heading, .offer-clause { margin: 0; }
    .offer-heading { margin-top: 4pt; }
    .offer-page-break-before, .offer-requisites-heading { page-break-before: always; }
    .offer-requisites-heading-word { margin-top: 0; }
    .offer-requisites-table { width: 506pt; table-layout: fixed; margin-top: 10pt; }
    .offer-requisites-table th, .offer-requisites-table td { width: 50%; padding: 0 3pt 3pt; border: 0; vertical-align: top; font-size: 9pt; line-height: 10.25pt; }
    .offer-requisites-table th { padding-bottom: 6pt; text-align: left; }
    .offer-signatures { width: 506pt; table-layout: fixed; margin-top: 78.4pt; }
    .offer-signatures td { padding: 0; vertical-align: bottom; }
    .offer-signature-role { padding-bottom: 46pt !important; }
    .offer-signature-name { height: 36pt; border-bottom: .75pt solid #000; text-align: center; vertical-align: bottom !important; }
    .offer-signature-caption { text-align: center; font-size: 8pt; line-height: 9pt; }
    .offer-signature-spacer { width: 10%; }
""";

        firstPageHtml = firstPageHtml.Replace("</style>", offerStyles + "\n  </style>", StringComparison.Ordinal);
        firstPageHtml = firstPageHtml.Replace(".items td { height: 78pt;", ".items td { height: 23pt;", StringComparison.Ordinal);
        var contractHtml = contractMatch.Groups[1].Value
            .Replace(
                "<div class=\"offer-heading offer-page-break-before offer-terms-page\">",
                "<p style=\"page-break-before:always;font-size:1pt;line-height:1pt;margin:0\">&nbsp;</p><div class=\"offer-heading offer-terms-page\">",
                StringComparison.Ordinal)
            .Replace(
                "<div class=\"offer-clause offer-page-break-before offer-payment-page\">",
                "<p style=\"page-break-before:always;font-size:1pt;line-height:14pt;margin:0\">&nbsp;</p><div class=\"offer-clause offer-payment-page\">",
                StringComparison.Ordinal)
            .Replace(
                "<div class=\"offer-requisites-heading\">",
                "<p style=\"page-break-before:always;font-size:1pt;line-height:1pt;margin:0\">&nbsp;</p><div class=\"offer-requisites-heading-word\">",
                StringComparison.Ordinal);
        contractHtml = Regex.Replace(
            contractHtml,
            "<(td|th)([^>]*)>(.*?)</\\1>",
            "<$1$2><p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;line-height:10pt;mso-line-height-rule:exactly\">$3</p></$1>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        contractHtml = Regex.Replace(
            contractHtml,
            "(<div class=\"offer-(?:clause|heading|title|subject-label)[^\"]*\">)(.*?)(</div>)",
            "$1<p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;line-height:9.4pt;mso-line-height-rule:exactly\">$2</p>$3",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        contractHtml = contractHtml.Replace(
            "<table class=\"offer-signatures\">",
            "<p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;font-size:1pt;line-height:59.4pt;mso-line-height-rule:exactly\">&nbsp;</p><table class=\"offer-signatures\">",
            StringComparison.Ordinal);
        firstPageHtml = Regex.Replace(
            firstPageHtml,
            "<div class=\"rule\"></div>.*?</table>\\s*</body>",
            contractHtml + "\n</body>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        return HtmlWordDocumentBuilder.Build(firstPageHtml, overlays);
    }
}
