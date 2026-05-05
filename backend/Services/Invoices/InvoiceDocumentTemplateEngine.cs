using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.LegalEntities;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceDocumentTemplateEngine
{
    public const string DefaultTemplateKey = "invoice-standard";
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
            new() { Key = "invoice.totalWords", Category = "Счет", Description = "Итого прописью", IsRepeating = false },
            new() { Key = "invoice.itemsCount", Category = "Счет", Description = "Количество наименований в счете", IsRepeating = false },
            new() { Key = "invoice.vatText", Category = "Счет", Description = "Строка по НДС", IsRepeating = false },
            new() { Key = "invoice.installmentScheduleHtml", Category = "Счет", Description = "HTML-блок графика платежей (если есть рассрочка)", IsRepeating = false },
            new() { Key = "invoice.qrCodeHtml", Category = "Счет", Description = "HTML-блок QR-кода для оплаты", IsRepeating = false },
            new() { Key = "seller.shortName", Category = "Продавец", Description = "Краткое название продавца", IsRepeating = false },
            new() { Key = "seller.fullName", Category = "Продавец", Description = "Полное название продавца", IsRepeating = false },
            new() { Key = "seller.inn", Category = "Продавец", Description = "ИНН продавца", IsRepeating = false },
            new() { Key = "seller.kpp", Category = "Продавец", Description = "КПП продавца", IsRepeating = false },
            new() { Key = "seller.kppOrDash", Category = "Продавец", Description = "КПП или дефис, если значение не заполнено", IsRepeating = false },
            new() { Key = "seller.address", Category = "Продавец", Description = "Адрес продавца", IsRepeating = false },
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
            new() { Key = "buyer.passport", Category = "Покупатель", Description = "Паспортные данные покупателя", IsRepeating = false },
            new() { Key = "buyer.inn", Category = "Покупатель", Description = "ИНН покупателя", IsRepeating = false },
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
        ];
    }

    private static InvoiceDocumentRenderModel BuildRenderModel(
        Payment payment,
        IReadOnlyCollection<InvoiceLineItemDto> lineItems,
        LegalEntity? mainCompany,
        bool includeSignatureAndSeal,
        string? installmentScheduleHtml)
    {
        var invoiceDate = (payment.Invoice?.Date ?? payment.AccountDate ?? payment.Date).Date;
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
        var seller = BuildSeller(mainCompany);
        var buyer = BuildBuyer(payment);
        var director = ParsePerson(mainCompany?.DocumentDirectorJson);
        var directorFullName = BuildPersonFullName(director, mainCompany?.Director);
        var directorShortName = BuildPersonShortName(director, directorFullName);
        var directorPosition = !string.IsNullOrWhiteSpace(director?.Position)
            ? director!.Position!
            : "Предприниматель";
        var chiefAccountant = ParsePerson(mainCompany?.ChiefAccountantJson);
        var chiefAccountantFullName = BuildPersonFullName(chiefAccountant, directorFullName);
        var chiefAccountantShortName = BuildPersonShortName(chiefAccountant, chiefAccountantFullName);
        var chiefAccountantPosition = !string.IsNullOrWhiteSpace(chiefAccountant?.Position)
            ? chiefAccountant!.Position!
            : "Главный (старший) бухгалтер";
        var qrCodeHtml = BuildInvoiceQrCodeHtml(payment, seller, total, invoiceDate, number);
        var hasInstallmentSchedule = !string.IsNullOrWhiteSpace(installmentScheduleHtml);
        var dueDateHtml = !hasInstallmentSchedule && payment.Date.Date != invoiceDate.Date
            ? $"<div class=\"invoice-meta-line\"><strong>Срок оплаты:</strong> {Encode(payment.Date.ToString("dd.MM.yyyy", RuCulture))}</div>"
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
                ["document.title"] = Encode($"Счет № {number} от {invoiceDate:dd.MM.yyyy}"),
                ["invoice.number"] = Encode(number),
                ["invoice.date"] = Encode(invoiceDate.ToString("dd.MM.yyyy", RuCulture)),
                ["invoice.dateLong"] = Encode(invoiceDate.ToString("d MMMM yyyy 'г.'", RuCulture)),
                ["invoice.dueDate"] = Encode(payment.Date.ToString("dd.MM.yyyy", RuCulture)),
                ["invoice.amount"] = Encode(FormatMoney(payment.Amount)),
                ["invoice.amountWords"] = Encode(SpellMoney(payment.Amount)),
                ["invoice.description"] = Encode(payment.Description),
                ["invoice.clientCommentHtml"] = clientCommentHtml,
                ["invoice.dueDateHtml"] = dueDateHtml,
                ["invoice.notForAccountingHtml"] = notForAccountingHtml,
                ["invoice.total"] = Encode(FormatMoney(total)),
                ["invoice.totalWords"] = Encode(SpellMoney(total)),
                ["invoice.itemsCount"] = Encode(normalizedLineItems.Count.ToString(CultureInfo.InvariantCulture)),
                ["invoice.vatText"] = "Без налога (НДС): —",
                ["invoice.qrCodeHtml"] = qrCodeHtml,
                ["invoice.installmentScheduleHtml"] = installmentScheduleHtml ?? string.Empty,
                ["seller.shortName"] = Encode(seller.ShortName),
                ["seller.fullName"] = Encode(seller.FullName),
                ["seller.inn"] = Encode(seller.Inn),
                ["seller.kpp"] = Encode(seller.Kpp),
                ["seller.kppOrDash"] = Encode(string.IsNullOrWhiteSpace(seller.Kpp) ? "-" : seller.Kpp),
                ["seller.address"] = Encode(seller.Address),
                ["seller.bankName"] = Encode(seller.BankName),
                ["seller.bik"] = Encode(seller.Bik),
                ["seller.accountNumber"] = Encode(seller.AccountNumber),
                ["seller.corrAccountNumber"] = Encode(seller.CorrAccountNumber),
                ["seller.requisites"] = Encode(seller.Requisites),
                ["seller.documentDirectorPosition"] = Encode(directorPosition),
                ["seller.documentDirectorFullName"] = Encode(directorFullName),
                ["seller.documentDirectorShortName"] = Encode(directorShortName),
                ["seller.chiefAccountantPosition"] = Encode(chiefAccountantPosition),
                ["seller.chiefAccountantFullName"] = Encode(chiefAccountantFullName),
                ["seller.chiefAccountantShortName"] = Encode(chiefAccountantShortName),
                ["seller.logoHtml"] = BuildImageHtml(mainCompany?.LogoDataUrl, "Логотип", "logo"),
                ["seller.sealHtml"] = includeSignatureAndSeal
                    ? BuildImageHtml(mainCompany?.SealDataUrl, "Печать", "seal")
                    : string.Empty,
                ["seller.signatureHtml"] = includeSignatureAndSeal
                    ? BuildImageHtml(mainCompany?.DirectorSignatureDataUrl, "Подпись", "signature")
                    : string.Empty,
                ["buyer.name"] = Encode(buyer.Name),
                ["buyer.address"] = Encode(buyer.Address),
                ["buyer.passport"] = Encode(buyer.Passport),
                ["buyer.inn"] = Encode(buyer.Inn),
                ["buyer.snils"] = Encode(buyer.Snils),
                ["buyer.phone"] = Encode(buyer.Phone),
                ["buyer.email"] = Encode(buyer.Email),
                ["buyer.contract"] = Encode(buyer.Contract),
                ["buyer.foundation"] = Encode(buyer.Foundation),
                ["buyer.case"] = Encode(buyer.Case),
                ["buyer.requisites"] = Encode(buyer.Requisites),
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
                })
                .ToList(),
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
        var address = LegalEntityAddressResolver.ResolvePreferredAddress(mainCompany);
        var bankName = FirstNotEmpty(bankAccount.Bank, mainCompany?.TreasuryBankName);
        var bik = FirstNotEmpty(bankAccount.Bik, mainCompany?.TreasuryBankBik);
        var accountNumber = FirstNotEmpty(bankAccount.AccountNumber, mainCompany?.TreasuryAccountNumber);
        var corrAccountNumber = FirstNotEmpty(bankAccount.CorrAccountNumber, mainCompany?.TreasuryCorrAccountNumber);

        var requisitesParts = new List<string>
        {
            fullName,
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

        return new SellerInfo
        {
            ShortName = shortName,
            FullName = fullName,
            Inn = mainCompany?.Inn ?? string.Empty,
            Kpp = mainCompany?.Kpp ?? string.Empty,
            Address = address,
            BankName = bankName,
            Bik = bik,
            AccountNumber = accountNumber,
            CorrAccountNumber = corrAccountNumber,
            Requisites = string.Join(", ", requisitesParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
        };
    }

    private static BuyerInfo BuildBuyer(Payment payment)
    {
        var isExpense = payment.Type == PaymentType.Expense;
        var directClient = isExpense ? payment.ExpenseClient : payment.Client;
        var directLegalEntity = isExpense ? payment.ExpenseLegalEntity : payment.LegalEntity;
        if (directClient is null && directLegalEntity is not null)
        {
            var legalName = FirstNotEmpty(directLegalEntity.FullName, directLegalEntity.ShortName, payment.CounterpartyName, "Контрагент");
            var legalAddress = LegalEntityAddressResolver.ResolvePreferredAddress(directLegalEntity);
            var legalRequisitesLines = new List<string>
            {
                string.Join(", ", new[]
                {
                    legalName,
                    string.IsNullOrWhiteSpace(directLegalEntity.Inn) ? null : $"ИНН {directLegalEntity.Inn}",
                    string.IsNullOrWhiteSpace(directLegalEntity.Kpp) ? null : $"КПП {directLegalEntity.Kpp}",
                    string.IsNullOrWhiteSpace(directLegalEntity.Ogrn) ? null : $"ОГРН {directLegalEntity.Ogrn}",
                }.Where(static part => !string.IsNullOrWhiteSpace(part))),
            };
            if (!string.IsNullOrWhiteSpace(legalAddress))
            {
                legalRequisitesLines.Add(legalAddress);
            }

            return new BuyerInfo
            {
                Name = legalName,
                Address = legalAddress,
                Passport = string.Empty,
                Inn = directLegalEntity.Inn ?? string.Empty,
                Snils = string.Empty,
                Phone = directLegalEntity.Phone ?? string.Empty,
                Email = directLegalEntity.Email ?? string.Empty,
                Contract = string.Empty,
                Foundation = string.Empty,
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
            Address = address,
            Passport = passport,
            Inn = inn,
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
            parts.Add($"от {contract.Date:dd.MM.yyyy}");
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
            parts.Add($"выдан {issueDate.Value:dd.MM.yyyy}");
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

        var purpose = $"Оплата счета №{invoiceNumber} от {invoiceDate:dd.MM.yyyy} на сумму {FormatMoney(total)} руб. (без НДС)";
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
            return normalized;
        }

        var managedFingerprint = ResolveManagedStandardTemplateFingerprint(
            standardTemplate.Html,
            normalized.StandardTemplateFingerprint);

        if (string.IsNullOrWhiteSpace(managedFingerprint))
        {
            normalized.StandardTemplateFingerprint = null;
            return normalized;
        }

        if (!string.Equals(managedFingerprint, CurrentDefaultTemplateFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            standardTemplate.Html = DefaultTemplateHtml;
        }

        normalized.StandardTemplateFingerprint = CurrentDefaultTemplateFingerprint;
        return normalized;
    }

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
            ? actualFingerprint
            : null;
    }

    private static string ComputeTemplateFingerprint(string templateHtml)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(templateHtml.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
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
        public string Inn { get; init; } = string.Empty;
        public string Kpp { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string BankName { get; init; } = string.Empty;
        public string Bik { get; init; } = string.Empty;
        public string AccountNumber { get; init; } = string.Empty;
        public string CorrAccountNumber { get; init; } = string.Empty;
        public string Requisites { get; init; } = string.Empty;
    }

    private sealed class BuyerInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Address { get; init; } = string.Empty;
        public string Passport { get; init; } = string.Empty;
        public string Inn { get; init; } = string.Empty;
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
      width: 55%;
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
    .items-table .sum {
      width: 12%;
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
            <td class="sum">{{total}}</td>
          </tr>
          {{/lineItems}}
        </tbody>
      </table>

      <table class="totals-table">
        <tbody>
          <tr>
            <td class="totals-label">Итого:</td>
            <td class="totals-value">{{invoice.total}}</td>
          </tr>
          <tr>
            <td class="totals-label">Без налога (НДС):</td>
            <td class="totals-value">-</td>
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
}
