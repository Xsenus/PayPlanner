using PayPlanner.Api.Services.Documents;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceStandardWordDocumentRenderer
{
    public static byte[] Render(string html)
    {
        try
        {
        var seller = Extract(html, "<td class=\"doc-title\"[^>]*>(.*?)</td>");
        var bankSource = Extract(html, "<table class=\"bank-grid\">(.*?)</table>");
        var title = Extract(html, "<div class=\"doc-main-title text-c\">(.*?)</div>");
        var parties = Extract(html, "<table class=\"counterparty-table\"[^>]*>(.*?)</table>");
        var items = Extract(html, "<table class=\"items-table\">(.*?)</table>");
        var totals = Extract(html, "<table class=\"totals-table\">(.*?)</table>");
        var summary = Extract(html, "<div class=\"summary-line\">(.*?)</div>");
        var words = Extract(html, "<div class=\"words\">(.*?)</div>");
        var signature = Extract(html, "<table class=\"signature-table\">(.*?)</table>");
        var bankCells = Regex.Matches(bankSource, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();
        if (bankCells.Length < 16)
        {
            throw new InvalidOperationException("Не удалось распознать банковские реквизиты стандартного счета для Word.");
        }

        var partyCells = Regex.Matches(parties, "<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.CultureInvariant)
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();
        if (partyCells.Length < 5)
        {
            throw new InvalidOperationException("Не удалось распознать реквизиты сторон стандартного счета для Word.");
        }

        parties = $$"""
<tr><td width="102" style="width:76pt;font-weight:700">{{WordParagraph(partyCells[0], "12pt")}}</td><td width="573" style="width:430pt">{{WordParagraph(partyCells[1], "12pt")}}</td></tr>
<tr><td colspan="2">{{Spacer("5.2pt")}}</td></tr>
<tr><td width="102" style="width:76pt;font-weight:700">{{WordParagraph(partyCells[2], "12pt")}}</td><td width="573" style="width:430pt">{{WordParagraph(partyCells[3], "12pt")}}</td></tr>
<tr><td colspan="2">{{Spacer("6.4pt")}}</td></tr>
<tr><td colspan="2">{{WordParagraph(partyCells[4], "12pt")}}</td></tr>
""";

        var bank = $$"""
<colgroup><col width="127" /><col width="127" /><col width="76" /><col width="178" /></colgroup>
<tr class="bank-row-1"><td colspan="2">{{WordParagraph(bankCells[0])}}</td><td>{{WordParagraph(bankCells[1])}}</td><td>{{WordParagraph(bankCells[2])}}</td></tr>
<tr class="bank-row-2"><td colspan="2">{{WordParagraph("&nbsp;")}}</td><td>{{WordParagraph(bankCells[3])}}</td><td rowspan="2">{{WordParagraph(bankCells[4])}}</td></tr>
<tr class="bank-row-3"><td colspan="2">{{WordParagraph(bankCells[5])}}</td><td>{{WordParagraph("&nbsp;")}}</td></tr>
<tr class="bank-row-4"><td>{{WordParagraph(bankCells[7])}}</td><td>{{WordParagraph(bankCells[8])}}</td><td rowspan="2">{{WordParagraph(bankCells[9])}}</td><td rowspan="2">{{WordParagraph(bankCells[10])}}</td></tr>
<tr class="bank-row-5"><td colspan="2">{{WordParagraph(bankCells[11])}}<div class="bank-caption">{{WordParagraph(bankCells[14])}}</div></td></tr>
""";

        items = Regex.Replace(items, "<(?:th|td) class=\"vat\"[^>]*>.*?</(?:th|td)>", string.Empty, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        items = WrapTableCells(items, "10pt");
        totals = WrapTableCells(totals, "10pt");
        signature = WrapTableCells(signature, "11pt");

        var wordHtml = $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <style>
    @page { size: A4; margin: 14mm 11.8mm; mso-page-orientation: portrait; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #000; font-family: 'DejaVu Sans Condensed', sans-serif; font-size: 10.5pt; line-height: 12pt; }
    table { border-collapse: collapse; }
    .seller { width: 216pt; margin-top: 24.5pt; font-size: 13.5pt; line-height: 16pt; font-weight: 700; }
    .bank { width: 381pt; table-layout: fixed; margin-left: 3.4pt; font-size: 10.5pt; }
    .bank td { border: .75pt solid #000; padding: 0 3.4pt; vertical-align: top; line-height: 11pt; }
    .bank-row-1 { height: 16pt; mso-height-source: exactly; }
    .bank-row-2 { height: 16pt; mso-height-source: exactly; }
    .bank-row-3 { height: 20pt; mso-height-source: exactly; }
    .bank-row-3 p { position: relative; top: -6pt; }
    .bank-row-4 { height: 17pt; mso-height-source: exactly; }
    .bank-row-5 { height: 47pt; mso-height-source: exactly; }
    .bank-row-5 > td > p:first-child { position: relative; top: -14pt; }
    .bank-caption { margin-top: 7pt; }
    .title { width: 506pt; margin-top: -14pt; padding: 0 0 5pt; border-bottom: 1.5pt solid #000; text-align: center; font-size: 15pt; line-height: 17pt; font-weight: 700; }
    .parties { width: 506pt; table-layout: fixed; margin-top: 17.5pt; }
    .parties td { padding: 0 2.8pt; vertical-align: top; line-height: 12pt; }
    .parties .counterparty-label { width: 76pt; font-weight: 700; }
    .parties .counterparty-value { width: 430pt; padding-left: 0; }
    .parties tr:nth-child(1) td { padding-bottom: 23.7pt; }
    .parties tr:nth-child(2) td { padding-bottom: 19.8pt; }
    .items { width: 506pt; table-layout: fixed; margin-top: 22.8pt; }
    .items th, .items td { border: .75pt solid #000; padding: 0 2.8pt; line-height: 10pt; }
    .items th { height: 17pt; text-align: center; vertical-align: middle; font-size: 9pt; }
    .items td { height: 78pt; vertical-align: top; }
    .items td.name { position: relative; top: -.5pt; }
    .items .num { width: 25.5pt; text-align: center; vertical-align: middle; }
    .items .name { width: 247pt; }
    .items .qty { width: 40.5pt; text-align: center; vertical-align: middle; }
    .items .unit { width: 25.5pt; text-align: center; vertical-align: middle; }
    .items .price { width: 76pt; text-align: right; vertical-align: middle; }
    .items .sum { width: 91.5pt; text-align: right; vertical-align: middle; }
    .totals { width: 177pt; margin: 23pt 0 0 329pt; }
    .totals td { height: 12pt; padding: 0; line-height: 10pt; mso-line-height-rule: exactly; }
    .totals .totals-label { width: 103pt; text-align: right; padding-right: 5.7pt; }
    .totals .totals-value { width: 74pt; text-align: right; font-weight: 700; }
    .summary { margin-top: 15.5pt; }
    .words { margin-top: -6pt; font-weight: 700; }
    .rule { margin-top: 4pt; border-bottom: 1.5pt solid #000; }
    .signature { width: 506pt; table-layout: fixed; margin-top: 94pt; }
    .signature td { vertical-align: bottom; }
    .signature .signature-line { height: 18pt; border-bottom: .75pt solid #000; text-align: center; }
    .signature .signature-caption { padding-top: 2.8pt; text-align: center; font-size: 8pt; }
  </style>
</head>
<body>
  <div class="seller">{{seller}}</div><p style="margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;font-size:1pt;line-height:41pt;mso-line-height-rule:exactly">&nbsp;</p>
  <table class="bank" width="508">{{bank}}</table>
  <div class="title">{{title}}</div>
  <table class="parties" width="675"><colgroup><col width="102" /><col width="573" /></colgroup>{{parties}}</table>
  {{Spacer("9.8pt")}}
  <table class="items" width="675">{{items}}</table>
  {{Spacer("9.9pt")}}
  <table class="totals" width="236">{{totals}}</table>
  <div class="summary">{{summary}}</div>
  <div class="words">{{words}}</div>
  <div class="rule"></div>
  {{Spacer("21.8pt")}}
  <table class="signature" width="675">{{signature}}</table>
</body>
</html>
""";

            return HtmlWordDocumentBuilder.Build(wordHtml);
        }
        catch (InvalidOperationException)
        {
            return HtmlWordDocumentBuilder.Build(html);
        }
    }

    private static string Extract(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new InvalidOperationException("Не удалось распознать стандартный шаблон счета для Word.");
        }

        return match.Groups[1].Value.Trim();
    }

    private static string WordParagraph(string value)
        => WordParagraph(value, "11pt");

    private static string WordParagraph(string value, string lineHeight)
        => $"<p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;line-height:{lineHeight};mso-line-height-rule:exactly\">{value}</p>";

    private static string Spacer(string lineHeight)
        => $"<p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;font-size:1pt;line-height:{lineHeight};mso-line-height-rule:exactly\">&nbsp;</p>";

    private static string WrapTableCells(string html, string lineHeight)
        => Regex.Replace(
            html,
            "<(td|th)([^>]*)>(.*?)</\\1>",
            match => $"<{match.Groups[1].Value}{match.Groups[2].Value}><p style=\"margin:0;mso-margin-top-alt:0;mso-margin-bottom-alt:0;line-height:{lineHeight};mso-line-height-rule:exactly\">{match.Groups[3].Value}</p></{match.Groups[1].Value}>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
}
