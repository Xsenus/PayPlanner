using System.IO.Compression;
using System.Security;
using System.Text;

namespace PayPlanner.Api.Services.Documents;

internal sealed record TaskPlanningWordItem(
    string ClientName,
    string CaseTitle,
    string Kind,
    string Title,
    string? Description,
    string DueAt,
    string Status,
    bool IsOverdue,
    string Assignee,
    string? Comment);

internal static class TaskPlanningWordDocumentBuilder
{
    internal const int ColumnCount = 7;
    internal const int PageWidthTwips = 16_838;
    internal const int PageHeightTwips = 11_906;
    internal const int PageMarginTwips = 680;
    internal const int TableWidthTwips = PageWidthTwips - (2 * PageMarginTwips);
    internal static readonly int[] ColumnWidths = [774, 1_548, 4_334, 1_857, 1_857, 2_476, 2_632];

    private const string FontName = "DejaVu Sans Condensed";

    internal static byte[] Build(
        IReadOnlyList<TaskPlanningWordItem> items,
        string generatedAt,
        string filterText)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(filterText);

        var packageBytes = HtmlWordDocumentBuilder.Build("<html><body><p></p></body></html>");
        using var packageStream = new MemoryStream();
        packageStream.Write(packageBytes);
        packageStream.Position = 0;

        using (var package = new ZipArchive(packageStream, ZipArchiveMode.Update, leaveOpen: true))
        {
            ReplaceTextEntry(package, "word/document.xml", BuildDocumentXml(items, generatedAt, filterText));
            ReplaceTextEntry(package, "word/_rels/document.xml.rels", DocumentRelationshipsXml);
            package.GetEntry("word/afchunk.html")?.Delete();
        }

        return packageStream.ToArray();
    }

    private static string BuildDocumentXml(
        IReadOnlyList<TaskPlanningWordItem> items,
        string generatedAt,
        string filterText)
    {
        var body = new StringBuilder();
        body.Append(Paragraph("ПЛАНИНГ ПО КЛИЕНТАМ", 20, bold: true, alignment: "center", after: 100));
        body.Append(Paragraph(
            $"Сформировано: {generatedAt}. Фильтр: {filterText}. Задач: {items.Count}.",
            10,
            color: "475569",
            alignment: "center",
            after: 200));
        body.Append(BuildTable(items));

        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
{{body}}
<w:sectPr><w:pgSz w:w="{{PageWidthTwips}}" w:h="{{PageHeightTwips}}" w:orient="landscape"/><w:pgMar w:top="{{PageMarginTwips}}" w:right="{{PageMarginTwips}}" w:bottom="{{PageMarginTwips}}" w:left="{{PageMarginTwips}}" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr>
</w:body></w:document>
""";
    }

    private static string BuildTable(IReadOnlyList<TaskPlanningWordItem> items)
    {
        var rows = new StringBuilder();
        if (items.Count == 0)
        {
            rows.Append(Row(
                Cell(
                    TableWidthTwips,
                    Paragraph("По выбранным фильтрам задач нет.", 11, color: "64748B", alignment: "center"),
                    gridSpan: ColumnCount,
                    verticalAlignment: "center"),
                cantSplit: true));
        }
        else
        {
            foreach (var clientGroup in items
                .GroupBy(item => item.ClientName)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                rows.Append(Row(
                    Cell(
                        TableWidthTwips,
                        Paragraph($"КЛИЕНТ | {TextOrDash(clientGroup.Key)}", 12, bold: true, keepNext: true),
                        fill: "DBEAFE",
                        gridSpan: ColumnCount),
                    cantSplit: true));

                foreach (var caseGroup in clientGroup
                    .GroupBy(item => item.CaseTitle)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
                {
                    rows.Append(Row(
                        Cell(
                            TableWidthTwips,
                            Paragraph($"ДЕЛО | {TextOrDash(caseGroup.Key)}", 11, bold: true, keepNext: true),
                            fill: "F1F5F9",
                            gridSpan: ColumnCount),
                        cantSplit: true));
                    rows.Append(BuildHeaderRow());

                    var rowNumber = 1;
                    foreach (var item in caseGroup)
                    {
                        var description = new StringBuilder(Paragraph(TextOrDash(item.Title), 10));
                        if (!string.IsNullOrWhiteSpace(item.Description) &&
                            !string.Equals(item.Description.Trim(), item.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            description.Append(Paragraph(item.Description.Trim(), 9, color: "64748B", before: 40));
                        }

                        var status = new StringBuilder(Paragraph(TextOrDash(item.Status), 10));
                        if (item.IsOverdue)
                        {
                            status.Append(Paragraph("Просрочена", 9, bold: true, color: "DC2626", before: 40));
                        }

                        rows.Append(Row(string.Concat(
                            Cell(ColumnWidths[0], Paragraph(rowNumber.ToString(), 10, alignment: "center"), verticalAlignment: "center"),
                            Cell(ColumnWidths[1], Paragraph(TextOrDash(item.Kind), 10)),
                            Cell(ColumnWidths[2], description.ToString()),
                            Cell(ColumnWidths[3], Paragraph(TextOrDash(item.DueAt), 10)),
                            Cell(ColumnWidths[4], status.ToString()),
                            Cell(ColumnWidths[5], Paragraph(TextOrDash(item.Assignee), 10)),
                            Cell(ColumnWidths[6], Paragraph(TextOrDash(item.Comment), 10))),
                            cantSplit: true));
                        rowNumber++;
                    }
                }
            }
        }

        var grid = string.Concat(ColumnWidths.Select(width => $"<w:gridCol w:w=\"{width}\"/>"));
        return $"<w:tbl><w:tblPr><w:tblW w:w=\"{TableWidthTwips}\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"94A3B8\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"94A3B8\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"94A3B8\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"94A3B8\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"94A3B8\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"94A3B8\"/></w:tblBorders><w:tblCellMar><w:top w:w=\"85\" w:type=\"dxa\"/><w:left w:w=\"100\" w:type=\"dxa\"/><w:bottom w:w=\"85\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>{grid}</w:tblGrid>{rows}</w:tbl>";
    }

    private static string BuildHeaderRow()
        => Row(string.Concat(
                HeaderCell(0, "№ П/П"),
                HeaderCell(1, "ЗАДАЧА"),
                HeaderCell(2, "ОПИСАНИЕ ЗАДАЧИ"),
                HeaderCell(3, "СРОК"),
                HeaderCell(4, "СТАТУС"),
                HeaderCell(5, "ИСПОЛНИТЕЛЬ"),
                HeaderCell(6, "КОММЕНТАРИЙ")),
            cantSplit: true,
            isHeader: true);

    private static string HeaderCell(int columnIndex, string text)
        => Cell(
            ColumnWidths[columnIndex],
            Paragraph(text, 9, bold: true, alignment: "center", keepNext: true),
            fill: "E2E8F0",
            verticalAlignment: "center");

    private static string Row(string cells, bool cantSplit = false, bool isHeader = false)
    {
        var properties = (cantSplit ? "<w:cantSplit/>" : string.Empty)
            + (isHeader ? "<w:tblHeader/>" : string.Empty);
        return $"<w:tr><w:trPr>{properties}</w:trPr>{cells}</w:tr>";
    }

    private static string Cell(
        int width,
        string content,
        string? fill = null,
        int? gridSpan = null,
        string verticalAlignment = "top")
    {
        var shading = fill is null ? string.Empty : $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{fill}\"/>";
        var span = gridSpan.HasValue ? $"<w:gridSpan w:val=\"{gridSpan.Value}\"/>" : string.Empty;
        return $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>{span}{shading}<w:vAlign w:val=\"{verticalAlignment}\"/></w:tcPr>{content}</w:tc>";
    }

    private static string Paragraph(
        string value,
        int size,
        bool bold = false,
        string color = "111827",
        string? alignment = null,
        int before = 0,
        int after = 0,
        bool keepNext = false)
    {
        var paragraphAlignment = alignment is null ? string.Empty : $"<w:jc w:val=\"{alignment}\"/>";
        var keep = keepNext ? "<w:keepNext/>" : string.Empty;
        var runs = string.Join(
            "<w:r><w:br/></w:r>",
            NormalizeLines(value).Select(line => Run(line, size, bold, color)));
        return $"<w:p><w:pPr><w:spacing w:before=\"{before}\" w:after=\"{after}\"/>{keep}{paragraphAlignment}</w:pPr>{runs}</w:p>";
    }

    private static string Run(string value, int size, bool bold, string color)
    {
        var escaped = SecurityElement.Escape(value) ?? string.Empty;
        var boldXml = bold ? "<w:b/>" : string.Empty;
        return $"<w:r><w:rPr><w:rFonts w:ascii=\"{FontName}\" w:hAnsi=\"{FontName}\" w:cs=\"{FontName}\"/><w:color w:val=\"{color}\"/>{boldXml}<w:sz w:val=\"{size * 2}\"/><w:szCs w:val=\"{size * 2}\"/></w:rPr><w:t xml:space=\"preserve\">{escaped}</w:t></w:r>";
    }

    private static IEnumerable<string> NormalizeLines(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string TextOrDash(string? value)
        => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static void ReplaceTextEntry(ZipArchive package, string name, string value)
    {
        package.GetEntry(name)?.Delete();
        var entry = package.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(value);
    }

    private const string DocumentRelationshipsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rIdFonts" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/fontTable" Target="fontTable.xml"/>
  <Relationship Id="rIdSettings" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
</Relationships>
""";
}
