using System.IO.Compression;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoiceWordLayoutTests
{
    private const string Image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private const string Custom = "<html><head><style>.position { text-align:right; page-break-before:always; }</style></head><body><p>Первая страница</p><div class=\"position\">Печать{{seller.sealHtml}}{{seller.sealHtml}}</div><p>Конец</p></body></html>";
    private static string Source(string kind) => kind switch
    {
        "invoice-standard" => InvoiceDocumentTemplateEngine.DefaultTemplateHtml,
        "invoice-offer" => InvoiceDocumentTemplateEngine.OfferTemplateHtml,
        _ => InvoiceDocumentTemplateEngine.OrderTemplateHtml,
    };
    private static string Html(string source) => InvoiceDocumentTemplateEngine.RenderHtml(source,
        new Payment { Account = "POSITION", Amount = 100m, Date = new DateTime(2026, 10, 30), Type = PaymentType.Income },
        [new InvoiceLineItemDto { Name = "Неизменённая услуга", Quantity = 1m, Price = 100m }],
        new LegalEntity { ShortName = "Сохранённая компания", LogoDataUrl = Image, SealDataUrl = Image, DirectorSignatureDataUrl = Image },
        new DocumentImagePrintOptions(true, true, true));
    private static string Read(ZipArchive zip, string path)
    {
        using var reader = new StreamReader(zip.GetEntry(path)!.Open());
        return reader.ReadToEnd();
    }

    [Theory]
    [InlineData("invoice-standard")]
    [InlineData("invoice-offer")]
    [InlineData("invoice-order")]
    public void BuiltIn_UnchangedSourceKeepsExistingWordHtmlAndAnchors(string key)
    {
        var source = Source(key);
        var html = Html(source);
        var expected = key switch
        {
            "invoice-standard" => InvoiceStandardWordDocumentRenderer.Render(html),
            "invoice-offer" => InvoiceOfferWordDocumentRenderer.Render(html),
            _ => InvoiceOrderWordDocumentRenderer.Render(html),
        };
        using var before = new ZipArchive(new MemoryStream(expected));
        using var after = new ZipArchive(new MemoryStream(InvoiceWordDocumentRenderer.Render(html, key.ToUpperInvariant(), "\n" + source.ReplaceLineEndings("\r\n") + "\n")));
        Assert.Equal(Read(before, "word/afchunk.html"), Read(after, "word/afchunk.html"));
        Assert.Equal(Read(before, "word/document.xml"), Read(after, "word/document.xml"));
        var media = before.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(media.Length, after.Entries.Count(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)));
        foreach (var entry in media)
        {
            using var a = new MemoryStream(); using var b = new MemoryStream();
            using var beforeImage = entry.Open(); using var afterImage = after.GetEntry(entry.FullName)!.Open();
            beforeImage.CopyTo(a); afterImage.CopyTo(b);
            Assert.Equal(a.ToArray(), b.ToArray());
        }
    }

    [Theory]
    [InlineData("invoice-standard")]
    [InlineData("invoice-offer")]
    [InlineData("invoice-order")]
    [InlineData("custom-position")]
    public void Custom_KeepsPositionsRepeatedImagesAndBreaksInsteadOfFixedOverlays(string key)
    {
        var html = Html(Custom);
        using var zip = new ZipArchive(new MemoryStream(InvoiceWordDocumentRenderer.Render(html, key, Custom)));
        var chunk = Read(zip, "word/afchunk.html");
        Assert.Equal(html, chunk);
        Assert.Equal(2, chunk.Split("data:image/png", StringSplitOptions.None).Length - 1);
        Assert.Contains("text-align:right; page-break-before:always", chunk);
        Assert.Contains("Конец", chunk);
        Assert.DoesNotContain("<w:drawing>", Read(zip, "word/document.xml"));
    }

    [Theory]
    [InlineData("invoice-standard")]
    [InlineData("invoice-offer")]
    [InlineData("invoice-order")]
    public void EditedBuiltIn_DoesNotDiscardExtraTextCssOrPosition(string key)
    {
        var source = Source(key).Replace("</body>", "<div style=\"text-align:right\">ДОПОЛНЕНИЕ{{seller.sealHtml}}</div></body>", StringComparison.Ordinal);
        var html = Html(source);
        using var zip = new ZipArchive(new MemoryStream(InvoiceWordDocumentRenderer.Render(html, key, source)));
        Assert.Contains("ДОПОЛНЕНИЕ", Read(zip, "word/afchunk.html"));
        Assert.Contains("data:image/png", Read(zip, "word/afchunk.html"));
        Assert.DoesNotContain("<w:drawing>", Read(zip, "word/document.xml"));
    }
}
