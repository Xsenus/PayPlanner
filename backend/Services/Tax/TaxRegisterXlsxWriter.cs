using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Tax;

/// <summary>
/// Минимальная выгрузка книги в .xlsx без внешних библиотек: один лист, текст как inline-строки (формулы не исполняются),
/// суммы — числовые ячейки. Это рабочая таблица регистра, а не официальная форма книги.
/// </summary>
public static class TaxRegisterXlsxWriter
{
    public static byte[] Build(TaxRegisterBook book)
    {
        var rows = new List<object?[]>
        {
            new object?[] { $"Книга доходов и расходов, {book.Year} год" },
            new object?[] { "№", "Дата операции", "Графа", "Тип", "Сумма со знаком", "Первичный документ", "Описание", "Категория" },
        };
        var number = 0;
        foreach (var entry in book.Entries)
        {
            number++;
            rows.Add([
                (double)number,
                entry.OperationDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                entry.Direction == TaxRegisterTypes.Income ? "Доход" : "Расход",
                entry.Type == TaxRegisterTypes.Correction ? "Корректировка" : "Обычная",
                (double)(entry.Sign * entry.Amount),
                entry.DocumentRef,
                entry.Description,
                entry.Category,
            ]);
        }

        rows.Add([null, null, null, "Итого доходов", (double)book.Totals.Income]);
        rows.Add([null, null, null, "Итого расходов", (double)book.Totals.Expense]);
        rows.Add([null, null, null, "Разность", (double)book.Totals.TaxBase]);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Книга\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml", BuildSheet(rows));
        }

        return stream.ToArray();
    }

    private static string BuildSheet(List<object?[]> rows)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append("<row r=\"").Append(r + 1).Append("\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c];
                if (value is null)
                {
                    continue;
                }

                var reference = (char)('A' + c) + (r + 1).ToString(CultureInfo.InvariantCulture);
                if (value is double number)
                {
                    sb.Append("<c r=\"").Append(reference).Append("\"><v>").Append(number.ToString("0.##", CultureInfo.InvariantCulture)).Append("</v></c>");
                }
                else
                {
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(SecurityElement.Escape(Clean(value.ToString() ?? string.Empty))).Append("</t></is></c>");
                }
            }

            sb.Append("</row>");
        }

        return sb.Append("</sheetData></worksheet>").ToString();
    }

    /// <summary>Убирает управляющие символы, недопустимые в XML 1.0.</summary>
    private static string Clean(string text)
        => new(text.Where(ch => ch is '\t' or '\n' or '\r' || (ch >= ' ' && ch != '￾' && ch != '￿' && !char.IsSurrogate(ch))).ToArray());

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
