using System.IO.Compression;
using System.Text;
using PayPlanner.Api.Services.Invoices;
using Xunit;

namespace PayPlanner.Api.Tests;

public class InvoiceStandardWordDocumentRendererTests
{
    [Fact]
    public void Render_EmbedsFourBrandImagesAsFixedOverlays()
    {
        const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var imageTag = $"<img src=\"data:image/png;base64,{image}\" alt=\"\"";
        var bankCells = string.Concat(Enumerable.Range(1, 16).Select(index => $"<td>Банк {index}</td>"));
        var html = $$"""
<!doctype html><html><body>
<table><tr><td class="doc-title">ИП Тестовый Т. Т.</td><td class="logo-wrap">{{imageTag}} class="logo" /></td></tr></table>
<table><tr><td><table class="bank-grid"><tr>{{bankCells}}</tr></table></td><td class="invoice-qr-cell">{{imageTag}} class="invoice-qr" /></td></tr></table>
<div class="doc-main-title text-c">Счет № 28 от 5 апреля 2026 г.</div>
<table class="counterparty-table"><tr><td>Поставщик:</td><td>ИП Тестовый Т. Т.</td></tr><tr><td>Покупатель:</td><td>Тестовый клиент</td></tr></table>
<table class="items-table"><tr><th class="num">№</th><th class="name">Товар</th><th class="qty">Кол-во</th><th class="unit">Ед.</th><th class="price">Цена</th><th class="vat">НДС</th><th class="sum">Сумма</th></tr><tr><td class="num">1</td><td class="name">Услуга</td><td class="qty">1</td><td class="unit">шт.</td><td class="price">100,00</td><td class="vat">-</td><td class="sum">100,00</td></tr></table>
<table class="totals-table"><tr><td class="totals-label">Итого:</td><td class="totals-value">100,00</td></tr></table>
<div class="summary-line">Всего наименований 1, на сумму 100,00 руб.</div><div class="words">Сто рублей 00 копеек</div>
<table class="signature-table"><tr><td>Предприниматель</td><td class="signature-line">{{imageTag}} class="signature" /></td><td class="seal-slot">{{imageTag}} class="seal" /></td><td class="signature-line">Тестовый Т. Т.</td></tr></table>
</body></html>
""";

        var document = InvoiceStandardWordDocumentRenderer.Render(html);

        using var archive = new ZipArchive(new MemoryStream(document), ZipArchiveMode.Read);
        var media = archive.Entries.Where(entry => entry.FullName.StartsWith("word/media/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, media.Length);

        var chunk = ReadText(archive, "word/afchunk.html");
        Assert.DoesNotContain("data:image", chunk, StringComparison.OrdinalIgnoreCase);

        var documentXml = ReadText(archive, "word/document.xml");
        Assert.Contains("<wp:posOffset>5270500</wp:posOffset>", documentXml, StringComparison.Ordinal);
        Assert.Contains("<wp:posOffset>400050</wp:posOffset>", documentXml, StringComparison.Ordinal);
        Assert.Equal(4, documentXml.Split("<w:drawing>", StringSplitOptions.None).Length - 1);
    }

    private static string ReadText(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
