using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Invoices;

public static class InvoiceDocumentTemplateEngine
{
    public const string DefaultTemplateKey = "invoice-standard";

    private static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly Regex LoopRegex = new("""\{\{#lineItems\}\}(.*?)\{\{\/lineItems\}\}""", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new("""\{\{\s*([a-zA-Z0-9\._]+)\s*\}\}""", RegexOptions.Compiled);

    public static InvoiceDocumentTemplateSettingsDto CreateDefaultSettings()
    {
        return CreateResponse(new InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = DefaultTemplateKey,
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

        return new InvoiceDocumentTemplateSettingsStorage
        {
            ActiveTemplateKey = request.ActiveTemplateKey.Trim(),
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
        LegalEntity? mainCompany)
    {
        var model = BuildRenderModel(payment, lineItems, mainCompany);
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
            new() { Key = "invoice.total", Category = "Счет", Description = "Итого по позициям", IsRepeating = false },
            new() { Key = "invoice.totalWords", Category = "Счет", Description = "Итого прописью", IsRepeating = false },
            new() { Key = "invoice.vatText", Category = "Счет", Description = "Строка по НДС", IsRepeating = false },
            new() { Key = "seller.shortName", Category = "Продавец", Description = "Краткое название продавца", IsRepeating = false },
            new() { Key = "seller.fullName", Category = "Продавец", Description = "Полное название продавца", IsRepeating = false },
            new() { Key = "seller.inn", Category = "Продавец", Description = "ИНН продавца", IsRepeating = false },
            new() { Key = "seller.kpp", Category = "Продавец", Description = "КПП продавца", IsRepeating = false },
            new() { Key = "seller.address", Category = "Продавец", Description = "Адрес продавца", IsRepeating = false },
            new() { Key = "seller.bankName", Category = "Продавец", Description = "Банк продавца", IsRepeating = false },
            new() { Key = "seller.bik", Category = "Продавец", Description = "БИК продавца", IsRepeating = false },
            new() { Key = "seller.accountNumber", Category = "Продавец", Description = "Расчетный счет продавца", IsRepeating = false },
            new() { Key = "seller.corrAccountNumber", Category = "Продавец", Description = "Корреспондентский счет продавца", IsRepeating = false },
            new() { Key = "seller.requisites", Category = "Продавец", Description = "Полные реквизиты продавца одной строкой", IsRepeating = false },
            new() { Key = "seller.documentDirectorPosition", Category = "Продавец", Description = "Должность подписанта", IsRepeating = false },
            new() { Key = "seller.documentDirectorFullName", Category = "Продавец", Description = "ФИО подписанта", IsRepeating = false },
            new() { Key = "seller.logoHtml", Category = "Продавец", Description = "HTML-блок логотипа", IsRepeating = false },
            new() { Key = "seller.sealHtml", Category = "Продавец", Description = "HTML-блок печати", IsRepeating = false },
            new() { Key = "seller.signatureHtml", Category = "Продавец", Description = "HTML-блок подписи", IsRepeating = false },
            new() { Key = "buyer.name", Category = "Покупатель", Description = "ФИО или название покупателя", IsRepeating = false },
            new() { Key = "buyer.address", Category = "Покупатель", Description = "Адрес покупателя", IsRepeating = false },
            new() { Key = "buyer.passport", Category = "Покупатель", Description = "Паспортные данные покупателя", IsRepeating = false },
            new() { Key = "buyer.requisites", Category = "Покупатель", Description = "Полные реквизиты покупателя одной строкой", IsRepeating = false },
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
        LegalEntity? mainCompany)
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
        var directorPosition = !string.IsNullOrWhiteSpace(director?.Position)
            ? director!.Position!
            : "Руководитель";

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
                ["invoice.total"] = Encode(FormatMoney(total)),
                ["invoice.totalWords"] = Encode(SpellMoney(total)),
                ["invoice.vatText"] = "Без налога (НДС): —",
                ["seller.shortName"] = Encode(seller.ShortName),
                ["seller.fullName"] = Encode(seller.FullName),
                ["seller.inn"] = Encode(seller.Inn),
                ["seller.kpp"] = Encode(seller.Kpp),
                ["seller.address"] = Encode(seller.Address),
                ["seller.bankName"] = Encode(seller.BankName),
                ["seller.bik"] = Encode(seller.Bik),
                ["seller.accountNumber"] = Encode(seller.AccountNumber),
                ["seller.corrAccountNumber"] = Encode(seller.CorrAccountNumber),
                ["seller.requisites"] = Encode(seller.Requisites),
                ["seller.documentDirectorPosition"] = Encode(directorPosition),
                ["seller.documentDirectorFullName"] = Encode(directorFullName),
                ["seller.logoHtml"] = BuildImageHtml(mainCompany?.LogoDataUrl, "Логотип", "logo"),
                ["seller.sealHtml"] = BuildImageHtml(mainCompany?.SealDataUrl, "Печать", "seal"),
                ["seller.signatureHtml"] = BuildImageHtml(mainCompany?.DirectorSignatureDataUrl, "Подпись", "signature"),
                ["buyer.name"] = Encode(buyer.Name),
                ["buyer.address"] = Encode(buyer.Address),
                ["buyer.passport"] = Encode(buyer.Passport),
                ["buyer.requisites"] = Encode(buyer.Requisites),
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
            .FirstOrDefault(account => !string.IsNullOrWhiteSpace(account.AccountNumber))
            ?? new BankAccountInfo();

        var fullName = FirstNotEmpty(mainCompany?.FullName, mainCompany?.ShortName, "Организация не выбрана");
        var shortName = FirstNotEmpty(mainCompany?.ShortName, mainCompany?.FullName, "Организация");
        var bankName = FirstNotEmpty(bankAccount.Bank, mainCompany?.TreasuryBankName);
        var bik = FirstNotEmpty(bankAccount.Bik, mainCompany?.TreasuryBankBik);
        var accountNumber = FirstNotEmpty(bankAccount.AccountNumber, mainCompany?.TreasuryAccountNumber);
        var corrAccountNumber = mainCompany?.TreasuryCorrAccountNumber ?? string.Empty;

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

        if (!string.IsNullOrWhiteSpace(mainCompany?.Address))
        {
            requisitesParts.Add(mainCompany.Address);
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
            Address = mainCompany?.Address ?? string.Empty,
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
        var primaryPassport = directClient?.Passports
            .OrderByDescending(passport => passport.IsPrimary)
            .ThenByDescending(passport => passport.UpdatedAt ?? passport.CreatedAt)
            .FirstOrDefault();

        var name = FirstNotEmpty(
            directClient?.Name,
            payment.CounterpartyName,
            isExpense ? "Контрагент" : "Клиент");

        var address = directClient?.Address ?? string.Empty;
        var passport = BuildPassport(primaryPassport);

        var requisitesParts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(address))
        {
            requisitesParts.Add(address);
        }

        if (!string.IsNullOrWhiteSpace(passport))
        {
            requisitesParts.Add(passport);
        }

        return new BuyerInfo
        {
            Name = name,
            Address = address,
            Passport = passport,
            Requisites = string.Join(", ", requisitesParts.Where(static part => !string.IsNullOrWhiteSpace(part))),
        };
    }

    private static string BuildPassport(ClientPassport? passport)
    {
        if (passport is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(passport.Series) || !string.IsNullOrWhiteSpace(passport.Number))
        {
            parts.Add($"Паспорт {string.Join(" ", new[] { passport.Series, passport.Number }.Where(static x => !string.IsNullOrWhiteSpace(x)))}".Trim());
        }

        if (passport.IssuedAt.HasValue)
        {
            parts.Add($"выдан {passport.IssuedAt.Value:dd.MM.yyyy}");
        }

        if (!string.IsNullOrWhiteSpace(passport.IssuedBy))
        {
            parts.Add(passport.IssuedBy);
        }

        if (!string.IsNullOrWhiteSpace(passport.DepartmentCode))
        {
            parts.Add($"код подразделения {passport.DepartmentCode}");
        }

        return string.Join(", ", parts);
    }

    private static List<BankAccountInfo> ParseBankAccounts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<BankAccountInfo>>(json) ?? [];
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

    private static string Encode(string? value)
        => WebUtility.HtmlEncode(value ?? string.Empty);

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
        public string Requisites { get; init; } = string.Empty;
    }

    private sealed class BankAccountInfo
    {
        public string? Bank { get; set; }
        public string? Name { get; set; }
        public string? Bik { get; set; }
        public string? Currency { get; set; }
        public string? AccountNumber { get; set; }
        public string? AccountType { get; set; }
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
    @page { size: A4; margin: 14mm; }
    * { box-sizing: border-box; }
    body { margin: 0; color: #111827; font-family: Arial, sans-serif; font-size: 13px; }
    .page { width: 100%; }
    .top { display: grid; grid-template-columns: 1.7fr 0.9fr; gap: 18px; align-items: start; }
    .seller-heading { font-size: 20px; font-weight: 700; line-height: 1.25; margin-bottom: 12px; }
    .brand-side { text-align: right; }
    .brand-side .logo { max-width: 190px; max-height: 92px; object-fit: contain; margin-left: auto; display: block; }
    .brand-side .seal { max-width: 160px; max-height: 160px; object-fit: contain; margin: 16px 0 0 auto; display: block; opacity: 0.95; }
    .brand-side .signature { max-width: 180px; max-height: 54px; object-fit: contain; margin-left: auto; display: block; }
    .bank-table, .items-table { width: 100%; border-collapse: collapse; }
    .bank-table td, .items-table th, .items-table td { border: 1px solid #111827; padding: 6px 8px; vertical-align: top; }
    .bank-table td.label { width: 118px; color: #475569; font-size: 12px; }
    .bank-table td.value { font-weight: 600; }
    h1 { margin: 18px 0 10px; font-size: 28px; font-weight: 700; text-align: center; }
    .details { border-top: 2px solid #111827; border-bottom: 2px solid #111827; padding: 10px 0 12px; margin-bottom: 16px; }
    .details-row { margin: 6px 0; line-height: 1.45; }
    .details-label { font-weight: 700; }
    .items-table thead th { text-align: center; font-weight: 700; }
    .items-table .num { width: 38px; text-align: center; }
    .items-table .qty { width: 70px; text-align: right; }
    .items-table .unit { width: 72px; text-align: center; }
    .items-table .price, .items-table .sum { width: 115px; text-align: right; white-space: nowrap; }
    .totals { margin-top: 10px; margin-left: auto; width: 330px; }
    .totals-row { display: grid; grid-template-columns: 1fr auto; gap: 12px; padding: 3px 0; }
    .totals-row strong { font-size: 16px; }
    .words { margin-top: 14px; font-weight: 700; }
    .signature-area { margin-top: 32px; display: grid; grid-template-columns: 220px 1fr 240px; gap: 14px; align-items: end; }
    .signature-line { border-bottom: 1px solid #111827; min-height: 42px; position: relative; }
    .signature-line .signature { position: absolute; right: 16px; bottom: 4px; }
    .muted { color: #64748b; font-size: 12px; }
  </style>
</head>
<body>
  <div class="page">
    <div class="top">
      <div>
        <div class="seller-heading">{{seller.fullName}}</div>
        <table class="bank-table">
          <tr>
            <td class="value">{{seller.bankName}}</td>
            <td class="label">БИК</td>
            <td class="value">{{seller.bik}}</td>
          </tr>
          <tr>
            <td class="label">Банк получателя</td>
            <td class="label">Сч. №</td>
            <td class="value">{{seller.accountNumber}}</td>
          </tr>
          <tr>
            <td class="value">ИНН {{seller.inn}} {{seller.kpp}}</td>
            <td class="label">Корр. сч.</td>
            <td class="value">{{seller.corrAccountNumber}}</td>
          </tr>
          <tr>
            <td class="value">{{seller.fullName}}</td>
            <td class="label"></td>
            <td class="value"></td>
          </tr>
          <tr>
            <td class="label">Получатель</td>
            <td class="label"></td>
            <td class="value"></td>
          </tr>
        </table>
      </div>
      <div class="brand-side">
        {{seller.logoHtml}}
        {{seller.sealHtml}}
      </div>
    </div>

    <h1>Счет № {{invoice.number}} от {{invoice.dateLong}}</h1>

    <div class="details">
      <div class="details-row"><span class="details-label">Поставщик (Исполнитель):</span> {{seller.requisites}}</div>
      <div class="details-row"><span class="details-label">Покупатель (Заказчик):</span> {{buyer.requisites}}</div>
    </div>

    <table class="items-table">
      <thead>
        <tr>
          <th class="num">№</th>
          <th>Товары (работы, услуги)</th>
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
          <td>{{name}}</td>
          <td class="qty">{{quantity}}</td>
          <td class="unit">{{unit}}</td>
          <td class="price">{{price}}</td>
          <td class="sum">{{total}}</td>
        </tr>
        {{/lineItems}}
      </tbody>
    </table>

    <div class="totals">
      <div class="totals-row"><span>Итого:</span><span>{{invoice.total}}</span></div>
      <div class="totals-row"><span>{{invoice.vatText}}</span><span></span></div>
      <div class="totals-row"><strong>Всего к оплате:</strong><strong>{{invoice.total}}</strong></div>
    </div>

    <div class="words">{{invoice.totalWords}}</div>

    <div class="signature-area">
      <div>{{seller.documentDirectorPosition}}</div>
      <div class="signature-line">{{seller.signatureHtml}}</div>
      <div>{{seller.documentDirectorFullName}}</div>
    </div>
    <div class="muted" style="margin-top:8px;">Срок оплаты: {{invoice.dueDate}}</div>
  </div>
</body>
</html>
""";
}
