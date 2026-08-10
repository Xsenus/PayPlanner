using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceDocumentTemplateEngine
{
    public const string DefaultTemplateKey = "invoice-standard";
    public const string OfferTemplateKey = "invoice-offer";
    public const string OrderTemplateKey = "invoice-order";
    private const string ClientProfileNotesPrefix = "__PP_CLIENT_PROFILE_V2__:";
    private static readonly HashSet<string> LegacyManagedStandardTemplateFingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "ba913b728b9bc5352ce6affa7845f5b7e89889420470a2179d12f9a223d6abe0",
        "8c5beee74da7dafc884bfc05d78b4681371f0bc9874540b0676d1e1c15bce7e4",
        "92ca4fb24fbf8673ebc6256a299fdb9580ef9f3a64f78a3e3db7209bc226b18f",
        "af28afb12f128c846dd0261081335eb96d8c4b4770947616f8a4f1b17783f534",
        "6df3df7d4529e7073c7f2293989494b8d31f6107ec08f7ced8eb11cd32e31a9d",
        "86db923aeb55580e373f983d6c99a48d6734669691ecf3d8004cd8cbaa4c99d3",
        "4edeef248152dfedeaaa14824817cb1915fd9aa376cc5ed14100e21ae1925a4d",
        "eceffbe9a3e13e511a8e6fbb1bd34bad41fa256698a6e18e99a75696cdb111f6",
    };

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly JsonSerializerOptions ClientProfileJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex LoopRegex = new("""\{\{#lineItems\}\}(.*?)\{\{\/lineItems\}\}""", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new("""\{\{\s*([a-zA-Z0-9\._]+)\s*\}\}""", RegexOptions.Compiled);

    public static InvoiceDocumentTemplateSettingsDto CreateDefaultSettings()
    {
        return CreateResponse(new InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = DefaultTemplateKey,
            PrintWithSignatureAndSealByDefault = false,
            StandardTemplateFingerprint = CurrentDefaultTemplateFingerprint,
            Templates =
            [
                new InvoiceDocumentTemplateStorageItem
                {
                    Key = DefaultTemplateKey,
                    Name = "Стандартный счет",
                    Description = "Базовый печатный шаблон счета с реквизитами, позициями и подписью.",
                    Html = DefaultTemplateHtml,
                },
                CreateOfferTemplateStorageItem(),
                CreateOrderTemplateStorageItem(),
            ],
        });
    }

    public static InvoiceDocumentTemplateSettingsDto CreateResponse(InvoiceDocumentTemplateSettingsStorage storage)
    {
        var normalizedStorage = NormalizeStorage(storage);
        var templates = normalizedStorage.Templates
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
            PrintWithSignatureAndSealByDefault = normalizedStorage.PrintWithSignatureAndSealByDefault,
            Templates = templates,
            Tokens = GetAvailableTokens(),
            DefaultTemplateHtml = DefaultTemplateHtml,
        };
    }

    public static InvoiceDocumentTemplateSettingsStorage NormalizeRequest(InvoiceDocumentTemplateSettingsRequest request)
    {
        var templates = request.Templates
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key) || !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Html))
            .Select(item => new InvoiceDocumentTemplateStorageItem
            {
                Key = item.Key.Trim(),
                Name = item.Name.Trim(),
                Description = string.IsNullOrWhiteSpace(item.Description) ? null : item.Description.Trim(),
                Html = item.Html.Trim(),
            })
            .ToList();

        var standardTemplate = templates.FirstOrDefault(item =>
            string.Equals(item.Key, DefaultTemplateKey, StringComparison.OrdinalIgnoreCase));

        return new InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = request.ActiveTemplateKey.Trim(),
            PrintWithSignatureAndSealByDefault = request.PrintWithSignatureAndSealByDefault,
            StandardTemplateFingerprint = ResolveManagedStandardTemplateFingerprint(
                standardTemplate?.Html,
                currentFingerprint: null),
            Templates = templates,
        };
    }

    public static string Serialize(InvoiceDocumentTemplateSettingsStorage storage)
        => JsonSerializer.Serialize(storage);

    public static InvoiceDocumentTemplateSettingsStorage? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<InvoiceDocumentTemplateSettingsStorage>(json);
        }
        catch
        {
            return null;
        }
    }

    public static string RenderHtml(
        string templateHtml,
        Payment payment,
        IReadOnlyCollection<InvoiceLineItemDto> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal = true,
        string? installmentScheduleHtml = null)
    {
        var model = BuildRenderModel(payment, lineItems, mainCompany, includeSignatureAndSeal, installmentScheduleHtml);
        var rendered = RenderLineItems(templateHtml, model.LineItems);
        return ReplaceTokens(rendered, model.Tokens);
    }

    public static List<InvoiceDocumentTemplateTokenDto> GetAvailableTokens()
    {
        return
        [
            new() { Key = "document.title", Category = "Документ", Description = "Полный заголовок документа: Счет №... от ...", IsRepeating = false },
            new() { Key = "invoice.number", Category = "Счет", Description = "Номер счета", IsRepeating = false },
            new() { Key = "invoice.date", Category = "Счет", Description = "Дата счета в формате дд.мм.гггг", IsRepeating = false },
            new() { Key = "invoice.dateLong", Category = "Счет", Description = "Дата счета словами", IsRepeating = false },
            new() { Key = "invoice.dueDate", Category = "Счет", Description = "Срок оплаты", IsRepeating = false },
            new() { Key = "invoice.amount", Category = "Счет", Description = "Сумма счета", IsRepeating = false },
            new() { Key = "invoice.amountWords", Category = "Счет", Description = "Сумма прописью", IsRepeating = false },
            new() { Key = "invoice.description", Category = "Счет", Description = "Описание счета", IsRepeating = false },
            new() { Key = "invoice.clientCommentHtml", Category = "Счет", Description = "HTML-блок комментария для клиента", IsRepeating = false },
            new() { Key = "invoice.dueDateHtml", Category = "Счет", Description = "HTML-блок срока оплаты", IsRepeating = false },
            new() { Key = "invoice.notForAccountingHtml", Category = "Счет", Description = "HTML-блок пометки, что счет не для бухгалтерии", IsRepeating = false },
            new() { Key = "invoice.total", Category = "Счет", Description = "Итого по позициям", IsRepeating = false },
            new() { Key = "invoice.subTotal", Category = "Счет", Description = "Итого без НДС", IsRepeating = false },
            new() { Key = "invoice.totalWords", Category = "Счет", Description = "Итого прописью", IsRepeating = false },
            new() { Key = "invoice.itemsCount", Category = "Счет", Description = "Количество наименований в счете", IsRepeating = false },
            new() { Key = "invoice.vatText", Category = "Счет", Description = "Строка по НДС", IsRepeating = false },
            new() { Key = "invoice.installmentScheduleHtml", Category = "Счет", Description = "HTML-блок графика платежей (если есть рассрочка)", IsRepeating = false },
            new() { Key = "invoice.qrCodeHtml", Category = "Счет", Description = "HTML-блок QR-кода для оплаты", IsRepeating = false },
            new() { Key = "seller.shortName", Category = "Продавец", Description = "Краткое название продавца", IsRepeating = false },
            new() { Key = "seller.fullName", Category = "Продавец", Description = "Полное название продавца", IsRepeating = false },
            new() { Key = "seller.organizationForm", Category = "Продавец", Description = "Организационно-правовая форма продавца", IsRepeating = false },
            new() { Key = "seller.okpo", Category = "Продавец", Description = "Код ОКПО продавца", IsRepeating = false },
            new() { Key = "seller.inn", Category = "Продавец", Description = "ИНН продавца", IsRepeating = false },
            new() { Key = "seller.kpp", Category = "Продавец", Description = "КПП продавца", IsRepeating = false },
            new() { Key = "seller.kppOrDash", Category = "Продавец", Description = "КПП или дефис, если значение не заполнено", IsRepeating = false },
            new() { Key = "seller.ogrn", Category = "Продавец", Description = "ОГРН или ОГРНИП продавца", IsRepeating = false },
            new() { Key = "seller.vatText", Category = "Продавец", Description = "Короткая пометка по НДС продавца", IsRepeating = false },
            new() { Key = "seller.address", Category = "Продавец", Description = "Адрес продавца", IsRepeating = false },
            new() { Key = "seller.phone", Category = "Продавец", Description = "Телефон продавца", IsRepeating = false },
            new() { Key = "seller.email", Category = "Продавец", Description = "Email продавца", IsRepeating = false },
            new() { Key = "seller.taxSystem", Category = "Продавец", Description = "Система налогообложения продавца", IsRepeating = false },
            new() { Key = "seller.okved", Category = "Продавец", Description = "ОКВЭД продавца", IsRepeating = false },
            new() { Key = "seller.bankName", Category = "Продавец", Description = "Банк продавца", IsRepeating = false },
            new() { Key = "seller.bik", Category = "Продавец", Description = "БИК продавца", IsRepeating = false },
            new() { Key = "seller.accountNumber", Category = "Продавец", Description = "Расчетный счет продавца", IsRepeating = false },
            new() { Key = "seller.corrAccountNumber", Category = "Продавец", Description = "Корреспондентский счет продавца", IsRepeating = false },
            new() { Key = "seller.requisites", Category = "Продавец", Description = "Полные реквизиты продавца одной строкой", IsRepeating = false },
            new() { Key = "seller.documentDirectorPosition", Category = "Продавец", Description = "Должность подписанта", IsRepeating = false },
            new() { Key = "seller.documentDirectorFullName", Category = "Продавец", Description = "ФИО подписанта", IsRepeating = false },
            new() { Key = "seller.documentDirectorShortName", Category = "Продавец", Description = "Краткая подпись подписанта, например Иванов И. И.", IsRepeating = false },
            new() { Key = "seller.chiefAccountantPosition", Category = "Продавец", Description = "Должность главного бухгалтера", IsRepeating = false },
            new() { Key = "seller.chiefAccountantFullName", Category = "Продавец", Description = "ФИО главного бухгалтера", IsRepeating = false },
            new() { Key = "seller.chiefAccountantShortName", Category = "Продавец", Description = "Краткая подпись главного бухгалтера", IsRepeating = false },
            new() { Key = "seller.logoHtml", Category = "Продавец", Description = "HTML-блок логотипа", IsRepeating = false },
            new() { Key = "seller.sealHtml", Category = "Продавец", Description = "HTML-блок печати", IsRepeating = false },
            new() { Key = "seller.signatureHtml", Category = "Продавец", Description = "HTML-блок подписи", IsRepeating = false },
            new() { Key = "buyer.name", Category = "Покупатель", Description = "ФИО или название покупателя", IsRepeating = false },
            new() { Key = "buyer.address", Category = "Покупатель", Description = "Адрес покупателя", IsRepeating = false },
            new() { Key = "buyer.organizationForm", Category = "Покупатель", Description = "Организационно-правовая форма покупателя", IsRepeating = false },
            new() { Key = "buyer.passport", Category = "Покупатель", Description = "Паспортные данные покупателя", IsRepeating = false },
            new() { Key = "buyer.inn", Category = "Покупатель", Description = "ИНН покупателя", IsRepeating = false },
            new() { Key = "buyer.kpp", Category = "Покупатель", Description = "КПП покупателя", IsRepeating = false },
            new() { Key = "buyer.kppOrDash", Category = "Покупатель", Description = "КПП покупателя или пустое значение", IsRepeating = false },
            new() { Key = "buyer.ogrn", Category = "Покупатель", Description = "ОГРН или ОГРНИП покупателя", IsRepeating = false },
            new() { Key = "buyer.okpo", Category = "Покупатель", Description = "Код ОКПО покупателя", IsRepeating = false },
            new() { Key = "buyer.snils", Category = "Покупатель", Description = "СНИЛС покупателя", IsRepeating = false },
            new() { Key = "buyer.phone", Category = "Покупатель", Description = "Телефон покупателя", IsRepeating = false },
            new() { Key = "buyer.email", Category = "Покупатель", Description = "Email покупателя", IsRepeating = false },
            new() { Key = "buyer.contract", Category = "Покупатель", Description = "Связанный договор покупателя", IsRepeating = false },
            new() { Key = "buyer.foundation", Category = "Покупатель", Description = "Основание счета по покупателю", IsRepeating = false },
            new() { Key = "buyer.case", Category = "Покупатель", Description = "Связанное дело покупателя", IsRepeating = false },
            new() { Key = "buyer.requisites", Category = "Покупатель", Description = "Полные реквизиты покупателя одной строкой", IsRepeating = false },
            new() { Key = "buyer.requisitesHtml", Category = "Покупатель", Description = "Полные реквизиты покупателя с переносами строк", IsRepeating = false },
            new() { Key = "buyer.foundationRowHtml", Category = "Покупатель", Description = "HTML-строка основания для таблицы покупателя", IsRepeating = false },
            new() { Key = "lineItems", Category = "Позиции", Description = "Повторяемый блок. Используйте {{#lineItems}} ... {{/lineItems}}", IsRepeating = true },
            new() { Key = "index", Category = "Позиции", Description = "Номер строки внутри блока lineItems", IsRepeating = true },
            new() { Key = "name", Category = "Позиции", Description = "Название позиции", IsRepeating = true },
            new() { Key = "quantity", Category = "Позиции", Description = "Количество", IsRepeating = true },
            new() { Key = "unit", Category = "Позиции", Description = "Единица измерения", IsRepeating = true },
            new() { Key = "price", Category = "Позиции", Description = "Цена позиции", IsRepeating = true },
            new() { Key = "total", Category = "Позиции", Description = "Сумма позиции", IsRepeating = true },
            new() { Key = "vat", Category = "Позиции", Description = "НДС позиции", IsRepeating = true },
        ];
    }

    private static InvoiceDocumentRenderModel BuildRenderModel(
        Payment payment,
        IReadOnlyCollection<InvoiceLineItemDto> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal,
        string? installmentScheduleHtml)
    {
        var invoiceDate = PaymentDomainService.NormalizeLocalDate(payment.Invoice?.Date ?? payment.AccountDate ?? payment.Date);
        var dueDate = PaymentDomainService.NormalizeLocalDate(payment.Date);
        var number = payment.Invoice?.Number ?? payment.Account ?? "—";
        var normalizedLineItems = lineItems.Count > 0
            ? lineItems.ToList()
            : new List<InvoiceLineItemDto>
            {
                new()
                {
                    Name = string.IsNullOrWhiteSpace(payment.Description) ? "Услуги по счету" : payment.Description,
                    Quantity = 1m,
                    Unit = "усл. ед.",
                    Price = payment.Amount,
                },
            };

        var total = normalizedLineItems.Sum(item => item.Quantity * item.Price);
        var sellerLegalEntity = ResolveSellerLegalEntity(payment, mainCompany);
        var seller = BuildSeller(payment, mainCompany);
        var buyer = BuildBuyer(payment, mainCompany);
        var director = ParsePerson(sellerLegalEntity?.DocumentDirectorJson);
        var directorFullName = BuildPersonFullName(director, sellerLegalEntity?.Director);
        var directorShortName = BuildPersonShortName(director, directorFullName);
        var directorPosition = !string.IsNullOrWhiteSpace(director?.Position)
            ? director!.Position!
            : "Предприниматель";
        var chiefAccountant = ParsePerson(sellerLegalEntity?.ChiefAccountantJson);
        var chiefAccountantFullName = BuildPersonFullName(chiefAccountant, directorFullName);
        var chiefAccountantShortName = BuildPersonShortName(chiefAccountant, chiefAccountantFullName);
        var chiefAccountantPosition = !string.IsNullOrWhiteSpace(chiefAccountant?.Position)
            ? chiefAccountant!.Position!
            : "Главный (старший) бухгалтер";
        var qrCodeHtml = BuildInvoiceQrCodeHtml(payment, seller, total, invoiceDate, number);
        var hasInstallmentSchedule = !string.IsNullOrWhiteSpace(installmentScheduleHtml);
        var vatText = ResolveVatText(seller);
        var lineItemVat = ResolveLineItemVat(seller);
        var orderSellerPosition = sellerLegalEntity is null ? string.Empty : directorPosition;
        var orderSellerSignatory = sellerLegalEntity is null ? seller.ShortName : directorShortName;
        var orderVatLabel = seller.IsVatExempt ? "Без налога (НДС):" : "В том числе НДС:";
        var orderVatValue = seller.IsVatExempt ? "-" : "0,00";
        var dueDateHtml = !hasInstallmentSchedule && dueDate != invoiceDate
            ? $"<div class=\"invoice-meta-line\"><strong>Срок оплаты:</strong> {Encode(FormatDate(dueDate))}</div>"
            : string.Empty;
        var clientCommentHtml = string.IsNullOrWhiteSpace(payment.Description)
            ? string.Empty
            : $"<div class=\"invoice-meta-line\"><strong>Комментарий для клиента:</strong> {Encode(payment.Description)}</div>";
        var notForAccountingHtml = payment.IsNotForAccounting
            ? "<div class=\"invoice-meta-line invoice-flag\">Счет не для бухгалтерии</div>"
            : string.Empty;

        return new InvoiceDocumentRenderModel
        {
            Tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["document.title"] = Encode($"Счет № {number} от {FormatDate(invoiceDate)}"),
                ["invoice.number"] = Encode(number),
                ["invoice.date"] = Encode(FormatDate(invoiceDate)),
                ["invoice.dateLong"] = Encode(invoiceDate.ToString("d MMMM yyyy 'г.'", RuCulture)),
                ["invoice.dueDate"] = Encode(FormatDate(dueDate)),
                ["invoice.amount"] = Encode(FormatMoney(payment.Amount)),
                ["invoice.amountWords"] = Encode(SpellMoney(payment.Amount)),
                ["invoice.description"] = Encode(payment.Description),
                ["invoice.clientCommentHtml"] = clientCommentHtml,
                ["invoice.dueDateHtml"] = dueDateHtml,
                ["invoice.notForAccountingHtml"] = notForAccountingHtml,
                ["invoice.subTotal"] = Encode(FormatMoney(total)),
                ["invoice.total"] = Encode(FormatMoney(total)),
                ["invoice.totalWords"] = Encode(SpellMoney(total)),
                ["invoice.itemsCount"] = Encode(normalizedLineItems.Count.ToString(CultureInfo.InvariantCulture)),
                ["invoice.vatText"] = Encode(vatText),
                ["invoice.orderVatLabel"] = Encode(orderVatLabel),
                ["invoice.orderVatValue"] = Encode(orderVatValue),
                ["invoice.qrCodeHtml"] = qrCodeHtml,
                ["invoice.installmentScheduleHtml"] = installmentScheduleHtml ?? string.Empty,
                ["seller.shortName"] = Encode(seller.ShortName),
                ["seller.fullName"] = Encode(seller.FullName),
                ["seller.organizationForm"] = Encode(seller.OrganizationForm),
                ["seller.okpo"] = Encode(seller.Okpo),
                ["seller.inn"] = Encode(seller.Inn),
                ["seller.kpp"] = Encode(seller.Kpp),
                ["seller.kppOrDash"] = Encode(string.IsNullOrWhiteSpace(seller.Kpp) ? "-" : seller.Kpp),
                ["seller.ogrn"] = Encode(seller.Ogrn),
                ["seller.vatText"] = Encode(vatText),
                ["seller.address"] = Encode(seller.Address),
                ["seller.phone"] = Encode(seller.Phone),
                ["seller.email"] = Encode(seller.Email),
                ["seller.taxSystem"] = Encode(seller.TaxSystem),
                ["seller.okved"] = Encode(seller.Okved),
                ["seller.bankName"] = Encode(seller.BankName),
                ["seller.bik"] = Encode(seller.Bik),
                ["seller.accountNumber"] = Encode(seller.AccountNumber),
                ["seller.corrAccountNumber"] = Encode(seller.CorrAccountNumber),
                ["seller.requisites"] = Encode(seller.Requisites),
                ["seller.orderRequisites"] = Encode(BuildOrderRequisites(seller.ShortName, seller.Inn, seller.Address)),
                ["seller.orderDirectorPosition"] = Encode(orderSellerPosition),
                ["seller.orderSignatoryShortName"] = Encode(orderSellerSignatory),
                ["seller.documentDirectorPosition"] = Encode(directorPosition),
                ["seller.documentDirectorFullName"] = Encode(directorFullName),
                ["seller.documentDirectorShortName"] = Encode(directorShortName),
                ["seller.chiefAccountantPosition"] = Encode(chiefAccountantPosition),
                ["seller.chiefAccountantFullName"] = Encode(chiefAccountantFullName),
                ["seller.chiefAccountantShortName"] = Encode(chiefAccountantShortName),
                ["seller.logoHtml"] = BuildImageHtml(sellerLegalEntity?.LogoDataUrl, "Логотип", "logo"),
                ["seller.sealHtml"] = includeSignatureAndSeal
                    ? BuildImageHtml(sellerLegalEntity?.SealDataUrl, "Печать", "seal")
                    : string.Empty,
                ["seller.signatureHtml"] = includeSignatureAndSeal
                    ? BuildImageHtml(sellerLegalEntity?.DirectorSignatureDataUrl, "Подпись", "signature")
                    : string.Empty,
                ["buyer.name"] = Encode(buyer.Name),
                ["buyer.address"] = Encode(buyer.Address),
                ["buyer.passport"] = Encode(buyer.Passport),
                ["buyer.organizationForm"] = Encode(buyer.OrganizationForm),
                ["buyer.inn"] = Encode(buyer.Inn),
                ["buyer.kpp"] = Encode(buyer.Kpp),
                ["buyer.kppOrDash"] = Encode(string.IsNullOrWhiteSpace(buyer.Kpp) ? string.Empty : buyer.Kpp),
                ["buyer.okpo"] = Encode(buyer.Okpo),
                ["buyer.ogrn"] = Encode(buyer.Ogrn),
                ["buyer.snils"] = Encode(buyer.Snils),
                ["buyer.phone"] = Encode(buyer.Phone),
                ["buyer.email"] = Encode(buyer.Email),
                ["buyer.contract"] = Encode(buyer.Contract),
                ["buyer.foundation"] = Encode(buyer.Foundation),
                ["buyer.case"] = Encode(buyer.Case),
                ["buyer.requisites"] = Encode(buyer.Requisites),
                ["buyer.orderRequisites"] = Encode(BuildOrderRequisites(buyer.Name, buyer.Inn, buyer.Address)),
                ["order.counterpartyName"] = Encode(payment.Type == PaymentType.Expense ? seller.ShortName : buyer.Name),
                ["order.signaturePageBreakClass"] = normalizedLineItems.Count >= 12 ? " signature-new-page" : string.Empty,
                ["buyer.requisitesHtml"] = buyer.RequisitesHtml,
                ["buyer.foundationRowHtml"] = BuildBuyerFoundationRowHtml(buyer.Foundation),
            },
            LineItems = normalizedLineItems
                .Select((item, index) => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["index"] = (index + 1).ToString(CultureInfo.InvariantCulture),
                    ["name"] = Encode(item.Name),
                    ["quantity"] = Encode(item.Quantity.ToString("0.##", RuCulture)),
                    ["unit"] = Encode(string.IsNullOrWhiteSpace(item.Unit) ? "шт" : item.Unit),
                    ["price"] = Encode(FormatMoney(item.Price)),
                    ["total"] = Encode(FormatMoney(item.Quantity * item.Price)),
                    ["vat"] = lineItemVat,
                })
                .ToList(),
        };
    }

    private static string BuildOrderRequisites(string name, string inn, string address)
    {
        var parts = new List<string> { name.Trim() };
        if (!string.IsNullOrWhiteSpace(inn)) parts.Add($"ИНН {inn.Trim()}");
        if (!string.IsNullOrWhiteSpace(address)) parts.Add(address.Trim());
        return string.Join(", ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private static LegalEntity? ResolveSellerLegalEntity(Payment payment, LegalEntity? mainCompany)
    {
        if (payment.Type == PaymentType.Expense)
        {
            return payment.LegalEntity
                ?? payment.Client?.LegalEntity
                ?? null;
        }

        return mainCompany;
    }

    private static SellerInfo BuildSeller(Payment payment, LegalEntity? mainCompany)
    {
        if (payment.Type == PaymentType.Expense)
        {
            if (payment.LegalEntity is not null)
            {
                return BuildSeller(payment.LegalEntity);
            }

            if (payment.Client?.LegalEntity is not null)
            {
                return BuildSeller(payment.Client.LegalEntity);
            }

            if (payment.Client is not null)
            {
                return BuildSeller(payment.Client, payment.CounterpartyName);
            }
        }

        return BuildSeller(mainCompany);
    }

    private static SellerInfo BuildSeller(Client client, string? fallbackName)
    {
        var profile = ParseBuyerProfile(client.Notes);
        var name = FirstNotEmpty(client.Name, fallbackName, "Поставщик");
        var organizationForm = string.Empty;
        var company = client.Company ?? string.Empty;
        var address = ResolveBuyerAddress(client);
        var inn = profile?.Inn?.Trim() ?? string.Empty;

        var requisitesParts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(inn))
        {
            requisitesParts.Add($"ИНН {inn}");
        }

        if (!string.IsNullOrWhiteSpace(company) && !string.Equals(company, name, StringComparison.OrdinalIgnoreCase))
        {
            requisitesParts.Add(company);
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            requisitesParts.Add(address);
        }

        return new SellerInfo
        {
            ShortName = name,
            FullName = name,
            OrganizationForm = organizationForm,
            Inn = inn,
            Kpp = string.Empty,
            Ogrn = string.Empty,
            Okpo = string.Empty,
            IsVatExempt = false,
            Address = address,
            Phone = client.Phone ?? string.Empty,
            Email = client.Email ?? string.Empty,
            TaxSystem = string.Empty,
            Okved = string.Empty,
            BankName = string.Empty,
            Bik = string.Empty,
            AccountNumber = string.Empty,
            CorrAccountNumber = string.Empty,
            Requisites = string.Join(", ", requisitesParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
        };
    }

    private static SellerInfo BuildSeller(LegalEntity? mainCompany)
    {
        var bankAccount = ParseBankAccounts(mainCompany?.BankAccountsJson)
            .OrderByDescending(account => string.Equals(account.AccountType?.Trim(), "Основной", StringComparison.OrdinalIgnoreCase))
            .ThenBy(account => string.IsNullOrWhiteSpace(account.AccountNumber) ? 1 : 0)
            .FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.AccountNumber))
            ?? new BankAccountInfo();

        var fullName = FirstNotEmpty(mainCompany?.FullName, mainCompany?.ShortName, "Организация не выбрана");
        var shortName = FirstNotEmpty(mainCompany?.ShortName, mainCompany?.FullName, "Организация");
        var organizationForm = mainCompany?.OrganizationForm?.Trim() ?? string.Empty;
        var okpo = mainCompany?.Okpo?.Trim() ?? string.Empty;
        var address = LegalEntityAddressResolver.ResolvePreferredAddress(mainCompany);
        var bankName = FirstNotEmpty(bankAccount.Bank, mainCompany?.TreasuryBankName);
        var bik = FirstNotEmpty(bankAccount.Bik, mainCompany?.TreasuryBankBik);
        var accountNumber = FirstNotEmpty(bankAccount.AccountNumber, mainCompany?.TreasuryAccountNumber);
        var corrAccountNumber = FirstNotEmpty(bankAccount.CorrAccountNumber, mainCompany?.TreasuryCorrAccountNumber);
        var legalName = string.IsNullOrWhiteSpace(organizationForm) ? fullName : $"{fullName} ({organizationForm})";

        var requisitesParts = new List<string>
        {
            legalName,
        };

        if (!string.IsNullOrWhiteSpace(mainCompany?.Inn))
        {
            requisitesParts.Add($"ИНН {mainCompany.Inn}");
        }

        if (!string.IsNullOrWhiteSpace(mainCompany?.Kpp))
        {
            requisitesParts.Add($"КПП {mainCompany.Kpp}");
        }

        if (!string.IsNullOrWhiteSpace(address))
        {
            requisitesParts.Add(address);
        }

        if (!string.IsNullOrWhiteSpace(accountNumber))
        {
            requisitesParts.Add($"р/с {accountNumber}");
        }

        if (!string.IsNullOrWhiteSpace(bankName))
        {
            requisitesParts.Add($"в банке {bankName}");
        }

        if (!string.IsNullOrWhiteSpace(bik))
        {
            requisitesParts.Add($"БИК {bik}");
        }

        if (!string.IsNullOrWhiteSpace(okpo))
        {
            requisitesParts.Add($"ОКПО {okpo}");
        }

        return new SellerInfo
        {
            ShortName = shortName,
            FullName = fullName,
            OrganizationForm = organizationForm,
            Inn = mainCompany?.Inn ?? string.Empty,
            Kpp = mainCompany?.Kpp ?? string.Empty,
            Ogrn = mainCompany?.Ogrn ?? string.Empty,
            Okpo = okpo,
            IsVatExempt = mainCompany?.VatWithoutVat == true,
            Address = address,
            Phone = mainCompany?.Phone ?? string.Empty,
            Email = mainCompany?.Email ?? string.Empty,
            TaxSystem = mainCompany?.TaxSystem ?? string.Empty,
            Okved = mainCompany?.Okved ?? string.Empty,
            BankName = bankName,
            Bik = bik,
            AccountNumber = accountNumber,
            CorrAccountNumber = corrAccountNumber,
            Requisites = string.Join(", ", requisitesParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
        };
    }

    private static BuyerInfo BuildBuyer(Payment payment, LegalEntity? mainCompany)
    {
        var isExpense = payment.Type == PaymentType.Expense;
        var directClient = isExpense ? payment.ExpenseClient : payment.Client;
        var directLegalEntity = isExpense ? payment.ExpenseLegalEntity : payment.LegalEntity;
        if (isExpense && directClient is null && directLegalEntity is null)
        {
            directLegalEntity = mainCompany;
        }

        if (directLegalEntity is not null)
        {
            var legalName = FirstNotEmpty(directLegalEntity.FullName, directLegalEntity.ShortName, payment.CounterpartyName, "Контрагент");
            var legalOrganizationForm = directLegalEntity.OrganizationForm?.Trim() ?? string.Empty;
            var legalNameWithForm = string.IsNullOrWhiteSpace(legalOrganizationForm)
                ? legalName
                : $"{legalName} ({legalOrganizationForm})";
            var legalAddress = LegalEntityAddressResolver.ResolvePreferredAddress(directLegalEntity);
            var legalContractValue = BuildBuyerContractValue(payment.Contract);
            var legalContract = string.IsNullOrWhiteSpace(legalContractValue)
                ? string.Empty
                : $"Договор: {legalContractValue}";
            var legalRequisitesLines = new List<string>
            {
                string.Join(", ", new[]
                {
                    legalNameWithForm,
                    string.IsNullOrWhiteSpace(directLegalEntity.Inn) ? null : $"ИНН {directLegalEntity.Inn}",
                    string.IsNullOrWhiteSpace(directLegalEntity.Kpp) ? null : $"КПП {directLegalEntity.Kpp}",
                    string.IsNullOrWhiteSpace(directLegalEntity.Ogrn) ? null : $"ОГРН {directLegalEntity.Ogrn}",
                    string.IsNullOrWhiteSpace(directLegalEntity.Okpo) ? null : $"ОКПО {directLegalEntity.Okpo}",
                }.Where(static part => !string.IsNullOrWhiteSpace(part))),
            };
            if (!string.IsNullOrWhiteSpace(legalAddress))
            {
                legalRequisitesLines.Add(legalAddress);
            }

            var bankRequisites = string.Join(", ", new[]
            {
                string.IsNullOrWhiteSpace(directLegalEntity.TreasuryAccountNumber) ? null : $"р/с {directLegalEntity.TreasuryAccountNumber}",
                string.IsNullOrWhiteSpace(directLegalEntity.TreasuryBankName) ? null : $"в банке {directLegalEntity.TreasuryBankName}",
                string.IsNullOrWhiteSpace(directLegalEntity.TreasuryBankBik) ? null : $"БИК {directLegalEntity.TreasuryBankBik}",
                string.IsNullOrWhiteSpace(directLegalEntity.TreasuryCorrAccountNumber) ? null : $"к/с {directLegalEntity.TreasuryCorrAccountNumber}",
            }.Where(static part => !string.IsNullOrWhiteSpace(part)));
            if (!string.IsNullOrWhiteSpace(bankRequisites))
            {
                legalRequisitesLines.Add(bankRequisites);
            }

            return new BuyerInfo
            {
                Name = legalName,
                OrganizationForm = legalOrganizationForm,
                Address = legalAddress,
                Passport = string.Empty,
                Inn = directLegalEntity.Inn ?? string.Empty,
                Kpp = directLegalEntity.Kpp ?? string.Empty,
                Ogrn = directLegalEntity.Ogrn ?? string.Empty,
                Okpo = directLegalEntity.Okpo ?? string.Empty,
                Snils = string.Empty,
                Phone = directLegalEntity.Phone ?? string.Empty,
                Email = directLegalEntity.Email ?? string.Empty,
                Contract = legalContract,
                Foundation = legalContractValue,
                Case = BuildBuyerCase(payment),
                Requisites = string.Join(", ", legalRequisitesLines.Where(static part => !string.IsNullOrWhiteSpace(part))),
                RequisitesHtml = BuildCounterpartyLinesHtml(legalRequisitesLines),
            };
        }
        var buyerProfile = ParseBuyerProfile(directClient?.Notes);
        var primaryPassport = directClient?.Passports
            .OrderByDescending(passport => passport.IsPrimary)
            .ThenByDescending(passport => passport.UpdatedAt ?? passport.CreatedAt)
            .FirstOrDefault();

        var name = FirstNotEmpty(
            directClient?.Name,
            payment.CounterpartyName,
            isExpense ? "Контрагент" : "Клиент");
        var company = directClient?.Company ?? string.Empty;
        var address = ResolveBuyerAddress(directClient);
        var passport = BuildPassport(primaryPassport);
        if (string.IsNullOrWhiteSpace(passport))
        {
            passport = BuildPassport(ResolvePrimaryPassport(buyerProfile));
        }

        var inn = buyerProfile?.Inn?.Trim() ?? string.Empty;
        var snils = buyerProfile?.Snils?.Trim() ?? string.Empty;
        var phone = directClient?.Phone ?? string.Empty;
        var email = directClient?.Email ?? string.Empty;
        var contractValue = BuildBuyerContractValue(payment.Contract);
        var contract = string.IsNullOrWhiteSpace(contractValue)
            ? string.Empty
            : $"Договор: {contractValue}";
        var foundation = contractValue;
        var clientCase = BuildBuyerCase(payment);
        var headlineParts = new List<string> { name };
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
            string.Join(", ", headlineParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
        };

        if (!string.IsNullOrWhiteSpace(company) && !string.Equals(company, name, StringComparison.OrdinalIgnoreCase))
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

        return new BuyerInfo
        {
            Name = name,
            OrganizationForm = "",
            Address = address,
            Passport = passport,
            Inn = inn,
            Kpp = string.Empty,
            Ogrn = string.Empty,
            Okpo = string.Empty,
            Snils = snils,
            Phone = phone,
            Email = email,
            Contract = contract,
            Foundation = foundation,
            Case = clientCase,
            Requisites = string.Join(", ", requisitesLines.Where(static part => !string.IsNullOrWhiteSpace(part))),
            RequisitesHtml = BuildCounterpartyLinesHtml(requisitesLines),
        };
    }

    private static string ResolveBuyerAddress(Client? client)
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

    private static string BuildBuyerContractValue(Contract? contract)
    {
        if (contract is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(contract.Number))
        {
            parts.Add($"Договор № {contract.Number}");
        }

        if (contract.Date != default)
        {
            parts.Add($"от {FormatDate(ContractDomainService.NormalizeLocalDate(contract.Date))}");
        }

        if (!string.IsNullOrWhiteSpace(contract.Title))
        {
            parts.Add(contract.Title.Trim());
        }

        return parts.Count == 0
            ? string.Empty
            : string.Join(" ", parts);
    }

    private static string BuildBuyerCase(Payment payment)
    {
        var labels = new List<string>();

        if (payment.InvoiceClientCases.Count > 0)
        {
            labels.AddRange(payment.InvoiceClientCases
                .Where(link => link.ClientCase is not null)
                .OrderByDescending(link => link.ClientCase!.CreatedAt)
                .ThenByDescending(link => link.ClientCaseId)
                .Select(link => link.ClientCase!.Title?.Trim())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase));
        }

        if (labels.Count == 0 && !string.IsNullOrWhiteSpace(payment.ClientCase?.Title))
        {
            labels.Add(payment.ClientCase.Title.Trim());
        }

        if (labels.Count == 0 && !string.IsNullOrWhiteSpace(payment.ClientCaseReference))
        {
            labels.Add(payment.ClientCaseReference.Trim());
        }

        return labels.Count == 0
            ? string.Empty
            : $"Дело: {string.Join("; ", labels)}";
    }

    private static string ResolveVatText(SellerInfo seller)
    {
        var taxSystem = seller.TaxSystem.Trim();
        if (seller.IsVatExempt)
        {
            return "Без НДС";
        }

        if (string.IsNullOrWhiteSpace(taxSystem))
        {
            return "По реквизитам документа";
        }

        if (taxSystem.Contains("УСН", StringComparison.OrdinalIgnoreCase)
            || taxSystem.Contains("ЕНВД", StringComparison.OrdinalIgnoreCase)
            || taxSystem.Contains("ПСН", StringComparison.OrdinalIgnoreCase))
        {
            return "Без НДС";
        }

        if (taxSystem.Contains("ОСНО", StringComparison.OrdinalIgnoreCase)
            || taxSystem.Contains("ОСН", StringComparison.OrdinalIgnoreCase)
            || taxSystem.Contains("ООО", StringComparison.OrdinalIgnoreCase)
            || taxSystem.Contains("ИП", StringComparison.OrdinalIgnoreCase))
        {
            return "Включено (указать в сумме позиции)";
        }

        return "По реквизитам документа";
    }

    private static string ResolveLineItemVat(SellerInfo seller)
        => seller.IsVatExempt ? "Без НДС" : "—";

    private static string BuildBuyerFoundationRowHtml(string foundation)
    {
        if (string.IsNullOrWhiteSpace(foundation))
        {
            return string.Empty;
        }

        return $"""
          <tr>
            <td class="counterparty-label pad-t-5">Основание:</td>
            <td class="counterparty-value pad-t-5">{Encode(foundation)}</td>
          </tr>
        """;
    }

    private static string BuildPassport(ClientPassport? passport)
    {
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
            parts.Add($"выдан {FormatDate(PaymentDomainService.NormalizeLocalDate(issueDate.Value))}");
        }

        if (!string.IsNullOrWhiteSpace(issuedBy))
        {
            parts.Add(issuedBy);
        }

        if (!string.IsNullOrWhiteSpace(departmentCode))
        {
            parts.Add($"код подразделения {departmentCode}");
        }

        return string.Join(", ", parts);
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

    private static string BuildPersonFullName(PersonInfo? person, string? fallback)
    {
        if (person is null)
        {
            return fallback ?? string.Empty;
        }

        var parts = new[]
        {
            person.LastName,
            person.FirstName,
            person.NoMiddleName ? null : person.MiddleName,
        };

        var fullName = string.Join(" ", parts.Where(static value => !string.IsNullOrWhiteSpace(value))).Trim();
        return string.IsNullOrWhiteSpace(fullName) ? (fallback ?? string.Empty) : fullName;
    }

    private static string BuildPersonShortName(PersonInfo? person, string? fallback)
    {
        if (person is not null)
        {
            var lastName = person.LastName?.Trim();
            var firstInitial = string.IsNullOrWhiteSpace(person.FirstName) ? null : $"{char.ToUpperInvariant(person.FirstName.Trim()[0])}.";
            var middleInitial = person.NoMiddleName || string.IsNullOrWhiteSpace(person.MiddleName)
                ? null
                : $"{char.ToUpperInvariant(person.MiddleName.Trim()[0])}.";

            var value = string.Join(" ", new[] { lastName, string.Join(" ", new[] { firstInitial, middleInitial }.Where(static x => !string.IsNullOrWhiteSpace(x))) }
                .Where(static x => !string.IsNullOrWhiteSpace(x)))
                .Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (string.IsNullOrWhiteSpace(fallback))
        {
            return string.Empty;
        }

        var parts = fallback.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        if (parts.Length == 1)
        {
            return parts[0];
        }

        var initials = parts
            .Skip(1)
            .Take(2)
            .Select(part => $"{char.ToUpperInvariant(part[0])}.")
            .ToArray();

        return $"{parts[0]} {string.Join(" ", initials)}".Trim();
    }

    private static string RenderLineItems(string template, IReadOnlyCollection<Dictionary<string, string>> lineItems)
    {
        return LoopRegex.Replace(template, match =>
        {
            var rowTemplate = match.Groups[1].Value;
            var builder = new StringBuilder();

            foreach (var item in lineItems)
            {
                builder.Append(ReplaceTokens(rowTemplate, item));
            }

            return builder.ToString();
        });
    }

    private static string ReplaceTokens(string template, IReadOnlyDictionary<string, string> values)
    {
        return TokenRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            return values.TryGetValue(key, out var value) ? value : string.Empty;
        });
    }

    private static string BuildImageHtml(string? dataUrl, string alt, string cssClass)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return string.Empty;
        }

        return $"<img src=\"{dataUrl}\" alt=\"{Encode(alt)}\" class=\"{cssClass}\" />";
    }

    private static string BuildInvoiceQrCodeHtml(
        Payment payment,
        SellerInfo seller,
        decimal total,
        DateTime invoiceDate,
        string invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(seller.AccountNumber)
            || string.IsNullOrWhiteSpace(seller.BankName)
            || string.IsNullOrWhiteSpace(seller.Bik)
            || string.IsNullOrWhiteSpace(seller.Inn))
        {
            return string.Empty;
        }

        var purpose = $"Оплата счета №{invoiceNumber} от {FormatDate(invoiceDate)} на сумму {FormatMoney(total)} руб. (без НДС)";
        var qrPayload =
            "ST00012"
            + $"|Name={seller.FullName}"
            + $"|PersonalAcc={seller.AccountNumber}"
            + $"|BankName={seller.BankName}"
            + $"|BIC={seller.Bik}"
            + $"|CorrespAcc={seller.CorrAccountNumber}"
            + $"|PayeeINN={seller.Inn}"
            + $"|KPP={seller.Kpp}"
            + $"|Purpose={purpose}"
            + "|TechCode=15"
            + $"|Sum={(long)Math.Round(total * 100m, MidpointRounding.AwayFromZero)}";

        var qrUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=150x150&data={Uri.EscapeDataString(qrPayload)}";
        return $"<img src=\"{WebUtility.HtmlEncode(qrUrl)}\" alt=\"QR-код для оплаты\" class=\"invoice-qr\" />";
    }

    private static string FormatMoney(decimal value)
        => value.ToString("N2", RuCulture);

    private static string FormatDate(DateTime value)
        => PaymentDomainService.NormalizeLocalDate(value).ToString("dd.MM.yyyy", RuCulture);

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

    private static string CurrentDefaultTemplateFingerprint
        => ComputeTemplateFingerprint(DefaultTemplateHtml);

    private static InvoiceDocumentTemplateSettingsStorage NormalizeStorage(InvoiceDocumentTemplateSettingsStorage storage)
    {
        var normalized = new InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = storage.ActiveTemplateKey,
            PrintWithSignatureAndSealByDefault = storage.PrintWithSignatureAndSealByDefault,
            StandardTemplateFingerprint = string.IsNullOrWhiteSpace(storage.StandardTemplateFingerprint)
                ? null
                : storage.StandardTemplateFingerprint.Trim(),
            Templates = storage.Templates
                .Select(item => new InvoiceDocumentTemplateStorageItem
                {
                    Key = item.Key,
                    Name = item.Name,
                    Description = item.Description,
                    Html = item.Html,
                })
                .ToList(),
        };

        var standardTemplate = normalized.Templates.FirstOrDefault(item =>
            string.Equals(item.Key, DefaultTemplateKey, StringComparison.OrdinalIgnoreCase));

        if (standardTemplate is null)
        {
            normalized.StandardTemplateFingerprint = null;
        }
        else
        {
            var managedFingerprint = ResolveManagedStandardTemplateFingerprint(
                standardTemplate.Html,
                normalized.StandardTemplateFingerprint);

            if (string.IsNullOrWhiteSpace(managedFingerprint))
            {
                normalized.StandardTemplateFingerprint = null;
            }
            else
            {
                if (!string.Equals(managedFingerprint, CurrentDefaultTemplateFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    standardTemplate.Html = DefaultTemplateHtml;
                }

                normalized.StandardTemplateFingerprint = CurrentDefaultTemplateFingerprint;
            }
        }

        if (normalized.Templates.Count > 0)
        {
            EnsureOfferTemplate(normalized.Templates);
            EnsureOrderTemplate(normalized.Templates);
        }

        return normalized;
    }

    private static void EnsureOfferTemplate(List<InvoiceDocumentTemplateStorageItem> templates)
    {
        var existing = templates.FirstOrDefault(item =>
            string.Equals(item.Key, OfferTemplateKey, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            templates.Add(CreateOfferTemplateStorageItem());
            return;
        }

        if (string.IsNullOrWhiteSpace(existing.Name))
        {
            existing.Name = "Счет-оферта";
        }

        if (string.IsNullOrWhiteSpace(existing.Description))
        {
            existing.Description = "Счет с теми же реквизитами и позициями, дополненный текстом публичной оферты для удаленных клиентов.";
        }

        if (string.IsNullOrWhiteSpace(existing.Html))
        {
            existing.Html = OfferTemplateHtml;
        }
    }

    private static InvoiceDocumentTemplateStorageItem CreateOfferTemplateStorageItem()
        => new()
        {
            Key = OfferTemplateKey,
            Name = "Счет-оферта",
            Description = "Счет с теми же реквизитами и позициями, дополненный текстом публичной оферты для удаленных клиентов.",
            Html = OfferTemplateHtml,
        };

    private static void EnsureOrderTemplate(List<InvoiceDocumentTemplateStorageItem> templates)
    {
        var existing = templates.FirstOrDefault(item => string.Equals(item.Key, OrderTemplateKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            templates.Add(CreateOrderTemplateStorageItem());
            return;
        }

        existing.Name = string.IsNullOrWhiteSpace(existing.Name) ? "Заказ" : existing.Name;
        existing.Description = string.IsNullOrWhiteSpace(existing.Description)
            ? "Заказ с банковскими реквизитами, перечнем товаров и услуг и подписью руководителя."
            : existing.Description;
        existing.Html = string.IsNullOrWhiteSpace(existing.Html) ? OrderTemplateHtml : existing.Html;
    }

    private static InvoiceDocumentTemplateStorageItem CreateOrderTemplateStorageItem()
        => new()
        {
            Key = OrderTemplateKey,
            Name = "Заказ",
            Description = "Заказ с банковскими реквизитами, перечнем товаров и услуг и подписью руководителя.",
            Html = OrderTemplateHtml,
        };

    private static string? ResolveManagedStandardTemplateFingerprint(string? templateHtml, string? currentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(templateHtml))
        {
            return null;
        }

        var actualFingerprint = ComputeTemplateFingerprint(templateHtml);
        if (string.Equals(actualFingerprint, CurrentDefaultTemplateFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return CurrentDefaultTemplateFingerprint;
        }

        if (!string.IsNullOrWhiteSpace(currentFingerprint)
            && string.Equals(actualFingerprint, currentFingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return actualFingerprint;
        }

        return LegacyManagedStandardTemplateFingerprints.Contains(actualFingerprint)
            || LooksLikeLegacyManagedStandardTemplateLayout(templateHtml)
            ? actualFingerprint
            : null;
    }

    private static string ComputeTemplateFingerprint(string templateHtml)
    {
        var normalizedHtml = templateHtml
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedHtml));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool LooksLikeLegacyManagedStandardTemplateLayout(string templateHtml)
    {
        return templateHtml.Contains(".brand-column", StringComparison.Ordinal)
            && templateHtml.Contains("class=\"brand-column\" rowspan=\"2\"", StringComparison.Ordinal)
            && templateHtml.Contains("class=\"brand-stack\"", StringComparison.Ordinal)
            && templateHtml.Contains("class=\"qr-wrap\"", StringComparison.Ordinal)
            && templateHtml.Contains("{{seller.logoHtml}}", StringComparison.Ordinal)
            && templateHtml.Contains("{{invoice.qrCodeHtml}}", StringComparison.Ordinal);
    }

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string BuildCounterpartyLinesHtml(IEnumerable<string> lines)
        => string.Join(
            string.Empty,
            lines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => $"<span class=\"counterparty-line\">{Encode(line)}</span>"));

    private static string FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private sealed class InvoiceDocumentRenderModel
    {
        public required Dictionary<string, string> Tokens { get; init; }
        public required List<Dictionary<string, string>> LineItems { get; init; }
    }

    public sealed class InvoiceDocumentTemplateSettingsStorage
    {
        public string ActiveTemplateKey { get; set; } = string.Empty;
        public bool PrintWithSignatureAndSealByDefault { get; set; }
        public string? StandardTemplateFingerprint { get; set; }
        public List<InvoiceDocumentTemplateStorageItem> Templates { get; set; } = new();
    }

    public sealed class InvoiceDocumentTemplateStorageItem
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string Html { get; set; } = string.Empty;
    }

    private sealed class SellerInfo
    {
        public string ShortName { get; init; } = string.Empty;
        public string FullName { get; init; } = string.Empty;
        public string OrganizationForm { get; init; } = string.Empty;
        public string Inn { get; init; } = string.Empty;
        public string Kpp { get; init; } = string.Empty;
        public string Ogrn { get; init; } = string.Empty;
        public string Okpo { get; init; } = string.Empty;
        public bool IsVatExempt { get; init; }
        public string Address { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string TaxSystem { get; init; } = string.Empty;
        public string Okved { get; init; } = string.Empty;
        public string BankName { get; init; } = string.Empty;
        public string Bik { get; init; } = string.Empty;
        public string AccountNumber { get; init; } = string.Empty;
        public string CorrAccountNumber { get; init; } = string.Empty;
        public string Requisites { get; init; } = string.Empty;
    }

    private sealed class BuyerInfo
    {
        public string Name { get; init; } = string.Empty;
        public string OrganizationForm { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string Passport { get; init; } = string.Empty;
        public string Inn { get; init; } = string.Empty;
        public string Kpp { get; init; } = string.Empty;
        public string Ogrn { get; init; } = string.Empty;
        public string Okpo { get; init; } = string.Empty;
        public string Snils { get; init; } = string.Empty;
        public string Phone { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string Contract { get; init; } = string.Empty;
        public string Foundation { get; init; } = string.Empty;
        public string Case { get; init; } = string.Empty;
        public string Requisites { get; init; } = string.Empty;
        public string RequisitesHtml { get; init; } = string.Empty;
    }

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

    public static readonly string DefaultTemplateHtml = """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <title>{{document.title}}</title>
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
    .doc-title {
      font-size: 14px !important;
      font-weight: 700;
      line-height: 1.15;
    }
    .logo-wrap {
      text-align: center;
    }
    .logo-wrap .logo {
      display: block;
      max-height: 90px;
      max-width: 180px;
      margin-left: auto;
      margin-right: auto;
      object-fit: contain;
    }
    .invoice-qr {
      display: block;
      width: 150px;
      height: 150px;
      max-width: 150px;
      max-height: 150px;
      margin-left: auto;
      object-fit: contain;
    }
    .bank-grid td {
      padding: 3px 5px;
      border: 1px solid #000000;
      vertical-align: top;
      line-height: 1.15;
      font-size: 12px !important;
    }
    .bank-grid .bor0-b { border-bottom: 0; }
    .bank-grid .bor0-t { border-top: 0; }
    .doc-main-title {
      margin: 6px 0 8px;
      text-align: center;
      font-size: 16px !important;
      font-weight: 700;
    }
    .counterparty-table {
      table-layout: fixed;
    }
    .counterparty-table td {
      padding: 3px 4px;
      vertical-align: top;
      font-size: 12px !important;
      line-height: 1.25;
    }
    .counterparty-label {
      width: 16%;
      color: #000000;
      white-space: nowrap;
    }
    .counterparty-value {
      font-weight: 600;
      white-space: normal;
      overflow-wrap: anywhere;
      word-break: normal;
      padding-left: 6px !important;
    }
    .counterparty-line {
      display: block;
      margin: 0;
      padding: 0;
      line-height: 1.25;
      white-space: normal;
      overflow-wrap: anywhere;
      word-break: normal;
    }
    .items-table {
      margin: 8px 0;
      border: 1px solid #000000;
      table-layout: fixed;
    }
    .items-table th,
    .items-table td {
      border: 1px solid #000000;
      padding: 4px 6px;
      vertical-align: top;
      line-height: 1.25;
    }
    .items-table th {
      text-align: center;
      font-weight: 700;
      font-size: 12px !important;
    }
    .items-table .num {
      width: 5%;
      text-align: center;
    }
    .items-table .name {
      width: 48%;
      overflow-wrap: anywhere;
      word-break: break-word;
    }
    .items-table .qty {
      width: 7%;
      text-align: center;
      white-space: nowrap;
    }
    .items-table .unit {
      width: 6%;
      text-align: center;
      white-space: nowrap;
    }
    .items-table .price,
    .items-table .vat,
    .items-table .sum {
      width: 11%;
      text-align: right;
      white-space: nowrap;
    }
    .totals-table td {
      padding: 0;
      font-size: 12px !important;
    }
    .totals-label {
      width: 80%;
      text-align: right;
      padding-right: 10px !important;
    }
    .totals-value {
      width: 20%;
      text-align: right;
      white-space: nowrap;
      font-weight: 700;
    }
    .summary-line {
      margin-top: 6px;
    }
    .words {
      font-weight: 700;
    }
    .invoice-meta {
      margin-top: 6px;
      line-height: 1.35;
    }
    .invoice-meta-line {
      margin-top: 2px;
    }
    .invoice-flag {
      font-weight: 700;
      text-transform: uppercase;
    }
    .installment-schedule {
      margin-top: 6px;
    }
    .installment-title {
      font-weight: 700;
    }
    .signature-table {
      margin-top: 12px;
    }
    .signature-line {
      border-bottom: 1px solid #000000;
      text-align: center;
      height: 48px;
      vertical-align: bottom;
      position: relative;
      overflow: visible;
    }
    .signature-line .signature {
      max-width: 150px;
      max-height: 38px;
      object-fit: contain;
      position: absolute;
      left: 50%;
      bottom: 6px;
      transform: translateX(-50%);
      z-index: 2;
    }
    .seal-slot {
      position: relative;
      height: 48px;
      vertical-align: bottom;
    }
    .seal-slot .seal {
      max-width: 110px;
      max-height: 110px;
      object-fit: contain;
      position: absolute;
      left: 50%;
      top: -18px;
      transform: translateX(-50%);
      opacity: 0.9;
      z-index: 1;
    }
    .signature-caption {
      padding-top: 3px;
      color: #222222;
      font-size: 11px !important;
    }
  </style>
</head>
<body>
  <div class="page-content">
    <div class="document-template invoice-template">
      <table style="margin-bottom: 10px;">
        <tbody>
          <tr>
            <td class="doc-title" style="width: 60%; vertical-align: middle; padding: 2px 0;">
              {{seller.fullName}}
            </td>
            <td class="logo-wrap" style="width: 40%; vertical-align: middle;">
              {{seller.logoHtml}}
            </td>
          </tr>
        </tbody>
      </table>

      <table style="margin: 0 0 10px;">
        <tbody>
          <tr>
            <td>
              <table class="bank-grid">
                <tbody>
                  <tr>
                    <td colspan="2" rowspan="2" class="v-al-t bor0-b" style="width: 50%;">
                      {{seller.bankName}}
                    </td>
                    <td style="width: 15%;">БИК</td>
                    <td class="bor0-b" style="width: 35%;">{{seller.bik}}</td>
                  </tr>
                  <tr>
                    <td class="v-al-t bor0-b" style="width: 15%;">Сч. №</td>
                    <td rowspan="2" class="v-al-t bor0-t" style="width: 35%;">{{seller.corrAccountNumber}}</td>
                  </tr>
                  <tr>
                    <td colspan="2" class="bor0-t" style="width: 50%;">Банк получателя</td>
                    <td class="v-al-t bor0-t" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td style="width: 25%;">ИНН {{seller.inn}}</td>
                    <td style="width: 25%;">КПП {{seller.kppOrDash}}</td>
                    <td class="v-al-t bor0-b" style="width: 15%;">Сч. №</td>
                    <td rowspan="4" class="v-al-t" style="width: 35%;">{{seller.accountNumber}}</td>
                  </tr>
                  <tr>
                    <td rowspan="2" colspan="2" class="v-al-t bor0-b" style="width: 50%;">{{seller.fullName}}</td>
                    <td class="v-al-t bor0-t bor0-b" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td class="v-al-t bor0-t bor0-b" style="width: 15%;">&nbsp;</td>
                  </tr>
                  <tr>
                    <td colspan="2" class="bor0-t" style="width: 50%;">Получатель</td>
                    <td class="v-al-t bor0-t" style="width: 15%;">&nbsp;</td>
                  </tr>
                </tbody>
              </table>
            </td>
            <td style="width: 150px; vertical-align: top;">
              {{invoice.qrCodeHtml}}
            </td>
          </tr>
        </tbody>
      </table>

      <div class="doc-main-title text-c">
        Счет № {{invoice.number}} от {{invoice.dateLong}}
      </div>

      <div class="color-line"></div>

      <table class="counterparty-table" style="margin: 6px 0;">
        <tbody>
          <tr>
            <td class="counterparty-label">Поставщик<br/>(Исполнитель):</td>
            <td class="counterparty-value">{{seller.requisites}}</td>
          </tr>
          <tr>
            <td class="counterparty-label pad-t-5">Покупатель<br/>(Заказчик):</td>
            <td class="counterparty-value pad-t-5">{{buyer.requisitesHtml}}</td>
          </tr>
          {{buyer.foundationRowHtml}}
        </tbody>
      </table>

      <table class="items-table">
        <thead>
          <tr>
            <th class="num">№</th>
            <th class="name">Товары (работы, услуги)</th>
            <th class="qty">Кол-во</th>
            <th class="unit">Ед.</th>
            <th class="price">Цена</th>
            <th class="vat">НДС</th>
            <th class="sum">Сумма</th>
          </tr>
        </thead>
        <tbody>
          {{#lineItems}}
          <tr>
            <td class="num">{{index}}</td>
            <td class="name">{{name}}</td>
            <td class="qty">{{quantity}}</td>
            <td class="unit">{{unit}}</td>
            <td class="price">{{price}}</td>
            <td class="vat">{{vat}}</td>
            <td class="sum">{{total}}</td>
          </tr>
          {{/lineItems}}
        </tbody>
      </table>

      <table class="totals-table">
        <tbody>
          <tr>
            <td class="totals-label">Итого:</td>
            <td class="totals-value">{{invoice.subTotal}}</td>
          </tr>
          <tr>
            <td class="totals-label">НДС:</td>
            <td class="totals-value">{{invoice.vatText}}</td>
          </tr>
          <tr>
            <td class="totals-label">Всего к оплате:</td>
            <td class="totals-value">{{invoice.total}}</td>
          </tr>
        </tbody>
      </table>

      <div class="summary-line">
        Всего наименований {{invoice.itemsCount}}, на сумму {{invoice.total}} руб.
      </div>
      <div class="words">{{invoice.totalWords}}</div>
      <div class="invoice-meta">
        {{invoice.clientCommentHtml}}
        {{invoice.dueDateHtml}}
        {{invoice.notForAccountingHtml}}
      </div>
      {{invoice.installmentScheduleHtml}}

      <div class="color-line" style="margin: 6px 0;"></div>

      <table class="signature-table">
        <tbody>
          <tr>
            <td class="v-al-b" style="width: 18%;">Руководитель:</td>
            <td class="signature-line text-c v-al-b" style="width: 24%;">{{seller.documentDirectorPosition}}</td>
            <td style="width: 4%;"></td>
            <td class="signature-line text-c" style="width: 24%;">{{seller.signatureHtml}}</td>
            <td class="seal-slot text-c" style="width: 8%;">{{seller.sealHtml}}</td>
            <td class="signature-line text-c v-al-b" style="width: 22%;">{{seller.documentDirectorShortName}}</td>
          </tr>
          <tr>
            <td style="width: 18%;"></td>
            <td class="text-c signature-caption" style="width: 24%;">должность</td>
            <td style="width: 4%;"></td>
            <td class="text-c signature-caption" style="width: 24%;">подпись</td>
            <td style="width: 8%;"></td>
            <td class="text-c signature-caption" style="width: 22%;">расшифровка подписи</td>
          </tr>
          <tr>
            <td class="v-al-b" colspan="3" style="width: 46%;">{{seller.chiefAccountantPosition}}:</td>
            <td class="signature-line text-c" style="width: 24%;"></td>
            <td style="width: 8%;"></td>
            <td class="signature-line text-c v-al-b" style="width: 22%;">{{seller.chiefAccountantShortName}}</td>
          </tr>
          <tr>
            <td colspan="3" style="width: 46%;"></td>
            <td class="text-c signature-caption" style="width: 24%;">подпись</td>
            <td style="width: 8%;"></td>
            <td class="text-c signature-caption" style="width: 22%;">расшифровка подписи</td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</body>
</html>
""";

    private const string OrderTemplateHtmlSource = """
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8" />
  <style>
    __PP_ORDER_FONT_CSS__
    @page { size: A4; margin: 11.3mm 8.5mm 10mm 16mm; }
    * { box-sizing: border-box; }
    html, body { margin: 0; padding: 0; }
    body { color: #000; font-family: 'DejaVu Sans Condensed', sans-serif; font-size: 9pt; line-height: 1.18; }
    p { margin-top: 0; margin-bottom: 0; mso-margin-top-alt: 0; mso-margin-bottom-alt: 0; }
    .order { width: 100%; }
    .counterparty-title { margin: 0 0 2.75mm; font-size: 12pt; font-weight: 700; }
    table { width: 100%; border-collapse: collapse; }
    .bank { table-layout: fixed; margin-bottom: 0; font-size: 8.8pt; line-height: 1.12; }
    .bank tr { height: 7mm; }
    .bank td { border: .7pt solid #000; padding: .5mm 1mm; vertical-align: top; }
    .bank .bank-label { padding-left: .7mm; padding-right: .4mm; }
    .caption { margin-top: 1mm; font-size: 6.5pt; }
    h1 { margin: 0 0 1.5mm; padding: 6.5mm 0 2.2mm; border-bottom: 1.5pt solid #000; font-size: 12pt; line-height: 1.1; text-align: center; }
    .party { margin: 0 0 2.7mm; font-size: 9pt; }
    .party-label { font-weight: 700; }
    .items { table-layout: fixed; margin-top: 5.6mm; font-size: 8.8pt; line-height: 1.14; }
    .items thead { display: table-header-group; }
    .items tr { break-inside: avoid; page-break-inside: avoid; }
    .items th, .items td { border: .7pt solid #000; padding: 1.8mm 1mm; mso-padding-alt: 5.1pt 2.8pt 5.1pt 2.8pt; vertical-align: top; }
    .items th p, .items td p { margin: 0 !important; mso-margin-top-alt: 0; mso-margin-bottom-alt: 0; }
    .items th { font-weight: 700; text-align: center; vertical-align: middle; }
    .items .index { width: 5%; text-align: center; }
    .items .name { width: 52%; text-align: left; }
    .items td.name { text-align: justify; text-justify: inter-character; }
    .items .quantity { width: 8%; text-align: right; }
    .items .unit { width: 9%; text-align: center; }
    .items .price, .items .sum { width: 13%; text-align: right; }
    .totals { width: 48%; margin: 2.2mm 0 0 auto; font-size: 9pt; }
    .totals td { padding: .45mm 0 .45mm 2mm; }
    .totals .label { text-align: right; font-weight: 700; }
    .totals .value { width: 30%; text-align: right; font-weight: 700; }
    .summary { margin-top: 3.5mm; font-size: 9pt; }
    .summary p { margin: 0 0 1.8mm; }
    .summary .words { font-weight: 700; }
    .signature-block { margin-top: 5mm; padding-top: 2mm; border-top: 1.5pt solid #000; break-inside: avoid; page-break-inside: avoid; }
    .signature-block.signature-new-page { margin-top: 0; break-before: page; page-break-before: always; }
    .signature { table-layout: fixed; }
    .signature td { padding: 0 1.3mm; vertical-align: bottom; }
    .signature .role { width: 21%; padding-left: 0; font-weight: 700; }
    .signature .position { width: 26%; }
    .signature .sign { width: 24%; }
    .signature .name { width: 29%; padding-right: 0; }
    .signature .line { min-height: 4.5mm; border-bottom: .7pt solid #000; text-align: center; }
    .signature .caption { margin-top: .6mm; text-align: center; font-size: 6.5pt; }
  </style>
</head>
<body>
  <div class="order">
    <div class="counterparty-title">{{order.counterpartyName}}</div>
    <table class="bank" width="100%">
      <colgroup><col width="24%" style="width:24%" /><col width="27%" style="width:27%" /><col width="7%" style="width:7%" /><col width="42%" style="width:42%" /></colgroup>
      <tr><td colspan="2" rowspan="2">{{seller.bankName}}<div class="caption">Банк получателя</div></td><td class="bank-label">БИК</td><td>{{seller.bik}}</td></tr>
      <tr><td class="bank-label">Сч. №</td><td>{{seller.corrAccountNumber}}</td></tr>
      <tr><td><strong>ИНН {{seller.inn}}</strong></td><td>КПП {{seller.kpp}}</td><td class="bank-label" rowspan="2">Сч. №</td><td rowspan="2">{{seller.accountNumber}}</td></tr>
      <tr><td colspan="2">{{seller.shortName}}<div class="caption">Получатель</div></td></tr>
    </table>
    <h1>Заказ № {{invoice.number}} от {{invoice.dateLong}}</h1>
    <p class="party"><span class="party-label">Поставщик:</span> {{seller.orderRequisites}}</p>
    <p class="party"><span class="party-label">Покупатель:</span> {{buyer.orderRequisites}}</p>
    <table class="items" width="100%">
      <colgroup><col width="5%" style="width:5%" /><col width="52%" style="width:52%" /><col width="8%" style="width:8%" /><col width="9%" style="width:9%" /><col width="13%" style="width:13%" /><col width="13%" style="width:13%" /></colgroup>
      <thead><tr><th class="index" width="5%" style="width:5%">№</th><th class="name" width="52%" style="width:52%">Товары (работы, услуги)</th><th class="quantity" width="8%" style="width:8%">Кол-во</th><th class="unit" width="9%" style="width:9%">Ед.</th><th class="price" width="13%" style="width:13%">Цена</th><th class="sum" width="13%" style="width:13%">Сумма</th></tr></thead>
      <tbody>{{#lineItems}}<tr><td class="index" width="5%" style="width:5%">{{index}}</td><td class="name" width="52%" style="width:52%">{{name}}</td><td class="quantity" width="8%" style="width:8%">{{quantity}}</td><td class="unit" width="9%" style="width:9%">{{unit}}</td><td class="price" width="13%" style="width:13%">{{price}}</td><td class="sum" width="13%" style="width:13%">{{total}}</td></tr>{{/lineItems}}</tbody>
    </table>
    <table class="totals">
      <tr><td class="label">{{invoice.orderVatLabel}}</td><td class="value">{{invoice.orderVatValue}}</td></tr>
      <tr><td class="label">Итого:</td><td class="value">{{invoice.total}}</td></tr>
      <tr><td class="label">Всего к оплате:</td><td class="value">{{invoice.total}}</td></tr>
    </table>
    <div class="summary"><p>Всего наименований {{invoice.itemsCount}}, на сумму {{invoice.total}} руб.</p><p class="words">Всего к оплате: {{invoice.totalWords}}</p></div>
    <div class="signature-block{{order.signaturePageBreakClass}}"><table class="signature"><tr><td class="role">Руководитель:</td><td class="position"><div class="line">{{seller.orderDirectorPosition}}</div><div class="caption">должность</div></td><td class="sign"><div class="line"></div><div class="caption">подпись</div></td><td class="name"><div class="line">{{seller.orderSignatoryShortName}}</div><div class="caption">расшифровка подписи</div></td></tr></table></div>
  </div>
</body>
</html>
""";

    public static readonly string OrderTemplateHtml = OrderTemplateHtmlSource.Replace("__PP_ORDER_FONT_CSS__", BuildEmbeddedOrderFontCss(), StringComparison.Ordinal);

    private static string BuildEmbeddedOrderFontCss()
    {
        var assembly = typeof(InvoiceDocumentTemplateEngine).Assembly;
        static string ReadFontBase64(System.Reflection.Assembly assembly, string resourceName)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException($"Встроенный шрифт {resourceName} не найден.");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return Convert.ToBase64String(memory.ToArray());
        }

        var regular = ReadFontBase64(assembly, "PayPlanner.Fonts.DejaVuSansCondensed.ttf");
        var bold = ReadFontBase64(assembly, "PayPlanner.Fonts.DejaVuSansCondensed-Bold.ttf");
        return $"@font-face {{ font-family: 'DejaVu Sans Condensed'; src: url(data:font/ttf;base64,{regular}) format('truetype'); font-weight: 400; }} "
            + $"@font-face {{ font-family: 'DejaVu Sans Condensed'; src: url(data:font/ttf;base64,{bold}) format('truetype'); font-weight: 700; }}";
    }

    public static readonly string OfferTemplateHtml = DefaultTemplateHtml
        .Replace(
            "<div class=\"document-template invoice-template\">",
            "<div class=\"document-template invoice-template offer-template\">",
            StringComparison.Ordinal)
        .Replace(
            "Счет № {{invoice.number}} от {{invoice.dateLong}}",
            "Счет-договор № {{invoice.number}} от {{invoice.dateLong}}",
            StringComparison.Ordinal)
        .Replace(
            """
    .signature-table {
      margin-top: 12px;
    }
""",
            """
    .offer-template .doc-main-title {
      margin: 8px 0 9px;
      font-size: 18px !important;
    }
    .offer-contract-subject {
      margin-top: 8px;
      font-size: 14px !important;
      line-height: 1.18;
    }
    .offer-subject-label {
      font-weight: 700;
    }
    .offer-template .invoice-meta {
      display: none;
    }
    .offer-section {
      margin-top: 0;
      font-size: 14px !important;
      line-height: 1.18;
    }
    .offer-title {
      margin: 0;
    }
    .offer-heading {
      margin-top: 4px;
      font-weight: 700;
    }
    .offer-clause {
      margin-top: 1px;
    }
    .offer-page-break-before {
      page-break-before: always;
    }
    .offer-requisites-heading {
      page-break-before: always;
      margin-top: 0;
      font-weight: 700;
    }
    .offer-requisites-table {
      margin-top: 14px;
      border: 0;
    }
    .offer-requisites-table th,
    .offer-requisites-table td {
      border: 0;
      padding: 0 4px 4px 4px;
      vertical-align: top;
      line-height: 1.18;
    }
    .offer-requisites-table th {
      text-align: left;
      font-weight: 700;
      padding-bottom: 8px;
    }
    .offer-signatures {
      margin-top: 34px;
    }
    .offer-signatures td {
      padding: 0;
      vertical-align: bottom;
    }
    .offer-signature-role {
      padding-bottom: 62px !important;
      font-weight: 400;
    }
    .offer-signature-name {
      border-bottom: 1px solid #000000;
      text-align: center;
      height: 48px;
      vertical-align: bottom !important;
    }
    .offer-signature-caption {
      text-align: center;
      font-size: 11px !important;
      line-height: 1.1;
    }
    .offer-signature-spacer {
      width: 10%;
    }
    .offer-signature-table {
      display: none;
    }
    .signature-table {
      margin-top: 12px;
    }
""",
            StringComparison.Ordinal)
        .Replace(
            """
      <div class="color-line" style="margin: 6px 0;"></div>

      <table class="signature-table">
""",
            """
      <div class="offer-contract-subject">
        <div class="offer-subject-label">Предмет договора:</div>
        <div>Предмет договора:</div>
        <div>Предмет договора:</div>
      </div>

      <div class="offer-section">
        <div class="offer-title">Договор публичной оферты об оказании юридических услуг</div>
        <div class="offer-heading">1. Общие положения</div>
        <div class="offer-clause">1.1. Данный документ является официальным предложением (публичной Офертой) {{seller.fullName}} ОГРН/ОГРНИП № {{seller.ogrn}}, именуемого в дальнейшем «Исполнитель», и содержит все существенные условия по оказанию юридических услуг (далее по тексту - «Услуги»).</div>
        <div class="offer-clause">1.2. В соответствии с пунктом 2 статьи 437 Гражданского Кодекса Российской Федерации (ГК РФ) в случае принятия изложенных ниже условий и оплаты услуг, юридическое или физическое лицо, производящее акцепт этой Оферты, становится Заказчиком (в соответствии с пунктом 3 статьи 438 ГК РФ акцепт Оферты равносилен заключению договора на условиях, изложенных в Оферте), а Исполнитель и Заказчик совместно именуются Сторонами договора Оферты.</div>
        <div class="offer-clause">1.3. Акцепт оферты осуществляется путем оплаты Заказчиком Счета-договора и означает полное и безоговорочное согласие Заказчика с условиями оказания услуг, определенных Счетом-договором.</div>
        <div class="offer-clause">1.4. Настоящий Счет-договор действителен в течение 10 (десяти) календарных дней от даты его составления включительно. При отсутствии оплаты в указанный срок настоящий Счет-договор признается недействительным.</div>
        <div class="offer-clause">1.5. В связи с вышеизложенным, внимательно прочитайте текст данной публичной Оферты. Если Вы не согласны с каким-либо пунктом настоящей Оферты, Исполнитель предлагает Вам отказаться от ее акцепта и заключить с Исполнителем отдельный договор.</div>

        <div class="offer-heading offer-page-break-before">2. Термины и определения</div>
        <div class="offer-clause">2.1. Оферта - настоящий документ «Договор публичной оферты по оказанию юридических услуг».</div>
        <div class="offer-clause">2.2. Акцепт Оферты - полное и безоговорочное принятие Оферты Заказчиком путем обращения к Исполнителю за оказанием предоставляемых им услуг. Акцепт Оферты создает Договор Оферты.</div>
        <div class="offer-clause">2.3. Тарифы - действующий систематизированный перечень услуг Исполнителя с ценами.</div>
        <div class="offer-clause">2.4. Заказчик - лицо, осуществившее акцепт оферты, и являющееся таким образом Заказчиком услуг Исполнителя по заключенному договору оферты.</div>
        <div class="offer-clause">2.5. Исполнитель - {{seller.fullName}}.</div>
        <div class="offer-clause">2.6. Договор оферты - договор между Исполнителем и Заказчиком на оказание юридических услуг, который заключается посредством акцепта оферты.</div>
        <div class="offer-clause">2.7. Услуга - проведение работ юридического характера в интересах Заказчика с последующей передачей результатов указанных работ, предоставляемая Исполнителем Заказчику.</div>
        <div class="offer-clause">2.8. Веб-сайт - информационный ресурс Исполнителя в сети Интернет, находящийся по адресу: https://elitpravo.com/</div>
        <div class="offer-clause">2.9. Техническое задание - поручение Заказчика, выраженное в виде документа в письменной и (или) электронной форме, содержащий акцепт публичной оферты, содержащий достоверные сведения, на основании предъявленных документов и информации, необходимых для оказания юридических Услуг Исполнителем.</div>
        <div class="offer-clause">2.10. Условия - настоящие условия, описанные в данном документе.</div>

        <div class="offer-heading">3. Предмет оферты</div>
        <div class="offer-clause">3.1. Исполнитель обязуется по Техническому заданию Заказчика в соответствии с условиями настоящей Оферты и текущими Тарифами оказать Заказчику Услуги, а Заказчик обязуется принять их и оплатить.</div>
        <div class="offer-clause">3.2. Исполнитель имеет право в любой момент изменять Прейскурант и условия настоящей публичной Оферты в одностороннем порядке без предварительного согласования с Заказчиком, обеспечивая при этом публикацию измененных условий.</div>
        <div class="offer-clause">3.3. Договор действует до момента полного оказания услуги в соответствии с Техническим заданием Заказчика.</div>
        <div class="offer-clause">3.4. В случае невозможности выполнения Исполнителем Технического задания ввиду недостаточности в нем сведений, необходимых для оказания Услуг в полном объеме, Исполнитель обращается к Заказчику с требованием предоставить дополнительные сведения, материалы и (или) документы. При этом Исполнитель вправе не приступать к исполнению услуг до момента предоставления Заказчиком дополнительных сведений (материалов, документов), а срок оказания услуг продлевается на время предоставления Заказчиком дополнительной необходимой Исполнителю информации (документов, сведений).</div>

        <div class="offer-heading">4. Права и обязанности Сторон</div>
        <div class="offer-clause">4.1. Исполнитель обязуется:</div>
        <div class="offer-clause">4.1.1. С момента выполнения Услуги уведомить Заказчика о выполненной услуге путем смс, e-mail, WhatsApp, Viber, VK, Одноклассники либо иным доступным способом.</div>
        <div class="offer-clause">4.1.2. Вместе с тем уведомление о выполнении услуги либо одной из услуг могут поступать с автоматизированной системы управления учета задачами и проектами.</div>
        <div class="offer-clause">4.2. Исполнитель имеет право:</div>
        <div class="offer-clause">4.2.1. Привлекать к исполнению настоящей Оферты сторонних консультантов, специалистов и экспертов.</div>
        <div class="offer-clause">4.2.2. Исполнитель вправе не выполнять услуги до зачисления оплаты на расчетный счет.</div>
        <div class="offer-clause">4.3. Заказчик обязуется:</div>
        <div class="offer-clause">4.3.1. Предоставлять Исполнителю все документы и достоверную информацию, необходимые для правильного и своевременного выполнения Исполнителем юридических услуг для заказчика, а также оказывать Исполнителю иное необходимое содействие.</div>
        <div class="offer-clause">4.3.2. Предоставить Исполнителю информацию и документы, содержащие все необходимые сведения в офисе Исполнителя, либо посредством электронной почты направить копии документов по адресу info@elitpravo.com, или иным доступным Заказчику способом по согласованию с Исполнителем.</div>
        <div class="offer-clause">4.3.3. Оформить доверенности для представления интересов Заказчика либо лиц, указанных им (при необходимости).</div>
        <div class="offer-clause">4.3.4. С момента получения уведомления о выполнении услуги либо комплекса услуг Заказчик обязан принять эти услуги либо дать мотивированный отзыв по выполненным услугам в течение 2 дней.</div>
        <div class="offer-clause">4.3.5. Если Заказчик не принимает либо не реагирует на выполненную услугу или на уведомление об оказанной услуге, то услуга считается принятой Заказчиком по истечении трех дней с даты направления уведомления в соответствии с пунктом 4.1.1. и 4.1.2.</div>
        <div class="offer-clause">4.3.6. Произвести своевременную и полную оплату услуг Исполнителя в течение 10 календарных дней с даты формирования Счета-договора.</div>

        <div class="offer-heading">5. Оплата услуг</div>
        <div class="offer-clause">5.1. Стоимость Услуг не включает НДС (НДС не облагается) исполнитель применяет УСН.</div>
        <div class="offer-clause">5.2. Стоимость Услуги определяется исходя из задания Заказчика, согласно стоимости по каждой услуге, определенной Исполнителем.</div>
        <div class="offer-clause offer-page-break-before">5.3. Оплата услуг осуществляется в рублях путем перечисления соответствующих денежных средств на расчетный счет Исполнителя.</div>
        <div class="offer-clause">5.4. При оплате ссылка на номер и дату Счета-договора обязательны.</div>
        <div class="offer-clause">5.5. Оплата Счета-договора третьими лицами (сторонами), а также неполная (частичная) оплата Счета-договора не допускается. Заказчик не имеет права производить выборочную оплату позиций счета и требовать оказания услуг по выбранным позициям.</div>

        <div class="offer-heading">6. Ответственность сторон</div>
        <div class="offer-clause">6.1. За невыполнение или ненадлежащее исполнение обязательств по настоящему Договору Стороны несут ответственность в соответствии с действующим законодательством Российской Федерации.</div>
        <div class="offer-clause">6.2. Исполнитель не несет ответственности за нарушение условий договора оферты, если такое нарушение вызвано действием обстоятельств непреодолимой силы (форс-мажор), включая: действия органов государственной власти, пожар, наводнение, землетрясение, другие стихийные действия, отсутствие электроэнергии и/или сбои работы компьютерной сети, забастовки, гражданские волнения, беспорядки, любые иные обстоятельства, не ограничиваясь перечисленным, которые могут повлиять на выполнение Исполнителем условий настоящей публичной оферты и неподконтрольные Исполнителю.</div>
        <div class="offer-clause">6.3. Исполнитель не несет ответственность за несоответствие сведений, представленных Заказчиком, действительным данным, а также за отказ в совершении регистрационных действий на основании представления в регистрирующий орган недостоверных сведений.</div>
        <div class="offer-clause">6.4. Заказчик единолично несет ответственность за достоверность представляемых исходных данных, включая сведения о месте его нахождения.</div>
        <div class="offer-clause">6.5. Исполнитель не несет ответственность за предъявление требований к Заказчику со стороны государственных органов в связи оказанием Услуг.</div>

        <div class="offer-heading">7. Заключительные положения</div>
        <div class="offer-clause">7.1. Заказчик соглашается на обработку персональных данных (фамилия, имя, отчество, год, дата и месяц рождения, адрес места жительства), в том числе на использование данных сведений в документах, связанных с осуществлением необходимых действий, для оказания Услуг, их хранение и передачу.</div>
        <div class="offer-clause">7.2. Стороны принимают все необходимые меры для разрешения разногласий, возникших в процессе исполнения условий Оферты, путем переговоров.</div>
        <div class="offer-clause">7.3. При невозможности урегулирования спора или разногласия в ходе переговоров заинтересованная сторона должна направить другой стороне письменную претензию с описанием существа допущенного нарушения и разумного срока для его устранения.</div>
        <div class="offer-clause">7.4. Нарушившая сторона должна устранить допущенное нарушение в течение срока, указанного в претензии, или направить отправителю претензии письменный мотивированный отказ в ее удовлетворении в течение 10 (десяти) дней с момента получения претензии.</div>
        <div class="offer-clause">7.5. В случае неудовлетворения претензии или неполучения ответа на претензию в течение срока, предусмотренного настоящим пунктом (с учетом срока доставки почтовой корреспонденции), заинтересованная сторона вправе обратиться в суд. Все споры касающиеся судебных тяжб будут рассматриваться в Новокуйбышевском городском суде Самарской области в порядке искового производства, в порядке договорной подсудности.</div>
        <div class="offer-clause">7.6. Настоящая публичная оферта действует на всей территории, где осуществляет деятельность {{seller.fullName}} при выполнении оказании любых услуг, поименованных в настоящем документе, если на их выполнение не заключен отдельный договор.</div>

        <div class="offer-heading">8. Реквизиты Исполнителя</div>
        <div class="offer-clause">{{seller.fullName}}</div>
        <div class="offer-clause">Система налогообложения: {{seller.taxSystem}}</div>
        <div class="offer-clause">Юридический адрес: {{seller.address}}</div>
        <div class="offer-clause">ОГРН/ОГРНИП: {{seller.ogrn}}</div>
        <div class="offer-clause">ИНН: {{seller.inn}}</div>
        <div class="offer-clause">ОКВЭД: {{seller.okved}}</div>
        <div class="offer-clause">Счет №: {{seller.accountNumber}}</div>
        <div class="offer-clause">Банк: {{seller.bankName}}</div>
        <div class="offer-clause">Корреспондентский счет: {{seller.corrAccountNumber}}</div>
        <div class="offer-clause">БИК: {{seller.bik}}</div>
        <div class="offer-clause">Телефон: {{seller.phone}}</div>
        <div class="offer-clause">Email: {{seller.email}}</div>

        <div class="offer-requisites-heading">Адреса и реквизиты сторон:</div>
        <table class="offer-requisites-table">
          <thead>
            <tr>
              <th>Исполнитель</th>
              <th>Заказчик</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>{{seller.fullName}}</td>
              <td>{{buyer.name}}</td>
            </tr>
            <tr>
              <td>ИНН {{seller.inn}}</td>
              <td>ИНН {{buyer.inn}}</td>
            </tr>
            <tr>
              <td>КПП {{seller.kppOrDash}}</td>
              <td>КПП {{buyer.kppOrDash}}</td>
            </tr>
            <tr>
              <td>ОГРН/ОГРНИП {{seller.ogrn}}</td>
              <td>ОГРН/ОГРНИП {{buyer.ogrn}}</td>
            </tr>
            <tr>
              <td>Юридический адрес {{seller.address}}</td>
              <td>Юридический адрес {{buyer.address}}</td>
            </tr>
            <tr>
              <td>Р/сч {{seller.accountNumber}}<br/>в {{seller.bankName}}</td>
              <td>{{buyer.requisitesHtml}}</td>
            </tr>
            <tr>
              <td>Кор.счет {{seller.corrAccountNumber}}<br/>БИК {{seller.bik}}</td>
              <td></td>
            </tr>
          </tbody>
        </table>

        <table class="offer-signatures">
          <tbody>
            <tr>
              <td class="offer-signature-role" style="width: 45%;">Предприниматель</td>
              <td class="offer-signature-spacer"></td>
              <td class="offer-signature-role" style="width: 45%;">Предприниматель</td>
            </tr>
            <tr>
              <td class="offer-signature-name">{{seller.documentDirectorShortName}}</td>
              <td></td>
              <td class="offer-signature-name"></td>
            </tr>
            <tr>
              <td class="offer-signature-caption">подпись&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;расшифровка<br/>подписи</td>
              <td></td>
              <td class="offer-signature-caption">подпись&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;расшифровка<br/>подписи</td>
            </tr>
          </tbody>
        </table>
      </div>

      <table class="signature-table offer-signature-table">
""",
            StringComparison.Ordinal);
}
