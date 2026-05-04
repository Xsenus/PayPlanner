using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Services.Acts;

public static class ActDocumentTemplateEngine
{
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";
    private static readonly JsonSerializerOptions ClientProfileJsonOptions = new(JsonSerializerDefaults.Web);

    public static string RenderHtml(
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
        var commentHtml = string.IsNullOrWhiteSpace(act.Comment)
            ? string.Empty
            : $"<div class=\"mar-t-10 comment\"><b>Комментарий:</b> {Encode(act.Comment)}</div>";

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>{{Encode(documentTitle)}}</title>
  <style>
    @page { size: A4; margin: 45px 0; }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      color: #000000;
      font-family: Arial, "DejaVu Sans", sans-serif;
      font-size: 12px;
      line-height: 1.25;
      -webkit-print-color-adjust: exact !important;
      print-color-adjust: exact !important;
    }
    .page-content {
      width: 675px;
      padding: 0 50px;
      height: 100%;
      box-sizing: content-box;
      zoom: 1;
      margin: 0 auto;
      background: #ffffff;
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
    }
    .text-r { text-align: right; }
    .text-c { text-align: center; }
    .font-b { font-weight: 700; }
    .v-al-t { vertical-align: top; }
    .v-al-b { vertical-align: bottom; }
    .pad-t-5 { padding-top: 5px; }
    .color-line { border-bottom: 1px solid #000000; }
    .doc-main-title {
      margin: 10px 0 12px;
      text-align: center;
      font-size: 16px !important;
      font-weight: 700;
      line-height: 1.15;
    }
    .counterparty-table td {
      padding: 3px 4px;
      vertical-align: top;
      font-size: 12px !important;
      line-height: 1.25;
    }
    .counterparty-label {
      width: 15%;
      color: #000000;
      white-space: nowrap;
    }
    .party-label {
      width: 15%;
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
      margin: 8px 0 6px;
      border: 1px solid #000000;
    }
    .items-table th,
    .items-table td {
      border: 1px solid #000000;
      padding: 4px 6px;
      vertical-align: top;
      line-height: 1.25;
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
      width: 55%;
      word-break: break-word;
    }
    .items-table .item-qty {
      width: 7%;
      text-align: center;
      vertical-align: middle;
      white-space: nowrap;
    }
    .items-table .item-unit {
      width: 8%;
      text-align: center;
      vertical-align: middle;
      white-space: nowrap;
    }
    .items-table .item-price,
    .items-table .item-sum {
      width: 12.5%;
      text-align: right;
      vertical-align: middle;
      white-space: nowrap;
    }
    .totals-table td {
      padding: 0;
      font-size: 12px !important;
    }
    .totals-label {
      width: 84%;
      text-align: right;
      padding-right: 10px !important;
      font-weight: 700;
    }
    .totals-value {
      width: 16%;
      text-align: right;
      white-space: nowrap;
    }
    .summary-block {
      margin-top: 10px;
      line-height: 1.25;
    }
    .mar-t-5 { margin-top: 5px !important; }
    .mar-t-10 { margin-top: 10px !important; }
    .mar-t-15 { margin-top: 15px !important; }
    .summary-words {
      margin-top: 2px;
      font-weight: 700;
    }
    .acceptance {
      margin-top: 12px;
      line-height: 1.35;
    }
    .comment {
      margin-top: 10px;
      line-height: 1.35;
    }
    .signature-area {
      margin-top: 18px;
    }
    .signature-table {
      margin-top: 16px;
    }
    .signature-table td {
      border: 0;
      vertical-align: top;
    }
    .signature-gap {
      width: 4%;
    }
    .signature-role {
      padding-bottom: 6px;
      font-size: 12px !important;
      font-weight: 700;
      text-transform: uppercase;
    }
    .signature-party {
      min-height: 30px;
      padding-bottom: 10px;
      font-size: 12px !important;
    }
    .signature-sign-cell {
      position: relative;
      min-height: 58px;
      padding-top: 28px;
      text-align: center;
      vertical-align: bottom;
    }
    .signature-mark {
      position: relative;
      z-index: 4;
      white-space: nowrap;
    }
    .signature {
      position: absolute;
      left: 6px;
      bottom: 8px;
      z-index: 3;
      max-width: 145px;
      max-height: 38px;
      object-fit: contain;
    }
    .seal {
      position: absolute;
      left: 72px;
      bottom: -20px;
      z-index: 2;
      max-width: 112px;
      max-height: 112px;
      object-fit: contain;
      opacity: 0.92;
    }
  </style>
</head>
<body>
  <div class="page-content">
    <div class="document-template act-template">
      <div class="doc-main-title">{{Encode(documentTitle)}}</div>

        <table class="counterparty-table">
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

        <table class="items-table">
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

        <table class="totals-table mar-t-5">
          <tbody>
            <tr>
              <td class="totals-label">Итого:</td>
              <td class="totals-value">{{FormatMoney(totalAmount)}}</td>
            </tr>
            <tr>
              <td class="totals-label">В том числе НДС:</td>
              <td class="totals-value">0,00</td>
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
        {{commentHtml}}

        <div class="signature-area">
          <table class="signature-table">
            <tbody>
              <tr>
                <td>
                  <div class="signature-role">ИСПОЛНИТЕЛЬ</div>
                  <div class="signature-party">{{Encode(seller.SignaturePartyName)}}</div>
                  <div class="signature-sign-cell">
                    {{signatureHtml}}
                    {{sealHtml}}
                    <span class="signature-mark">/ {{Encode(seller.SignatoryShortName)}} /</span>
                  </div>
                </td>
                <td class="signature-gap"></td>
                <td>
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

    private static List<RenderedActLineItem> BuildEffectiveLineItems(
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
            .OrderByDescending(account => string.Equals(account.AccountType?.Trim(), "Основной", StringComparison.OrdinalIgnoreCase))
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
        if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            requisites.Add($"р/с {accountNumber}");
        }

        var bankName = FirstNotEmpty(bankAccount.Bank, mainCompany?.TreasuryBankName);
        if (!string.IsNullOrWhiteSpace(bankName))
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
        => BuildClientParty(
            EnumerateBuyerClients(act).ToList(),
            FirstNotEmpty(
                act.Client?.Name,
                act.Invoice?.Client?.Name,
                act.Invoice?.ExpenseClient?.Name,
                act.Invoice?.CounterpartyName,
                "Заказчик"));

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

        if (!string.IsNullOrWhiteSpace(passport))
        {
            requisitesLines.Add(passport);
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            requisitesLines.Add(address);
        }

        var requisitesText = string.Join(", ", requisitesLines.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return new BuyerInfo(
            Encode(requisitesText),
            string.Join("<br/>", requisitesLines.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(Encode)),
            displayName,
            BuildShortName(displayName));
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
        var invoiceDate = invoiceDateValue?.ToString("dd.MM.yyyy", RuCulture);

        return string.IsNullOrWhiteSpace(invoiceDate)
            ? $"Счет № {invoiceNumber}"
            : $"Счет № {invoiceNumber} от {invoiceDate}";
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
                passportIdentity = $"Паспорт {series.Trim()} № {number.Trim()}";
            }
            else if (!string.IsNullOrWhiteSpace(series))
            {
                passportIdentity = $"Паспорт {series.Trim()}";
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
            parts.Add($"выдан {issueDate.Value:dd.MM.yyyy}");
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

    private static string BuildImageHtml(string? dataUrl, string alt, string cssClass)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return string.Empty;
        }

        return $"<img src=\"{WebUtility.HtmlEncode(dataUrl)}\" alt=\"{Encode(alt)}\" class=\"{cssClass}\" />";
    }

    private static string FormatMoney(decimal value)
        => value.ToString("N2", RuCulture);

    private static string FormatQuantity(decimal value)
        => value.ToString("0.####", RuCulture);

    private static string FormatLongDate(DateTime value)
        => value.ToString("d MMMM yyyy 'г.'", RuCulture);

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

    private sealed record RenderedActLineItem(string Name, decimal Quantity, string Unit, decimal Price)
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
