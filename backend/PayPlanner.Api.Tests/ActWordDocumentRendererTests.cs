using System.IO.Compression;
using System.Text;
using PayPlanner.Api.Services.Acts;
using Xunit;

namespace PayPlanner.Api.Tests;

public class ActWordDocumentRendererTests
{
    [Theory]
    [InlineData(2, 4297680, 4678680)]
    [InlineData(3, 4344670, 4725670)]
    public void Render_ShiftsSignedOverlaysWithAdditionalItemRows(
        int itemCount,
        long expectedSealY,
        long expectedSignatureY)
    {
        const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
        var items = string.Concat(Enumerable.Range(1, itemCount).Select(
            index => $"<tr><td class=\"bor item-name\">Услуга {index}</td></tr>"));
        var html = $$"""
<!doctype html><html><head><style></style></head><body>
<table class="items-table" cellspacing="0">{{items}}</table>
<img src="data:image/png;base64,{{image}}" alt="" class="seal" />
<img src="data:image/png;base64,{{image}}" alt="" class="signature" />
</body></html>
""";

        using var archive = new ZipArchive(
            new MemoryStream(ActWordDocumentRenderer.Render(html)),
            ZipArchiveMode.Read);
        var documentXml = ReadText(archive, "word/document.xml");

        Assert.Contains($"<wp:posOffset>{expectedSealY}</wp:posOffset>", documentXml, StringComparison.Ordinal);
        Assert.Contains($"<wp:posOffset>{expectedSignatureY}</wp:posOffset>", documentXml, StringComparison.Ordinal);
    }

    private static string ReadText(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
