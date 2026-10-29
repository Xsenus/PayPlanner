using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Services.Contracts;

public static class ContractDocumentTemplateEngine
{
    public const string DefaultTemplateKey = "contract-standard";
    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Regex TokenRegex = new("""\{\{\s*([a-zA-Z0-9\._]+)\s*\}\}""", RegexOptions.Compiled);

    public static InvoiceDocumentTemplateSettingsDto CreateDefaultSettings()
    {
        return CreateResponse(new ContractDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = DefaultTemplateKey,
            Templates =
            [
                new ContractDocumentTemplateStorageItem
                {
                    Key = DefaultTemplateKey,
                    Name = "Стандартный договор",
                    Description = "Базовый печатный шаблон договора со сторонами, условиями и подписями.",
                    Html = DefaultTemplateHtml,
                },
            ],
        });
    }

    public static InvoiceDocumentTemplateSettingsDto CreateResponse(ContractDocumentTemplateSettingsStorage storage)
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
                Html = item.Html.Trim(),
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
            PrintWithSignatureAndSealByDefault = false,
            Templates = templates,
            Tokens = GetAvailableTokens(),
            DefaultTemplateHtml = DefaultTemplateHtml,
        };
    }

    public static ContractDocumentTemplateSettingsStorage NormalizeRequest(InvoiceDocumentTemplateSettingsRequest request)
        => new()
        {
            ActiveTemplateKey = request.ActiveTemplateKey.Trim(),
            Templates = request.Templates
                .Where(static item => !string.IsNullOrWhiteSpace(item.Key) || !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Html))
                .Select(item => new ContractDocumentTemplateStorageItem
                {
                    Key = item.Key.Trim(),
                    Name = item.Name.Trim(),
                    Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                    Html = item.Html.Trim(),
                })
                .ToList(),
        };

    public static string Serialize(ContractDocumentTemplateSettingsStorage storage)
        => JsonSerializer.Serialize(storage);

    public static ContractDocumentTemplateSettingsStorage? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ContractDocumentTemplateSettingsStorage>(json);
        }
        catch
        {
            return null;
        }
    }

    public static List<InvoiceDocumentTemplateTokenDto> GetAvailableTokens()
        =>
        [
            new() { Key = "contract.number", Category = "Договор", Description = "Номер договора", IsRepeating = false },
            new() { Key = "contract.date", Category = "Договор", Description = "Дата договора", IsRepeating = false },
            new() { Key = "contract.title", Category = "Договор", Description = "Предмет или заголовок договора", IsRepeating = false },
            new() { Key = "contract.description", Category = "Договор", Description = "Описание или условия договора", IsRepeating = false },
            new() { Key = "contract.status", Category = "Договор", Description = "Текущий статус договора", IsRepeating = false },
            new() { Key = "contract.startDate", Category = "Договор", Description = "Дата начала действия", IsRepeating = false },
            new() { Key = "contract.validUntil", Category = "Договор", Description = "Дата окончания или бессрочно", IsRepeating = false },
            new() { Key = "contract.amount", Category = "Договор", Description = "Сумма договора", IsRepeating = false },
            new() { Key = "contract.invoiceTotal", Category = "Договор", Description = "Сумма связанных счетов", IsRepeating = false },
            new() { Key = "contract.actTotal", Category = "Договор", Description = "Сумма связанных актов", IsRepeating = false },
            new() { Key = "contract.paymentTotal", Category = "Договор", Description = "Сумма связанных оплат", IsRepeating = false },
            new() { Key = "contract.detailsRowsHtml", Category = "HTML-блоки", Description = "Строки таблицы с основными сведениями", IsRepeating = false },
            new() { Key = "contract.conditionsHtml", Category = "HTML-блоки", Description = "Блок условий, расторжения и аннулирования", IsRepeating = false },
            new() { Key = "seller.name", Category = "Исполнитель", Description = "Название исполнителя", IsRepeating = false },
            new() { Key = "seller.requisites", Category = "Исполнитель", Description = "Реквизиты исполнителя", IsRepeating = false },
            new() { Key = "customer.name", Category = "Заказчик", Description = "Имя или название заказчика", IsRepeating = false },
            new() { Key = "customer.requisites", Category = "Заказчик", Description = "Реквизиты заказчика", IsRepeating = false },
        ];

    public static string RenderHtml(Contract contract, LegalEntity? mainCompany)
        => RenderHtml(DefaultTemplateHtml, contract, mainCompany);

    public static string RenderHtml(string templateHtml, Contract contract, LegalEntity? mainCompany)
        => RenderCore(templateHtml, contract, mainCompany, null);

    public static string RenderHtml(string templateHtml, Contract contract, LegalEntity? mainCompany, DocumentImagePrintOptions imageOptions)
    {
        ArgumentNullException.ThrowIfNull(imageOptions);
        templateHtml = string.IsNullOrWhiteSpace(templateHtml) ? DefaultTemplateHtml : templateHtml;
        if (string.Equals(templateHtml.Trim(), DefaultTemplateHtml, StringComparison.Ordinal))
        {
            if (imageOptions.LogoOf(mainCompany) is not null)
                templateHtml = templateHtml.Replace("<h1>Договор № {{contract.number}}</h1>",
                    "<table class=\"document-header\"><tr><td style=\"width:75%;vertical-align:middle\"><h1>Договор № {{contract.number}}</h1></td><td style=\"width:25%;text-align:right;vertical-align:middle\">{{seller.logoHtml}}</td></tr></table>", StringComparison.Ordinal);
            var hasSeal = imageOptions.SealOf(mainCompany) is not null;
            if (hasSeal || imageOptions.SignatureOf(mainCompany) is not null)
            {
                var height = hasSeal ? 90 : 42;
                var rowStart = $"<table class=\"image-signature\" style=\"width:190px;height:{height}px;table-layout:fixed\"><tr>";
                var emptyCell = $"<td style=\"padding:0;vertical-align:middle;height:{height}px\">";
                templateHtml = templateHtml.Replace("Исполнитель<br>",
                    $"Исполнитель<br>{rowStart}{emptyCell}{{{{seller.signatureHtml}}}}</td>{emptyCell}{{{{seller.sealHtml}}}}</td></tr></table>", StringComparison.Ordinal)
                    .Replace("Заказчик<br>", $"Заказчик<br>{rowStart}{emptyCell}</td></tr></table>", StringComparison.Ordinal);
                templateHtml = templateHtml.Replace("<h2>Подписи сторон</h2>", "<h2 style=\"page-break-after:avoid\">Подписи сторон</h2>", StringComparison.Ordinal)
                    .Replace("<table class=\"signatures\">", "<table class=\"signatures\" style=\"page-break-inside:avoid;break-inside:avoid\">", StringComparison.Ordinal);
                var signatureTable = templateHtml.IndexOf("<table class=\"signatures\"", StringComparison.Ordinal);
                var signatureRow = templateHtml.IndexOf("<tr>", signatureTable, StringComparison.Ordinal);
                templateHtml = templateHtml.Insert(signatureRow + 3, " style=\"page-break-inside:avoid;break-inside:avoid\"");
            }
        }
        return RenderCore(templateHtml, contract, mainCompany, imageOptions);
    }

    private static string RenderCore(string templateHtml, Contract contract, LegalEntity? mainCompany, DocumentImagePrintOptions? imageOptions)
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

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["contract.number"] = Encode(contract.Number),
            ["contract.date"] = Encode(FormatDate(contract.Date)),
            ["contract.title"] = Encode(contract.Title ?? string.Empty),
            ["contract.description"] = Encode(contract.Description ?? string.Empty),
            ["contract.status"] = Encode(statusLabel),
            ["contract.startDate"] = Encode(FormatDate(contract.StartDate)),
            ["contract.validUntil"] = Encode(validUntil),
            ["contract.amount"] = Encode(amountText),
            ["contract.invoiceTotal"] = Encode(FormatMoney(linkedInvoicesTotal)),
            ["contract.actTotal"] = Encode(FormatMoney(linkedActsTotal)),
            ["contract.paymentTotal"] = Encode(FormatMoney(linkedPaymentsTotal)),
            ["contract.detailsRowsHtml"] = rows.ToString(),
            ["contract.conditionsHtml"] = conditions.Length > 0 ? conditions.ToString() : "<p>Условия договора не заполнены в карточке.</p>",
            ["seller.name"] = Encode(sellerName),
            ["seller.requisites"] = Encode(sellerRequisites),
            ["seller.logoHtml"] = DocumentPrintImage.Render(imageOptions?.LogoOf(mainCompany), "Логотип", "logo", 120, 90),
            ["seller.sealHtml"] = DocumentPrintImage.Render(imageOptions?.SealOf(mainCompany), "Печать", "seal", 90, 90),
            ["seller.signatureHtml"] = DocumentPrintImage.Render(imageOptions?.SignatureOf(mainCompany), "Подпись", "signature", 76, 42),
            ["customer.name"] = Encode(customerName),
            ["customer.requisites"] = Encode(customerRequisites),
        };

        return ReplaceTokens(templateHtml, tokens);
    }

    public static string RenderTerminationAgreementHtml(Contract contract, LegalEntity? mainCompany)
    {
        var sellerName = FirstNotEmpty(mainCompany?.FullName, mainCompany?.ShortName, "Исполнитель");
        var sellerRequisites = BuildLegalEntityRequisites(mainCompany, sellerName);
        var customerName = ResolveCustomerName(contract);
        var customerRequisites = ResolveCustomerRequisites(contract);
        var terminationDate = FormatDate(contract.TerminationDate);
        var financialTerms = string.IsNullOrWhiteSpace(contract.TerminationFinancialTerms)
            ? "Стороны подтверждают отсутствие дополнительных финансовых условий, если иное не указано в договоре."
            : contract.TerminationFinancialTerms.Trim();

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>Соглашение о расторжении договора № {{Encode(contract.Number)}}</title>
  <style>
    @page { size: A4; margin: 18mm 16mm; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #111827; font-family: "Times New Roman", serif; font-size: 13px; line-height: 1.45; }
    h1 { margin: 0 0 6px; text-align: center; font-size: 22px; }
    h2 { margin: 22px 0 8px; font-size: 16px; }
    .subtitle { margin-bottom: 24px; text-align: center; color: #4b5563; }
    .parties, .signatures { width: 100%; border-collapse: collapse; margin-top: 16px; }
    .parties td { width: 50%; vertical-align: top; padding: 12px; border: 1px solid #d1d5db; }
    .party-title { margin-bottom: 6px; font-weight: 700; text-transform: uppercase; }
    .muted { color: #4b5563; font-size: 12px; }
    .section { margin-top: 12px; }
    .section p { margin: 0 0 8px; }
    .signatures td { width: 50%; padding-top: 42px; vertical-align: top; }
    .line { display: inline-block; width: 220px; border-bottom: 1px solid #111827; }
  </style>
</head>
<body>
  <h1>Соглашение о расторжении договора № {{Encode(contract.Number)}}</h1>
  <div class="subtitle">к договору от {{Encode(FormatDate(contract.Date))}}</div>

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

  <h2>1. Предмет соглашения</h2>
  <div class="section">
    <p>Стороны договорились расторгнуть договор № {{Encode(contract.Number)}} от {{Encode(FormatDate(contract.Date))}} с {{Encode(terminationDate)}}.</p>
    <p>Основание расторжения: {{Encode(contract.TerminationReason ?? "по соглашению сторон")}}.</p>
  </div>

  <h2>2. Условия расторжения</h2>
  <div class="section">
    <p>{{Encode(contract.TerminationTerms ?? "Условия расторжения не заполнены в карточке договора.")}}</p>
  </div>

  <h2>3. Финансовые условия</h2>
  <div class="section">
    <p>{{Encode(financialTerms)}}</p>
  </div>

  <h2>4. Заключительные положения</h2>
  <div class="section">
    <p>Соглашение вступает в силу с даты подписания сторонами, если стороны не указали иной порядок.</p>
    <p>Остальные обязательства сторон прекращаются в порядке, указанном в договоре и настоящем соглашении.</p>
  </div>

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

    private static string ReplaceTokens(string templateHtml, IReadOnlyDictionary<string, string> tokens)
        => TokenRegex.Replace(templateHtml, match =>
        {
            var key = match.Groups[1].Value;
            return tokens.TryGetValue(key, out var value) ? value : string.Empty;
        });

    public sealed class ContractDocumentTemplateSettingsStorage
    {
        public string ActiveTemplateKey { get; set; } = string.Empty;
        public List<ContractDocumentTemplateStorageItem> Templates { get; set; } = new();
    }

    public sealed class ContractDocumentTemplateStorageItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Html { get; set; } = string.Empty;
    }

    public static readonly string DefaultTemplateHtml = """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>Договор № {{contract.number}}</title>
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
  <h1>Договор № {{contract.number}}</h1>
  <div class="subtitle">{{contract.date}}</div>

  <table class="parties">
    <tr>
      <td>
        <div class="party-title">Исполнитель</div>
        <div>{{seller.name}}</div>
        <div class="muted">{{seller.requisites}}</div>
      </td>
      <td>
        <div class="party-title">Заказчик</div>
        <div>{{customer.name}}</div>
        <div class="muted">{{customer.requisites}}</div>
      </td>
    </tr>
  </table>

  <h2>Основные сведения</h2>
  <table class="details">
    {{contract.detailsRowsHtml}}
  </table>

  <h2>Условия договора</h2>
  {{contract.conditionsHtml}}

  <h2>Подписи сторон</h2>
  <table class="signatures">
    <tr>
      <td>
        Исполнитель<br>
        <span class="line"></span><br>
        <span class="muted">{{seller.name}}</span>
      </td>
      <td>
        Заказчик<br>
        <span class="line"></span><br>
        <span class="muted">{{customer.name}}</span>
      </td>
    </tr>
  </table>
</body>
</html>
""";

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
            ContractStatus.Concluded => "Подписан клиентом",
            ContractStatus.Terminated => "Расторгнут",
            ContractStatus.Cancelled => "Аннулирован",
            _ => "Черновик",
        };

    private static string FormatDate(DateTime? value)
        => value.HasValue ? ContractDomainService.NormalizeLocalDate(value.Value).ToString("dd.MM.yyyy", RuCulture) : "—";

    private static string FormatMoney(decimal value)
        => value.ToString("N2", RuCulture) + " ₽";

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);
}
