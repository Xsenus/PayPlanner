using System.IO.Compression;
using System.Text.Json;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Acts;
using PayPlanner.Api.Services.Companies;
using PayPlanner.Api.Services.Contracts;
using PayPlanner.Api.Services.Documents;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DocumentImagePrintOptionsTests
{
    private const string Image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private const string OtherImage = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAYAAABzenr0AAAAWUlEQVR4AezVQQ0AIQxE0V0s4AVxCEAcXtAAEuYwIczhN+mpl/alacv3OGgAAQTyBWpv20l1ZvIF1ARuHQEEEEAAAQTyBdaYv5PqW+YLqAncOgIIXBdQS3oAAAD//1gyo7YAAAAGSURBVAMA4cpAQbU7YKYAAAAASUVORK5CYII=";
    private const string ImageTemplate = "{{seller.logoHtml}}|{{seller.sealHtml}}|{{seller.signatureHtml}}";

    public static IEnumerable<object[]> Combinations()
    {
        for (var mask = 0; mask < 8; mask++) yield return [mask];
    }

    private static DocumentImagePrintOptions Options(int mask) => new((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
    private static LegalEntity Seller(string? image = Image) => new() { Id = 9, ShortName = "Продавец", LogoDataUrl = image, SealDataUrl = image, DirectorSignatureDataUrl = image };
    private static Payment Invoice(PaymentType type = PaymentType.Income) => new() { Id = 7, Account = "CMP-03", Date = new DateTime(2026, 10, 29), Amount = 100m, Type = type };
    private static Act Act(Payment? invoice = null) => new() { Id = 8, Number = "CMP-03", Date = new DateTime(2026, 10, 29), Amount = 100m, Invoice = invoice ?? Invoice() };
    private static Contract Contract() => new() { Id = 10, Number = "CMP-03", Date = new DateTime(2026, 10, 29) };
    private static string Render(string document, LegalEntity? seller, DocumentImagePrintOptions options) => document switch
    {
        "invoice" => InvoiceDocumentTemplateEngine.RenderHtml(ImageTemplate, Invoice(), [], seller, options),
        "act" => ActDocumentTemplateEngine.RenderHtml(ImageTemplate, Act(), [], seller, options),
        "contract" => ContractDocumentTemplateEngine.RenderHtml(ImageTemplate, Contract(), seller, options),
        _ => throw new ArgumentOutOfRangeException(nameof(document)),
    };

    private static void AssertImages(string html, DocumentImagePrintOptions expected)
    {
        Assert.Equal(expected.IncludeLogo, html.Contains("class=\"logo\"", StringComparison.Ordinal));
        Assert.Equal(expected.IncludeSeal, html.Contains("class=\"seal\"", StringComparison.Ordinal));
        Assert.Equal(expected.IncludeSignature, html.Contains("class=\"signature\"", StringComparison.Ordinal));
        Assert.DoesNotContain("{{", html);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Render_RespectsEveryIndependentImageCombination(int mask)
    {
        foreach (var document in new[] { "invoice", "act", "contract" })
        {
            var options = Options(mask);
            var seller = Seller();
            var original = JsonSerializer.Serialize(seller);
            var html = Render(document, seller, options);
            AssertImages(html, options);
            Assert.Equal(original, JsonSerializer.Serialize(seller));
            Assert.Equal(options.IncludeLogo ? Image : null, options.LogoOf(seller));
            Assert.Equal(options.IncludeSeal ? Image : null, options.SealOf(seller));
            Assert.Equal(options.IncludeSignature ? Image : null, options.SignatureOf(seller));
        }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void ExpenseAct_DoesNotStampCounterpartyDocumentWithOurImages(int mask)
    {
        var html = ActDocumentTemplateEngine.RenderHtml(ImageTemplate, Act(Invoice(PaymentType.Expense)), [], Seller(), Options(mask));
        AssertImages(html, Options(0));
        Assert.DoesNotContain(Image, html);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void ExpenseInvoice_UsesOnlyResolvedCounterpartyImages(int mask)
    {
        var invoice = Invoice(PaymentType.Expense);
        invoice.LegalEntity = Seller(OtherImage);
        var html = InvoiceDocumentTemplateEngine.RenderHtml(ImageTemplate, invoice, [], Seller(), Options(mask));
        AssertImages(html, Options(mask));
        Assert.DoesNotContain(Image, html);
        Assert.Equal(mask != 0, html.Contains(OtherImage, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://example.invalid/logo.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+")]
    [InlineData("data:image/png;base64,not-base64")]
    [InlineData("data:image/png;base64,AAAA\" onerror=\"alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void ExplicitSelection_RejectsExternalAndUnsafeImageSources(string? source)
    {
        foreach (var document in new[] { "invoice", "act", "contract" })
        {
            var html = Render(document, Seller(source), Options(7));
            AssertImages(html, Options(0));
            Assert.DoesNotContain("<img", html);
        }
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/webp")]
    public void ExplicitSelection_PreservesExistingInlineFormatsWithoutNewUploadValidation(string mime)
    {
        // Container/base64 compatibility only: the print reader must not revalidate a historical raster upload.
        var value = $"data:{mime};base64,AAAA";
        foreach (var document in new[] { "invoice", "act", "contract" })
        {
            var html = Render(document, Seller(value), Options(7));
            AssertImages(html, Options(7));
            Assert.Contains(value, html);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingCombinedFlag_PreservesInvoiceAndActOutput(bool combined)
    {
        var seller = Seller();
        var invoice = Invoice();
        var act = Act(invoice);
        Assert.Equal(InvoiceDocumentTemplateEngine.RenderHtml(InvoiceDocumentTemplateEngine.DefaultTemplateHtml, invoice, [], seller, combined),
            InvoiceDocumentTemplateEngine.RenderHtml(InvoiceDocumentTemplateEngine.DefaultTemplateHtml, invoice, [], seller, new DocumentImagePrintOptions(true, combined, combined)));
        Assert.Equal(ActDocumentTemplateEngine.RenderHtml(act, [], seller, combined),
            ActDocumentTemplateEngine.RenderHtml(act, [], seller, new DocumentImagePrintOptions(false, combined, combined)));
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void InvoiceWord_EmbedsOnlySelectedImages(int mask)
    {
        var options = Options(mask);
        var html = InvoiceDocumentTemplateEngine.RenderHtml(InvoiceDocumentTemplateEngine.DefaultTemplateHtml, Invoice(), [], Seller(), options);
        using var archive = new ZipArchive(new MemoryStream(InvoiceStandardWordDocumentRenderer.Render(html)), ZipArchiveMode.Read);
        var images = archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ToArray();
        Assert.Equal((options.IncludeLogo ? 1 : 0) + (options.IncludeSeal ? 1 : 0) + (options.IncludeSignature ? 1 : 0), images.Length);
        foreach (var image in images)
        {
            using var stream = image.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.Equal(Convert.FromBase64String(Image[(Image.IndexOf(',') + 1)..]), bytes.ToArray());
        }
    }

    [Fact]
    public void FrozenSnapshot_UsesOnlyStoredImagesAndDoesNotFallBackToLiveProfile()
    {
        var live = Seller(OtherImage);
        var snapshot = SellerLegalEntityComposer.ToSnapshotJson(Seller(), 11, 12, 13, imagesFrozen: true);
        var frozen = SellerLegalEntityComposer.FromSnapshot(snapshot, live, new Dictionary<int, string> { [11] = Image, [12] = Image, [13] = Image });
        foreach (var document in new[] { "invoice", "act", "contract" })
        {
            var html = Render(document, frozen, Options(7));
            AssertImages(html, Options(7));
            Assert.Contains(Image, html);
            Assert.DoesNotContain(OtherImage, html);
            var missingVersions = SellerLegalEntityComposer.FromSnapshot(snapshot, live, new Dictionary<int, string>());
            AssertImages(Render(document, missingVersions, Options(7)), Options(0));
        }
        Assert.Equal(OtherImage, live.LogoDataUrl);
    }

    [Fact]
    public void MissingSellerAndUnselectedLegacyContract_ProduceNoImages()
    {
        foreach (var document in new[] { "invoice", "act", "contract" }) AssertImages(Render(document, null, Options(7)), Options(0));
        Assert.Equal("||", ContractDocumentTemplateEngine.RenderHtml(ImageTemplate, Contract(), Seller()));
        Assert.Equal(ContractDocumentTemplateEngine.RenderHtml(Contract(), Seller()),
            ContractDocumentTemplateEngine.RenderHtml(ContractDocumentTemplateEngine.DefaultTemplateHtml, Contract(), Seller(), Options(0)));
    }
}
