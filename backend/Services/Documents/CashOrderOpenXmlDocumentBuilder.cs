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
    string ChiefAccountant,
    string Cashier,
    string TaxText,
    string CancelNotice);

internal sealed record OutgoingCashOrderWordData(
    string Organization,
    string Number,
    string Date,
    string Receiver,
    string Basis,
    string Appendix,
    string AmountRubKop,
    string AmountWords,
    string Kopecks,
    string Day,
    string Month,
    string Year,
    string LeaderPosition,
    string LeaderName,
    string ChiefAccountant,
    string Cashier,
    string CancelNotice);

internal static class CashOrderOpenXmlDocumentBuilder
{
    private const string FontName = "Liberation Sans";
    private const string RegularFontResource = "PayPlanner.Fonts.LiberationSans-Regular.ttf";
    private const string BoldFontResource = "PayPlanner.Fonts.LiberationSans-Bold.ttf";

    public static byte[] Build(CashOrderWordData data)
    {
        var packageBytes = BuildBasePackage();
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

    public static byte[] BuildOutgoing(OutgoingCashOrderWordData data)
    {
        var packageBytes = BuildBasePackage();
        using var packageStream = new MemoryStream();
        packageStream.Write(packageBytes);
        packageStream.Position = 0;

        using (var package = new ZipArchive(packageStream, ZipArchiveMode.Update, leaveOpen: true))
        {
            package.GetEntry("word/document.xml")?.Delete();
            var entry = package.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(BuildOutgoingDocumentXml(data));
        }

        return packageStream.ToArray();
    }

    private static byte[] BuildBasePackage() => HtmlWordDocumentBuilder.Build(
        "<html><body><p></p></body></html>",
        fontName: FontName,
        regularFontResource: RegularFontResource,
        boldFontResource: BoldFontResource);

    private static string BuildDocumentXml(CashOrderWordData data)
    {
        var body = string.Concat(
            BuildMainOrder(data),
            BuildCodeBox(),
            BuildCutLine(),
            BuildReceipt(data),
            BuildCancellationNotice(data.CancelNotice, 45, 417, 283.5));

        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
{{body}}
<w:p><w:pPr><w:spacing w:before="0" w:after="0" w:line="1" w:lineRule="exact"/></w:pPr></w:p>
<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="0" w:right="0" w:bottom="0" w:left="0" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr>
</w:body></w:document>
""";
    }

    private static string BuildOutgoingDocumentXml(OutgoingCashOrderWordData data)
    {
        var body = string.Concat(
            Table(199.5, [199.5], [Row(24, Cell(199.5, Paragraph("Унифицированная форма № КО-2\nУтверждена постановлением Госкомстата России от 18.08.98 № 88", 5, line: 6)))], x: 359.5, y: 49),
            BuildOutgoingOrganization(data.Organization),
            BuildOutgoingCodeBox(),
            BuildOutgoingTitleAndMeta(data),
            BuildOutgoingLedger(data.AmountRubKop),
            BuildOutgoingDetails(data),
            BuildCancellationNotice(data.CancelNotice, 20, 488, 555.6));

        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
{{body}}
<w:p><w:pPr><w:spacing w:before="0" w:after="0" w:line="1" w:lineRule="exact"/></w:pPr></w:p>
<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="0" w:right="0" w:bottom="0" w:left="0" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr>
</w:body></w:document>
""";
    }

    private static string BuildOutgoingOrganization(string organization)
        => Table(428, [428], [
            Row(10, Cell(428, Paragraph(organization, FitSize(organization, 7, 96, 6), align: "center", line: 7, widthScale: 95), BottomBorder())),
            Row(6, Cell(428, Paragraph("(организация)", 5, align: "center", line: 5))),
            Row(10, Cell(428, EmptyParagraph(1), BottomBorder())),
            Row(7, Cell(428, Paragraph("(структурное подразделение)", 5, align: "center", line: 5)))
        ], x: 19.25, y: 93);

    private static string BuildOutgoingCodeBox()
    {
        double[] widths = [54.8, 84.1];
        return Table(138.9, widths, [
            Row(12.4, Cell(widths[0], Paragraph("Код", 7, align: "right", line: 7), RightBorder(6)) + Cell(widths[1], EmptyParagraph(1), BoxBorder(6))),
            Row(12.4, Cell(widths[0], Paragraph("Форма по ОКУД", 6, align: "right", line: 6), RightBorder(6)) + Cell(widths[1], Paragraph("0310002", 6, bold: true, align: "center", line: 6), BoxBorder(6))),
            Row(12, Cell(widths[0], Paragraph("по ОКПО", 6, align: "right", line: 6), RightBorder(6)) + Cell(widths[1], EmptyParagraph(1), BoxBorder(6))),
            Row(15.8, Cell(widths[0], EmptyParagraph(1), RightBorder(6)) + Cell(widths[1], EmptyParagraph(1), BoxBorder(6)))
        ], x: 437.45, y: 66);
    }

    private static string BuildOutgoingTitleAndMeta(OutgoingCashOrderWordData data)
    {
        var title = Table(260.8, [260.8], [Row(28, Cell(260.8, Paragraph("РАСХОДНЫЙ КАССОВЫЙ ОРДЕР", 10, align: "center", before: 15, line: 10, widthScale: 97)))], x: 120, y: 131);
        double[] widths = [83.7, 83.5];
        var meta = Table(167.2, widths, [
            Row(11.75, Cell(widths[0], Paragraph("Номер документа", 6, align: "center", line: 6), BoxBorder(6)) + Cell(widths[1], Paragraph("Дата составления", 6, align: "center", line: 6), BoxBorder(6))),
            Row(15, Cell(widths[0], Paragraph(data.Number, FitSize(data.Number, 7, 18, 3), bold: true, align: "center", line: 7), BoxBorder(6)) + Cell(widths[1], Paragraph(data.Date, 7, bold: true, align: "center", line: 7), BoxBorder(6)))
        ], x: 409.15, y: 127.5);
        return title + meta;
    }

    private static string BuildOutgoingLedger(string amount)
    {
        double[] widths = [33.25, 84.35, 117.6, 112.1, 38.95, 66, 66.2, 38.4];
        return Table(556.85, widths, [
            Row(13,
                Cell(widths[0] + widths[1] + widths[2] + widths[3], Paragraph("Дебет", 6, align: "center", line: 6), BoxBorder(), gridSpan: 4) +
                Cell(widths[4], Paragraph("Кредит", 6, align: "center", line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[5], Paragraph("Сумма,\nруб. коп.", 6, align: "center", line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[6], Paragraph("Код целевого\nназначения", 6, align: "center", line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[7], EmptyParagraph(1), BoxBorder(), vMerge: "restart")),
            Row(21.75,
                Cell(widths[0], Paragraph("код структурного\nподразделения", 5.5, align: "center", before: 6, line: 6), BoxBorder()) +
                Cell(widths[1], Paragraph("корреспондирующий\nсчет, субсчет", 5.5, align: "center", before: 6, line: 6), BoxBorder()) +
                Cell(widths[2], Paragraph("код аналитического\nучета", 5.5, align: "center", before: 6, line: 6), BoxBorder()) +
                Cell(widths[3], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[4], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[5], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[6], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[7], EmptyParagraph(1), BoxBorder(), vMerge: "continue")),
            Row(12.75,
                Cell(widths[0], EmptyParagraph(1), BoxBorder()) + Cell(widths[1], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[2], EmptyParagraph(1), BoxBorder()) + Cell(widths[3], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[4], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[5], Paragraph(amount, FitSize(amount, 6, 11, 5), align: "center", line: 6), BoxBorder()) +
                Cell(widths[6], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[7], EmptyParagraph(1), BoxBorder()))
        ], x: 20, y: 171.75);
    }

    private static string BuildOutgoingDetails(OutgoingCashOrderWordData data)
    {
        const double width = 556.85;
        return string.Concat(
            Positioned(width, Field("Выдать", data.Receiver, 28, width - 28, FitSize(data.Receiver, 6.75, 105, 5.5), before: 1, valueWidthScale: 97), 226.5, 16),
            Positioned(width, Paragraph("(фамилия, имя, отчество)", 5.4, align: "center", left: 28, line: 5.5, widthScale: 98), 239, 8),
            Positioned(width, Field("Основание", data.Basis, 41, width - 41, FitSize(data.Basis, 6, 115, 5), before: 1), 246, 16),
            Positioned(width, OutgoingWords("Сумма", data.AmountWords), 259.2, 16),
            Positioned(width, Paragraph("(прописью)", 5.5, align: "center", left: 27, line: 5.5, widthScale: 95), 276, 8),
            Positioned(width, OutgoingDigits(data.Kopecks), 278.7, 16),
            Positioned(width, Field("Приложение", data.Appendix, 45, width - 45, FitSize(data.Appendix, 6, 115, 5), labelSize: 5.25, before: 3), 297.7, 16),
            Positioned(width, EmptyParagraph(1), 308.4, 10, BottomBorder()),
            Positioned(width, OutgoingLeader(data.LeaderPosition, data.LeaderName), 322.7, 26),
            Positioned(width, OutgoingSignature("Главный бухгалтер", data.ChiefAccountant), 342, 23),
            Positioned(width, OutgoingWords("Получил", data.AmountWords), 359, 16),
            Positioned(width, Paragraph("(сумма прописью)", 5.5, align: "center", left: 33, line: 5.5, widthScale: 98), 376, 8),
            Positioned(width, OutgoingDigits(data.Kopecks), 379.2, 16),
            Positioned(width, OutgoingReceivedDate(data), 399, 17),
            Positioned(width, Field("По", string.Empty, 12, width - 12, 6, before: 1.5), 414.85, 12),
            Positioned(width, Paragraph("(наименование, номер, дата и место выдачи документа,", 5.5, align: "center", before: 3.5, left: 12, line: 5.5, widthScale: 97), 424.5, 10),
            Positioned(width, EmptyParagraph(1), 433.5, 12, BottomBorder()),
            Positioned(width, Paragraph("удостоверяющего личность получателя)", 5.5, align: "center", before: .375, line: 5.5, widthScale: 97), 447, 8),
            Positioned(width, OutgoingSignature("Выдал кассир", data.Cashier), 455.7, 26));
    }

    private static string Positioned(double width, string content, double y, double height, string? borders = null)
        => PositionedAt(width, content, 20, y - 0.75, height, borders);

    private static string PositionedAt(double width, string content, double x, double y, double height, string? borders = null)
        => Table(width, [width], [Row(height, Cell(width, content, borders))], x: x, y: y);

    private static string OutgoingWords(string label, string words)
    {
        var labelWidth = string.Equals(label, "Получил", StringComparison.Ordinal) ? 33d : 28d;
        double[] widths = [labelWidth, 556.85 - labelWidth];
        return Table(556.85, widths, [Row(15,
            Cell(widths[0], Paragraph(label, 6, before: 5.5, line: 6)) +
            Cell(widths[1], Paragraph(words, FitSize(words, 6.75, 105, 5.5), align: string.Equals(label, "Получил", StringComparison.Ordinal) ? "center" : null, before: 6, left: 3, line: 7, widthScale: 97), BottomBorder()))]);
    }

    private static string OutgoingDigits(string kopecks)
    {
        double[] widths = [405, 28.5, 94, 29.35];
        return Table(556.85, widths, [Row(15,
            Cell(widths[0], EmptyParagraph(1), BottomBorder()) +
            Cell(widths[1], Paragraph("руб.", 6, align: "center", line: 6)) +
            Cell(widths[2], Paragraph(kopecks, 6, align: "center", line: 6), BottomBorder()) +
            Cell(widths[3], Paragraph("коп.", 6, align: "center", line: 6)))]);
    }

    private static string OutgoingLeader(string position, string signer)
    {
        double[] widths = [106.5, 120.75, 16.5, 120.75, 17.25, 121.5, 53.6];
        return Table(556.85, widths, [
            Row(11,
                Cell(widths[0], Paragraph("Руководитель организации", 6.75, before: 1, line: 7, widthScale: 97), vMerge: "restart") +
                Cell(widths[1], Paragraph(position, FitSize(position, 6.75, 24, 5.5), align: "center", before: 1, line: 7, widthScale: 97), BottomBorder()) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], EmptyParagraph(1), BottomBorder()) +
                Cell(widths[4], EmptyParagraph(1)) +
                Cell(widths[5], Paragraph(signer, 6.75, align: "center", before: 1, line: 7, widthScale: 97), BottomBorder()) +
                Cell(widths[6], EmptyParagraph(1))),
            Row(8,
                Cell(widths[0], EmptyParagraph(1), vMerge: "continue") +
                Cell(widths[1], Paragraph("(должность)", 5, align: "center", line: 5)) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                Cell(widths[4], EmptyParagraph(1)) +
                Cell(widths[5], Paragraph("(расшифровка подписи)", 5, align: "center", line: 5)) +
                Cell(widths[6], EmptyParagraph(1)))
        ]);
    }

    private static string OutgoingSignature(string role, string signer)
    {
        var cashier = string.Equals(role, "Выдал кассир", StringComparison.Ordinal);
        var roleWidth = cashier ? 66.75 : 84.75;
        var lineWidth = cashier ? 114 : 115.5;
        var gapWidth = 15.75;
        var tailWidth = 556.85 - roleWidth - lineWidth - gapWidth - lineWidth;
        double[] widths = [roleWidth, lineWidth, gapWidth, lineWidth, tailWidth];
        return Table(556.85, widths, [
            Row(11.5,
                Cell(widths[0], Paragraph(role, 6.75, before: cashier ? 1.25 : 1, line: 7, widthScale: 97), vMerge: "restart") +
                Cell(widths[1], EmptyParagraph(1), BottomBorder()) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph(signer, 6.75, align: "center", before: cashier ? 1.25 : 1, line: 7, widthScale: 97), BottomBorder()) +
                Cell(widths[4], EmptyParagraph(1))),
            Row(8,
                Cell(widths[0], EmptyParagraph(1), vMerge: "continue") +
                Cell(widths[1], Paragraph("(подпись)", 5, align: "center", before: cashier ? .75 : 0, line: 5)) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph("(расшифровка подписи)", 5, align: "center", before: cashier ? .75 : 0, line: 5)) +
                Cell(widths[4], EmptyParagraph(1)))
        ]);
    }

    private static string OutgoingReceivedDate(OutgoingCashOrderWordData data)
    {
        double[] widths = [14, 34, 14, 84, 50, 18, 65, 277.85];
        return Table(556.85, widths, [Row(12,
            Cell(widths[0], Paragraph("«", 6, before: 2.25, left: 4, line: 6)) + Cell(widths[1], Paragraph(data.Day, 6, align: "center", before: 2.25, line: 6), BottomBorder()) +
            Cell(widths[2], Paragraph("»", 6, align: "center", before: 2.25, line: 6)) + Cell(widths[3], Paragraph(data.Month, 6, align: "center", before: 2.25, line: 6), BottomBorder()) +
            Cell(widths[4], Paragraph(data.Year, 6, align: "center", before: 2.25, line: 6), BottomBorder()) + Cell(widths[5], Paragraph("г.", 6, align: "center", before: 2.25, line: 6)) +
            Cell(widths[6], Paragraph("Подпись", 6, align: "right", before: 2.25, line: 6)) + Cell(widths[7], EmptyParagraph(1), BottomBorder()))]);
    }

    private static string BuildCancellationNotice(string notice, double x, double y, double width)
        => string.IsNullOrWhiteSpace(notice)
            ? string.Empty
            : Table(width, [width], [Row(18, Cell(width, Paragraph(notice, FitSize(notice, 6, 115, 5), bold: true, left: 4, before: 4, line: 6), BoxBorder()))], x: x, y: y);

    private static double FitSize(string value, double regular, int compactThreshold, double compact)
        => Decode(value).Length > compactThreshold ? compact : regular;

    private static string BuildMainOrder(CashOrderWordData data)
    {
        const double width = 283.5;
        const double x = 45;

        return string.Concat(
            Table(width + 4.5, [width + 4.5], [Row(33, Cell(width + 4.5, Paragraph(
                "Унифицированная форма № КО-1\nУтверждена постановлением Госкомстата\nРоссии от 18.08.98 № 88",
                6,
                left: 148,
                line: 7,
                widthScale: 103)))], x: x + 11.5, y: 74.75),
            Table(
                197.5,
                [197.5],
                [
                    Row(14, Cell(197.5, EmptyParagraph(1))),
                    Row(8, Cell(197.5, Paragraph(data.Organization, FitSize(data.Organization, 7, 70, 5.5), left: 2, line: 7), BottomBorder())),
                    Row(5, Cell(197.5, Paragraph("(организация)", 5, align: "center", line: 5)))
                ], x: x, y: 108.05),
            Table(
                239.5,
                [239.5],
                [
                    Row(12.7, Cell(239.5, EmptyParagraph(1), BottomBorder())),
                    Row(8, Cell(239.5, Paragraph("(структурное подразделение)", 5, align: "center", line: 5)))
                ], x: x, y: 134.3),
            PositionedAt(width, BuildTitleAndMeta(data), x, 155.85, 46.5),
            PositionedAt(width, BuildLedger(data.AmountRubKop), x, 204.05, 57.3),
            PositionedAt(width, Field("Принято от", data.Payer, 39, width - 39, FitSize(data.Payer, 6.5, 55, 4.75), before: 2, valueWidthScale: 96), x, 264.9, 14),
            PositionedAt(width, Field("Основание:", data.Basis, 39, width - 39, FitSize(data.Basis, 6, 75, 4.5)), x, 280.6, 15),
            PositionedAt(width, EmptyParagraph(1), x, 303.9, 10, BottomBorder()),
            PositionedAt(width, BuildAmountWordsRow(data.AmountWords), x, 302.5, 14),
            PositionedAt(161, Paragraph("(прописью)", 5, align: "center", line: 5), x + 28, 315.6, 8),
            PositionedAt(width, BuildAmountDigitsRow(data.Kopecks), x, 322.6, 14),
            PositionedAt(width, Field("В том числе", data.TaxText, 42, width - 42, 6.5, before: 1.5), x, 339.3, 13),
            PositionedAt(width, Field("Приложение", data.Appendix, 39, width - 39, FitSize(data.Appendix, 6, 70, 4.5), before: 2), x, 353, 16),
            PositionedAt(width, Signature("Главный бухгалтер", data.ChiefAccountant, width), x, 370.7, 24),
            PositionedAt(width, Signature("Получил кассир", data.Cashier, width), x, 389.9, 20));
    }

    private static string BuildTitleAndMeta(CashOrderWordData data)
    {
        const double left = 196.25;
        const double spacer = .75;
        const double right = 84.5;
        const double tail = 2;
        var title = Cell(left, Paragraph("ПРИХОДНЫЙ КАССОВЫЙ ОРДЕР", 10, align: "center", before: 21, right: 1.5, line: 10, widthScale: 89));
        var meta = Table(
            right,
            [41.75, 42.75],
            [
                Row(18.25, Cell(41.75, Paragraph("Номер\nдокумента", 5, align: "center", before: 1.5, line: 5), BoxBorder(6)) + Cell(42.75, Paragraph("Дата\nсоставления", 5, align: "center", before: 1.5, line: 5), BoxBorder(6))),
                Row(13.5, Cell(41.75, Paragraph(data.Number, FitSize(data.Number, 6, 16, 2.5), bold: true, align: "center", line: 6), BoxBorder(6)) + Cell(42.75, Paragraph(data.Date, 6, bold: true, align: "center", line: 6), BoxBorder(6)))
            ]);
        return Table(left + spacer + right + tail, [left, spacer, right, tail], [Row(46.5, title + Cell(spacer, EmptyParagraph(1)) + Cell(right, meta) + Cell(tail, EmptyParagraph(1)))]);
    }

    private static string BuildLedger(string amount)
    {
        double[] widths = [28.35, 51.03, 51.03, 51.03, 39.69, 62.37];
        var rows = new List<string>
        {
            Row(11.6,
                Cell(widths[0], Paragraph("Дебет", 6, align: "center", before: 2.7, line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[1] + widths[2] + widths[3], Paragraph("Кредит", 6, align: "center", before: 2.7, line: 6), BoxBorder(), gridSpan: 3) +
                Cell(widths[4], Paragraph("Сумма\nруб. коп.", 6, align: "center", before: 2.7, line: 6), BoxBorder(), vMerge: "restart") +
                Cell(widths[5], Paragraph("Код целевого\nназначения", 6, align: "center", before: 2.7, line: 6), BoxBorder(), vMerge: "restart")),
            Row(33.4,
                Cell(widths[0], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[1], Paragraph("код струк-\nтурного\nподразде-\nления", 6, align: "center", before: .7, line: 6), BoxBorder()) +
                Cell(widths[2], Paragraph("корреспон-\nдирующий счет,\nсубсчет", 6, align: "center", before: 4.3, line: 6), BoxBorder()) +
                Cell(widths[3], Paragraph("код аналити-\nческого учета", 6, align: "center", before: 7.9, line: 6), BoxBorder()) +
                Cell(widths[4], EmptyParagraph(1), BoxBorder(), vMerge: "continue") +
                Cell(widths[5], EmptyParagraph(1), BoxBorder(), vMerge: "continue")),
            Row(12.3,
                Cell(widths[0], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[1], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[2], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[3], EmptyParagraph(1), BoxBorder()) +
                Cell(widths[4], Paragraph(amount, 6, align: "center", before: 1.7, line: 6), BoxBorder()) +
                Cell(widths[5], EmptyParagraph(1), BoxBorder()))
        };
        return Table(283.5, widths, rows);
    }

    private static string BuildAmountWordsRow(string words)
    {
        double[] widths = [28, 161, 20, 48, 26.5];
        return Table(283.5, widths, [Row(13,
            Cell(widths[0], Paragraph("Сумма", 6, before: 4.25, line: 6)) +
            Cell(widths[1], Paragraph(words, 6.5, before: 4.5, left: 1.75, line: 6.5, widthScale: 97), BottomBorder()) +
            Cell(widths[2], EmptyParagraph(1)) +
            Cell(widths[3], EmptyParagraph(1)) +
            Cell(widths[4], EmptyParagraph(1))) ]);
    }

    private static string BuildAmountDigitsRow(string kopecks)
    {
        double[] widths = [189, 20, 48, 26.5];
        return Table(283.5, widths, [Row(12,
            Cell(widths[0], EmptyParagraph(1), BottomBorder()) +
            Cell(widths[1], Paragraph("руб.", 6, align: "center", line: 6)) +
            Cell(widths[2], Paragraph(kopecks, 6, align: "center", line: 6), BottomBorder()) +
            Cell(widths[3], Paragraph("коп.", 6, align: "center", line: 6))) ]);
    }

    private static string Field(string label, string value, double labelWidth, double valueWidth, double valueSize = 6, double labelSize = 6, double before = 0, int valueWidthScale = 100)
        => Table(labelWidth + valueWidth, [labelWidth, valueWidth], [Row(11,
            Cell(labelWidth, Paragraph(label, labelSize, before: before, line: Math.Max(6, labelSize))) +
            Cell(valueWidth, Paragraph(value, valueSize, before: before, line: Math.Max(6, valueSize), left: 4, widthScale: valueWidthScale), BottomBorder()))]);

    private static string Signature(string role, string signer, double width)
    {
        double[] widths = [74.5, 75.75, 14.25, 92, width - 256.5];
        return Table(width, widths, [
            Row(10,
                Cell(widths[0], Paragraph(role, 6.5, before: 1, line: 6.25, widthScale: 95), vMerge: "restart") +
                Cell(widths[1], EmptyParagraph(1), BottomBorder()) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph(signer, 6.5, align: "center", before: 1, line: 6.25, widthScale: 95), BottomBorder()) +
                Cell(widths[4], EmptyParagraph(1))),
            Row(8,
                Cell(widths[0], EmptyParagraph(1), vMerge: "continue") +
                Cell(widths[1], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph("(расшифровка подписи)", 5, align: "center", line: 5)) +
                Cell(widths[4], EmptyParagraph(1)))
        ]);
    }

    private static string BuildCodeBox()
    {
        double[] widths = [48, 42.5];
        return Table(90.5, widths, [
            Row(11.25, Cell(widths[0], Paragraph("Код", 7, align: "right", line: 7), RightBorder(6)) + Cell(widths[1], EmptyParagraph(1), BoxBorder(6))),
            Row(11.25, Cell(widths[0], Paragraph("Форма по ОКУД", 6, align: "right", line: 6), RightBorder(6)) + Cell(widths[1], Paragraph("0310002", 6, bold: true, align: "center", line: 6), BoxBorder(6))),
            Row(10.9, Cell(widths[0], Paragraph("по ОКПО", 6, align: "right", line: 6), RightBorder(6)) + Cell(widths[1], EmptyParagraph(1), BoxBorder(6))),
            Row(17.7, Cell(widths[0], EmptyParagraph(1), RightBorder(6)) + Cell(widths[1], EmptyParagraph(1), BoxBorder(6)))
        ], x: 236.5, y: 96.45);
    }

    private static string BuildCutLine()
    {
        double[] widths = [20.5, 8.5];
        var leftBorders = Border(left: ("single", 4), right: ("dashed", 4));
        var rightBorders = Border(right: ("single", 12));
        return Table(29, widths, [Row(368,
            Cell(widths[0], Paragraph("Л и н и я   о т р е з а", 6, bold: true, align: "center", line: 8), leftBorders, textDirection: "tbRl") +
            Cell(widths[1], EmptyParagraph(1), rightBorders))], x: 330, y: 72.5);
    }

    private static string BuildReceipt(CashOrderWordData data)
    {
        const double width = 181.5;
        const double x = 368.5;
        return string.Concat(
            Table(width, [width], [
                Row(8.75, Cell(width, Paragraph(data.Organization, 6, align: "center", line: 6), BottomBorder())),
                Row(5.25, Cell(width, Paragraph("(организация)", 5, align: "center", line: 5)))
            ], x: x, y: 84.75),
            PositionedAt(width, Paragraph("КВИТАНЦИЯ", 10, bold: true, align: "center", before: 2, line: 10.5), x, 108.5, 15),
            PositionedAt(width, ReceiptNumber(data.Number), x, 141.5, 11),
            PositionedAt(width, ReceiptDate("от", data.Day, data.Month, data.Year), x, 152.2, 12),
            PositionedAt(width, ReceiptField("Принято от", string.Empty, 39, before: 4), x, 168.825, 12),
            PositionedAt(width, EmptyParagraph(1), x, 182.1, 10, BottomBorder()),
            PositionedAt(width, ReceiptField("Основание:", data.Basis, 39, before: 4), x, 194.825, 12),
            PositionedAt(width, EmptyParagraph(1), x, 208.475, 10, BottomBorder()),
            PositionedAt(width, EmptyParagraph(1), x, 220.45, 10, BottomBorder()),
            PositionedAt(width, EmptyParagraph(1), x, 231.3, 10, BottomBorder()),
            PositionedAt(width, ReceiptDigits(data.Rubles, data.Kopecks), x, 247.15, 20),
            PositionedAt(width, Paragraph(data.AmountWords, FitSize(data.AmountWords, 6.5, 52, 4.75), before: 6, left: 2.25, line: 6.5, widthScale: 95), x, 262.8, 14, BottomBorder()),
            PositionedAt(width, Paragraph("(прописью)", 5, align: "center", line: 5), x, 278.425, 8),
            PositionedAt(width, EmptyParagraph(1), x, 283.3, 10, BottomBorder()),
            PositionedAt(width, OutgoingDigitsForWidth(width, data.Kopecks), x, 295.55, 14),
            PositionedAt(width, ReceiptField("В том числе", data.TaxText, 38, before: 4, valueSize: 6.5), x, 305.65, 12),
            PositionedAt(width, ReceiptDate("\"", data.Day, data.Month, data.Year), x, 321.4, 12),
            PositionedAt(width, Paragraph("М.П. (штампа)", 6, before: 1, line: 6), x, 351, 12),
            PositionedAt(width, ReceiptSignature("Главный бухгалтер", data.ChiefAccountant), x, 373.575, 17.1),
            PositionedAt(width, ReceiptSignature("Кассир", data.Cashier), x, 393.375, 20));
    }

    private static string ReceiptNumber(string number)
        => Table(181.5, [130, 51.5], [Row(11,
            Cell(130, Paragraph("к приходному кассовому ордеру №", 6.5, before: 1.5, line: 6.5, widthScale: 97)) +
            Cell(51.5, Paragraph(number, FitSize(number, 6, 16, 2.5), align: "center", line: 6), BottomBorder()))]);

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

    private static string ReceiptField(string label, string value, double labelWidth, double before = 0, double valueSize = 6)
        => Table(181.5, [labelWidth, 181.5 - labelWidth], [Row(12,
            Cell(labelWidth, Paragraph(label, 6, before: before, line: 6)) +
            Cell(181.5 - labelWidth, Paragraph(value, valueSize, before: before, line: Math.Max(6, valueSize), left: 3), BottomBorder()))]);

    private static string ReceiptDigits(string rubles, string kopecks)
    {
        double[] widths = [31, 70, 24, 25.5, 31];
        return Table(181.5, widths, [
            Row(14,
                Cell(widths[0], Paragraph("Сумма", 6, before: 5, line: 6)) +
                Cell(widths[1], Paragraph(rubles, 6, align: "center", before: 5, line: 6), BottomBorder()) +
                Cell(widths[2], Paragraph("руб.", 6, align: "center", before: 5, line: 6)) +
                Cell(widths[3], Paragraph(kopecks, 6, align: "center", before: 5, line: 6), BottomBorder()) +
                Cell(widths[4], Paragraph("коп.", 6, align: "center", before: 5, line: 6))),
            Row(6,
                Cell(widths[0], EmptyParagraph(1)) +
                Cell(widths[1], Paragraph("(цифрами)", 5, align: "center", before: 0.5, line: 5)) +
                Cell(widths[2], EmptyParagraph(1)) + Cell(widths[3], EmptyParagraph(1)) + Cell(widths[4], EmptyParagraph(1)))
        ]);
    }

    private static string OutgoingDigitsForWidth(double width, string kopecks)
    {
        double[] widths = [width - 72, 20, 34, 18];
        return Table(width, widths, [Row(12,
            Cell(widths[0], EmptyParagraph(1), BottomBorder()) +
            Cell(widths[1], Paragraph("руб.", 6, align: "center", line: 6)) +
            Cell(widths[2], Paragraph(kopecks, 6, align: "center", line: 6), BottomBorder()) +
            Cell(widths[3], Paragraph("коп.", 6, align: "center", line: 6)))]);
    }

    private static string ReceiptSignature(string role, string signer)
    {
        if (string.Equals(role, "Кассир", StringComparison.Ordinal))
        {
            double[] cashierWidths = [29.95, 34.1, 8.9, 83.95, 9.6, 15];
            return Table(181.5, cashierWidths, [
                Row(8.7,
                    Cell(cashierWidths[0], Paragraph(role, 6.5, before: 1, line: 6.25, widthScale: 95), vMerge: "restart") +
                    Cell(cashierWidths[1], EmptyParagraph(1), BottomBorder()) +
                    Cell(cashierWidths[2], EmptyParagraph(1)) +
                    Cell(cashierWidths[3], Paragraph(signer, 6.5, before: 1, line: 6.25, widthScale: 95), BottomBorder()) +
                    Cell(cashierWidths[4], EmptyParagraph(1)) +
                    Cell(cashierWidths[5], EmptyParagraph(1))),
                Row(7,
                    Cell(cashierWidths[0], EmptyParagraph(1), vMerge: "continue") +
                    Cell(cashierWidths[1], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                    Cell(cashierWidths[2] + cashierWidths[3] + cashierWidths[4], Paragraph("(расшифровка подписи)", 5, align: "center", line: 5), gridSpan: 3) +
                    Cell(cashierWidths[5], EmptyParagraph(1)))
            ]);
        }

        double[] widths = [66.05, 34.8, 5.5, 75.15];
        return Table(181.5, widths, [
            Row(8.7,
                Cell(widths[0], Paragraph(role, 6.5, before: 1, line: 6.25, widthScale: 95), vMerge: "restart") +
                Cell(widths[1], EmptyParagraph(1), BottomBorder()) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph(signer, 6.5, align: "center", before: 1, line: 6.25, widthScale: 95), BottomBorder())),
            Row(7,
                Cell(widths[0], EmptyParagraph(1), vMerge: "continue") +
                Cell(widths[1], Paragraph("(подпись)", 5, align: "center", line: 5)) +
                Cell(widths[2], EmptyParagraph(1)) +
                Cell(widths[3], Paragraph("(расшифровка подписи)", 5, align: "center", line: 5)))
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
        double line = 6,
        int widthScale = 100)
    {
        var alignment = align is null ? string.Empty : $"<w:jc w:val=\"{align}\"/>";
        var indent = left > 0 || right > 0
            ? $"<w:ind w:left=\"{Twips(left)}\" w:right=\"{Twips(right)}\"/>"
            : string.Empty;
        var runs = string.Join("<w:r><w:br/></w:r>", Decode(value).Split('\n').Select(part => Run(part, size, bold, widthScale)));
        return $"<w:p><w:pPr><w:spacing w:before=\"{Twips(before)}\" w:after=\"{Twips(after)}\" w:line=\"{Twips(line)}\" w:lineRule=\"exact\"/>{alignment}{indent}</w:pPr>{runs}</w:p>";
    }

    private static string EmptyParagraph(double line)
        => Paragraph(" ", 1, line: line);

    private static string Run(string value, double size, bool bold, int widthScale)
    {
        var escaped = SecurityElement.Escape(value) ?? string.Empty;
        var boldXml = bold ? "<w:b/>" : string.Empty;
        var widthXml = widthScale == 100 ? string.Empty : $"<w:w w:val=\"{widthScale}\"/>";
        return $"<w:r><w:rPr><w:rFonts w:ascii=\"{FontName}\" w:hAnsi=\"{FontName}\" w:cs=\"{FontName}\"/><w:color w:val=\"333333\"/>{boldXml}{widthXml}<w:sz w:val=\"{HalfPoints(size)}\"/><w:szCs w:val=\"{HalfPoints(size)}\"/></w:rPr><w:t xml:space=\"preserve\">{escaped}</w:t></w:r>";
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
            => value.HasValue ? $"<w:{name} w:val=\"{value.Value.Style}\" w:sz=\"{value.Value.Size}\" w:space=\"0\" w:color=\"000000\"/>" : string.Empty;
        return $"<w:tcBorders>{Side("top", top)}{Side("left", left)}{Side("bottom", bottom)}{Side("right", right)}</w:tcBorders>";
    }

    private static int Twips(double points) => checked((int)Math.Round(points * 20d));
    private static int HalfPoints(double points) => checked((int)Math.Round(points * 2d));
}
