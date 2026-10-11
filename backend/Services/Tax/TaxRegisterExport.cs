using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Tax;

/// <summary>
/// Потоковая выгрузка книги: записи читаются партиями и сразу пишутся в выходной поток, поэтому память не растёт
/// с размером года. Итоги считаются по тем же строкам, что вошли в файл. Это рабочая таблица регистра,
/// а не утверждённая бухгалтерская форма.
/// </summary>
public static class TaxRegisterExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] Header = ["№", "Дата операции", "Графа", "Тип", "Сумма со знаком", "Первичный документ", "Описание", "Категория"];

    public static async Task WriteCsvAsync(Stream output, IAsyncEnumerable<TaxRegisterEntry> entries, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(true), 16 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteAsync("№;Дата операции;Направление;Тип;Сумма со знаком;Первичный документ;Описание;Категория\r\n");
        decimal income = 0m, expense = 0m;
        var number = 0;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            number++;
            Accumulate(entry, ref income, ref expense);
            await writer.WriteAsync(string.Join(';',
                number.ToString(Inv),
                entry.OperationDate.ToString("dd.MM.yyyy", Inv),
                DirectionLabel(entry),
                TypeLabel(entry),
                SignedAmount(entry).ToString("0.00", Inv),
                CsvCell(entry.DocumentRef),
                CsvCell(entry.Description),
                CsvCell(entry.Category)) + "\r\n");
        }

        await writer.WriteAsync($";;;Итого доходов;{income.ToString("0.00", Inv)};;;\r\n");
        await writer.WriteAsync($";;;Итого расходов;{expense.ToString("0.00", Inv)};;;\r\n");
        await writer.WriteAsync($";;;Разность;{(income - expense).ToString("0.00", Inv)};;;\r\n");
        await writer.FlushAsync(ct);
    }

    public static async Task WriteXlsxAsync(Stream output, int year, IAsyncEnumerable<TaxRegisterEntry> entries, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Книга\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");

        var sheet = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
        await using var stream = sheet.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 16 * 1024);
        await writer.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        var row = 1;
        await WriteRowAsync(writer, row++, [$"Книга доходов и расходов, {year} год"]);
        await WriteRowAsync(writer, row++, Header);
        decimal income = 0m, expense = 0m;
        var number = 0;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            number++;
            Accumulate(entry, ref income, ref expense);
            await WriteRowAsync(writer, row++, [
                (double)number,
                entry.OperationDate.ToString("dd.MM.yyyy", Inv),
                DirectionLabel(entry),
                TypeLabel(entry),
                (double)SignedAmount(entry),
                entry.DocumentRef,
                entry.Description,
                entry.Category,
            ]);
        }

        await WriteRowAsync(writer, row++, [null, null, null, "Итого доходов", (double)income]);
        await WriteRowAsync(writer, row++, [null, null, null, "Итого расходов", (double)expense]);
        await WriteRowAsync(writer, row, [null, null, null, "Разность", (double)(income - expense)]);
        await writer.WriteAsync("</sheetData></worksheet>");
        await writer.FlushAsync(ct);
    }

    private static void Accumulate(TaxRegisterEntry entry, ref decimal income, ref decimal expense)
    {
        if (entry.Direction == TaxRegisterTypes.Income) income += SignedAmount(entry);
        else if (entry.Direction == TaxRegisterTypes.Expense) expense += SignedAmount(entry);
    }

    private static decimal SignedAmount(TaxRegisterEntry entry) => entry.Sign * entry.Amount;
    private static string DirectionLabel(TaxRegisterEntry entry) => entry.Direction == TaxRegisterTypes.Income ? "Доход" : "Расход";
    private static string TypeLabel(TaxRegisterEntry entry) => entry.Type == TaxRegisterTypes.Correction ? "Корректировка" : "Обычная";

    private static async Task WriteRowAsync(StreamWriter writer, int row, object?[] cells)
    {
        var sb = new StringBuilder();
        sb.Append("<row r=\"").Append(row).Append("\">");
        for (var c = 0; c < cells.Length; c++)
        {
            var value = cells[c];
            if (value is null) continue;
            var reference = (char)('A' + c) + row.ToString(Inv);
            if (value is double number)
            {
                sb.Append("<c r=\"").Append(reference).Append("\"><v>").Append(number.ToString("0.##", Inv)).Append("</v></c>");
            }
            else
            {
                sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                  .Append(SecurityElement.Escape(CleanXml(value.ToString() ?? string.Empty))).Append("</t></is></c>");
            }
        }

        sb.Append("</row>");
        await writer.WriteAsync(sb.ToString());
    }

    /// <summary>Убирает символы, недопустимые в XML 1.0; пары суррогатов (эмодзи) сохраняются.</summary>
    private static string CleanXml(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Append(ch).Append(text[++i]);
            }
            else if (System.Xml.XmlConvert.IsXmlChar(ch))
            {
                result.Append(ch);
            }
        }

        return result.ToString();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    internal static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var cell = value.Replace("\r", " ").Replace("\n", " ");
        // Защита от формул Excel: ячейка не должна начинаться с управляющего символа формулы.
        if (cell[0] is '=' or '+' or '-' or '@' or '\t') cell = "'" + cell;
        return cell.Contains(';') || cell.Contains('"') ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
    }
}
