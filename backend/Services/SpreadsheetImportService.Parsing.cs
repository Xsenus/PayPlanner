using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;

namespace PayPlanner.Api.Services;

public sealed partial class SpreadsheetImportService
{
    private ParsedImportBundle ParseFiles(IReadOnlyCollection<SpreadsheetImportUploadFile> files)
    {
        var bundle = new ParsedImportBundle();

        foreach (var file in files.Where(file => file.Content.Length > 0))
        {
            var parsedFile = ParseFile(file);
            bundle.Files.AddRange(parsedFile.Files);
            bundle.CounterpartyDrafts.AddRange(parsedFile.CounterpartyDrafts);
            bundle.InvoiceDrafts.AddRange(parsedFile.InvoiceDrafts);
            bundle.ActDrafts.AddRange(parsedFile.ActDrafts);
            bundle.Messages.AddRange(parsedFile.Messages);
        }

        return bundle;
    }

    private ParsedImportBundle ParseFile(SpreadsheetImportUploadFile file)
    {
        var bundle = new ParsedImportBundle();
        using var stream = new MemoryStream(file.Content, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var fileHash = ComputeFileHash(file.Content);

        do
        {
            var sheet = ReadSheet(reader);
            if (sheet.Rows.Count == 0)
            {
                continue;
            }

            var headerRowIndex = FindHeaderRowIndex(sheet.Rows);
            if (headerRowIndex < 0)
            {
                bundle.Messages.Add($"Файл {file.FileName}: не удалось определить строку заголовков.");
                continue;
            }

            var headers = sheet.Rows[headerRowIndex];
            var kind = DetectSheetKind(headers);
            var dataRows = sheet.Rows
                .Skip(headerRowIndex + 1)
                .Select((row, index) => new SheetRow(headerRowIndex + index + 2, row))
                .Where(row => row.Values.Any(value => !string.IsNullOrWhiteSpace(ToCellString(value))))
                .ToList();

            bundle.Files.Add(new ParsedFile
            {
                FileName = file.FileName,
                FileHash = fileHash,
                DetectedKind = kind.Key,
                SheetName = sheet.Name,
                RowCount = dataRows.Count,
            });

            switch (kind.Key)
            {
                case "invoices":
                    bundle.InvoiceDrafts.AddRange(ParseInvoices(file.FileName, fileHash, sheet.Name, headers, dataRows));
                    break;
                case "acts":
                    bundle.ActDrafts.AddRange(ParseActs(file.FileName, fileHash, sheet.Name, headers, dataRows));
                    break;
                case "counterparties":
                    bundle.CounterpartyDrafts.AddRange(ParseCounterparties(file.FileName, fileHash, sheet.Name, headers, dataRows));
                    break;
                default:
                    bundle.Messages.Add(
                        $"Файл {file.FileName}, лист {sheet.Name}: структура не распознана. Поддерживаются счета, акты и списки контрагентов.");
                    break;
            }
        }
        while (reader.NextResult());

        return bundle;
    }

    private static SheetData ReadSheet(IExcelDataReader reader)
    {
        var rows = new List<object?[]>();
        var fieldCount = reader.FieldCount;

        while (reader.Read())
        {
            var values = new object?[fieldCount];
            for (var index = 0; index < fieldCount; index++)
            {
                values[index] = reader.GetValue(index);
            }

            rows.Add(values);
        }

        return new SheetData(reader.Name, rows);
    }

    private static int FindHeaderRowIndex(IReadOnlyList<object?[]> rows)
    {
        for (var index = 0; index < Math.Min(rows.Count, 5); index++)
        {
            var filled = rows[index].Count(value => !string.IsNullOrWhiteSpace(ToCellString(value)));
            if (filled >= 2)
            {
                return index;
            }
        }

        return -1;
    }

    private static (string Key, string Label) DetectSheetKind(object?[] headers)
    {
        var normalizedHeaders = headers.Select(value => NormalizeHeader(ToCellString(value))).ToHashSet(StringComparer.Ordinal);

        if (normalizedHeaders.Contains("датаакта") && normalizedHeaders.Contains("номеракта"))
        {
            return ("acts", "Акты");
        }

        if (normalizedHeaders.Contains("датасчета") && normalizedHeaders.Contains("номерсчета"))
        {
            return ("invoices", "Счета");
        }

        if (normalizedHeaders.Contains("название")
            && (normalizedHeaders.Contains("статьяприходов") || normalizedHeaders.Contains("статьярасходов")))
        {
            return ("counterparties", "Контрагенты");
        }

        return ("unknown", "Неизвестный файл");
    }

    private static List<CounterpartyDraft> ParseCounterparties(
        string fileName,
        string fileHash,
        string sheetName,
        object?[] headers,
        IReadOnlyCollection<SheetRow> rows)
    {
        var columns = BuildHeaderIndex(headers);
        var nameIndex = FindColumn(columns, "название");
        var innIndex = FindColumn(columns, "инн");
        var contactIndex = FindColumn(columns, "контакт");
        var phoneIndex = FindColumn(columns, "телефон");
        var emailIndex = FindColumn(columns, "email");
        var contractIndex = FindColumn(columns, "договор");
        var delayIndex = FindColumn(columns, "отсрочкадн");
        var incomeArticleIndex = FindColumn(columns, "статьяприходов");
        var expenseArticleIndex = FindColumn(columns, "статьярасходов");
        var responsibleIndex = FindColumn(columns, "ответственный");
        var autoInvoiceIndex = FindColumn(columns, "автосчет");

        var drafts = new List<CounterpartyDraft>();
        foreach (var row in rows)
        {
            drafts.Add(new CounterpartyDraft
            {
                PreviewId = BuildPreviewId(fileHash, "counterparties", row.RowNumber),
                FileName = fileName,
                FileHash = fileHash,
                SheetName = sheetName,
                RowNumber = row.RowNumber,
                Name = GetCell(row.Values, nameIndex) ?? string.Empty,
                Inn = NormalizeInn(GetCell(row.Values, innIndex)),
                ContactName = GetCell(row.Values, contactIndex),
                Phone = NormalizePhone(GetCell(row.Values, phoneIndex)),
                Email = NormalizeEmail(GetCell(row.Values, emailIndex)),
                ContractName = GetCell(row.Values, contractIndex),
                DelayDays = TryParseInt(GetValue(row.Values, delayIndex)),
                Article = GetCell(row.Values, incomeArticleIndex) ?? GetCell(row.Values, expenseArticleIndex),
                ResponsibleName = GetCell(row.Values, responsibleIndex),
                AutoInvoiceResponsibleName = GetCell(row.Values, autoInvoiceIndex),
                Derived = false,
            });
        }

        return drafts;
    }

    private static List<InvoiceDraft> ParseInvoices(
        string fileName,
        string fileHash,
        string sheetName,
        object?[] headers,
        IReadOnlyCollection<SheetRow> rows)
    {
        var columns = BuildHeaderIndex(headers);
        var dateIndex = FindColumn(columns, "датасчета");
        var numberIndex = FindColumn(columns, "номерсчета");
        var amountIndex = FindColumn(columns, "сумма");
        var dueDateIndex = FindColumn(columns, "оплатитьдо");
        var counterpartyIndex = FindColumn(columns, "контрагент");
        var statusIndex = FindColumn(columns, "статус");
        var paidDateIndex = FindColumn(columns, "датаоплаты");
        var actIndex = FindColumn(columns, "акт");
        var nomenclatureIndex = FindColumn(columns, "номенклатурнаячасть");
        var createdByIndex = FindColumn(columns, "создал");
        var responsibleIndex = FindColumn(columns, "ответственный");

        var drafts = new List<InvoiceDraft>();
        foreach (var row in rows)
        {
            var items = ParseLineItems(GetCell(row.Values, nomenclatureIndex), TryParseDecimal(GetValue(row.Values, amountIndex)) ?? 0m);
            drafts.Add(new InvoiceDraft
            {
                PreviewId = BuildPreviewId(fileHash, "invoices", row.RowNumber),
                FileName = fileName,
                FileHash = fileHash,
                SheetName = sheetName,
                RowNumber = row.RowNumber,
                Number = GetCell(row.Values, numberIndex),
                Date = TryParseDate(GetValue(row.Values, dateIndex)),
                DueDate = TryParseDate(GetValue(row.Values, dueDateIndex)),
                Amount = TryParseDecimal(GetValue(row.Values, amountIndex)),
                CounterpartyName = GetCell(row.Values, counterpartyIndex),
                StatusText = GetCell(row.Values, statusIndex),
                PaidDate = TryParseDate(GetValue(row.Values, paidDateIndex)),
                ActReference = GetCell(row.Values, actIndex),
                Nomenclature = GetCell(row.Values, nomenclatureIndex),
                CreatedByName = GetCell(row.Values, createdByIndex),
                ResponsibleName = GetCell(row.Values, responsibleIndex),
                ParsedLineItems = items.Items,
                ParseWarnings = items.Warnings,
            });
        }

        return drafts;
    }

    private static List<ActDraft> ParseActs(
        string fileName,
        string fileHash,
        string sheetName,
        object?[] headers,
        IReadOnlyCollection<SheetRow> rows)
    {
        var columns = BuildHeaderIndex(headers);
        var dateIndex = FindColumn(columns, "датаакта");
        var numberIndex = FindColumn(columns, "номеракта");
        var amountIndex = FindColumn(columns, "сумма");
        var counterpartyIndex = FindColumn(columns, "контрагент");
        var innIndex = FindColumn(columns, "иннконтрагента");
        var statusIndex = FindColumn(columns, "статус");
        var invoiceNumberIndex = FindColumn(columns, "счетномер");
        var nomenclatureIndex = FindColumn(columns, "номенклатурнаячасть");
        var paymentDateIndex = FindColumn(columns, "датаплатежа");
        var responsibleIndex = FindColumn(columns, "actresponsible");

        var drafts = new List<ActDraft>();
        foreach (var row in rows)
        {
            var items = ParseLineItems(GetCell(row.Values, nomenclatureIndex), TryParseDecimal(GetValue(row.Values, amountIndex)) ?? 0m);
            drafts.Add(new ActDraft
            {
                PreviewId = BuildPreviewId(fileHash, "acts", row.RowNumber),
                FileName = fileName,
                FileHash = fileHash,
                SheetName = sheetName,
                RowNumber = row.RowNumber,
                Number = GetCell(row.Values, numberIndex),
                Date = TryParseDate(GetValue(row.Values, dateIndex)),
                Amount = TryParseDecimal(GetValue(row.Values, amountIndex)),
                CounterpartyName = GetCell(row.Values, counterpartyIndex),
                CounterpartyInn = NormalizeInn(GetCell(row.Values, innIndex)),
                StatusText = GetCell(row.Values, statusIndex),
                InvoiceNumber = ExtractNumberReference(GetCell(row.Values, invoiceNumberIndex)),
                PaymentDate = TryParseDate(GetValue(row.Values, paymentDateIndex)),
                Nomenclature = GetCell(row.Values, nomenclatureIndex),
                ResponsibleName = GetCell(row.Values, responsibleIndex),
                ParsedLineItems = items.Items,
                ParseWarnings = items.Warnings,
            });
        }

        return drafts;
    }

    private static Dictionary<string, int> BuildHeaderIndex(object?[] headers)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
        {
            var normalized = NormalizeHeader(ToCellString(headers[index]));
            if (!string.IsNullOrWhiteSpace(normalized) && !result.ContainsKey(normalized))
            {
                result[normalized] = index;
            }
        }

        return result;
    }

    private static int? FindColumn(IReadOnlyDictionary<string, int> columns, params string[] aliases)
    {
        foreach (var alias in aliases)
        {
            if (columns.TryGetValue(alias, out var index))
            {
                return index;
            }
        }

        return null;
    }

    private static object? GetValue(IReadOnlyList<object?> values, int? index)
        => index.HasValue && index.Value >= 0 && index.Value < values.Count
            ? values[index.Value]
            : null;

    private static string? GetCell(IReadOnlyList<object?> values, int? index)
        => NormalizeText(ToCellString(GetValue(values, index)));

    private static string NormalizeHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant().Replace('ё', 'е'))
        {
            if (character == '№')
            {
                builder.Append("номер");
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string ToCellString(object? value)
    {
        return value switch
        {
            null => string.Empty,
            DateTime dateTime => dateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            double doubleValue when doubleValue % 1 == 0 => doubleValue.ToString("0", CultureInfo.InvariantCulture),
            double doubleValue => doubleValue.ToString("0.####", CultureInfo.InvariantCulture),
            decimal decimalValue => decimalValue.ToString("0.####", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static DateTime? TryParseDate(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is DateTime dateTime)
        {
            return dateTime.Date;
        }

        if (value is double excelDate)
        {
            try
            {
                return DateTime.FromOADate(excelDate).Date;
            }
            catch
            {
                return null;
            }
        }

        var stringValue = NormalizeText(ToCellString(value));
        if (stringValue is null)
        {
            return null;
        }

        if (DateTime.TryParse(stringValue, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed.Date;
        }

        return DateTime.TryParse(stringValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed)
            ? parsed.Date
            : null;
    }

    private static decimal? TryParseDecimal(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is decimal decimalValue)
        {
            return decimalValue;
        }

        if (value is double doubleValue)
        {
            return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        }

        if (value is int intValue)
        {
            return intValue;
        }

        var stringValue = NormalizeText(ToCellString(value));
        if (stringValue is null)
        {
            return null;
        }

        stringValue = stringValue
            .Replace("р.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("руб.", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty)
            .Replace(",", ".", StringComparison.Ordinal);

        return decimal.TryParse(stringValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static int? TryParseInt(object? value)
    {
        var decimalValue = TryParseDecimal(value);
        if (!decimalValue.HasValue)
        {
            return null;
        }

        return decimal.ToInt32(decimal.Truncate(decimalValue.Value));
    }

    private static string ComputeFileHash(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildPreviewId(string fileHash, string sectionKey, int rowNumber)
        => $"{fileHash}:{sectionKey}:{rowNumber}";
}
