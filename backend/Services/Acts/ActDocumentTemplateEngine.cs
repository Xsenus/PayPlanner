using System.Net;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Acts;

public static class ActDocumentTemplateEngine
{
    public static string RenderHtml(
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal)
    {
        var sellerName = Encode(mainCompany?.FullName ?? mainCompany?.ShortName ?? "—");
        var sellerInn = Encode(mainCompany?.Inn);
        var sellerAddress = Encode(mainCompany?.Address);
        var sellerDirector = Encode(mainCompany?.Director ?? mainCompany?.ShortName ?? string.Empty);

        var buyerName = Encode(
            act.Client?.Name
            ?? act.Invoice?.Client?.Name
            ?? act.Invoice?.ExpenseClient?.Name
            ?? act.Invoice?.CounterpartyName
            ?? "—");
        var buyerCompany = Encode(
            act.Client?.Company
            ?? act.Invoice?.Client?.Company
            ?? act.Invoice?.ExpenseClient?.Company);
        var buyerAddress = Encode(
            act.Client?.Address
            ?? act.Invoice?.Client?.Address
            ?? act.Invoice?.ExpenseClient?.Address);
        var buyerPhone = Encode(
            act.Client?.Phone
            ?? act.Invoice?.Client?.Phone
            ?? act.Invoice?.ExpenseClient?.Phone);
        var buyerEmail = Encode(
            act.Client?.Email
            ?? act.Invoice?.Client?.Email
            ?? act.Invoice?.ExpenseClient?.Email);

        var effectiveLineItems = BuildEffectiveLineItems(act, lineItems);
        var rows = new StringBuilder();
        var index = 1;
        foreach (var item in effectiveLineItems)
        {
            rows.AppendLine("<tr>");
            rows.AppendLine($"<td class=\"c\">{index}</td>");
            rows.AppendLine($"<td>{Encode(item.Name)}</td>");
            rows.AppendLine($"<td class=\"r\">{item.Quantity:0.####}</td>");
            rows.AppendLine($"<td class=\"c\">{Encode(item.Unit)}</td>");
            rows.AppendLine($"<td class=\"r\">{item.Price:N2}</td>");
            rows.AppendLine($"<td class=\"r\">{item.Total:N2}</td>");
            rows.AppendLine("</tr>");
            index++;
        }

        var signatureHtml = includeSignatureAndSeal
            ? BuildImageHtml(mainCompany?.DirectorSignatureDataUrl, "Подпись", "signature")
            : string.Empty;
        var sealHtml = includeSignatureAndSeal
            ? BuildImageHtml(mainCompany?.SealDataUrl, "Печать", "seal")
            : string.Empty;
        var commentHtml = string.IsNullOrWhiteSpace(act.Comment)
            ? string.Empty
            : $"<div class=\"comment\"><strong>Комментарий:</strong> {Encode(act.Comment)}</div>";

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>Акт №{{Encode(act.Number)}} от {{act.Date:dd.MM.yyyy}}</title>
  <style>
    @page { size: A4; margin: 14mm 12mm 16mm; }
    body { font-family: Arial, Helvetica, sans-serif; font-size: 13px; color: #111; margin: 0; }
    .doc { width: 100%; }
    .top { margin-bottom: 18px; }
    .title { text-align: center; font-size: 24px; font-weight: 700; margin: 18px 0 14px; }
    .meta, .parties, .items, .totals, .signatures { width: 100%; border-collapse: collapse; }
    .meta td, .parties td { vertical-align: top; padding: 4px 0; }
    .parties .label { width: 120px; color: #555; font-weight: 700; }
    .items { margin-top: 16px; }
    .items th, .items td { border: 1px solid #111; padding: 6px 8px; vertical-align: top; }
    .items th { text-align: center; font-weight: 700; }
    .totals { margin-top: 10px; }
    .totals td { padding: 2px 0; }
    .r { text-align: right; }
    .c { text-align: center; }
    .comment { margin-top: 12px; }
    .signatures { margin-top: 30px; table-layout: fixed; }
    .signatures td { vertical-align: bottom; }
    .line { border-top: 1px solid #111; height: 34px; }
    .signature, .seal { display: block; margin: 0 auto; }
    .signature { max-height: 48px; max-width: 140px; }
    .seal { max-height: 120px; max-width: 120px; }
    .caption { font-size: 12px; text-align: center; padding-top: 4px; }
    .summary { margin-top: 14px; font-weight: 700; }
  </style>
</head>
<body>
  <div class="doc">
    <div class="top">
      <table class="meta">
        <tr>
          <td><strong>{{sellerName}}</strong></td>
          <td class="r">Основание: счет №{{Encode(act.InvoiceNumber ?? act.Invoice?.Account ?? "—")}}</td>
        </tr>
        <tr>
          <td>ИНН {{sellerInn}}{{(string.IsNullOrWhiteSpace(mainCompany?.Address) ? string.Empty : $", {sellerAddress}")}}</td>
          <td class="r">Дата: {{act.Date:dd.MM.yyyy}}</td>
        </tr>
      </table>
      <div class="title">АКТ №{{Encode(act.Number)}} от {{act.Date:dd.MM.yyyy}}</div>
    </div>

    <table class="parties">
      <tr>
        <td class="label">Исполнитель:</td>
        <td>{{sellerName}}</td>
      </tr>
      <tr>
        <td class="label">Заказчик:</td>
        <td>
          <div>{{buyerName}}{{(string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(buyerCompany)) ? string.Empty : $" — {buyerCompany}")}}</div>
          <div>{{buyerAddress}}</div>
          <div>{{(string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(buyerPhone)) ? string.Empty : $"Тел.: {buyerPhone}")}}{{(string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(buyerEmail)) ? string.Empty : $" {buyerEmail}")}}</div>
        </td>
      </tr>
    </table>

    <table class="items">
      <thead>
        <tr>
          <th style="width: 6%">№</th>
          <th>Услуги</th>
          <th style="width: 10%">Кол-во</th>
          <th style="width: 10%">Ед.</th>
          <th style="width: 14%">Цена</th>
          <th style="width: 16%">Сумма</th>
        </tr>
      </thead>
      <tbody>
        {{rows}}
      </tbody>
    </table>

    <div class="summary">Итого: {{act.Amount:N2}} руб.</div>
    {{commentHtml}}

    <table class="signatures">
      <tr>
        <td style="width: 26%;"><strong>Исполнитель</strong></td>
        <td style="width: 28%;" class="line c">{{signatureHtml}}</td>
        <td style="width: 10%;" class="c">{{sealHtml}}</td>
        <td style="width: 36%;" class="line c">{{sellerDirector}}</td>
      </tr>
      <tr>
        <td></td>
        <td class="caption">подпись</td>
        <td></td>
        <td class="caption">расшифровка подписи</td>
      </tr>
    </table>
  </div>
</body>
</html>
""";
    }

    private static IReadOnlyCollection<RenderedActLineItem> BuildEffectiveLineItems(
        Act act,
        IReadOnlyCollection<ActLineItem> lineItems)
    {
        if (lineItems.Count > 0)
        {
            return lineItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .Select(item => new RenderedActLineItem(item.Name, item.Quantity, item.Unit, item.Price))
                .ToList();
        }

        var title = string.IsNullOrWhiteSpace(act.Title)
            ? $"Услуги по акту №{act.Number}"
            : act.Title.Trim();

        return
        [
            new RenderedActLineItem(title, 1m, "усл.ед.", act.Amount)
        ];
    }

    private static string BuildImageHtml(string? dataUrl, string alt, string cssClass)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return string.Empty;
        }

        return $"<img src=\"{WebUtility.HtmlEncode(dataUrl)}\" alt=\"{WebUtility.HtmlEncode(alt)}\" class=\"{cssClass}\" />";
    }

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private sealed record RenderedActLineItem(string Name, decimal Quantity, string Unit, decimal Price)
    {
        public decimal Total => Quantity * Price;
    }
}
