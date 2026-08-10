using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Documents;

public static partial class CashOrderWordDocumentRenderer
{
    public static byte[] RenderIncoming(string sourceHtml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHtml);

        var organization = Extract(sourceHtml, "<div class=\"org-line\">(?<value>.*?)</div>");
        var documentMeta = Match(sourceHtml, "<tr><td class=\"value\">(?<number>.*?)</td><td class=\"value\">(?<date>.*?)</td></tr>");
        var payer = Extract(sourceHtml, "<div class=\"line-field\"><span>Принято от</span><div class=\"line\">(?<value>.*?)</div></div>");
        var basis = Extract(sourceHtml, "<div class=\"line-field\"><span>Основание:</span><div class=\"line\">(?<value>.*?)</div></div>");
        var appendix = Extract(sourceHtml, "<div class=\"line-field\"><span>Приложение</span><div class=\"line\">(?<value>.*?)</div></div>");
        var amountRubKop = Extract(sourceHtml, "<td class=\"sum\">(?<value>.*?)</td>");
        var amountWordsBlock = Match(sourceHtml, "<div class=\"amount-words\">\\s*<span>Сумма</span><div class=\"line\">(?<words>.*?)</div><span>руб\\.</span><div class=\"line kopecks\">(?<kopecks>.*?)</div>");
        var amountDigits = Match(sourceHtml, "<div class=\"amount-digits\"><span>Сумма</span><div class=\"line\">(?<rubles>.*?)</div><span>руб\\.</span><div class=\"line\">(?<kopecks>.*?)</div>");
        var receiptDate = Match(sourceHtml, "<div class=\"receipt-date\"><span>от</span><span>\"</span><div class=\"line\">(?<day>.*?)</div><span>\" (?<month>.*?)</span><div class=\"line\">(?<year>.*?)</div>");
        var signer = Extract(sourceHtml, "<div class=\"sign-line\">(?<value>[^<]+)</div><div class=\"sign-label\">\\(расшифровка подписи\\)</div>");
        var cancelNotice = OptionalExtract(sourceHtml, "(?<value><div class=\"cancelled\">.*?</div>)");

        var number = documentMeta.Groups["number"].Value;
        var date = documentMeta.Groups["date"].Value;
        var amountWords = amountWordsBlock.Groups["words"].Value;
        var kopecks = amountWordsBlock.Groups["kopecks"].Value;
        var rubles = amountDigits.Groups["rubles"].Value;
        var day = receiptDate.Groups["day"].Value;
        var month = receiptDate.Groups["month"].Value;
        var year = receiptDate.Groups["year"].Value;

        var html = $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Приходный кассовый ордер {{number}}</title>
  <style>
    @page { size: A4 portrait; margin: 0; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #111; background: #fff; font-family: "DejaVu Sans Condensed", sans-serif; font-size: 6pt; line-height: 6pt; }
    table { border-collapse: collapse; table-layout: fixed; }
    p { margin: 0; padding: 0; }
    td { line-height: 6pt; mso-line-height-rule: exactly; }
    .tight { margin: 0; padding: 0; line-height: 5pt; mso-line-height-rule: exactly; }
    .top-note-text { margin: 0; padding: 0; line-height: 7pt; mso-line-height-rule: exactly; }
    .top-spacer { height: 40pt; line-height: 40pt; mso-line-height-rule: exactly; font-size: 1pt; margin: 0; }
    .page { width: 526pt; margin-top: 0; }
    .page-cell { padding: 0; vertical-align: top; }
    .order-cell { width: 283.5pt; }
    .cut-cell { width: 39.5pt; height: 370pt; border-left: .75pt solid #111; border-right: 1.5pt solid #111; vertical-align: top; padding: 0; }
    .receipt-cell { width: 181.5pt; padding-left: 8pt; vertical-align: top; }
    .cut-table { width: 39.5pt; height: 370pt; }
    .cut-table td { width: 19.75pt; height: 370pt; padding: 0; }
    .cut-dashed { border-right: .75pt dashed #111; }
    .cut-label { width: 19.75pt; text-align: center; font-size: 5.5pt; font-weight: 700; letter-spacing: 2pt; writing-mode: tb-rl; mso-rotate: 90; }
    .top-note { width: 283.5pt; height: 21pt; }
    .top-note td { padding: 0 0 0 157pt; vertical-align: top; font-size: 5pt; line-height: 7pt; mso-line-height-rule: exactly; }
    .header { width: 283.5pt; }
    .header-org { width: 212.5pt; padding: 22.5pt 14pt 0 0; vertical-align: top; }
    .code-cell { width: 71pt; padding: 0; vertical-align: top; }
    .org-line { height: 7pt; border-bottom: 1.5pt solid #111; padding: 0 3pt 1pt 3pt; vertical-align: bottom; font-size: 6pt; }
    .hint { height: 4pt; line-height: 4pt; mso-line-height-rule: exactly; text-align: center; vertical-align: top; font-size: 4.5pt; color: #333; }
    .subdivision { height: 8pt; border-bottom: 1.5pt solid #111; }
    .code { width: 71pt; font-size: 5pt; }
    .code td { border: .75pt solid #111; padding: 0 2pt; text-align: center; vertical-align: middle; }
    .code .label { width: 28pt; border-left: 0; text-align: right; }
    .code .value { width: 43pt; font-size: 6pt; font-weight: 700; }
    .code-row-1 { height: 13pt; }
    .code-row-2 { height: 23pt; }
    .code-row-3 { height: 17pt; }
    .title-meta { width: 283.5pt; height: 32.5pt; }
    .title-cell { width: 205.5pt; padding: 25.5pt 0 0 0; text-align: center; vertical-align: top; font-size: 10pt; line-height: 10.5pt; mso-line-height-rule: exactly; font-weight: 700; }
    .meta-cell { width: 78pt; padding: 0; vertical-align: top; }
    .meta { width: 78pt; font-size: 5pt; }
    .meta td { border: .75pt solid #111; text-align: center; vertical-align: middle; padding: 0; }
    .meta-head { height: 20pt; }
    .meta-value { height: 20pt; font-size: 7pt; font-weight: 700; }
    .ledger { width: 283.5pt; font-size: 5pt; }
    .ledger td { border: .75pt solid #111; padding: 1pt; text-align: center; vertical-align: middle; }
    .ledger-top { height: 7pt; }
    .ledger-head { height: 21pt; }
    .ledger-value { height: 6.5pt; }
    .field { width: 283.5pt; margin-top: 1.5pt; font-size: 5pt; }
    .field td { vertical-align: bottom; padding: 0; }
    .field-label { width: 39pt; white-space: nowrap; }
    .field-line { height: 9pt; border-bottom: .75pt solid #111; padding: 0 4pt 1pt 4pt !important; }
    .blank-line { width: 283.5pt; height: 10pt; border-bottom: .75pt solid #111; }
    .amount { width: 283.5pt; margin-top: 1.5pt; font-size: 5pt; }
    .amount td { vertical-align: bottom; padding: 0; }
    .amount-label { width: 28pt; }
    .amount-words { width: 161pt; height: 9pt; border-bottom: .75pt solid #111; padding: 0 4pt 1pt !important; }
    .amount-rub { width: 20pt; text-align: center; }
    .amount-kop { width: 48pt; height: 9pt; border-bottom: .75pt solid #111; text-align: center; }
    .amount-kop-label { width: 26.5pt; text-align: center; }
    .signature { width: 283.5pt; margin-top: 2pt; font-size: 5pt; }
    .signature td { vertical-align: bottom; padding: 0; }
    .signature-role { width: 76pt; height: 12pt; }
    .signature-part { width: 88pt; text-align: center; }
    .signature-name { width: 98pt; text-align: center; }
    .signature-line { height: 8pt; border-bottom: .75pt solid #111; }
    .signature-hint { height: 4pt; line-height: 4pt; mso-line-height-rule: exactly; vertical-align: top !important; font-size: 4.5pt; color: #333; }
    .receipt { width: 181.5pt; font-size: 5pt; }
    .receipt-org { height: 6pt; border-bottom: 1.5pt solid #111; text-align: center; vertical-align: bottom; font-size: 6pt; }
    .receipt-title { height: 14pt; padding-top: 2pt; text-align: center; vertical-align: top; font-size: 10pt; line-height: 10.5pt; mso-line-height-rule: exactly; font-weight: 700; }
    .receipt-field { width: 181.5pt; margin-top: 2pt; font-size: 5pt; }
    .receipt-field td { padding: 0; vertical-align: bottom; }
    .receipt-label { white-space: nowrap; }
    .receipt-line { height: 9pt; border-bottom: .75pt solid #111; padding: 0 3pt 1pt !important; }
    .receipt-date { width: 181.5pt; margin-top: 2pt; font-size: 5pt; }
    .receipt-date td { height: 9pt; padding: 0; vertical-align: bottom; text-align: center; }
    .receipt-blank { width: 181.5pt; height: 10pt; border-bottom: .75pt solid #111; }
    .receipt-amount { width: 181.5pt; margin-top: 5pt; font-size: 5pt; }
    .receipt-amount td { height: 9pt; padding: 0; vertical-align: bottom; }
    .center-line { border-bottom: .75pt solid #111; text-align: center; }
    .small-hint { height: 4pt; line-height: 4pt; mso-line-height-rule: exactly; text-align: center; vertical-align: top; font-size: 4.5pt; color: #333; }
    .receipt-sign { width: 181.5pt; margin-top: 3pt; font-size: 5pt; }
    .receipt-sign td { padding: 0; vertical-align: bottom; }
    .stamp { height: 13pt; padding-top: 4pt; font-size: 6pt; }
    .cancelled { margin: 0 0 4pt; border: .75pt solid #111; padding: 3pt; font-weight: 700; }
  </style>
</head>
<body>
  <p class="top-spacer">&nbsp;</p>
  <table class="page" width="702"><tr>
    <td class="page-cell order-cell" width="378">
      <table class="top-note" width="378"><tr height="28" style="height:21pt;mso-height-source:userset"><td>Унифицированная форма № КО-1<br>Утверждена постановлением Госкомстата<br>России от 18.08.98 № 88</td></tr></table>
      <table class="header" width="378"><tr height="72" style="height:54pt;mso-height-source:userset">
        <td class="header-org" width="283"><table width="265"><tr height="9" style="height:7pt;mso-height-source:userset"><td class="org-line"><p class="tight">{{organization}}</p></td></tr><tr height="5" style="height:4pt;mso-height-source:userset"><td class="hint"><p class="tight">(организация)</p></td></tr><tr height="11" style="height:8pt;mso-height-source:userset"><td class="subdivision"></td></tr><tr height="5" style="height:4pt;mso-height-source:userset"><td class="hint"><p class="tight">(структурное подразделение)</p></td></tr></table></td>
        <td class="code-cell" width="95"><table class="code" width="95"><tr><td class="label code-row-1" width="38"><p class="tight">Код</p></td><td class="code-row-1" width="57"></td></tr><tr><td class="label code-row-2" width="38"><p class="tight">Форма по ОКУД</p></td><td class="value code-row-2" width="57"><p class="tight">0310002</p></td></tr><tr><td class="label code-row-3" width="38"><p class="tight">по ОКПО</p></td><td class="code-row-3" width="57"></td></tr></table></td>
      </tr></table>
      <table class="title-meta" width="378"><tr height="43" style="height:32.5pt;mso-height-source:userset"><td class="title-cell" width="274"><p style="margin:0;line-height:10.5pt;mso-line-height-rule:exactly">ПРИХОДНЫЙ КАССОВЫЙ ОРДЕР</p></td><td class="meta-cell" width="104"><table class="meta" width="104"><tr><td class="meta-head"><p class="tight">Номер<br>документа</p></td><td class="meta-head"><p class="tight">Дата<br>составления</p></td></tr><tr><td class="meta-value"><p class="tight">{{number}}</p></td><td class="meta-value"><p class="tight">{{date}}</p></td></tr></table></td></tr></table>
      {{cancelNotice}}
      <table class="ledger" width="378">
        <tr class="ledger-top" height="9" style="height:7pt;mso-height-source:userset"><td rowspan="2" width="38"><p class="tight">Дебет</p></td><td colspan="3" width="204"><p class="tight">Кредит</p></td><td rowspan="2" width="52"><p class="tight">Сумма<br>руб. коп.</p></td><td rowspan="2" width="84"><p class="tight">Код целевого<br>назначения</p></td></tr>
        <tr class="ledger-head" height="28" style="height:21pt;mso-height-source:userset"><td width="50"><p class="tight">код струк-<br>турного<br>подразде-<br>ления</p></td><td width="70"><p class="tight">корреспон-<br>дирующий счет,<br>субсчет</p></td><td width="84"><p class="tight">код аналити-<br>ческого учета</p></td></tr>
        <tr class="ledger-value" height="9" style="height:6.5pt;mso-height-source:userset"><td></td><td></td><td></td><td></td><td><p class="tight">{{amountRubKop}}</p></td><td></td></tr>
      </table>
      <table class="field" width="378"><tr><td class="field-label"><p class="tight">Принято от</p></td><td class="field-line"><p class="tight">{{payer}}</p></td></tr></table>
      <table class="field" width="378"><tr><td class="field-label"><p class="tight">Основание:</p></td><td class="field-line"><p class="tight">{{basis}}</p></td></tr></table>
      <table class="blank-line" width="378"><tr><td></td></tr></table>
      <table class="amount" width="378"><tr><td class="amount-label"><p class="tight">Сумма</p></td><td class="amount-words"><p class="tight">{{amountWords}}</p></td><td class="amount-rub"><p class="tight">руб.</p></td><td class="amount-kop"><p class="tight">{{kopecks}}</p></td><td class="amount-kop-label"><p class="tight">коп.</p></td></tr></table>
      <table class="field" width="378"><tr><td class="field-label"><p class="tight">В том числе</p></td><td class="field-line"><p class="tight">Без налога (НДС) 0,00 руб.</p></td></tr></table>
      <table class="field" width="378"><tr><td class="field-label"><p class="tight">Приложение</p></td><td class="field-line"><p class="tight">{{appendix}}</p></td></tr></table>
      {{SignatureRow("Главный бухгалтер", signer)}}
      {{SignatureRow("Получил кассир", signer)}}
    </td>
    <td class="page-cell cut-cell" width="53"><table class="cut-table" width="53"><tr><td class="cut-dashed"></td><td class="cut-label">Л и н и я&nbsp;&nbsp;о т р е з а</td></tr></table></td>
    <td class="page-cell receipt-cell" width="242" style="padding-top:11.5pt">
      <table class="receipt" width="242">
        <tr><td class="receipt-org">{{organization}}</td></tr><tr><td class="hint">(организация)</td></tr><tr><td class="receipt-title">КВИТАНЦИЯ</td></tr>
      </table>
      <table class="receipt-field" width="242" style="margin-top:12pt"><tr><td>к приходному кассовому ордеру №</td><td class="receipt-line" width="34">{{number}}</td></tr></table>
      <table class="receipt-date" width="242"><tr><td width="14">от</td><td width="10">&quot;</td><td class="center-line" width="29">{{day}}</td><td width="62">&quot; {{month}}</td><td class="center-line" width="44">{{year}}</td><td width="12">г.</td></tr></table>
      {{ReceiptField("Принято от", payer)}}
      {{ReceiptField("Основание:", basis)}}
      <table class="receipt-blank" width="242"><tr><td></td></tr></table>
      <table class="receipt-amount" width="242"><tr><td width="31">Сумма</td><td class="center-line" width="100">{{rubles}}</td><td width="20">руб.</td><td class="center-line" width="28">{{kopecks}}</td><td width="18">коп.</td></tr><tr><td></td><td class="small-hint">(цифрами)</td><td></td><td></td><td></td></tr></table>
      <table class="receipt-field" width="242"><tr><td class="receipt-line">{{amountWords}}</td></tr><tr><td class="small-hint">(прописью)</td></tr></table>
      <table class="receipt-field" width="242"><tr><td width="45">В том числе</td><td class="receipt-line">Без налога (НДС) 0,00 руб.</td></tr></table>
      <table class="receipt-date" width="242"><tr><td width="10">&quot;</td><td class="center-line" width="29">{{day}}</td><td width="62">&quot; {{month}}</td><td class="center-line" width="44">{{year}}</td><td width="12">г.</td></tr></table>
      <div class="stamp">М.П. (штампа)</div>
      {{ReceiptSignatureRow("Главный бухгалтер", signer)}}
      {{ReceiptSignatureRow("Кассир", signer)}}
    </td>
  </tr></table>
</body>
</html>
""";

        return CashOrderOpenXmlDocumentBuilder.Build(new CashOrderWordData(
            organization,
            number,
            date,
            payer,
            basis,
            appendix,
            amountRubKop,
            rubles,
            kopecks,
            amountWords,
            day,
            month,
            year,
            signer,
            cancelNotice));
    }

    private static string SignatureRow(string role, string signer) => $$"""
<table class="signature" width="378"><tr><td class="signature-role" rowspan="2">{{role}}</td><td class="signature-part signature-line"></td><td class="signature-name signature-line">{{signer}}</td></tr><tr><td class="signature-hint">(подпись)</td><td class="signature-hint">(расшифровка подписи)</td></tr></table>
""";

    private static string ReceiptField(string label, string value) => $$"""
<table class="receipt-field" width="242"><tr><td class="receipt-label">{{label}}</td><td class="receipt-line">{{value}}</td></tr></table>
""";

    private static string ReceiptSignatureRow(string role, string signer) => $$"""
<table class="receipt-sign" width="242"><tr><td width="68" rowspan="2">{{role}}</td><td class="signature-line" width="48"></td><td class="signature-line" width="65">{{signer}}</td></tr><tr><td class="signature-hint">(подпись)</td><td class="signature-hint">(расшифровка подписи)</td></tr></table>
""";

    private static Match Match(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success
            ? match
            : throw new InvalidOperationException("Не удалось извлечь реквизиты ПКО для Word-документа.");
    }

    private static string Extract(string html, string pattern)
        => Match(html, pattern).Groups["value"].Value;

    private static string OptionalExtract(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }
}
