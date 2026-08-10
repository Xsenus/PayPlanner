using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;

namespace PayPlanner.Api.Services.Documents;

internal sealed record CashOrderWordData(
    string Organization,
    string Number,
    string Date,
    string Payer,
    string Basis,
    string Appendix,
    string AmountRubKop,
    string Rubles,
    string Kopecks,
    string AmountWords,
    string Day,
    string Month,
    string Year,
    string Signer,
    string CancelNotice);

internal static class CashOrderOpenXmlDocumentBuilder
{
    private const string FontName = "DejaVu Sans Condensed";

    public static byte[] Build(CashOrderWordData data)
    {
        var packageBytes = HtmlWordDocumentBuilder.Build("<html><body><p></p></body></html>");
        using var packageStream = new MemoryStream();
        packageStream.Write(packageBytes);
        packageStream.Position = 0;

        using (var package = new ZipArchive(packageStream, ZipArchiveMode.Update, leaveOpen: true))
        {
            package.GetEntry("word/document.xml")?.Delete();
            var entry = package.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildDocumentXml(data));
        }

        return packageStream.ToArray();
    }

    private static string BuildDocumentXml(CashOrderWordData data)
    {
        var body = string.Concat(
            BuildMainOrder(data),
            BuildCodeBox(),
            BuildCutLine(),
            BuildReceipt(data));

        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
{{body}}
<w:p><w:pPr><w:spacing w:before="0" w:after="0" w:line="1" w:lineRule="exact"/></w:pPr></w:p>
<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="0" w:right="0" w:bottom="0" w:left="0" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr>
</w:body></w:document>
""";
    }

    private static string BuildMainOrder(CashOrderWordData data)
    {
        const double width = 283.5;
        var rows = new List<string>
        {
            Row(33, Cell(width, Paragraph(
                "Унифицированная форма № КО-1\nУтверждена постановлением Госкомстата\nРоссии от 18.08.98 № 88",
                6,
                left: 157,
                line: 7))),
            Row(53, Cell(width, Table(
                198,
                [198],
                [
                    Row(20, Cell(198, EmptyParagraph(1))),
                    Row(8, Cell(198, Paragraph(data.Organization, 6, left: 3, line: 7), BottomBorder(12))),
                    Row(5, Cell(198, Paragraph("(организация)", 5, align: "center", line: 5))),
                    Row(12, Cell(198, EmptyParagraph(1), BottomBorder(12))),
                    Row(8, Cell(198, Paragraph("(структурное подразделение)", 5, align: "center", line: 5)))
                ]), rightMargin: 85.5)),
            Row(46.5, Cell(width, BuildTitleAndMeta(data))),
            Row(54.7, Cell(width, BuildLedger(data.AmountRubKop))),
            Row(10.9, Cell(width, Field("Принято от", data.Payer, 39, width - 39))),
            Row(10.5, Cell(width, Field("Основание:", data.Basis, 39, width - 39))),
            Row(14, Cell(width, Table(width, [width], [Row(14, Cell(width, EmptyParagraph(1), BottomBorder()))]))),
            Row(28.84, Cell(width, BuildAmountRow(data.AmountWords, data.Kopecks))),
            Row(11, Cell(width, Field("В том числе", "Без налога (НДС) 0,00 руб.", 42, width - 42))),
            Row(11, Cell(width, Field("Приложение", data.Appendix, 39, width - 39))),
            Row(16.7, Cell(width, Signature("Главный бухгалтер", data.Signer, width))),
            Row(18, Cell(width, Signature("Получил кассир", data.Signer, width)))
        };

        return Table(width, [width], rows, x: 45, y: 77);
    }

    private static string BuildTitleAndMeta(CashOrderWordData data)
    {
        const double left = 195.5;
        const double right = 88;
        var title = Cell(left, Paragraph("ПРИХОДНЫЙ КАССОВЫЙ ОРДЕР", 9, bold: true, align: "center", before: 19.5, line: 9.5));
        var meta = Table(
            right,
            [42.5, 45.5],
            [
                Row(20, Cell(42.5, Paragraph("Номер\nдокумента", 6, align: "center", line: 6), BoxBorder()) + Cell(45.5, Paragraph("Дата\nсоставления", 6, align: "center", line: 6), BoxBorder())),
                Row(20, Cell(42.5, Paragraph(data.Number, 7, bold: true, align: "center", line: 7), BoxBorder()) + Cell(45.5, Paragraph(data.Date, 7, bold: true, align: "center", line: 7), BoxBorder()))
            ]);
        return Table(left + right, [left, right], [Row(46.5, title + Cell(right, meta))]);
    }

    private static string BuildLedger(string amount)
    {
        double[] widths = [28.5, 37.5, 52.5, 63, 39, 63];
        var rows = new List<string>
        {
            Row(11.5,
                Cell(widths[0], Paragraph("Дебет", 6, align: "center", before: 2.7, line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[1] + widths[2] + widths[3], Paragraph("Кредит", 6, align: "center", before: 2.7, line: 6), BoxBorder(), gridSpan: 3) +
                Cell(widths[4], Paragraph("Сумма\nруб. коп.", 6, align: "center", before: 2.7, line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[5], Paragraph("Код целевого\nназначения", 6, align: "center", before: 2.7, line: 6), BoxBorder(), vMerge: "restart")),
            Row(30,
                Cell(widths[0], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[1], Paragraph("код струк-\nтурного\nподразде-\nления", 6, align: "center", before: .7, line: 6), BoxBorder()) +
                Cell(widths[2], Paragraph("корреспон-\nдирующий счет,\nсубсчет", 6, align: "center", before: 4.3, line: 6), BoxBorder()) +
                Cell(widths[3], Paragraph("код аналити-\nческого учета", 6, align: "center", before: 7.9, line: 6), BoxBorder()) +
                Cell(widths[4], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[5], EmptyParagraph(1), BoxBorder(), vMerge: "continue")),
            Row(10,
                Cell(widths[0], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[1], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[2], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[3], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[4], Paragraph(amount, 6, align: "center", before: 1.7, line: 6), BoxBorder()) +
                Cell(widths[5], EmptyParagraph(1), BoxBorder()))
        };
        return Table(283.5, widths, rows);
    }

    private static string BuildAmountRow(string words, string kopecks)
    {
        double[] widths = [28, 161, 20, 48, 26.5];
        return Table(283.5, widths, [Row(13,
            Cell(widths[0], Paragraph("Сумма", 6, line: 6)) +
            Cell(widths[1], Paragraph(words, 6, left: 3.3, line: 6), BottomBorder()) +
            Cell(widths[2], Paragraph("руб.", 6, align: "center", line: 6)) +
            Cell(widths[3], Paragraph(kopecks, 6, align: "center", line: 6), BottomBorder()) +
            Cell(widths[4], Paragraph("коп.", 6, align: "center", line: 6)))]);
    }

    private static string Field(string label, string value, double labelWidth, double valueWidth)
        => Table(labelWidth + valueWidth, [labelWidth, valueWidth], [Row(11,
            Cell(labelWidth, Paragraph(label, 6, line: 6)) +
            Cell(valueWidth, Paragraph(value, 6, line: 6, left: 4), BottomBorder()))]);

    private static string Signature(string role, string signer, double width)
    {
        double[] widths = [76, 88, 96, width - 260];
        return Table(width, widths, [
            Row(10,
                Cell(widths[0], Paragraph(role, 6, line: 6), vMerge: "restart") +
                Cell(widths[1], EmptyParagraph(1), BottomBorder()) +
                Cell(widths[2], Paragraph(signer, 6, align: "center", line: 6), BottomBorder()) +
                Cell(widths[3], EmptyParagraph(1))),
            Row(8,
                Cell(widths[0], EmptyParagraph(1), vMerge: "continue") +
                Cell(widths[1], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                Cell(widths[2], Paragraph("(расшифровка подписи)", 5, align: "center", line: 5)) +
                Cell(widths[3], EmptyParagraph(1)))
        ]);
    }

    private static string BuildCodeBox()
    {
        double[] widths = [48, 42.5];
        return Table(90.5, widths, [
            Row(13, Cell(widths[0], Paragraph("Код", 7, align: "right", line: 7), RightBorder()) + Cell(widths[1], EmptyParagraph(1), BoxBorder())),
            Row(23, Cell(widths[0], Paragraph("Форма по ОКУД", 6, align: "right", line: 6), RightBorder()) + Cell(widths[1], Paragraph("0310002", 6, bold: true, align: "center", line: 6), BoxBorder())),
            Row(17, Cell(widths[0], Paragraph("по ОКПО", 6, align: "right", line: 6), RightBorder()) + Cell(widths[1], EmptyParagraph(1), BoxBorder()))
        ], x: 236.5, y: 107);
    }

    private static string BuildCutLine()
    {
        double[] widths = [20.5, 8.5];
        var leftBorders = Border(left: ("single", 4), right: ("dashed", 4));
        var rightBorders = Border(right: ("single", 12));
        return Table(29, widths, [Row(369.5,
            Cell(widths[0], Paragraph("Л и н и я   о т р е з а", 6, bold: true, align: "center", line: 8), leftBorders, textDirection: "tbRl") +
            Cell(widths[1], EmptyParagraph(1), rightBorders))], x: 330, y: 72.5);
    }

    private static string BuildReceipt(CashOrderWordData data)
    {
        const double width = 181.5;
        var rows = new List<string>
        {
            Row(8, Cell(width, Paragraph(data.Organization, 6, align: "center", line: 6), BottomBorder(12))),
            Row(6, Cell(width, Paragraph("(организация)", 5, align: "center", line: 5))),
            Row(15, Cell(width, Paragraph("КВИТАНЦИЯ", 10, bold: true, align: "center", before: 2, line: 10.5))),
            Row(20, Cell(width, EmptyParagraph(1))),
            Row(11, Cell(width, ReceiptNumber(data.Number))),
            Row(12, Cell(width, ReceiptDate("от", data.Day, data.Month, data.Year))),
            Row(21.5, Cell(width, ReceiptField("Принято от", data.Payer, 39, before: 1))),
            Row(21.5, Cell(width, ReceiptField("Основание:", data.Basis, 39, before: 1))),
            Row(22, Cell(width, Table(width, [width], [Row(11, Cell(width, EmptyParagraph(1), BottomBorder())), Row(11, Cell(width, EmptyParagraph(1), BottomBorder()))]))),
            Row(15.3, Cell(width, ReceiptDigits(data.Rubles, data.Kopecks))),
            Row(39.5, Cell(width, ReceiptWords(data.AmountWords))),
            Row(12, Cell(width, ReceiptField("В том числе", "Без налога (НДС) 0,00 руб.", 45))),
            Row(12, Cell(width, ReceiptDate("\"", data.Day, data.Month, data.Year))),
            Row(8, Cell(width, Paragraph("М.П. (штампа)", 6, before: 1, line: 6))),
            Row(17.1, Cell(width, ReceiptSignature("Главный бухгалтер", data.Signer))),
            Row(20, Cell(width, ReceiptSignature("Кассир", data.Signer)))
        };
        return Table(width, [width], rows, x: 368.5, y: 85.5);
    }

    private static string ReceiptNumber(string number)
        => Table(181.5, [147.5, 34], [Row(11,
            Cell(147.5, Paragraph("к приходному кассовому ордеру №", 6, line: 6)) +
            Cell(34, Paragraph(number, 9, align: "center", line: 9), BottomBorder()))]);

    private static string ReceiptDate(string prefix, string day, string month, string year)
    {
        double[] widths = [14, 10, 29, 62, 44, 22.5];
        return Table(181.5, widths, [Row(12,
            Cell(widths[0], Paragraph(prefix, 6, line: 6)) +
            Cell(widths[1], Paragraph("\"", 6, align: "center", line: 6)) +
            Cell(widths[2], Paragraph(day, 6, align: "center", line: 6), BottomBorder()) +
            Cell(widths[3], Paragraph($"\" {month}", 6, align: "center", line: 6)) +
            Cell(widths[4], Paragraph(year, 6, align: "center", line: 6), BottomBorder()) +
            Cell(widths[5], Paragraph("г.", 6, align: "center", line: 6)))]);
    }

    private static string ReceiptField(string label, string value, double labelWidth, double before = 0)
        => Table(181.5, [labelWidth, 181.5 - labelWidth], [Row(12,
            Cell(labelWidth, Paragraph(label, 6, before: before, line: 6)) +
            Cell(181.5 - labelWidth, Paragraph(value, 6, before: before, line: 6, left: 3), BottomBorder()))]);

    private static string ReceiptDigits(string rubles, string kopecks)
    {
        double[] widths = [31, 100, 20, 28, 18];
        return Table(181.5, widths, [
            Row(12,
                Cell(widths[0], Paragraph("Сумма", 6, line: 6)) +
                Cell(widths[1], Paragraph(rubles, 6, align: "center", line: 6), BottomBorder()) +
                Cell(widths[2], Paragraph("руб.", 6, align: "center", line: 6)) +
                Cell(widths[3], Paragraph(kopecks, 6, align: "center", line: 6), BottomBorder()) +
                Cell(widths[4], Paragraph("коп.", 6, align: "center", line: 6))),
            Row(8,
                Cell(widths[0], EmptyParagraph(1)) +
                Cell(widths[1], Paragraph("(цифрами)", 5, align: "center", line: 5)) +
                Cell(widths[2], EmptyParagraph(1)) + Cell(widths[3], EmptyParagraph(1)) + Cell(widths[4], EmptyParagraph(1)))
        ]);
    }

    private static string ReceiptWords(string words)
        => Table(181.5, [181.5], [
            Row(14, Cell(181.5, Paragraph(words, 6, left: 3, line: 6), BottomBorder())),
            Row(8, Cell(181.5, Paragraph("(прописью)", 5, align: "center", line: 5)))
        ]);

    private static string ReceiptSignature(string role, string signer)
    {
        if (string.Equals(role, "Кассир", StringComparison.Ordinal))
        {
            double[] cashierWidths = [32.5, 32.2, 27.8, 54, 20, 15];
            return Table(181.5, cashierWidths, [
                Row(8.7,
                    Cell(cashierWidths[0], Paragraph(role, 6, line: 6), vMerge: "restart") +
                    Cell(cashierWidths[1], EmptyParagraph(1), BottomBorder()) +
                    Cell(cashierWidths[2], EmptyParagraph(1)) +
                    Cell(cashierWidths[3], Paragraph(signer, 6, line: 6), BottomBorder()) +
                    Cell(cashierWidths[4], EmptyParagraph(1)) +
                    Cell(cashierWidths[5], EmptyParagraph(1))),
                Row(7,
                    Cell(cashierWidths[0], EmptyParagraph(1), vMerge: "continue") +
                    Cell(cashierWidths[1], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                    Cell(cashierWidths[2] + cashierWidths[3] + cashierWidths[4], Paragraph("(расшифровка подписи)", 5, align: "center", line: 5), gridSpan: 3) +
                    Cell(cashierWidths[5], EmptyParagraph(1)))
            ]);
        }

        double[] widths = [63.6, 38.3, 79.6];
        return Table(181.5, widths, [
            Row(8.7,
                Cell(widths[0], Paragraph(role, 6, line: 6), vMerge: "restart") +
                Cell(widths[1], EmptyParagraph(1), BottomBorder()) +
                Cell(widths[2], Paragraph(signer, 6, align: "center", left: 5.6, line: 6), BottomBorder())),
            Row(7,
                Cell(widths[0], EmptyParagraph(1), vMerge: "continue") +
                Cell(widths[1], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                Cell(widths[2], Paragraph("(расшифровка подписи)", 5, align: "center", left: 6, line: 5)))
        ]);
    }

    private static string Table(double width, IReadOnlyList<double> grid, IReadOnlyList<string> rows, double? x = null, double? y = null)
    {
        var position = x.HasValue && y.HasValue
            ? $"<w:tblpPr w:leftFromText=\"0\" w:rightFromText=\"0\" w:topFromText=\"0\" w:bottomFromText=\"0\" w:vertAnchor=\"page\" w:horzAnchor=\"page\" w:tblpX=\"{Twips(x.Value)}\" w:tblpY=\"{Twips(y.Value)}\"/><w:tblOverlap w:val=\"overlap\"/>"
            : string.Empty;
        var gridXml = string.Concat(grid.Select(item => $"<w:gridCol w:w=\"{Twips(item)}\"/>"));
        return $"<w:tbl><w:tblPr>{position}<w:tblW w:w=\"{Twips(width)}\" w:type=\"dxa\"/><w:tblLayout w:type=\"fixed\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"0\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"0\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>{gridXml}</w:tblGrid>{string.Concat(rows)}</w:tbl>";
    }

    private static string Row(double height, string cells)
        => $"<w:tr><w:trPr><w:trHeight w:val=\"{Twips(height)}\" w:hRule=\"exact\"/><w:cantSplit/></w:trPr>{cells}</w:tr>";

    private static string Cell(
        double width,
        string content,
        string? borders = null,
        int? gridSpan = null,
        string? vMerge = null,
        string? textDirection = null,
        double rightMargin = 0)
    {
        var span = gridSpan.HasValue ? $"<w:gridSpan w:val=\"{gridSpan.Value}\"/>" : string.Empty;
        var merge = vMerge is null ? string.Empty : vMerge == "continue" ? "<w:vMerge/>" : "<w:vMerge w:val=\"restart\"/>";
        var direction = textDirection is null ? string.Empty : $"<w:textDirection w:val=\"{textDirection}\"/>";
        var margins = rightMargin > 0 ? $"<w:tcMar><w:right w:w=\"{Twips(rightMargin)}\" w:type=\"dxa\"/></w:tcMar>" : string.Empty;
        return $"<w:tc><w:tcPr><w:tcW w:w=\"{Twips(width)}\" w:type=\"dxa\"/>{span}{merge}{direction}{margins}{borders}</w:tcPr>{content}<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"1\" w:lineRule=\"exact\"/></w:pPr></w:p></w:tc>";
    }

    private static string Paragraph(
        string value,
        double size,
        bool bold = false,
        string? align = null,
        double before = 0,
        double after = 0,
        double left = 0,
        double right = 0,
        double line = 6)
    {
        var alignment = align is null ? string.Empty : $"<w:jc w:val=\"{align}\"/>";
        var indent = left > 0 || right > 0
            ? $"<w:ind w:left=\"{Twips(left)}\" w:right=\"{Twips(right)}\"/>"
            : string.Empty;
        var runs = string.Join("<w:r><w:br/></w:r>", Decode(value).Split('\n').Select(part => Run(part, size, bold)));
        return $"<w:p><w:pPr><w:spacing w:before=\"{Twips(before)}\" w:after=\"{Twips(after)}\" w:line=\"{Twips(line)}\" w:lineRule=\"exact\"/>{alignment}{indent}</w:pPr>{runs}</w:p>";
    }

    private static string EmptyParagraph(double line)
        => Paragraph(" ", 1, line: line);

    private static string Run(string value, double size, bool bold)
    {
        var escaped = SecurityElement.Escape(value) ?? string.Empty;
        var boldXml = bold ? "<w:b/>" : string.Empty;
        return $"<w:r><w:rPr><w:rFonts w:ascii=\"{FontName}\" w:hAnsi=\"{FontName}\" w:cs=\"{FontName}\"/>{boldXml}<w:sz w:val=\"{HalfPoints(size)}\"/><w:szCs w:val=\"{HalfPoints(size)}\"/></w:rPr><w:t xml:space=\"preserve\">{escaped}</w:t></w:r>";
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value ?? string.Empty);

    private static string BoxBorder(int size = 4)
        => Border(top: ("single", size), left: ("single", size), bottom: ("single", size), right: ("single", size));

    private static string BottomBorder(int size = 4)
        => Border(bottom: ("single", size));

    private static string RightBorder(int size = 4)
        => Border(right: ("single", size));

    private static string Border(
        (string Style, int Size)? top = null,
        (string Style, int Size)? left = null,
        (string Style, int Size)? bottom = null,
        (string Style, int Size)? right = null)
    {
        static string Side(string name, (string Style, int Size)? value)
            => value.HasValue ? $"<w:{name} w:val=\"{value.Value.Style}\" w:sz=\"{value.Value.Size}\" w:space=\"0\" w:color=\"111111\"/>" : string.Empty;
        return $"<w:tcBorders>{Side("top", top)}{Side("left", left)}{Side("bottom", bottom)}{Side("right", right)}</w:tcBorders>";
    }

    private static int Twips(double points) => checked((int)Math.Round(points * 20d));
    private static int HalfPoints(double points) => checked((int)Math.Round(points * 2d));
}
