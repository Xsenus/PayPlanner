using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private const string OneCExchangeKind = "onec-xml";

    private static readonly HashSet<string> OneCLineItemTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "Товары",
        "Услуги",
        "АгентскиеУслуги",
        "Оборудование",
    };

    private ParsedImportBundle ParseXmlImportFile(SpreadsheetImportUploadFile file)
    {
        var fileHash = ComputeFileHash(file.Content);
        using var stream = new MemoryStream(file.Content, writable: false);
        return ParseOneCXmlStream(file.FileName, fileHash, file.FileName, stream);
    }

    private ParsedImportBundle ParseZipImportFile(SpreadsheetImportUploadFile file)
    {
        var bundle = new ParsedImportBundle();
        using var stream = new MemoryStream(file.Content, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var xmlEntries = archive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                && string.Equals(Path.GetExtension(entry.Name), ".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (xmlEntries.Count == 0)
        {
            bundle.Files.Add(new ParsedFile
            {
                FileName = file.FileName,
                FileHash = ComputeFileHash(file.Content),
                DetectedKind = "unknown",
                RowCount = 0,
            });
            bundle.Messages.Add($"Архив {file.FileName}: XML-файл выгрузки КУБ/1С не найден.");
            return bundle;
        }

        foreach (var entry in xmlEntries)
        {
            using var entryStream = entry.Open();
            using var memory = new MemoryStream();
            entryStream.CopyTo(memory);
            var entryContent = memory.ToArray();
            var entryHash = ComputeFileHash(entryContent);
            using var xmlStream = new MemoryStream(entryContent, writable: false);
            var parsed = ParseOneCXmlStream($"{file.FileName}/{entry.FullName}", entryHash, entry.FullName, xmlStream);
            MergeParsedBundle(bundle, parsed);
        }

        return bundle;
    }

    private ParsedImportBundle ParseOneCXmlStream(string sourceFileName, string fileHash, string? sheetName, Stream stream)
    {
        var bundle = new ParsedImportBundle();
        var counterpartyByExternalId = new Dictionary<string, OneCCounterpartyInfo>(StringComparer.OrdinalIgnoreCase);
        var bankByBik = new Dictionary<string, OneCBankInfo>(StringComparer.OrdinalIgnoreCase);
        var bankAccounts = new List<OneCBankAccountInfo>();
        var invoiceByExternalId = new Dictionary<string, List<OneCInvoiceReference>>(StringComparer.OrdinalIgnoreCase);
        var objectCount = 0;
        var importedAt = string.Empty;
        var periodStart = string.Empty;
        var periodEnd = string.Empty;

        try
        {
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            };

            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ФайлОбмена")
                {
                    importedAt = reader.GetAttribute("ДатаВыгрузки") ?? string.Empty;
                    periodStart = reader.GetAttribute("НачалоПериодаВыгрузки") ?? string.Empty;
                    periodEnd = reader.GetAttribute("ОкончаниеПериодаВыгрузки") ?? string.Empty;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Объект")
                {
                    continue;
                }

                objectCount += 1;
                var type = reader.GetAttribute("Тип") ?? string.Empty;
                var rule = reader.GetAttribute("ИмяПравила") ?? string.Empty;
                var discriminator = !string.IsNullOrWhiteSpace(rule) ? rule : type;
                var rowNumber = TryParseOneCRowNumber(reader.GetAttribute("Нпп")) ?? objectCount;
                using var subtree = reader.ReadSubtree();
                var document = XDocument.Load(subtree, LoadOptions.None);
                var root = document.Root;
                if (root is null)
                {
                    continue;
                }

                switch (discriminator)
                {
                    case "Контрагенты":
                    case "СправочникСсылка.Контрагенты":
                    {
                        var counterparty = ParseOneCCounterparty(sourceFileName, fileHash, sheetName, rowNumber, root);
                        if (counterparty is not null)
                        {
                            bundle.CounterpartyDrafts.Add(counterparty.Draft);
                            if (!string.IsNullOrWhiteSpace(counterparty.Info.ExternalId))
                            {
                                counterpartyByExternalId[counterparty.Info.ExternalId] = counterparty.Info;
                            }
                        }

                        break;
                    }
                    case "Банки":
                    case "СправочникСсылка.Банки":
                    {
                        IncrementOneCNonImportObjectCount(bundle, discriminator);
                        var bank = ParseOneCBank(root);
                        if (bank is not null && !string.IsNullOrWhiteSpace(bank.Bik))
                        {
                            bankByBik[bank.Bik] = bank;
                        }

                        break;
                    }
                    case "БанковскиеСчета":
                    case "СправочникСсылка.БанковскиеСчета":
                    {
                        IncrementOneCNonImportObjectCount(bundle, discriminator);
                        var bankAccount = ParseOneCBankAccount(root);
                        if (bankAccount is not null)
                        {
                            bankAccounts.Add(bankAccount);
                        }

                        break;
                    }
                    case "СчетНаОплатуПокупателю":
                    case "ДокументСсылка.СчетНаОплатуПокупателю":
                    case "СчетНаОплатуПоставщика":
                    case "ДокументСсылка.СчетНаОплатуПоставщика":
                    {
                        var invoice = ParseOneCInvoice(
                            sourceFileName,
                            fileHash,
                            sheetName,
                            rowNumber,
                            root,
                            discriminator is "СчетНаОплатуПоставщика" or "ДокументСсылка.СчетНаОплатуПоставщика",
                            counterpartyByExternalId);
                        if (invoice is not null)
                        {
                            bundle.InvoiceDrafts.Add(invoice);
                            if (!string.IsNullOrWhiteSpace(invoice.ExternalId) && !string.IsNullOrWhiteSpace(invoice.Number))
                            {
                                if (!invoiceByExternalId.TryGetValue(invoice.ExternalId, out var invoiceReferences))
                                {
                                    invoiceReferences = new List<OneCInvoiceReference>();
                                    invoiceByExternalId[invoice.ExternalId] = invoiceReferences;
                                }

                                invoiceReferences.Add(new OneCInvoiceReference(
                                    invoice.ExternalId,
                                    invoice.Number,
                                    invoice.Date,
                                    invoice.Type));
                            }
                        }

                        break;
                    }
                    case "РеализацияТоваровУслуг":
                    case "ДокументСсылка.РеализацияТоваровУслуг":
                    case "ПоступлениеТоваровУслуг":
                    case "ДокументСсылка.ПоступлениеТоваровУслуг":
                    {
                        var act = ParseOneCAct(
                            sourceFileName,
                            fileHash,
                            sheetName,
                            rowNumber,
                            root,
                            discriminator is "ПоступлениеТоваровУслуг" or "ДокументСсылка.ПоступлениеТоваровУслуг",
                            counterpartyByExternalId);
                        if (act is not null)
                        {
                            bundle.ActDrafts.Add(act);
                        }

                        break;
                    }
                    default:
                    {
                        IncrementOneCNonImportObjectCount(bundle, discriminator);
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            bundle.Files.Add(new ParsedFile
            {
                FileName = sourceFileName,
                FileHash = fileHash,
                DetectedKind = "unknown",
                SheetName = sheetName,
                RowCount = 0,
            });
            bundle.Messages.Add($"Файл {sourceFileName}: не удалось разобрать XML выгрузки КУБ/1С ({ex.Message}).");
            return bundle;
        }

        EnrichOneCCounterpartiesWithBankAccounts(bundle.CounterpartyDrafts, bankAccounts, bankByBik);
        ResolveOneCActInvoiceNumbers(bundle.ActDrafts, invoiceByExternalId);

        var rowCount = bundle.CounterpartyDrafts.Count + bundle.InvoiceDrafts.Count + bundle.ActDrafts.Count;
        bundle.Files.Add(new ParsedFile
        {
            FileName = sourceFileName,
            FileHash = fileHash,
            DetectedKind = rowCount > 0 ? OneCExchangeKind : "unknown",
            SheetName = sheetName,
            RowCount = rowCount,
        });

        if (rowCount > 0)
        {
            var period = FormatOneCPeriod(periodStart, periodEnd);
            bundle.Messages.Add(
                $"Файл {sourceFileName}: распознана выгрузка КУБ/1С{FormatOneCDate(importedAt)}{period}. Контрагенты: {bundle.CounterpartyDrafts.Count}, счета: {bundle.InvoiceDrafts.Count}, акты: {bundle.ActDrafts.Count}.");
        }
        else
        {
            bundle.Messages.Add($"Файл {sourceFileName}: XML не содержит поддерживаемых объектов КУБ/1С для импорта.");
        }

        return bundle;
    }

    private static CounterpartyWithInfo? ParseOneCCounterparty(
        string sourceFileName,
        string fileHash,
        string? sheetName,
        int rowNumber,
        XElement root)
    {
        var link = GetOneCObjectLink(root);
        var externalId = NormalizeText(GetOneCPropertyValue(link, "{УникальныйИдентификатор}"));
        var inn = NormalizeInn(GetOneCPropertyValue(link, "ИНН") ?? GetOneCDirectValue(root, "ИНН"));
        var kpp = NormalizeText(GetOneCPropertyValue(link, "КПП") ?? GetOneCDirectValue(root, "КПП"));
        var name = NormalizeText(GetOneCDirectValue(root, "Наименование")
            ?? GetOneCPropertyValue(link, "Наименование")
            ?? GetOneCPropertyValue(link, "НазваниеКонтрагента"));

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var organizationForm = NormalizeText(GetOneCDirectValue(root, "ФормаСобственностиСокращенно"));
        var organizationFormFull = NormalizeText(GetOneCDirectValue(root, "ФормаСобственностиПолностью"));
        var info = new OneCCounterpartyInfo(
            externalId,
            name,
            inn,
            kpp,
            NormalizeText(GetOneCDirectValue(root, "ОГРН")),
            NormalizeText(GetOneCDirectValue(root, "ЮридическийАдрес")),
            organizationForm,
            organizationFormFull,
            NormalizeText(GetOneCDirectValue(root, "БанкБИК")),
            NormalizeText(GetOneCDirectValue(root, "РасчетныйСчет")));

        var draft = new CounterpartyDraft
        {
            PreviewId = BuildPreviewId(fileHash, "counterparties", rowNumber),
            FileName = sourceFileName,
            FileHash = fileHash,
            SheetName = sheetName,
            RowNumber = rowNumber,
            Name = name,
            Inn = inn,
            Kpp = kpp,
            Ogrn = info.Ogrn,
            Address = info.Address,
            OrganizationForm = organizationForm,
            OrganizationFormFull = organizationFormFull,
            BankBik = info.BankBik,
            BankAccount = info.BankAccount,
            ExternalId = externalId,
            Derived = false,
        };

        return new CounterpartyWithInfo(draft, info);
    }

    private static InvoiceDraft? ParseOneCInvoice(
        string sourceFileName,
        string fileHash,
        string? sheetName,
        int rowNumber,
        XElement root,
        bool isExpense,
        IReadOnlyDictionary<string, OneCCounterpartyInfo> counterparties)
    {
        var link = GetOneCObjectLink(root);
        var externalId = NormalizeText(GetOneCPropertyValue(link, "{УникальныйИдентификатор}"));
        var number = NormalizeText(GetOneCDirectValue(root, "Номер") ?? GetOneCDirectValue(root, "НомерВходящегоДокумента"));
        var date = GetOneCDate(link, "Дата")
            ?? GetOneCDate(root, "Дата")
            ?? GetOneCDate(root, "ДатаВходящегоДокумента");
        var dueDate = GetOneCDate(root, "ДатаОплатитьДо")
            ?? (isExpense ? GetOneCDate(root, "ДатаВходящегоДокумента") : null);
        var amount = TryParseDecimal(GetOneCDirectValue(root, "СуммаДокумента"));
        var counterparty = ResolveOneCCounterparty(root, counterparties);
        var lineItems = ParseOneCLineItems(root, amount ?? 0m);
        var warnings = new List<string>(lineItems.Warnings);
        var statusText = NormalizeText(GetOneCDirectValue(root, "Статус"));

        if (IsOneCMarkedForDeletion(root))
        {
            statusText ??= "Отменен";
            warnings.Add("Документ помечен на удаление в выгрузке 1С.");
        }

        return new InvoiceDraft
        {
            PreviewId = BuildPreviewId(fileHash, "invoices", rowNumber),
            FileName = sourceFileName,
            FileHash = fileHash,
            SheetName = sheetName,
            RowNumber = rowNumber,
            Number = number,
            Date = date,
            DueDate = dueDate,
            Amount = amount,
            Type = isExpense ? PaymentType.Expense : PaymentType.Income,
            CounterpartyName = counterparty.Name,
            CounterpartyInn = counterparty.Inn,
            StatusText = statusText,
            ActReference = null,
            Nomenclature = BuildOneCNomenclatureSummary(lineItems.Items),
            ContractName = GetOneCContractName(root),
            Comment = NormalizeText(GetOneCDirectValue(root, "Комментарий")),
            ExternalId = externalId,
            ParsedLineItems = lineItems.Items,
            ParseWarnings = warnings,
        };
    }

    private static ActDraft? ParseOneCAct(
        string sourceFileName,
        string fileHash,
        string? sheetName,
        int rowNumber,
        XElement root,
        bool isExpense,
        IReadOnlyDictionary<string, OneCCounterpartyInfo> counterparties)
    {
        var link = GetOneCObjectLink(root);
        var externalId = NormalizeText(GetOneCPropertyValue(link, "{УникальныйИдентификатор}"));
        var number = NormalizeText(GetOneCDirectValue(root, "Номер") ?? GetOneCDirectValue(root, "НомерВходящегоДокумента"));
        var date = GetOneCDate(root, "Дата") ?? GetOneCDate(root, "ДатаВходящегоДокумента") ?? GetOneCDate(link, "Дата");
        var amount = TryParseDecimal(GetOneCDirectValue(root, "СуммаДокумента"));
        var counterparty = ResolveOneCCounterparty(root, counterparties);
        var lineItems = ParseOneCLineItems(root, amount ?? 0m);
        var warnings = new List<string>(lineItems.Warnings);
        var invoiceProperty = GetOneCDirectProperty(root, "Счет");
        var invoiceNumber = NormalizeText(GetOneCReferenceValue(invoiceProperty, "Номер"));
        var invoiceDate = TryParseDate(GetOneCReferenceValue(invoiceProperty, "Дата"));
        var invoiceExternalId = NormalizeText(GetOneCDirectValue(root, "УидСчета")
            ?? GetOneCReferenceValue(invoiceProperty, "{УникальныйИдентификатор}"));
        var statusText = NormalizeText(GetOneCDirectValue(root, "Статус"));

        if (IsOneCMarkedForDeletion(root))
        {
            statusText ??= "Расторгнут";
            warnings.Add("Документ помечен на удаление в выгрузке 1С.");
        }

        return new ActDraft
        {
            PreviewId = BuildPreviewId(fileHash, "acts", rowNumber),
            FileName = sourceFileName,
            FileHash = fileHash,
            SheetName = sheetName,
            RowNumber = rowNumber,
            Number = number,
            Date = date,
            Amount = amount,
            Type = isExpense ? PaymentType.Expense : PaymentType.Income,
            CounterpartyName = counterparty.Name,
            CounterpartyInn = counterparty.Inn,
            StatusText = statusText,
            InvoiceNumber = invoiceNumber,
            InvoiceExternalId = invoiceExternalId,
            InvoiceDate = invoiceDate,
            PaymentDate = null,
            Nomenclature = BuildOneCNomenclatureSummary(lineItems.Items),
            ContractName = GetOneCContractName(root),
            Comment = NormalizeText(GetOneCDirectValue(root, "Комментарий")),
            ExternalId = externalId,
            ParsedLineItems = lineItems.Items,
            ParseWarnings = warnings,
        };
    }

    private static OneCBankInfo? ParseOneCBank(XElement root)
    {
        var link = GetOneCObjectLink(root);
        var bik = NormalizeText(GetOneCPropertyValue(link, "Код"));
        var name = NormalizeText(GetOneCDirectValue(root, "Наименование"));
        if (string.IsNullOrWhiteSpace(bik) && string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new OneCBankInfo(
            bik,
            name,
            NormalizeText(GetOneCDirectValue(root, "КоррСчет")),
            NormalizeText(GetOneCDirectValue(root, "Город")));
    }

    private static OneCBankAccountInfo? ParseOneCBankAccount(XElement root)
    {
        var link = GetOneCObjectLink(root);
        var accountNumber = NormalizeText(GetOneCPropertyValue(link, "НомерСчета"));
        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            return null;
        }

        var ownerProperty = GetOneCProperty(link, "Владелец");
        var bankProperty = GetOneCDirectProperty(root, "Банк");
        return new OneCBankAccountInfo(
            NormalizeText(GetOneCReferenceValue(ownerProperty, "{УникальныйИдентификатор}")),
            NormalizeInn(GetOneCReferenceValue(ownerProperty, "ИНН")),
            accountNumber,
            NormalizeText(GetOneCReferenceValue(bankProperty, "Код")),
            NormalizeText(GetOneCDirectValue(root, "Наименование")),
            NormalizeText(GetOneCDirectValue(root, "ВидСчета")));
    }

    private static void EnrichOneCCounterpartiesWithBankAccounts(
        IList<CounterpartyDraft> drafts,
        IReadOnlyCollection<OneCBankAccountInfo> bankAccounts,
        IReadOnlyDictionary<string, OneCBankInfo> banks)
    {
        if (drafts.Count == 0 || bankAccounts.Count == 0)
        {
            return;
        }

        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            var bankAccount = bankAccounts.FirstOrDefault(account =>
                (!string.IsNullOrWhiteSpace(draft.ExternalId)
                    && string.Equals(account.OwnerExternalId, draft.ExternalId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(draft.Inn)
                    && string.Equals(account.OwnerInn, draft.Inn, StringComparison.Ordinal)));

            if (bankAccount is null)
            {
                continue;
            }

            banks.TryGetValue(bankAccount.BankBik ?? string.Empty, out var bank);
            drafts[index] = draft with
            {
                BankAccount = draft.BankAccount ?? bankAccount.AccountNumber,
                BankBik = draft.BankBik ?? bankAccount.BankBik,
                BankName = draft.BankName ?? bank?.Name ?? bankAccount.BankName,
                BankCorrAccount = draft.BankCorrAccount ?? bank?.CorrAccount,
            };
        }
    }

    private static void ResolveOneCActInvoiceNumbers(
        IEnumerable<ActDraft> acts,
        IReadOnlyDictionary<string, List<OneCInvoiceReference>> invoices)
    {
        foreach (var act in acts)
        {
            if (!string.IsNullOrWhiteSpace(act.InvoiceNumber) || string.IsNullOrWhiteSpace(act.InvoiceExternalId))
            {
                continue;
            }

            if (!invoices.TryGetValue(act.InvoiceExternalId, out var invoiceReferences))
            {
                act.ParseWarnings.Add("Связанный счет из 1С не найден в этой выгрузке.");
                continue;
            }

            var matchingInvoices = invoiceReferences
                .Where(invoice => invoice.Type == act.Type)
                .GroupBy(invoice => BuildInvoiceNaturalKey(invoice.Number, invoice.Date, invoice.Type), StringComparer.Ordinal)
                .Select(group => group.First())
                .Take(2)
                .ToList();
            if (matchingInvoices.Count == 1)
            {
                act.InvoiceNumber = matchingInvoices[0].Number;
                act.InvoiceDate = matchingInvoices[0].Date;
            }
            else
            {
                act.ParseWarnings.Add("UID связанного счета 1С неоднозначен; номер счета для акта не заполнен автоматически.");
            }
        }
    }

    private static OneCCounterpartyInfo ResolveOneCCounterparty(
        XElement root,
        IReadOnlyDictionary<string, OneCCounterpartyInfo> counterparties)
    {
        var property = GetOneCDirectProperty(root, "Контрагент");
        var externalId = NormalizeText(GetOneCReferenceValue(property, "{УникальныйИдентификатор}"));
        counterparties.TryGetValue(externalId ?? string.Empty, out var known);

        var name = NormalizeText(GetOneCReferenceValue(property, "Наименование", "НазваниеКонтрагента", "ПолноеНаименование"))
            ?? known?.Name;
        var inn = NormalizeInn(GetOneCReferenceValue(property, "ИНН") ?? known?.Inn);
        var kpp = NormalizeText(GetOneCReferenceValue(property, "КПП") ?? known?.Kpp);

        return new OneCCounterpartyInfo(
            externalId ?? known?.ExternalId,
            name ?? inn ?? "Контрагент без названия",
            inn,
            kpp,
            known?.Ogrn,
            known?.Address,
            known?.OrganizationForm,
            known?.OrganizationFormFull,
            known?.BankBik,
            known?.BankAccount);
    }

    private static LineItemParseResult ParseOneCLineItems(XElement root, decimal totalAmount)
    {
        var items = new List<ImportedLineItem>();
        var warnings = new List<string>();

        foreach (var table in root.Elements().Where(element => element.Name.LocalName == "ТабличнаяЧасть"))
        {
            var tableName = table.Attribute("Имя")?.Value ?? string.Empty;
            if (!OneCLineItemTables.Contains(tableName))
            {
                continue;
            }

            foreach (var row in table.Elements().Where(element => element.Name.LocalName == "Запись"))
            {
                var rowAmount = TryParseDecimal(GetOneCDirectValue(row, "Сумма"));
                var quantity = TryParseDecimal(GetOneCDirectValue(row, "Количество")) ?? 1m;
                if (quantity <= 0)
                {
                    quantity = 1m;
                }

                var price = TryParseDecimal(GetOneCDirectValue(row, "Цена"))
                    ?? TryParseDecimal(GetOneCDirectValue(row, "ЦенаБезНДС"))
                    ?? (rowAmount.HasValue ? Math.Round(rowAmount.Value / quantity, 2) : null);
                var name = NormalizeText(GetOneCDirectValue(row, "Содержание"))
                    ?? NormalizeText(GetOneCReferenceValue(GetOneCDirectProperty(row, "Номенклатура"), "Наименование"))
                    ?? NormalizeText(tableName);
                var unit = NormalizeText(GetOneCReferenceValue(GetOneCDirectProperty(row, "ЕдиницаИзмерения"), "Наименование"))
                    ?? "шт";

                if (string.IsNullOrWhiteSpace(name) || !price.HasValue)
                {
                    warnings.Add($"Не удалось разобрать строку табличной части 1С: {tableName}.");
                    continue;
                }

                items.Add(new ImportedLineItem(name, quantity, unit, price.Value));
            }
        }

        var parsedTotal = items.Sum(item => Math.Round(item.Quantity * item.Price, 2));
        if (items.Count > 0 && totalAmount > 0 && Math.Abs(parsedTotal - totalAmount) > 0.01m)
        {
            warnings.Add($"Сумма строк 1С ({parsedTotal:0.00}) не совпадает с суммой документа ({totalAmount:0.00}).");
        }

        return new LineItemParseResult(items, warnings);
    }

    private static string? BuildOneCNomenclatureSummary(IReadOnlyCollection<ImportedLineItem> items)
        => items.Count == 0
            ? null
            : string.Join("\n", items.Take(10).Select(item =>
                $"{item.Name} {item.Quantity.ToString("0.####", CultureInfo.InvariantCulture)} {item.Unit} x {item.Price.ToString("0.##", CultureInfo.InvariantCulture)}"));

    private static string? GetOneCContractName(XElement root)
    {
        var property = GetOneCDirectProperty(root, "ДоговорКонтрагента");
        var name = NormalizeText(GetOneCReferenceValue(property, "Наименование"));
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var number = NormalizeText(GetOneCReferenceValue(property, "Номер"));
        var date = TryParseDate(GetOneCReferenceValue(property, "Дата"));
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        return date.HasValue ? $"{number} от {date.Value:dd.MM.yyyy}" : number;
    }

    private static XElement? GetOneCObjectLink(XElement root)
        => root.Elements().FirstOrDefault(element => element.Name.LocalName == "Ссылка");

    private static XElement? GetOneCDirectProperty(XElement root, string name)
        => GetOneCProperty(root, name);

    private static XElement? GetOneCProperty(XElement? parent, string name)
        => parent?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Свойство"
                && string.Equals(element.Attribute("Имя")?.Value, name, StringComparison.Ordinal));

    private static string? GetOneCDirectValue(XElement root, string name)
        => GetOneCPropertyValue(GetOneCDirectProperty(root, name));

    private static string? GetOneCPropertyValue(XElement? parent, string name)
        => GetOneCPropertyValue(GetOneCProperty(parent, name));

    private static string? GetOneCPropertyValue(XElement? property)
    {
        if (property is null)
        {
            return null;
        }

        var value = property.Elements().FirstOrDefault(element => element.Name.LocalName == "Значение")?.Value;
        return NormalizeText(value);
    }

    private static string? GetOneCReferenceValue(XElement? property, params string[] names)
    {
        var link = property?.Elements().FirstOrDefault(element => element.Name.LocalName == "Ссылка");
        if (link is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var value = GetOneCPropertyValue(link, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static DateTime? GetOneCDate(XElement? parent, string name)
        => TryParseDate(GetOneCPropertyValue(parent, name));

    private static bool IsOneCMarkedForDeletion(XElement root)
        => string.Equals(GetOneCDirectValue(root, "ПометкаУдаления"), "true", StringComparison.OrdinalIgnoreCase);

    private static int? TryParseOneCRowNumber(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static void MergeParsedBundle(ParsedImportBundle target, ParsedImportBundle source)
    {
        target.Files.AddRange(source.Files);
        target.CounterpartyDrafts.AddRange(source.CounterpartyDrafts);
        target.InvoiceDrafts.AddRange(source.InvoiceDrafts);
        target.ActDrafts.AddRange(source.ActDrafts);
        target.Messages.AddRange(source.Messages);
        foreach (var (kind, count) in source.OneCNonImportObjectCounts)
        {
            target.OneCNonImportObjectCounts[kind] = target.OneCNonImportObjectCounts.TryGetValue(kind, out var currentCount)
                ? currentCount + count
                : count;
        }
    }

    private static void IncrementOneCNonImportObjectCount(ParsedImportBundle bundle, string? discriminator)
    {
        var kind = NormalizeOneCObjectKind(discriminator);
        bundle.OneCNonImportObjectCounts[kind] = bundle.OneCNonImportObjectCounts.TryGetValue(kind, out var count)
            ? count + 1
            : 1;
    }

    private static string NormalizeOneCObjectKind(string? discriminator)
    {
        var kind = NormalizeText(discriminator) ?? "Неизвестный тип";
        foreach (var prefix in new[] { "СправочникСсылка.", "ДокументСсылка.", "СправочникОбъект.", "ДокументОбъект." })
        {
            if (kind.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind[prefix.Length..];
            }
        }

        return kind;
    }

    private static string FormatOneCDate(string value)
        => TryParseDate(value) is { } date
            ? $" от {date:dd.MM.yyyy HH:mm}"
            : string.Empty;

    private static string FormatOneCPeriod(string periodStart, string periodEnd)
    {
        var start = TryParseDate(periodStart);
        var end = TryParseDate(periodEnd);
        return start.HasValue || end.HasValue
            ? $", период {FormatDate(start)}-{FormatDate(end)}"
            : string.Empty;
    }

    private sealed record CounterpartyWithInfo(CounterpartyDraft Draft, OneCCounterpartyInfo Info);

    private sealed record OneCCounterpartyInfo(
        string? ExternalId,
        string Name,
        string? Inn,
        string? Kpp,
        string? Ogrn,
        string? Address,
        string? OrganizationForm,
        string? OrganizationFormFull,
        string? BankBik,
        string? BankAccount);

    private sealed record OneCBankInfo(string? Bik, string? Name, string? CorrAccount, string? City);

    private sealed record OneCBankAccountInfo(
        string? OwnerExternalId,
        string? OwnerInn,
        string AccountNumber,
        string? BankBik,
        string? BankName,
        string? AccountType);

    private sealed record OneCInvoiceReference(string ExternalId, string Number, DateTime? Date, PaymentType Type);
}
