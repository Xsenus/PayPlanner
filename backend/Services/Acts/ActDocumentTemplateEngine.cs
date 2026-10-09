using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Tax;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Invoices;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Services.Acts;

public static class ActDocumentTemplateEngine
{
    public const string DefaultTemplateKey = "act-standard";
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Regex TokenRegex = new("""\{\{\s*([a-zA-Z0-9\._]+)\s*\}\}""", RegexOptions.Compiled);
    private static readonly Regex LineItemsRegex = new("""\{\{#lineItems\}\}([\s\S]*?)\{\{/lineItems\}\}""", RegexOptions.Compiled);
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";
    private static readonly JsonSerializerOptions ClientProfileJsonOptions = new(JsonSerializerDefaults.Web);
    public static readonly string DefaultTemplateHtml = """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>{{document.title}}</title>
  <style>
    @page { size: A4; margin: 15mm 14mm; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #000; font-family: Arial, "DejaVu Sans", sans-serif; font-size: 12px; line-height: 1.3; }
    .document { width: 100%; max-width: 182mm; margin: 0 auto; }
    h1 { margin: 8px 0 14px; text-align: center; font-size: 17px; line-height: 1.2; }
    table { width: 100%; border-collapse: collapse; }
    .parties td { padding: 3px 4px; vertical-align: top; }
    .label { width: 17%; white-space: nowrap; }
    .party { font-weight: 600; overflow-wrap: anywhere; }
    .items { margin: 10px 0 7px; }
    .items th, .items td { border: 1px solid #000; padding: 4px 5px; vertical-align: top; }
    .items th { text-align: center; font-weight: 700; }
    .num, .qty, .unit { text-align: center; }
    .price, .sum, .total-value { text-align: right; white-space: nowrap; }
    .num { width: 5%; }
    .name { width: 53%; overflow-wrap: anywhere; }
    .qty { width: 8%; }
    .unit { width: 9%; }
    .price, .sum { width: 12.5%; }
    .totals td { padding: 2px 0; }
    .total-label { text-align: right; padding-right: 10px !important; font-weight: 700; }
    .total-value { width: 17%; }
    .summary { margin-top: 9px; }
    .summary-words { margin-top: 3px; font-weight: 700; }
    .acceptance { margin-top: 14px; line-height: 1.4; }
    .comment { margin-top: 10px; }
    .signatures { margin-top: 24px; table-layout: fixed; }
    .signatures > tbody > tr > td { width: 48%; vertical-align: top; }
    .signatures > tbody > tr > .gap { width: 4%; }
    .role { margin-bottom: 7px; font-weight: 700; }
    .party-name { min-height: 34px; }
    .sign-line { min-height: 72px; text-align: center; }
    .sign-content { width: 100%; min-height: 72px; table-layout: fixed; }
    .sign-content td { padding: 0; border: 0; vertical-align: middle; }
    .sign-content .signature-cell { width: 24%; }
    .sign-content .seal-cell { width: 28%; }
    .sign-content .mark-cell { width: 48%; text-align: right; }
    .sign-content .signature { display: block; width: 76px; height: 42px; margin: 0 auto; object-fit: contain; }
    .sign-content .seal { display: block; width: 90px; height: 90px; margin: 0 auto; object-fit: contain; opacity: .92; }
    .mark { white-space: nowrap; }
    @media print { thead { display: table-header-group; } tr { break-inside: avoid; } }
  </style>
</head>
<body>
  <main class="document">
    <h1>{{document.title}}</h1>
    <table class="parties"><tbody>
      <tr><td class="label">Исполнитель:</td><td class="party">{{seller.requisitesHtml}}</td></tr>
      <tr><td class="label">Заказчик:</td><td class="party">{{buyer.requisitesHtml}}</td></tr>
      <tr><td class="label">Основание:</td><td class="party">{{act.basis}}</td></tr>
    </tbody></table>
    <table class="items">
      <thead><tr><th class="num">№</th><th class="name">Наименование работ, услуг</th><th class="qty">Кол-во</th><th class="unit">Ед.</th><th class="price">Цена</th><th class="sum">Сумма</th></tr></thead>
      <tbody>{{#lineItems}}<tr><td class="num">{{index}}</td><td class="name">{{name}}</td><td class="qty">{{quantity}}</td><td class="unit">{{unit}}</td><td class="price">{{price}}</td><td class="sum">{{total}}</td></tr>{{/lineItems}}</tbody>
    </table>
    <table class="totals"><tbody>
      <tr><td class="total-label">Итого:</td><td class="total-value">{{act.total}}</td></tr>
      <tr><td class="total-label">В том числе НДС:</td><td class="total-value">{{act.vat}}</td></tr>
    </tbody></table>
    <div class="summary">Всего оказано услуг {{act.itemsCount}}, на сумму {{act.total}} руб.</div>
    <div class="summary-words">{{act.totalWords}}</div>
    <div class="acceptance">Вышеперечисленные услуги выполнены полностью и в срок. Заказчик претензий по объему, качеству и срокам оказания услуг не имеет.</div>
    {{act.commentHtml}}
    <table class="signatures"><tbody><tr>
      <td><div class="role">ИСПОЛНИТЕЛЬ</div><div class="party-name">{{seller.signaturePartyName}}</div><div class="sign-line"><table class="sign-content"><tbody><tr><td class="signature-cell">{{seller.signatureHtml}}</td><td class="seal-cell">{{seller.sealHtml}}</td><td class="mark-cell"><span class="mark">/ {{seller.signatoryShortName}} /</span></td></tr></tbody></table></div></td>
      <td class="gap"></td>
      <td><div class="role">ЗАКАЗЧИК</div><div class="party-name">{{buyer.signaturePartyName}}</div><div class="sign-line"><span class="mark">/ {{buyer.signatoryShortName}} /</span></div></td>
    </tr></tbody></table>
  </main>
</body>
</html>
""";

    public static InvoiceDocumentTemplateSettingsDto CreateDefaultSettings()
        => CreateResponse(new ActDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = DefaultTemplateKey,
            PrintWithSignatureAndSealByDefault = false,
            Templates =
            [
                new ActDocumentTemplateStorageItem
                {
                    Key = DefaultTemplateKey,
                    Name = "Стандартный акт",
                    Description = "Печатный шаблон акта выполненных работ с реквизитами, услугами и подписями.",
                    Html = DefaultTemplateHtml,
                },
            ],
        });

    public static InvoiceDocumentTemplateSettingsDto CreateResponse(ActDocumentTemplateSettingsStorage storage)
    {
        var templates = storage.Templates
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Html))
            .GroupBy(item => item.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new InvoiceDocumentTemplateDto
            {
                Key = item.Key.Trim(),
                Name = item.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                Html = IsRetiredManagedDefaultTemplate(item.Key, item.Html)
                    ? DefaultTemplateHtml
                    : item.Html.Trim(),
            })
            .ToList();

        if (templates.Count == 0)
        {
            return CreateDefaultSettings();
        }

        var activeTemplateKey = templates.Any(item => string.Equals(item.Key, storage.ActiveTemplateKey, StringComparison.OrdinalIgnoreCase))
            ? templates.First(item => string.Equals(item.Key, storage.ActiveTemplateKey, StringComparison.OrdinalIgnoreCase)).Key
            : templates[0].Key;

        return new InvoiceDocumentTemplateSettingsDto
        {
            ActiveTemplateKey = activeTemplateKey,
            PrintWithSignatureAndSealByDefault = storage.PrintWithSignatureAndSealByDefault,
            Templates = templates,
            Tokens = GetAvailableTokens(),
            DefaultTemplateHtml = DefaultTemplateHtml,
        };
    }

    private static bool IsRetiredManagedDefaultTemplate(string? key, string? html)
        => string.Equals(key?.Trim(), DefaultTemplateKey, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(html)
            && html.Contains("DejaVu Sans Condensed", StringComparison.Ordinal)
            && html.Contains("@page WordSection1", StringComparison.Ordinal)
            && html.Contains(".name { width: 45%;", StringComparison.Ordinal)
            && html.Contains("signature-party-cell", StringComparison.Ordinal)
            && html.Contains("{{seller.signatureHtml}}", StringComparison.Ordinal);

    public static ActDocumentTemplateSettingsStorage NormalizeRequest(InvoiceDocumentTemplateSettingsRequest request)
        => new()
        {
            ActiveTemplateKey = request.ActiveTemplateKey.Trim(),
            PrintWithSignatureAndSealByDefault = request.PrintWithSignatureAndSealByDefault,
            Templates = request.Templates
                .Where(static item => !string.IsNullOrWhiteSpace(item.Key) || !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Html))
                .Select(item => new ActDocumentTemplateStorageItem
                {
                    Key = item.Key.Trim(),
                    Name = item.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                    Html = item.Html.Trim(),
                })
                .ToList(),
        };

    public static string Serialize(ActDocumentTemplateSettingsStorage storage)
        => JsonSerializer.Serialize(storage);

    public static ActDocumentTemplateSettingsStorage? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ActDocumentTemplateSettingsStorage>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<InvoiceDocumentTemplateTokenDto> GetAvailableTokens()
        =>
        [
            new() { Key = "document.title", Category = "Акт", Description = "Полный заголовок акта", IsRepeating = false },
            new() { Key = "act.number", Category = "Акт", Description = "Номер акта", IsRepeating = false },
            new() { Key = "act.date", Category = "Акт", Description = "Дата акта", IsRepeating = false },
            new() { Key = "act.dateLong", Category = "Акт", Description = "Дата акта прописью", IsRepeating = false },
            new() { Key = "act.basis", Category = "Акт", Description = "Основание оказания услуг", IsRepeating = false },
            new() { Key = "act.total", Category = "Акт", Description = "Итоговая сумма", IsRepeating = false },
            new() { Key = "act.totalWords", Category = "Акт", Description = "Итоговая сумма прописью", IsRepeating = false },
            new() { Key = "act.vat", Category = "Акт", Description = "Сумма НДС", IsRepeating = false },
            new() { Key = "act.itemsCount", Category = "Акт", Description = "Количество строк услуг", IsRepeating = false },
            new() { Key = "act.commentHtml", Category = "HTML-блоки", Description = "Комментарий к акту", IsRepeating = false },
            new() { Key = "seller.requisitesHtml", Category = "Исполнитель", Description = "Реквизиты исполнителя", IsRepeating = false },
            new() { Key = "seller.signaturePartyName", Category = "Исполнитель", Description = "Название стороны в подписи", IsRepeating = false },
            new() { Key = "seller.signatoryShortName", Category = "Исполнитель", Description = "Подписант исполнителя", IsRepeating = false },
            new() { Key = "seller.signatureHtml", Category = "HTML-блоки", Description = "Изображение подписи", IsRepeating = false },
            new() { Key = "seller.sealHtml", Category = "HTML-блоки", Description = "Изображение печати", IsRepeating = false },
            new() { Key = "buyer.requisitesHtml", Category = "Заказчик", Description = "Реквизиты заказчика", IsRepeating = false },
            new() { Key = "buyer.signaturePartyName", Category = "Заказчик", Description = "Название стороны в подписи", IsRepeating = false },
            new() { Key = "buyer.signatoryShortName", Category = "Заказчик", Description = "Подписант заказчика", IsRepeating = false },
            new() { Key = "lineItems", Category = "Строки услуг", Description = "Повторяемый блок строк услуг", IsRepeating = true },
            new() { Key = "index", Category = "Строки услуг", Description = "Номер строки", IsRepeating = true },
            new() { Key = "name", Category = "Строки услуг", Description = "Наименование услуги", IsRepeating = true },
            new() { Key = "quantity", Category = "Строки услуг", Description = "Количество", IsRepeating = true },
            new() { Key = "unit", Category = "Строки услуг", Description = "Единица измерения", IsRepeating = true },
            new() { Key = "price", Category = "Строки услуг", Description = "Цена", IsRepeating = true },
            new() { Key = "total", Category = "Строки услуг", Description = "Сумма строки", IsRepeating = true },
        ];

    public static string RenderHtml(
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal)
        => RenderCustomTemplateHtml(DefaultTemplateHtml, act, lineItems, mainCompany, includeSignatureAndSeal);

    public static string RenderHtml(
        string templateHtml,
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal)
    {
        if (string.IsNullOrWhiteSpace(templateHtml) || string.Equals(templateHtml.Trim(), DefaultTemplateHtml, StringComparison.Ordinal))
        {
            return RenderCustomTemplateHtml(DefaultTemplateHtml, act, lineItems, mainCompany, includeSignatureAndSeal);
        }

        return RenderCustomTemplateHtml(templateHtml, act, lineItems, mainCompany, includeSignatureAndSeal);
    }

    private static string RenderSystemDefaultHtml(
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal)
    {
        var companyParty = BuildSeller(mainCompany);
        var isExpenseAct = act.Invoice?.Type == PaymentType.Expense;
        var seller = isExpenseAct
            ? BuildExpenseSeller(act)
            : companyParty;
        var buyer = isExpenseAct
            ? BuildCompanyBuyer(companyParty)
            : BuildBuyer(act);
        var effectiveLineItems = BuildEffectiveLineItems(act, lineItems);
        var basis = BuildBasis(act);
        var totalAmount = effectiveLineItems.Sum(item => item.Total);
        var totalWords = SpellMoney(totalAmount);
        var documentTitle = $"Акт № {act.Number} от {FormatLongDate(act.Date)}";

        var rows = new StringBuilder();
        for (var index = 0; index < effectiveLineItems.Count; index++)
        {
            var item = effectiveLineItems[index];
            rows.AppendLine("<tr>");
            rows.AppendLine($"<td class=\"bor item-index\">{index + 1}</td>");
            rows.AppendLine($"<td class=\"bor item-name\">{Encode(item.Name)}</td>");
            rows.AppendLine($"<td class=\"bor item-qty\">{FormatQuantity(item.Quantity)}</td>");
            rows.AppendLine($"<td class=\"bor item-unit\">{Encode(item.Unit)}</td>");
            rows.AppendLine($"<td class=\"bor item-price\">{FormatMoney(item.Price)}</td>");
            rows.AppendLine($"<td class=\"bor item-sum\">{FormatMoney(item.Total)}</td>");
            rows.AppendLine("</tr>");
        }

        var signatureHtml = includeSignatureAndSeal && !isExpenseAct
            ? BuildImageHtml(mainCompany?.DirectorSignatureDataUrl, "Подпись", "signature")
            : string.Empty;
        var sealHtml = includeSignatureAndSeal && !isExpenseAct
            ? BuildImageHtml(mainCompany?.SealDataUrl, "Печать", "seal")
            : string.Empty;
        var sellerWithoutVat = isExpenseAct
            ? (act.Invoice?.LegalEntity?.VatWithoutVat
                ?? act.Invoice?.Client?.LegalEntity?.VatWithoutVat
                ?? act.LegalEntity?.VatWithoutVat
                ?? act.Client?.LegalEntity?.VatWithoutVat) == true
            : mainCompany?.VatWithoutVat == true;
        var vatTotals = CalculateVat(effectiveLineItems);
        // Если у позиций задана ставка НДС, документ считается построчно; иначе — прежние правила по признаку продавца.
        var withoutVat = vatTotals.HasRates ? vatTotals.AllWithoutVat : sellerWithoutVat;
        var vatLabel = withoutVat ? "Без налога (НДС):" : "В том числе НДС:";
        var vatValue = withoutVat ? "-" : (vatTotals.HasRates ? FormatMoney(vatTotals.Vat) : "0,00");

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>{{Encode(documentTitle)}}</title>
  <style>
    @page { size: A4; margin: 10mm 7.3mm 10mm 15mm; }
    @page WordSection1 { size: 595.3pt 841.9pt; margin: 28.35pt 20.69pt 28.35pt 42.52pt; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: #000000;
      font-family: "DejaVu Sans Condensed", "Arial Narrow", Arial, sans-serif;
      font-size: 12px;
      line-height: 1.25;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .page-content {
      width: 531.78pt;
      padding: 0;
      height: 100%;
      box-sizing: content-box;
      zoom: 1;
      margin: 0;
      background: #ffffff;
      page: WordSection1;
    }
    .document-template,
    .document-template div,
    .document-template span,
    .document-template table,
    .document-template tbody,
    .document-template thead,
    .document-template tfoot,
    .document-template tr,
    .document-template th,
    .document-template td {
      font-size: 12px;
    }
    .document-template table {
      width: 100%;
      border-collapse: collapse;
      border-spacing: 0;
      mso-table-lspace: 0pt;
      mso-table-rspace: 0pt;
    }
    .text-r { text-align: right; }
    .text-c { text-align: center; }
    .font-b { font-weight: 700; }
    .v-al-t { vertical-align: top; }
    .v-al-b { vertical-align: bottom; }
    .pad-t-5 { padding-top: 5px; }
    .color-line { border-bottom: 1px solid #000000; }
    .doc-main-title {
      margin: 0 0 14px;
      text-align: center;
      font-size: 18px !important;
      font-weight: 700;
      line-height: 1.15;
    }
    .counterparty-table td {
      padding: 1px 3px;
      vertical-align: top;
      font-size: 12px !important;
      line-height: 1.25;
    }
    .counterparty-label {
      width: 12.5%;
      color: #000000;
      white-space: nowrap;
    }
    .party-label {
      width: 12.5%;
      color: #000000;
      padding-right: 8px !important;
      white-space: nowrap;
    }
    .counterparty-value {
      font-weight: 600;
    }
    .party-value {
      font-weight: 600;
    }
    .items-table {
      margin: 2px 0 2px;
      border: 1px solid #000000;
    }
    .items-table th,
    .items-table td {
      border: 1px solid #000000;
      padding: 2px 3px;
      vertical-align: top;
      line-height: 1.2;
    }
    .items-table th {
      font-weight: 700;
      text-align: center;
    }
    .items-table .item-index {
      width: 5%;
      text-align: center;
      vertical-align: middle;
    }
    .items-table .item-name {
      width: 45%;
      word-break: break-word;
    }
    .items-table tbody .item-name {
      text-align: justify;
      text-justify: inter-character;
    }
    .items-table tbody .item-qty {
      text-align: right;
    }
    .items-table .item-qty {
      width: 10%;
      text-align: center;
      vertical-align: middle;
      white-space: nowrap;
    }
    .items-table .item-unit {
      width: 10%;
      text-align: center;
      vertical-align: middle;
      white-space: nowrap;
    }
    .items-table .item-price,
    .items-table .item-sum {
      width: 15%;
      text-align: right;
      vertical-align: middle;
      white-space: nowrap;
    }
    .items-table tbody .item-index,
    .items-table tbody .item-qty,
    .items-table tbody .item-unit,
    .items-table tbody .item-price,
    .items-table tbody .item-sum {
      vertical-align: top;
    }
    .totals-table td {
      padding: 0;
      font-size: 12px !important;
      line-height: 13.8pt;
    }
    .totals-label {
      width: 85%;
      text-align: right;
      padding-right: 4px !important;
      font-weight: 700;
    }
    .totals-value {
      width: 15%;
      text-align: right;
      white-space: nowrap;
    }
    .summary-block {
      margin-top: 6px;
      line-height: 1.2;
    }
    .mar-t-5 { margin-top: 5px !important; }
    .mar-t-10 { margin-top: 10px !important; }
    .mar-t-15 { margin-top: 15px !important; }
    .summary-words {
      margin-top: 0;
      font-weight: 700;
    }
    .totals-table.mar-t-5 { margin-top: 6.08px !important; }
    .summary-block .mar-t-5 { margin-top: 3px !important; }
    .acceptance {
      margin-top: 12px;
      line-height: 1.2;
    }
    .comment {
      margin-top: 10px;
      line-height: 1.35;
    }
    .signature-area {
      margin-top: 15.02px;
    }
    .signature-table {
      margin-top: 10.8px;
    }
    .signature-table td {
      border: 0;
      vertical-align: top;
    }
    .signature-party-cell:first-child {
      width: 48.5%;
    }
    .signature-gap {
      width: 2.575%;
    }
    .signature-party-cell:last-child {
      width: 48.925%;
    }
    .signature-party-cell:last-child .signature-sign-cell {
      padding-right: 3px;
    }
    .signature-role {
      padding-left: 2px;
      padding-bottom: 6px;
      font-size: 13.333px !important;
      font-weight: 700;
      text-transform: uppercase;
    }
    .signature-party {
      padding-left: 2px;
      min-height: 30px;
      padding-bottom: 10px;
      font-size: 13.333px !important;
    }
    .signature-sign-cell {
      position: relative;
      min-height: 28px;
      padding-top: 5px;
      border-bottom: 1px solid #000000;
      text-align: right;
      vertical-align: bottom;
    }
    .signature-mark {
      position: relative;
      z-index: 4;
      white-space: nowrap;
    }
    .signature {
      position: absolute;
      left: -2px;
      bottom: -8px;
      z-index: 3;
      width: 90px;
      height: 50px;
      object-fit: contain;
    }
    .seal {
      position: absolute;
      left: 143px;
      bottom: -67px;
      z-index: 2;
      width: 149px;
      height: 150px;
      object-fit: contain;
      opacity: 0.92;
    }
  </style>
</head>
<body>
  <div class="page-content">
    <div class="document-template act-template">
      <div class="doc-main-title">{{Encode(documentTitle)}}</div>

        <table class="counterparty-table" cellspacing="0">
          <tbody>
            <tr>
              <td class="party-label v-al-t">Исполнитель:</td>
              <td class="counterparty-value">{{seller.RequisitesHtml}}</td>
            </tr>
            <tr>
              <td class="party-label v-al-t">Заказчик:</td>
              <td class="counterparty-value pad-t-5">{{buyer.RequisitesHtml}}</td>
            </tr>
            <tr>
              <td class="party-label v-al-t">Основание:</td>
              <td class="counterparty-value pad-t-5">{{Encode(basis)}}</td>
            </tr>
          </tbody>
        </table>

        <table class="items-table" cellspacing="0">
          <thead>
            <tr class="heading">
              <th class="bor item-index">№</th>
              <th class="bor item-name">Наименование работ, услуг</th>
              <th class="bor item-qty">Кол-во</th>
              <th class="bor item-unit">Ед</th>
              <th class="bor item-price">Цена</th>
              <th class="bor item-sum">Сумма</th>
            </tr>
          </thead>
          <tbody>
            {{rows}}
          </tbody>
        </table>

        <table class="totals-table mar-t-5" cellspacing="0">
          <tbody>
            <tr>
              <td class="totals-label">Итого:</td>
              <td class="totals-value">{{FormatMoney(totalAmount)}}</td>
            </tr>
            <tr>
              <td class="totals-label">{{vatLabel}}</td>
              <td class="totals-value">{{vatValue}}</td>
            </tr>
          </tbody>
        </table>

        <div class="summary-block">
          <div class="mar-t-5">Всего оказано услуг {{effectiveLineItems.Count}}, на сумму {{FormatMoney(totalAmount)}} руб.</div>
          <div class="summary-words">{{Encode(totalWords)}}</div>
        </div>

        <div class="mar-t-15 acceptance">
          Вышеперечисленные услуги выполнены полностью и в срок. Заказчик претензий по объему, качеству и срокам оказания услуг не имеет.
        </div>
        <div class="signature-area">
          <table class="signature-table" cellspacing="0" width="100%">
            <tbody>
              <tr>
                <td class="signature-party-cell" width="48.5%">
                  <div class="signature-role">ИСПОЛНИТЕЛЬ</div>
                  <div class="signature-party">{{Encode(seller.SignaturePartyName)}}</div>
                  <div class="signature-sign-cell">
                    {{signatureHtml}}
                    {{sealHtml}}
                    <span class="signature-mark">/ {{Encode(seller.SignatoryShortName)}} /</span>
                  </div>
                </td>
                <td class="signature-gap" width="2.575%"></td>
                <td class="signature-party-cell" width="48.925%">
                  <div class="signature-role">ЗАКАЗЧИК</div>
                  <div class="signature-party">{{Encode(buyer.SignaturePartyName)}}</div>
                  <div class="signature-sign-cell">
                    <span class="signature-mark">/ {{Encode(buyer.SignatoryShortName)}} /</span>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  </div>
</body>
</html>
""";
    }

    private static string RenderCustomTemplateHtml(
        string templateHtml,
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal)
    {
        var companyParty = BuildSeller(mainCompany);
        var isExpenseAct = act.Invoice?.Type == PaymentType.Expense;
        var seller = isExpenseAct ? BuildExpenseSeller(act) : companyParty;
        var buyer = isExpenseAct ? BuildCompanyBuyer(companyParty) : BuildBuyer(act);
        var effectiveLineItems = BuildEffectiveLineItems(act, lineItems);
        var totalAmount = effectiveLineItems.Sum(item => item.Total);
        var documentTitle = $"Акт № {act.Number} от {FormatLongDate(act.Date)}";
        var usesManagedDefaultSignatureLayout = templateHtml.Contains("class=\"sign-content\"", StringComparison.Ordinal);
        var signatureHtml = includeSignatureAndSeal && !isExpenseAct
            ? BuildImageHtml(mainCompany?.DirectorSignatureDataUrl, "Подпись", "signature", usesManagedDefaultSignatureLayout ? 76 : null, usesManagedDefaultSignatureLayout ? 42 : null)
            : string.Empty;
        var sealHtml = includeSignatureAndSeal && !isExpenseAct
            ? BuildImageHtml(mainCompany?.SealDataUrl, "Печать", "seal", usesManagedDefaultSignatureLayout ? 90 : null, usesManagedDefaultSignatureLayout ? 90 : null)
            : string.Empty;
        var commentHtml = string.IsNullOrWhiteSpace(act.Comment)
            ? string.Empty
            : $"<div class=\"comment\"><b>Комментарий:</b> {Encode(act.Comment)}</div>";

        var rendered = LineItemsRegex.Replace(templateHtml, match =>
        {
            var rowTemplate = match.Groups[1].Value;
            return string.Concat(effectiveLineItems.Select((item, index) =>
            {
                var rowTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["index"] = (index + 1).ToString(CultureInfo.InvariantCulture),
                    ["name"] = Encode(item.Name),
                    ["quantity"] = Encode(FormatQuantity(item.Quantity)),
                    ["unit"] = Encode(item.Unit),
                    ["price"] = Encode(FormatMoney(item.Price)),
                    ["total"] = Encode(FormatMoney(item.Total)),
                    ["vat"] = Encode(item.VatRate is null ? "—" : item.VatRate == VatRates.None ? "Без НДС" : VatRates.Label(item.VatRate)),
                };
                return ReplaceTokens(rowTemplate, rowTokens);
            }));
        });

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["document.title"] = Encode(documentTitle),
            ["act.number"] = Encode(act.Number),
            ["act.date"] = Encode(act.Date.ToString("dd.MM.yyyy", RuCulture)),
            ["act.dateLong"] = Encode(FormatLongDate(act.Date)),
            ["act.basis"] = Encode(BuildBasis(act)),
            ["act.total"] = Encode(FormatMoney(totalAmount)),
            ["act.totalWords"] = Encode(SpellMoney(totalAmount)),
            ["act.vat"] = ResolveVatToken(effectiveLineItems),
            ["act.itemsCount"] = effectiveLineItems.Count.ToString(CultureInfo.InvariantCulture),
            ["act.commentHtml"] = commentHtml,
            ["seller.requisitesHtml"] = seller.RequisitesHtml,
            ["seller.signaturePartyName"] = Encode(seller.SignaturePartyName),
            ["seller.signatoryShortName"] = Encode(seller.SignatoryShortName),
            ["seller.signatureHtml"] = signatureHtml,
            ["seller.sealHtml"] = sealHtml,
            ["buyer.requisitesHtml"] = buyer.RequisitesHtml,
            ["buyer.signaturePartyName"] = Encode(buyer.SignaturePartyName),
            ["buyer.signatoryShortName"] = Encode(buyer.SignatoryShortName),
        };

        return ReplaceTokens(rendered, tokens);
    }

    private static string ReplaceTokens(string templateHtml, IReadOnlyDictionary<string, string> tokens)
        => TokenRegex.Replace(templateHtml, match => tokens.TryGetValue(match.Groups[1].Value, out var value) ? value : string.Empty);

    public sealed class ActDocumentTemplateSettingsStorage
    {
        public string ActiveTemplateKey { get; set; } = string.Empty;
        public bool PrintWithSignatureAndSealByDefault { get; set; }
        public List<ActDocumentTemplateStorageItem> Templates { get; set; } = new();
    }

    public sealed class ActDocumentTemplateStorageItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Html { get; set; } = string.Empty;
    }

    private static VatTotals CalculateVat(IEnumerable<RenderedActLineItem> items)
        => VatCalculator.Calculate(items.Select(item => new VatLine(item.Quantity, item.Price, item.VatRate)));

    private static string ResolveVatToken(IReadOnlyCollection<RenderedActLineItem> items)
    {
        var totals = CalculateVat(items);
        if (!totals.HasRates)
        {
            return "0,00";
        }

        return totals.AllWithoutVat ? "Без НДС" : FormatMoney(totals.Vat);
    }

    private static List<RenderedActLineItem> BuildEffectiveLineItems(
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems)
    {
        if (lineItems.Count > 0)
        {
            return lineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item => new RenderedActLineItem(item.Name, item.Quantity, item.Unit, item.Price, item.VatRate))
                .ToList();
        }

        var title = string.IsNullOrWhiteSpace(act.Title)
            ? $"Услуги по акту № {act.Number}"
            : act.Title.Trim();

        return
        [
            new RenderedActLineItem(title, 1m, "усл.", act.Amount),
        ];
    }

    private static SellerInfo BuildSeller(LegalEntity? mainCompany)
    {
        var bankAccount = ParseBankAccounts(mainCompany?.BankAccountsJson)
            .Where(account => account.IsArchived != true)
            .OrderByDescending(account => account.IsPrimary == true)
            .ThenByDescending(account => string.Equals(account.AccountType?.Trim(), "Основной", StringComparison.OrdinalIgnoreCase))
            .ThenBy(account => string.IsNullOrWhiteSpace(account.AccountNumber) ? 1 : 0)
            .FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.AccountNumber))
            ?? new BankAccountInfo();

        var fullName = FirstNotEmpty(mainCompany?.FullName, mainCompany?.ShortName, "Организация");
        var signaturePartyName = FirstNotEmpty(mainCompany?.ShortName, mainCompany?.FullName, "Организация");
        var signatoryShortName = BuildPersonShortName(
            ParsePerson(mainCompany?.DocumentDirectorJson),
            mainCompany?.Director ?? mainCompany?.ShortName);
        var address = LegalEntityAddressResolver.ResolvePreferredAddress(mainCompany);

        var requisites = new List<string> { fullName };

        if (!string.IsNullOrWhiteSpace(mainCompany?.Inn))
        {
            requisites.Add($"ИНН {mainCompany.Inn}");
        }

        if (!string.IsNullOrWhiteSpace(mainCompany?.Kpp))
        {
            requisites.Add($"КПП {mainCompany.Kpp}");
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            requisites.Add(address);
        }

        var accountNumber = FirstNotEmpty(bankAccount.AccountNumber, mainCompany?.TreasuryAccountNumber);
        var bankName = FirstNotEmpty(bankAccount.Bank, mainCompany?.TreasuryBankName);
        if (!string.IsNullOrWhiteSpace(accountNumber) && !string.IsNullOrWhiteSpace(bankName))
        {
            requisites.Add($"р/с {accountNumber} в банке {bankName}");
        }
        else if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            requisites.Add($"р/с {accountNumber}");
        }
        else if (!string.IsNullOrWhiteSpace(bankName))
        {
            requisites.Add($"в банке {bankName}");
        }

        var bik = FirstNotEmpty(bankAccount.Bik, mainCompany?.TreasuryBankBik);
        if (!string.IsNullOrWhiteSpace(bik))
        {
            requisites.Add($"БИК {bik}");
        }

        var corrAccountNumber = FirstNotEmpty(bankAccount.CorrAccountNumber, mainCompany?.TreasuryCorrAccountNumber);
        if (!string.IsNullOrWhiteSpace(corrAccountNumber))
        {
            requisites.Add($"к/с {corrAccountNumber}");
        }

        var requisitesText = string.Join(", ", requisites.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return new SellerInfo(Encode(requisitesText), signaturePartyName, signatoryShortName);
    }

    private static SellerInfo BuildExpenseSeller(Act act)
    {
        var supplier = act.Invoice?.Client ?? act.Client;
        var supplierLegalEntity = act.Invoice?.LegalEntity ?? act.LegalEntity;
        if (supplier is null && supplierLegalEntity is not null)
        {
            var legalSupplierInfo = BuildLegalEntityBuyerParty(
                supplierLegalEntity,
                FirstNotEmpty(supplierLegalEntity.FullName, supplierLegalEntity.ShortName, act.Invoice?.CounterpartyName, "Исполнитель"));

            return new SellerInfo(
                legalSupplierInfo.RequisitesHtml,
                legalSupplierInfo.SignaturePartyName,
                legalSupplierInfo.SignatoryShortName);
        }

        var supplierInfo = BuildClientParty(
            supplier is null ? [] : [supplier],
            FirstNotEmpty(supplier?.Name, act.Invoice?.CounterpartyName, "Исполнитель"));

        return new SellerInfo(
            supplierInfo.RequisitesHtml,
            supplierInfo.SignaturePartyName,
            supplierInfo.SignatoryShortName);
    }

    private static BuyerInfo BuildCompanyBuyer(SellerInfo companyParty)
        => new(
            WebUtility.HtmlDecode(companyParty.RequisitesHtml),
            companyParty.RequisitesHtml,
            companyParty.SignaturePartyName,
            companyParty.SignatoryShortName);

    private static BuyerInfo BuildBuyer(Act act)
    {
        var clients = EnumerateBuyerClients(act).ToList();
        if (clients.Count > 0)
        {
            return BuildClientParty(
                clients,
                FirstNotEmpty(
                    act.Client?.Name,
                    act.Invoice?.Client?.Name,
                    act.Invoice?.ExpenseClient?.Name,
                    act.Invoice?.CounterpartyName,
                    "Заказчик"));
        }

        var legalEntity = ResolveIncomeBuyerLegalEntity(act);
        if (legalEntity is not null)
        {
            return BuildLegalEntityBuyerParty(
                legalEntity,
                FirstNotEmpty(
                    legalEntity.FullName,
                    legalEntity.ShortName,
                    act.Invoice?.CounterpartyName,
                    "Заказчик"));
        }

        return BuildClientParty(
            [],
            FirstNotEmpty(act.Invoice?.CounterpartyName, "Заказчик"));
    }

    private static LegalEntity? ResolveIncomeBuyerLegalEntity(Act act)
        => act.Invoice?.LegalEntity
           ?? act.LegalEntity
           ?? act.Invoice?.ExpenseLegalEntity;

    private static BuyerInfo BuildLegalEntityBuyerParty(LegalEntity? legalEntity, string fallbackName)
    {
        var displayName = FirstNotEmpty(legalEntity?.FullName, legalEntity?.ShortName, fallbackName, "Контрагент");
        var address = legalEntity is null ? string.Empty : LegalEntityAddressResolver.ResolvePreferredAddress(legalEntity);
        var requisitesLines = new List<string>
        {
            string.Join(", ", new[]
            {
                displayName,
                string.IsNullOrWhiteSpace(legalEntity?.Inn) ? null : $"ИНН {legalEntity!.Inn}",
                string.IsNullOrWhiteSpace(legalEntity?.Kpp) ? null : $"КПП {legalEntity!.Kpp}",
                string.IsNullOrWhiteSpace(legalEntity?.Ogrn) ? null : $"ОГРН {legalEntity!.Ogrn}",
            }.Where(static value => !string.IsNullOrWhiteSpace(value))),
        };

        if (!string.IsNullOrWhiteSpace(address))
        {
            requisitesLines.Add(address);
        }

        return new BuyerInfo(
            Encode(string.Join(", ", requisitesLines.Where(static value => !string.IsNullOrWhiteSpace(value)))),
            string.Join("<br/>", requisitesLines.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(Encode)),
            displayName,
            BuildCompactShortName(FirstNotEmpty(legalEntity?.Director, displayName)));
    }

    private static BuyerInfo BuildClientParty(IReadOnlyCollection<Client> clients, string fallbackName)
    {
        var buyerClients = clients.ToList();
        var buyerClient = buyerClients.FirstOrDefault();
        var buyerProfiles = buyerClients
            .Select(client => new BuyerClientData(client, ParseBuyerProfile(client.Notes)))
            .ToList();

        var displayName = FirstNotEmpty(
            buyerClient?.Name,
            fallbackName,
            "Заказчик");

        var company = buyerClients
            .Select(client => client.Company?.Trim())
            .FirstOrDefault(companyName =>
                !string.IsNullOrWhiteSpace(companyName)
                && !string.Equals(companyName, displayName, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;

        var address = buyerClients
            .Select(ResolveAddress)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;

        var passport = buyerProfiles
            .Select(item => BuildPassport(item.Client))
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(passport))
        {
            passport = buyerProfiles
                .Select(item => BuildPassport(ResolvePrimaryPassport(item.Profile)))
                .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
                ?? string.Empty;
        }

        var inn = buyerProfiles
            .Select(item => item.Profile?.Inn?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;
        var snils = buyerProfiles
            .Select(item => item.Profile?.Snils?.Trim())
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            ?? string.Empty;

        var headlineParts = new List<string> { displayName };
        if (!string.IsNullOrWhiteSpace(inn))
        {
            headlineParts.Add($"ИНН {inn}");
        }

        if (!string.IsNullOrWhiteSpace(snils))
        {
            headlineParts.Add($"СНИЛС {snils}");
        }

        var requisitesLines = new List<string>
        {
            string.Join(", ", headlineParts.Where(static value => !string.IsNullOrWhiteSpace(value))),
        };

        if (!string.IsNullOrWhiteSpace(company))
        {
            requisitesLines.Add(company);
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            requisitesLines.Add(address);
        }

        if (!string.IsNullOrWhiteSpace(passport))
        {
            requisitesLines.Add(passport);
        }

        var requisitesText = string.Join(", ", requisitesLines.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return new BuyerInfo(
            Encode(requisitesText),
            Encode(requisitesText),
            displayName,
            BuildCompactShortName(displayName));
    }

    private static BuyerProfileInfo? ParseBuyerProfile(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var raw = notes.Trim();
        if (!raw.StartsWith(ClientProfileNotesPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var payload = raw[ClientProfileNotesPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BuyerProfileInfo>(payload, ClientProfileJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static ClientPassportProfileInfo? ResolvePrimaryPassport(BuyerProfileInfo? profile)
    {
        if (profile is null)
        {
            return null;
        }

        if (profile.PassportEntries is { Count: > 0 })
        {
            return profile.PassportEntries.FirstOrDefault(passport =>
                !string.IsNullOrWhiteSpace(passport.Series)
                || !string.IsNullOrWhiteSpace(passport.Number)
                || !string.IsNullOrWhiteSpace(passport.IssuedBy)
                || passport.IssueDate.HasValue
                || !string.IsNullOrWhiteSpace(passport.DepartmentCode));
        }

        if (string.IsNullOrWhiteSpace(profile.PassportSeries)
            && string.IsNullOrWhiteSpace(profile.PassportNumber)
            && string.IsNullOrWhiteSpace(profile.PassportIssuedBy)
            && !profile.PassportIssueDate.HasValue
            && string.IsNullOrWhiteSpace(profile.PassportDepartmentCode))
        {
            return null;
        }

        return new ClientPassportProfileInfo
        {
            Series = profile.PassportSeries,
            Number = profile.PassportNumber,
            IssuedBy = profile.PassportIssuedBy,
            IssueDate = profile.PassportIssueDate,
            DepartmentCode = profile.PassportDepartmentCode,
        };
    }

    private static string BuildBasis(Act act)
    {
        var invoiceNumber = FirstNotEmpty(
            act.Invoice?.Invoice?.Number,
            act.InvoiceNumber,
            act.Invoice?.Account);
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return "—";
        }

        var invoiceDateValue = act.Invoice?.Invoice?.Date
            ?? act.Invoice?.AccountDate
            ?? (act.Invoice is { Date: var paymentDate } && paymentDate != default ? paymentDate : null);
        var invoiceDate = FormatDate(invoiceDateValue);

        var documentName = string.Equals(
            act.Invoice?.InvoiceDocumentTemplateKey,
            InvoiceDocumentTemplateEngine.OfferTemplateKey,
            StringComparison.OrdinalIgnoreCase)
            ? "Счет-договор"
            : "Счет";

        return string.IsNullOrWhiteSpace(invoiceDate)
            ? $"{documentName} № {invoiceNumber}"
            : $"{documentName} № {invoiceNumber} от {invoiceDate}";
    }

    private static IEnumerable<Client> EnumerateBuyerClients(Act act)
    {
        var invoiceBuyer = act.Invoice?.Type == PaymentType.Expense
            ? act.Invoice?.ExpenseClient ?? act.Invoice?.Client
            : act.Invoice?.Client ?? act.Invoice?.ExpenseClient;
        var seenClientIds = new HashSet<int>();
        var seenTransientClients = new HashSet<Client>();

        foreach (var client in new[]
                 {
                     invoiceBuyer,
                     act.Client,
                     act.Invoice?.Client,
                     act.Invoice?.ExpenseClient,
                 })
        {
            if (client is null)
            {
                continue;
            }

            if (client.Id > 0)
            {
                if (!seenClientIds.Add(client.Id))
                {
                    continue;
                }
            }
            else if (!seenTransientClients.Add(client))
            {
                continue;
            }

            yield return client;
        }
    }

    private static string ResolveAddress(Client? client)
    {
        if (client is null)
        {
            return string.Empty;
        }

        var normalizedAddress = client.Addresses
            .OrderByDescending(address => address.IsPrimary)
            .ThenBy(address => address.Type == AddressType.Registration ? 0 : address.Type == AddressType.Actual ? 1 : 2)
            .Select(address => FirstNotEmpty(address.UnrestrictedValue, address.Value))
            .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address));

        return FirstNotEmpty(normalizedAddress, client.Address);
    }

    private static string BuildPassport(Client? client)
    {
        var passport = client?.Passports
            .OrderByDescending(item => item.IsPrimary)
            .ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
            .FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(item.Series)
                || !string.IsNullOrWhiteSpace(item.Number)
                || !string.IsNullOrWhiteSpace(item.IssuedBy));

        if (passport is null)
        {
            return string.Empty;
        }

        return BuildPassportValue(
            passport.Series,
            passport.Number,
            passport.IssuedAt,
            passport.IssuedBy,
            passport.DepartmentCode);
    }

    private static string BuildPassport(ClientPassportProfileInfo? passport)
    {
        if (passport is null)
        {
            return string.Empty;
        }

        return BuildPassportValue(
            passport.Series,
            passport.Number,
            passport.IssueDate,
            passport.IssuedBy,
            passport.DepartmentCode);
    }

    private static string BuildPassportValue(
        string? series,
        string? number,
        DateTime? issueDate,
        string? issuedBy,
        string? departmentCode)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(series) || !string.IsNullOrWhiteSpace(number))
        {
            var passportIdentity = string.Empty;
            if (!string.IsNullOrWhiteSpace(series) && !string.IsNullOrWhiteSpace(number))
            {
                passportIdentity = $"Паспорт серия {series.Trim()} № {number.Trim()}";
            }
            else if (!string.IsNullOrWhiteSpace(series))
            {
                passportIdentity = $"Паспорт серия {series.Trim()}";
            }
            else if (!string.IsNullOrWhiteSpace(number))
            {
                passportIdentity = $"Паспорт № {number.Trim()}";
            }

            if (!string.IsNullOrWhiteSpace(passportIdentity))
            {
                parts.Add(passportIdentity);
            }
        }

        if (issueDate.HasValue)
        {
            parts.Add($"выдан {FormatDate(issueDate)}");
        }

        if (!string.IsNullOrWhiteSpace(issuedBy))
        {
            parts.Add(issuedBy.Trim());
        }

        if (!string.IsNullOrWhiteSpace(departmentCode))
        {
            parts.Add($"код подразделения {departmentCode.Trim()}");
        }

        return string.Join(", ", parts.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static List<BankAccountInfo> ParseBankAccounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<BankAccountInfo>>(
                       json,
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true,
                       })
                   ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static PersonInfo? ParsePerson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PersonInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildPersonShortName(PersonInfo? person, string? fallback)
    {
        if (person is not null)
        {
            var parts = new[]
            {
                person.LastName?.Trim(),
                string.IsNullOrWhiteSpace(person.FirstName) ? null : $"{char.ToUpperInvariant(person.FirstName.Trim()[0])}.",
                person.NoMiddleName || string.IsNullOrWhiteSpace(person.MiddleName)
                    ? null
                    : $"{char.ToUpperInvariant(person.MiddleName.Trim()[0])}.",
            };

            var value = string.Join(" ", parts.Where(static item => !string.IsNullOrWhiteSpace(item))).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return BuildShortName(fallback);
    }

    private static string BuildShortName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return string.Empty;
        }

        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            return fullName.Trim();
        }

        var initials = parts
            .Skip(1)
            .Take(2)
            .Select(part => $"{char.ToUpperInvariant(part[0])}.")
            .ToArray();

        return $"{parts[0]} {string.Join(" ", initials)}".Trim();
    }

    private static string BuildCompactShortName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return string.Empty;
        }

        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1)
        {
            return fullName.Trim();
        }

        var initials = parts
            .Skip(1)
            .Take(2)
            .Select(part => $"{char.ToUpperInvariant(part[0])}.");

        return $"{parts[0]} {string.Concat(initials)}".Trim();
    }

    private static string BuildImageHtml(string? dataUrl, string alt, string cssClass, int? width = null, int? height = null)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return string.Empty;
        }

        var dimensions = width.HasValue && height.HasValue
            ? $" width=\"{width.Value}\" height=\"{height.Value}\" style=\"width: {width.Value}px; height: {height.Value}px; object-fit: contain;\""
            : string.Empty;
        return $"<img src=\"{WebUtility.HtmlEncode(dataUrl)}\" alt=\"{Encode(alt)}\" class=\"{cssClass}\"{dimensions} />";
    }

    private static string FormatMoney(decimal value)
        => value.ToString("N2", RuCulture).Replace('\u00A0', ' ');

    private static string FormatQuantity(decimal value)
        => value.ToString("0.####", RuCulture);

    private static string FormatLongDate(DateTime value)
        => ActDomainService.NormalizeLocalDate(value).ToString("d MMMM yyyy 'г.'", RuCulture);

    private static string? FormatDate(DateTime? value)
        => value.HasValue ? ActDomainService.NormalizeLocalDate(value.Value).ToString("dd.MM.yyyy", RuCulture) : null;

    private static string SpellMoney(decimal value)
    {
        var rubles = (long)Math.Floor(value);
        var kopecks = (int)Math.Round((value - rubles) * 100m, MidpointRounding.AwayFromZero);
        if (kopecks == 100)
        {
            rubles += 1;
            kopecks = 0;
        }

        var rublesWords = NumberToWords(rubles, GrammaticalGender.Masculine);
        return $"{Capitalize(rublesWords)} {ChoosePlural(rubles, "рубль", "рубля", "рублей")} {kopecks:00} {ChoosePlural(kopecks, "копейка", "копейки", "копеек")}";
    }

    private static string NumberToWords(long value, GrammaticalGender gender)
    {
        if (value == 0)
        {
            return "ноль";
        }

        var groups = new[]
        {
            new GroupForms("", "", "", GrammaticalGender.Masculine),
            new GroupForms("тысяча", "тысячи", "тысяч", GrammaticalGender.Feminine),
            new GroupForms("миллион", "миллиона", "миллионов", GrammaticalGender.Masculine),
            new GroupForms("миллиард", "миллиарда", "миллиардов", GrammaticalGender.Masculine),
        };

        var parts = new List<string>();
        var groupIndex = 0;
        while (value > 0 && groupIndex < groups.Length)
        {
            var triad = (int)(value % 1000);
            if (triad > 0)
            {
                var group = groups[groupIndex];
                var triadParts = new List<string>();
                if (triad / 100 > 0)
                {
                    triadParts.Add(Hundreds[triad / 100]);
                }

                var tensUnits = triad % 100;
                if (tensUnits is >= 10 and <= 19)
                {
                    triadParts.Add(Teens[tensUnits - 10]);
                }
                else
                {
                    if (tensUnits / 10 > 0)
                    {
                        triadParts.Add(Tens[tensUnits / 10]);
                    }

                    var unit = tensUnits % 10;
                    if (unit > 0)
                    {
                        triadParts.Add((groupIndex == 0 ? gender : group.Gender) switch
                        {
                            GrammaticalGender.Feminine => UnitsFeminine[unit],
                            _ => UnitsMasculine[unit],
                        });
                    }
                }

                if (groupIndex > 0)
                {
                    triadParts.Add(ChoosePlural(triad, group.One, group.Two, group.Five));
                }

                parts.Insert(0, string.Join(" ", triadParts.Where(static part => !string.IsNullOrWhiteSpace(part))));
            }

            value /= 1000;
            groupIndex++;
        }

        return string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part))).Trim();
    }

    private static string ChoosePlural(long value, string one, string two, string five)
    {
        var mod100 = value % 100;
        if (mod100 is >= 11 and <= 14)
        {
            return five;
        }

        return (value % 10) switch
        {
            1 => one,
            2 or 3 or 4 => two,
            _ => five,
        };
    }

    private static string Capitalize(string value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : char.ToUpper(value[0], RuCulture) + value[1..];

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record RenderedActLineItem(string Name, decimal Quantity, string Unit, decimal Price, string? VatRate = null)
    {
        public decimal Total => Quantity * Price;
    }

    private sealed record SellerInfo(
        string RequisitesHtml,
        string SignaturePartyName,
        string SignatoryShortName);

    private sealed record BuyerInfo(
        string RequisitesText,
        string RequisitesHtml,
        string SignaturePartyName,
        string SignatoryShortName);

    private sealed record BuyerClientData(Client Client, BuyerProfileInfo? Profile);

    private sealed class BuyerProfileInfo
    {
        public string? Inn { get; init; }
        public string? Snils { get; init; }
        public string? PassportSeries { get; init; }
        public string? PassportNumber { get; init; }
        public string? PassportIssuedBy { get; init; }
        public DateTime? PassportIssueDate { get; init; }
        public string? PassportDepartmentCode { get; init; }
        public List<ClientPassportProfileInfo>? PassportEntries { get; init; }
    }

    private sealed class ClientPassportProfileInfo
    {
        public string? Series { get; init; }
        public string? Number { get; init; }
        public string? IssuedBy { get; init; }
        public DateTime? IssueDate { get; init; }
        public string? DepartmentCode { get; init; }
    }

    private sealed class BankAccountInfo
    {
        public int? BankId { get; set; }
        public string? Bank { get; set; }
        public string? Name { get; set; }
        public string? Bik { get; set; }
        public string? Currency { get; set; }
        public string? AccountNumber { get; set; }
        public string? AccountType { get; set; }
        public string? CorrAccountNumber { get; set; }
        public string? City { get; set; }
        public bool? IsForeignBank { get; set; }
        public bool? IsPrimary { get; set; }
        public bool? IsArchived { get; set; }
    }

    private sealed class PersonInfo
    {
        public string? Position { get; set; }
        public string? LastName { get; set; }
        public string? FirstName { get; set; }
        public string? MiddleName { get; set; }
        public bool NoMiddleName { get; set; }
    }

    private sealed record GroupForms(string One, string Two, string Five, GrammaticalGender Gender);

    private enum GrammaticalGender
    {
        Masculine,
        Feminine,
    }

    private static readonly string[] Hundreds =
    [
        "",
        "сто",
        "двести",
        "триста",
        "четыреста",
        "пятьсот",
        "шестьсот",
        "семьсот",
        "восемьсот",
        "девятьсот",
    ];

    private static readonly string[] Tens =
    [
        "",
        "",
        "двадцать",
        "тридцать",
        "сорок",
        "пятьдесят",
        "шестьдесят",
        "семьдесят",
        "восемьдесят",
        "девяносто",
    ];

    private static readonly string[] Teens =
    [
        "десять",
        "одиннадцать",
        "двенадцать",
        "тринадцать",
        "четырнадцать",
        "пятнадцать",
        "шестнадцать",
        "семнадцать",
        "восемнадцать",
        "девятнадцать",
    ];

    private static readonly string[] UnitsMasculine =
    [
        "",
        "один",
        "два",
        "три",
        "четыре",
        "пять",
        "шесть",
        "семь",
        "восемь",
        "девять",
    ];

    private static readonly string[] UnitsFeminine =
    [
        "",
        "одна",
        "две",
        "три",
        "четыре",
        "пять",
        "шесть",
        "семь",
        "восемь",
        "девять",
    ];
}
