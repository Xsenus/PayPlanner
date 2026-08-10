using PayPlanner.Api.Services.Documents;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceOfferWordDocumentRenderer
{
    public static byte[] Render(string html)
    {
        var firstPageHtml = InvoiceStandardWordDocumentRenderer.BuildWordHtml(html);
        if (string.Equals(firstPageHtml, html, StringComparison.Ordinal))
        {
            return HtmlWordDocumentBuilder.Build(html);
        }

        var contractMatch = Regex.Match(
            html,
            "(<div class=\"offer-contract-subject\">.*?)(?=<table class=\"signature-table offer-signature-table\">)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!contractMatch.Success)
        {
            return HtmlWordDocumentBuilder.Build(html);
        }

        const string offerStyles = """
    .offer-contract-subject { margin-top: -54pt; font-size: 9pt; line-height: 10.25pt; letter-spacing: -.8pt; }
    .offer-subject-label, .offer-heading, .offer-requisites-heading { font-weight: 700; }
    .offer-section { margin-top: 0; font-size: 9pt; line-height: 10.25pt; letter-spacing: -.8pt; }
    .offer-title, .offer-heading, .offer-clause { margin: 0; }
    .offer-heading { margin-top: 4pt; }
    .offer-page-break-before, .offer-requisites-heading { page-break-before: always; }
    .offer-requisites-table { width: 506pt; table-layout: fixed; margin-top: 10pt; }
    .offer-requisites-table th, .offer-requisites-table td { width: 50%; padding: 0 3pt 3pt; border: 0; vertical-align: top; font-size: 9pt; line-height: 10.25pt; }
    .offer-requisites-table th { padding-bottom: 6pt; text-align: left; }
    .offer-signatures { width: 506pt; table-layout: fixed; margin-top: 25pt; }
    .offer-signatures td { padding: 0; vertical-align: bottom; }
    .offer-signature-role { padding-bottom: 46pt !important; }
    .offer-signature-name { height: 36pt; border-bottom: .75pt solid #000; text-align: center; vertical-align: bottom !important; }
    .offer-signature-caption { text-align: center; font-size: 8pt; line-height: 9pt; }
    .offer-signature-spacer { width: 10%; }
""";

        firstPageHtml = firstPageHtml.Replace("</style>", offerStyles + "\n  </style>", StringComparison.Ordinal);
        firstPageHtml = firstPageHtml.Replace(".items td { height: 78pt;", ".items td { height: 23pt;", StringComparison.Ordinal);
        firstPageHtml = Regex.Replace(
            firstPageHtml,
            "<div class=\"rule\"></div>.*?</table>\\s*</body>",
            contractMatch.Groups[1].Value + "\n</body>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        return HtmlWordDocumentBuilder.Build(firstPageHtml);
    }
}
