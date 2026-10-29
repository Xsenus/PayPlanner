using System.Buffers.Binary;
using System.Text;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Acts;
using PayPlanner.Api.Services.Contracts;
using PayPlanner.Api.Services.Documents;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DocumentPrintImageTests
{
    // Эти заголовки проверяют только чтение размеров, не пригодность растра для загрузки/декодирования.
    private static byte[] Png(uint width, uint height)
    {
        var bytes = new byte[33];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static string Url(byte[] bytes, string mime = "png") => $"data:image/{mime};base64,{Convert.ToBase64String(bytes)}";
    private static void AssertSize(string url, int width, int height, int maximumWidth = 120, int maximumHeight = 90)
        => Assert.Contains($"width=\"{width}\" height=\"{height}\"", DocumentPrintImage.Render(url, "Логотип", "logo", maximumWidth, maximumHeight));

    [Theory]
    [InlineData(1, 1, 90, 90)]
    [InlineData(800, 200, 120, 30)]
    [InlineData(200, 800, 22, 90)]
    [InlineData(4294967295, 1, 120, 1)]
    [InlineData(1, 4294967295, 1, 90)]
    [InlineData(0, 200, 120, 90)]
    public void Png_FitsWithinExistingBoxWithoutOverflow(uint sourceWidth, uint sourceHeight, int width, int height)
        => AssertSize(Url(Png(sourceWidth, sourceHeight)), width, height);

    [Theory]
    [InlineData(0xc0)]
    [InlineData(0xc2)]
    [InlineData(0xc3)]
    public void Jpeg_ReadsSofAfterMetadataAndMarkerPadding(int marker)
    {
        byte[] bytes = [0xff, 0xd8, 0xff, 0xe1, 0, 6, 1, 2, 3, 4, 0xff, 0xff, 0x01, 0xff, (byte)marker, 0, 8, 8, 0, 100, 1, 144, 1];
        AssertSize(Url(bytes, "jpeg"), 120, 30);
    }

    [Theory]
    [InlineData("VP8X")]
    [InlineData("VP8L")]
    [InlineData("VP8 ")]
    public void Webp_ReadsAllThreeContainerHeaders(string chunk)
    {
        var bytes = new byte[32];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 24);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 12);
        switch (chunk)
        {
            case "VP8X": bytes[24] = 143; bytes[25] = 1; bytes[27] = 99; break;
            case "VP8L": bytes[20] = 0x2f; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(21), 399 | (99 << 14)); break;
            default:
                bytes[23] = 0x9d; bytes[24] = 1; bytes[25] = 0x2a;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26), 400);
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), 100);
                break;
        }
        AssertSize(Url(bytes, "webp"), 120, 30);
    }

    [Fact]
    public void HeaderRead_AcceptsBase64WhitespaceAndBoundsMetadataWork()
    {
        var png = Url(Png(400, 100));
        var comma = png.IndexOf(',');
        var wrapped = png[..(comma + 1)] + string.Join("\r\n", png[(comma + 1)..].Chunk(4).Select(chars => new string(chars)));
        AssertSize(wrapped, 120, 30);
        var bytes = new List<byte> { 0xff, 0xd8 };
        for (var i = 0; i < 3; i++)
        {
            bytes.AddRange(new byte[] { 0xff, 0xe1, 0xff, 0xff });
            bytes.AddRange(new byte[65533]);
        }
        bytes.AddRange(new byte[] { 0xff, 0xc0, 0, 8, 8, 0, 100, 1, 144, 1 });
        AssertSize(Url(bytes.ToArray(), "jpeg"), 120, 90);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/9j/2gAIAAAAZAGQAQ==")]
    [InlineData("/9j/4QAB")]
    [InlineData("/9j/wAAC")]
    [InlineData("AAAA")]
    public void UnknownOrTruncatedHeader_PreservesHistoricalSourceAndExistingBox(string payload)
    {
        var url = "data:image/png;base64," + payload;
        var html = DocumentPrintImage.Render(url, "Логотип", "logo", 120, 90);
        Assert.Contains(url, html);
        AssertSize(url, 120, 90);
    }

    [Fact]
    public void MissingImage_ProducesNoPlaceholder()
    {
        Assert.Empty(DocumentPrintImage.Render(null, "Подпись", "signature", 76, 42));
        Assert.Empty(DocumentPrintImage.Render(" ", "Печать", "seal", 90, 90));
    }

    [Theory]
    [MemberData(nameof(DocumentImagePrintOptionsTests.Combinations), MemberType = typeof(DocumentImagePrintOptionsTests))]
    public void StandardActAndContract_UseOnlySelectedImagesInDocumentFlow(int mask)
    {
        var logo = Url(Png(800, 200));
        var seal = Url(Png(200, 200));
        var signature = Url(Png(400, 100));
        var company = new LegalEntity { LogoDataUrl = logo, SealDataUrl = seal, DirectorSignatureDataUrl = signature };
        var options = new DocumentImagePrintOptions((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        var act = new Act { Number = "ТЕСТ", Invoice = new Payment { Type = PaymentType.Income }, Date = new DateTime(2026, 10, 29) };
        var contract = new Contract { Number = "ТЕСТ", Date = act.Date };
        foreach (var html in new[] { ActDocumentTemplateEngine.RenderHtml(act, [], company, options), ContractDocumentTemplateEngine.RenderHtml(ContractDocumentTemplateEngine.DefaultTemplateHtml, contract, company, options) })
        {
            Assert.Equal(options.IncludeLogo, html.Contains("class=\"logo\" width=\"120\" height=\"30\"", StringComparison.Ordinal));
            Assert.Equal(options.IncludeSeal, html.Contains("class=\"seal\" width=\"90\" height=\"90\"", StringComparison.Ordinal));
            Assert.Equal(options.IncludeSignature, html.Contains("class=\"signature\" width=\"76\" height=\"19\"", StringComparison.Ordinal));
            Assert.Equal(options.IncludeLogo, html.Contains("class=\"document-header\"", StringComparison.Ordinal));
            Assert.DoesNotContain("position: absolute", html);
            Assert.DoesNotContain("{{", html);
        }
        var contractHtml = ContractDocumentTemplateEngine.RenderHtml(ContractDocumentTemplateEngine.DefaultTemplateHtml, contract, company, options);
        Assert.Equal(options.IncludeSignature || options.IncludeSeal, contractHtml.Contains("<tr style=\"page-break-inside:avoid;break-inside:avoid\">", StringComparison.Ordinal));
        Assert.Contains("<tr style=\"page-break-inside:avoid;break-inside:avoid\">", ActDocumentTemplateEngine.RenderHtml(act, [], company, options));
        act.Invoice.Type = PaymentType.Expense;
        var expense = ActDocumentTemplateEngine.RenderHtml(act, [], company, options);
        Assert.DoesNotContain("<img", expense);
        Assert.DoesNotContain("class=\"document-header\"", expense);
    }

    [Fact]
    public void CustomTemplates_DoNotAcquireAutomaticImagePositions()
    {
        var company = new LegalEntity { LogoDataUrl = Url(Png(32, 32)), SealDataUrl = Url(Png(32, 32)) };
        var options = new DocumentImagePrintOptions(true, true, true);
        Assert.Equal("<h1>Свой акт</h1>", ActDocumentTemplateEngine.RenderHtml("<h1>Свой акт</h1>", new Act(), [], company, options));
        Assert.Equal("<h1>Свой договор</h1>", ContractDocumentTemplateEngine.RenderHtml("<h1>Свой договор</h1>", new Contract(), company, options));
        Assert.Equal(ContractDocumentTemplateEngine.RenderHtml(new Contract(), company),
            ContractDocumentTemplateEngine.RenderHtml(ContractDocumentTemplateEngine.DefaultTemplateHtml, new Contract(), company, new(false, false, false)));
    }
}
