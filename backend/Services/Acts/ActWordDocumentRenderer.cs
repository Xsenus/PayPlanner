using System.Text.RegularExpressions;
using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Services.Acts;

public static class ActWordDocumentRenderer
{
    private static readonly Regex CounterpartyTableRegex = new(
        "<table class=\"counterparty-table\"[\\s\\S]*?</table>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TotalsTableRegex = new(
        "<table class=\"totals-table mar-t-5\"[\\s\\S]*?</table>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SummaryRegex = new(
        "<div class=\"summary-block\">\\s*<div class=\"mar-t-5\">([\\s\\S]*?)</div>\\s*<div class=\"summary-words\">([\\s\\S]*?)</div>\\s*</div>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SignatureCellRegex = new(
        "<td class=\"signature-party-cell\" width=\"([^\"]+)\">([\\s\\S]*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ImageRegex = new(
        "<img\\s+src=\"data:(image/(?<type>png|jpeg));base64,(?<data>[^\"]+)\"[^>]*class=\"(?<class>signature|seal)\"[^>]*/>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CounterpartyRowRegex = new(
        "<tr>([\\s\\S]*?)</tr>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CounterpartyValueRegex = new(
        "<td class=\"counterparty-value[^\"]*\">([\\s\\S]*?)</td>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    public static byte[] Render(string html)
    {
        var overlays = new List<WordImageOverlay>();
        var withoutImages = ImageRegex.Replace(html, match =>
        {
            var isSignature = string.Equals(match.Groups["class"].Value, "signature", StringComparison.OrdinalIgnoreCase);
            var type = match.Groups["type"].Value.ToLowerInvariant();
            overlays.Add(new WordImageOverlay(
                Convert.FromBase64String(match.Groups["data"].Value),
                $"image/{type}",
                type == "jpeg" ? "jpg" : type,
                isSignature ? 42.02 : 150.77,
                isSignature ? 368.4 : 338.4,
                isSignature ? 67.5 : 111.75,
                isSignature ? 37.5 : 112.5,
                isSignature ? "Подпись" : "Печать",
                isSignature ? 2 : 1));
            return string.Empty;
        });

        return HtmlWordDocumentBuilder.Build(TransformHtml(withoutImages), overlays);
    }

    private static string TransformHtml(string source)
    {
        var html = source
            .Replace("@page { size: A4; margin: 10mm 7.3mm 10mm 15mm; }", "@page { size: A4; margin: 10mm 8.4mm 10mm 15.9mm; }", StringComparison.Ordinal)
            .Replace("@page WordSection1 { size: 595.3pt 841.9pt; margin: 28.35pt 20.69pt 28.35pt 42.52pt;", "@page WordSection1 { size: 595.3pt 841.9pt; margin: 28.35pt 23.81pt 28.35pt 45.07pt;", StringComparison.Ordinal)
            .Replace("class=\"counterparty-value pad-t-5\"", "class=\"counterparty-value\"", StringComparison.Ordinal)
            .Replace("<table class=\"items-table\" cellspacing=\"0\">", "<table class=\"items-table\" cellspacing=\"0\" width=\"100%\" style=\"width:100%;table-layout:fixed;margin-top:0;\">", StringComparison.Ordinal)
            .Replace("<th class=\"bor item-index\">", "<th class=\"bor item-index\" width=\"5%\">", StringComparison.Ordinal)
            .Replace("<th class=\"bor item-name\">", "<th class=\"bor item-name\" width=\"45%\">", StringComparison.Ordinal)
            .Replace("<th class=\"bor item-qty\">", "<th class=\"bor item-qty\" width=\"10%\">", StringComparison.Ordinal)
            .Replace("<th class=\"bor item-unit\">", "<th class=\"bor item-unit\" width=\"10%\">", StringComparison.Ordinal)
            .Replace("<th class=\"bor item-price\">", "<th class=\"bor item-price\" width=\"15%\">", StringComparison.Ordinal)
            .Replace("<th class=\"bor item-sum\">", "<th class=\"bor item-sum\" width=\"15%\">", StringComparison.Ordinal)
            .Replace("<td class=\"bor item-index\">", "<td class=\"bor item-index\" width=\"5%\" align=\"center\">", StringComparison.Ordinal)
            .Replace("<td class=\"bor item-name\">", "<td class=\"bor item-name\" width=\"45%\" align=\"justify\" style=\"text-align:justify;\">", StringComparison.Ordinal)
            .Replace("<td class=\"bor item-qty\">", "<td class=\"bor item-qty\" width=\"10%\" align=\"right\">", StringComparison.Ordinal)
            .Replace("<td class=\"bor item-unit\">", "<td class=\"bor item-unit\" width=\"10%\" align=\"center\">", StringComparison.Ordinal)
            .Replace("<td class=\"bor item-price\">", "<td class=\"bor item-price\" width=\"15%\" align=\"right\">", StringComparison.Ordinal)
            .Replace("<td class=\"bor item-sum\">", "<td class=\"bor item-sum\" width=\"15%\" align=\"right\">", StringComparison.Ordinal)
            .Replace("<table class=\"signature-table\" cellspacing=\"0\" width=\"100%\">", "<table class=\"signature-table\" cellspacing=\"0\" width=\"526\" style=\"width:526pt;table-layout:fixed;mso-table-layout-alt:fixed;\">", StringComparison.Ordinal)
            .Replace("<td class=\"signature-gap\" width=\"2.575%\"></td>", "<td class=\"signature-gap\" width=\"15\" style=\"width:15.15pt;\"></td>", StringComparison.Ordinal);

        html = html.Replace("</style>", WordCss + "</style>", StringComparison.OrdinalIgnoreCase);
        html = ReplaceCounterpartyRows(html);
        html = ReplaceTotals(html);
        html = ReplaceSummary(html);
        return ReplaceSignatureCells(html);
    }

    private static string ReplaceCounterpartyRows(string html)
    {
        var table = CounterpartyTableRegex.Match(html);
        if (!table.Success)
        {
            return html;
        }

        var replacement = CounterpartyRowRegex.Replace(table.Value, match =>
        {
            var rowHtml = match.Groups[1].Value;
            var valueMatch = CounterpartyValueRegex.Match(rowHtml);
            var plainText = valueMatch.Success
                ? WhitespaceRegex.Replace(
                    System.Net.WebUtility.HtmlDecode(HtmlTagRegex.Replace(valueMatch.Groups[1].Value, " ")),
                    " ").Trim()
                : string.Empty;
            var lineCount = Math.Max(1, (plainText.Length + 89) / 90);
            var height = lineCount == 1 ? 12.9 : 13.8 + (lineCount - 1) * 10.8;
            return $"<tr style=\"height:{height.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}pt;mso-height-rule:exactly;\">{rowHtml}</tr>";
        });
        return ReplaceMatch(html, table, replacement);
    }

    private static string ReplaceTotals(string html)
    {
        var match = TotalsTableRegex.Match(html);
        if (!match.Success)
        {
            return html;
        }

        var table = match.Value
            .Replace("<table class=\"totals-table mar-t-5\" cellspacing=\"0\">", "<table class=\"totals-table mar-t-5\" cellspacing=\"0\" width=\"100%\" style=\"width:100%;table-layout:fixed;mso-table-layout-alt:fixed;\">", StringComparison.Ordinal)
            .Replace("<tr>", "<tr style=\"height:13.8pt;mso-height-rule:exactly;\">", StringComparison.Ordinal)
            .Replace("<td class=\"totals-label\">", "<td class=\"totals-label\" width=\"52%\" align=\"right\" style=\"width:52%;\">", StringComparison.Ordinal)
            .Replace("<td class=\"totals-value\">", "<td class=\"totals-value\" width=\"48%\" align=\"right\" style=\"width:48%;\">", StringComparison.Ordinal);
        var positioned = "<table cellspacing=\"0\" cellpadding=\"0\" width=\"100%\" style=\"width:100%;table-layout:fixed;mso-table-layout-alt:fixed;\"><tr><td width=\"69%\" style=\"width:69%;\"></td><td width=\"31%\" style=\"width:31%;padding-top:6.3pt;\">" + table + "</td></tr></table>";
        return ReplaceMatch(html, match, positioned);
    }

    private static string ReplaceSummary(string html)
    {
        var match = SummaryRegex.Match(html);
        if (!match.Success)
        {
            return html;
        }

        var replacement = "<table cellspacing=\"0\" cellpadding=\"0\" width=\"100%\" style=\"width:100%;\"><tr style=\"height:12pt;mso-height-rule:exactly;\"><td valign=\"top\" style=\"padding-top:1.2pt;line-height:10.8pt;mso-line-height-rule:exactly;\">" + match.Groups[1].Value.Trim() + "</td></tr><tr style=\"height:10.8pt;mso-height-rule:exactly;\"><td valign=\"top\" style=\"font-weight:bold;line-height:10.8pt;mso-line-height-rule:exactly;\">" + match.Groups[2].Value.Trim() + "</td></tr></table>";
        return ReplaceMatch(html, match, replacement);
    }

    private static string ReplaceSignatureCells(string html)
    {
        var matches = SignatureCellRegex.Matches(html);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var match = matches[index];
            var role = Regex.Match(match.Groups[2].Value, "<div class=\"signature-role\">([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
            var party = Regex.Match(match.Groups[2].Value, "<div class=\"signature-party\">([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
            var sign = Regex.Match(match.Groups[2].Value, "<div class=\"signature-sign-cell\">([\\s\\S]*?)</div>", RegexOptions.IgnoreCase);
            if (!role.Success || !party.Success || !sign.Success)
            {
                continue;
            }

            var outerPadding = index == 1
                ? "padding:0 0 0 1.4pt;mso-padding-alt:0pt 0pt 0pt 1.4pt;"
                : "padding:0;mso-padding-alt:0pt 0pt 0pt 0pt;";
            var widthAttribute = index == 1 ? "256" : "255";
            var widthStyle = index == 1 ? "255.85pt" : "255pt";
            var replacement = "<td class=\"signature-party-cell\" width=\"" + widthAttribute + "\" valign=\"top\" style=\"width:" + widthStyle + ";" + outerPadding + "\"><table cellspacing=\"0\" cellpadding=\"0\" width=\"100%\" style=\"width:100%;table-layout:fixed;mso-table-layout-alt:fixed;mso-cellspacing:0;mso-padding-alt:0pt 0pt 0pt 0pt;\"><tr style=\"height:17.2pt;mso-height-rule:exactly;\"><td valign=\"top\" class=\"signature-role\" style=\"padding:0;margin:0;mso-padding-alt:0pt 0pt 0pt 0pt;\">" + role.Groups[1].Value.Trim() + "</td></tr><tr style=\"height:26.3pt;mso-height-rule:exactly;\"><td valign=\"top\" class=\"signature-party\" style=\"padding:0;margin:0;mso-padding-alt:0pt 0pt 0pt 0pt;\">" + party.Groups[1].Value.Trim() + "</td></tr><tr><td valign=\"top\" class=\"signature-sign-cell\" style=\"padding:0;margin:0;mso-padding-alt:0pt 0pt 0pt 0pt;\">" + sign.Groups[1].Value.Trim() + "</td></tr></table></td>";
            html = ReplaceMatch(html, match, replacement);
        }

        return html;
    }

    private static string ReplaceMatch(string source, Match match, string replacement) =>
        source.Remove(match.Index, match.Length).Insert(match.Index, replacement);

    private const string WordCss = """

    .counterparty-table td { padding-top:0 !important; padding-bottom:0 !important; line-height:10.8pt !important; mso-line-height-rule:exactly; }
    .items-table th, .items-table td { line-height:10.8pt !important; mso-line-height-rule:exactly; padding-top:0 !important; padding-bottom:0 !important; }
    .summary-block { margin:0 !important; }
    .summary-block > div { margin:0 !important; mso-margin-top-alt:0; mso-margin-bottom-alt:0; line-height:10.8pt !important; mso-line-height-rule:exactly; }
    .signature-area { margin-top:4.5pt !important; }
    .signature-table { margin-top:0 !important; }
    .signature-party, .signature-sign-cell { margin:0 !important; padding:0 !important; }
  """;
}
