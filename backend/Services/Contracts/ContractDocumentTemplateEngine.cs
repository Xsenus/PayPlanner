using System.Globalization;
using System.Net;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Contracts;

public static class ContractDocumentTemplateEngine
{
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");

    public static string RenderHtml(Contract contract, LegalEntity? mainCompany)
    {
        var sellerName = FirstNotEmpty(mainCompany?.FullName, mainCompany?.ShortName, "Исполнитель");
        var sellerRequisites = BuildLegalEntityRequisites(mainCompany, sellerName);
        var customerName = ResolveCustomerName(contract);
        var customerRequisites = ResolveCustomerRequisites(contract);
        var statusLabel = FormatStatus(contract.Status);
        var amountText = contract.BillingMode == ContractBillingMode.Fixed && contract.Amount.HasValue
            ? FormatMoney(contract.Amount.Value)
            : "по выставленным счетам и актам";
        var validUntil = contract.IsOpenEnded
            ? "бессрочно"
            : FormatDate(contract.ValidUntil);

        var linkedInvoicesTotal = ResolveInvoiceTotal(contract);
        var linkedActsTotal = ResolveActTotal(contract);
        var linkedPaymentsTotal = ResolvePaymentTotal(contract);

        var rows = new StringBuilder();
        AppendDetailsRow(rows, "Номер договора", contract.Number);
        AppendDetailsRow(rows, "Дата договора", FormatDate(contract.Date));
        AppendDetailsRow(rows, "Статус", statusLabel);
        AppendDetailsRow(rows, "Начало действия", FormatDate(contract.StartDate));
        AppendDetailsRow(rows, "Окончание действия", validUntil);
        AppendDetailsRow(rows, "Сумма договора", amountText);
        AppendDetailsRow(rows, "Связанные счета", FormatMoney(linkedInvoicesTotal));
        AppendDetailsRow(rows, "Связанные акты", FormatMoney(linkedActsTotal));
        AppendDetailsRow(rows, "Связанные оплаты", FormatMoney(linkedPaymentsTotal));

        var conditions = new StringBuilder();
        AppendSection(conditions, "Предмет договора", contract.Title);
        AppendSection(conditions, "Условия", contract.Description);
        if (contract.Status == ContractStatus.Terminated)
        {
            AppendSection(conditions, "Условия расторжения", contract.TerminationTerms);
            AppendSection(conditions, "Финансовые условия расторжения", contract.TerminationFinancialTerms);
            AppendSection(conditions, "Причина расторжения", contract.TerminationReason);
            AppendSection(conditions, "Дата расторжения", FormatDate(contract.TerminationDate));
        }
        if (contract.Status == ContractStatus.Cancelled)
        {
            AppendSection(conditions, "Причина аннулирования", contract.CancelledReason);
            AppendSection(conditions, "Дата аннулирования", FormatDate(contract.CancelledAt));
        }

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>Договор № {{Encode(contract.Number)}}</title>
  <style>
    @page { size: A4; margin: 18mm 16mm; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: #111827;
      font-family: "Times New Roman", Times, serif;
      font-size: 13px;
      line-height: 1.45;
      background: #ffffff;
    }
    h1 {
      margin: 0 0 16px;
      text-align: center;
      font-size: 22px;
      font-weight: 700;
    }
    h2 {
      margin: 22px 0 8px;
      font-size: 16px;
      font-weight: 700;
    }
    .subtitle {
      margin-bottom: 20px;
      text-align: center;
      color: #374151;
    }
    table {
      width: 100%;
      border-collapse: collapse;
    }
    .details th,
    .details td {
      border: 1px solid #d1d5db;
      padding: 7px 8px;
      vertical-align: top;
    }
    .details th {
      width: 34%;
      background: #f3f4f6;
      text-align: left;
      font-weight: 700;
    }
    .parties {
      margin-top: 18px;
    }
    .parties td {
      width: 50%;
      padding: 0 12px 0 0;
      vertical-align: top;
    }
    .party-title {
      margin-bottom: 4px;
      font-weight: 700;
    }
    .text-block {
      white-space: pre-wrap;
    }
    .signatures {
      margin-top: 34px;
    }
    .signatures td {
      width: 50%;
      padding-top: 24px;
      vertical-align: top;
    }
    .line {
      display: inline-block;
      width: 190px;
      border-bottom: 1px solid #111827;
    }
    .muted {
      color: #6b7280;
    }
  </style>
</head>
<body>
  <h1>Договор № {{Encode(contract.Number)}}</h1>
  <div class="subtitle">{{Encode(FormatDate(contract.Date))}}</div>

  <table class="parties">
    <tr>
      <td>
        <div class="party-title">Исполнитель</div>
        <div>{{Encode(sellerName)}}</div>
        <div class="muted">{{Encode(sellerRequisites)}}</div>
      </td>
      <td>
        <div class="party-title">Заказчик</div>
        <div>{{Encode(customerName)}}</div>
        <div class="muted">{{Encode(customerRequisites)}}</div>
      </td>
    </tr>
  </table>

  <h2>Основные сведения</h2>
  <table class="details">
    {{rows}}
  </table>

  <h2>Условия договора</h2>
  {{(conditions.Length > 0 ? conditions.ToString() : "<p>Условия договора не заполнены в карточке.</p>")}}

  <h2>Подписи сторон</h2>
  <table class="signatures">
    <tr>
      <td>
        Исполнитель<br>
        <span class="line"></span><br>
        <span class="muted">{{Encode(sellerName)}}</span>
      </td>
      <td>
        Заказчик<br>
        <span class="line"></span><br>
        <span class="muted">{{Encode(customerName)}}</span>
      </td>
    </tr>
  </table>
</body>
</html>
""";
    }

    private static string ResolveCustomerName(Contract contract)
    {
        var clients = contract.ClientContracts
            .Where(link => link.Client is not null)
            .Select(link => FirstNotEmpty(link.Client!.Name, link.Client.Company))
            .Where(value => !string.IsNullOrWhiteSpace(value));

        var legalEntities = contract.LegalEntityContracts
            .Where(link => link.LegalEntity is not null)
            .Select(link => FirstNotEmpty(link.LegalEntity!.FullName, link.LegalEntity.ShortName))
            .Where(value => !string.IsNullOrWhiteSpace(value));

        var names = clients.Concat(legalEntities).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count > 0 ? string.Join("; ", names) : "Заказчик";
    }

    private static string ResolveCustomerRequisites(Contract contract)
    {
        var requisites = contract.ClientContracts
            .Where(link => link.Client is not null)
            .Select(link => BuildClientRequisites(link.Client!))
            .Concat(contract.LegalEntityContracts
                .Where(link => link.LegalEntity is not null)
                .Select(link => BuildLegalEntityRequisites(link.LegalEntity!, link.LegalEntity!.ShortName)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return requisites.Count > 0 ? string.Join("; ", requisites) : "Реквизиты не заполнены";
    }

    private static string BuildClientRequisites(Client client)
    {
        var parts = new[]
        {
            FirstNotEmpty(client.Company, null),
            FirstNotEmpty(client.Address, null),
            FirstNotEmpty(client.Phone, null),
            FirstNotEmpty(client.Email, null),
        };

        return string.Join(", ", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildLegalEntityRequisites(LegalEntity? entity, string fallbackName)
    {
        if (entity is null)
        {
            return fallbackName;
        }

        var parts = new[]
        {
            FirstNotEmpty(entity.FullName, entity.ShortName, fallbackName),
            string.IsNullOrWhiteSpace(entity.Inn) ? null : $"ИНН {entity.Inn}",
            string.IsNullOrWhiteSpace(entity.Kpp) ? null : $"КПП {entity.Kpp}",
            string.IsNullOrWhiteSpace(entity.Ogrn) ? null : $"ОГРН {entity.Ogrn}",
            FirstNotEmpty(entity.Address, null),
            FirstNotEmpty(entity.Phone, null),
            FirstNotEmpty(entity.Email, null),
        };

        return string.Join(", ", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static decimal ResolveInvoiceTotal(Contract contract)
    {
        var legacyPaymentIds = contract.ContractInvoices.Select(link => link.InvoiceId).ToHashSet();
        var canonicalTotal = contract.Payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice && !legacyPaymentIds.Contains(payment.Id))
            .Sum(payment => payment.Amount);

        return contract.ContractInvoices.Sum(link => link.Invoice?.Amount ?? 0m) + canonicalTotal;
    }

    private static decimal ResolveActTotal(Contract contract)
        => contract.ContractActs.Sum(link => link.Act?.Amount ?? 0m);

    private static decimal ResolvePaymentTotal(Contract contract)
        => contract.ContractPayments.Sum(link => link.Payment?.Amount ?? 0m)
           + contract.Payments.Where(payment => payment.DocumentKind != DocumentKind.Invoice).Sum(payment => payment.Amount);

    private static void AppendDetailsRow(StringBuilder builder, string title, string? value)
    {
        builder
            .Append("<tr><th>")
            .Append(Encode(title))
            .Append("</th><td>")
            .Append(Encode(string.IsNullOrWhiteSpace(value) ? "—" : value))
            .Append("</td></tr>");
    }

    private static void AppendSection(StringBuilder builder, string title, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder
            .Append("<p><strong>")
            .Append(Encode(title))
            .Append(":</strong></p><p class=\"text-block\">")
            .Append(Encode(value))
            .Append("</p>");
    }

    private static string FormatStatus(string? status)
        => ContractStatus.Normalize(status) switch
        {
            ContractStatus.Draft => "Черновик",
            ContractStatus.NotSigned => "Не подписан",
            ContractStatus.RequiresClientSignature => "Требуется подпись клиента",
            ContractStatus.OnApproval => "На согласовании",
            ContractStatus.Approved => "Согласован",
            ContractStatus.ApprovalRejected => "Отказ в согласовании",
            ContractStatus.NeedsCorrection => "Требует исправления",
            ContractStatus.OnSigning => "На подписании",
            ContractStatus.Concluded => "Подписан",
            ContractStatus.Terminated => "Расторгнут",
            ContractStatus.Cancelled => "Аннулирован",
            _ => "Черновик",
        };

    private static string FormatDate(DateTime? value)
        => value.HasValue ? value.Value.ToString("dd.MM.yyyy", RuCulture) : "—";

    private static string FormatMoney(decimal value)
        => value.ToString("N2", RuCulture) + " ₽";

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);
}
