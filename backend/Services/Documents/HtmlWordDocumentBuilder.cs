using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services.Documents;

public static class HtmlWordDocumentBuilder
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private const string RegularFontResource = "PayPlanner.Fonts.DejaVuSansCondensed.ttf";
    private const string BoldFontResource = "PayPlanner.Fonts.DejaVuSansCondensed-Bold.ttf";
    private static readonly Regex EmbeddedFontFaceRegex = new(
        "@font-face\\s*\\{[^}]*\\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static byte[] Build(string html, IReadOnlyList<WordImageOverlay>? overlays = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        overlays ??= [];

        var regularKey = Guid.NewGuid();
        var boldKey = Guid.NewGuid();
        var regularName = $"{regularKey:N}".ToUpperInvariant() + ".odttf";
        var boldName = $"{boldKey:N}".ToUpperInvariant() + ".odttf";
        var normalizedHtml = EmbeddedFontFaceRegex.Replace(html, string.Empty);

        using var packageStream = new MemoryStream();
        using (var package = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddTextEntry(package, "[Content_Types].xml", BuildContentTypesXml(overlays));
            AddTextEntry(package, "_rels/.rels", PackageRelationshipsXml);
            AddTextEntry(package, "word/document.xml", BuildDocumentXml(overlays));
            AddTextEntry(package, "word/_rels/document.xml.rels", BuildDocumentRelationshipsXml(overlays));
            AddTextEntry(package, "word/settings.xml", SettingsXml);
            AddTextEntry(package, "word/afchunk.html", normalizedHtml);
            AddTextEntry(package, "word/fontTable.xml", BuildFontTableXml(regularKey, boldKey));
            AddTextEntry(package, "word/_rels/fontTable.xml.rels", BuildFontRelationshipsXml(regularName, boldName));
            AddByteEntry(package, $"word/fonts/{regularName}", ObfuscateFont(ReadEmbeddedResource(RegularFontResource), regularKey));
            AddByteEntry(package, $"word/fonts/{boldName}", ObfuscateFont(ReadEmbeddedResource(BoldFontResource), boldKey));
            for (var index = 0; index < overlays.Count; index++)
            {
                AddByteEntry(package, $"word/media/image{index + 1}.{overlays[index].Extension}", overlays[index].Content);
            }
        }

        return packageStream.ToArray();
    }

    private static byte[] ReadEmbeddedResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Не найден встроенный шрифт '{resourceName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static byte[] ObfuscateFont(byte[] source, Guid key)
    {
        var result = source.ToArray();
        var hex = key.ToString("N", CultureInfo.InvariantCulture);
        var keyBytes = new byte[16];
        for (var index = 0; index < keyBytes.Length; index++)
        {
            keyBytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
        }

        Array.Reverse(keyBytes);
        for (var index = 0; index < Math.Min(32, result.Length); index++)
        {
            result[index] ^= keyBytes[index % keyBytes.Length];
        }

        return result;
    }

    private static void AddTextEntry(ZipArchive package, string name, string value)
    {
        var entry = package.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(value);
    }

    private static void AddByteEntry(ZipArchive package, string name, byte[] value)
    {
        var entry = package.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(value);
    }

    private static string BuildFontTableXml(Guid regularKey, Guid boldKey) => $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:fonts xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <w:font w:name="DejaVu Sans Condensed">
    <w:altName w:val="DejaVu Sans Condensed"/>
    <w:family w:val="swiss"/>
    <w:pitch w:val="variable"/>
    <w:embedRegular r:id="rIdRegular" w:fontKey="{{{regularKey.ToString().ToUpperInvariant()}}}"/>
    <w:embedBold r:id="rIdBold" w:fontKey="{{{boldKey.ToString().ToUpperInvariant()}}}"/>
  </w:font>
</w:fonts>
""";

    private static string BuildFontRelationshipsXml(string regularName, string boldName) => $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rIdRegular" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/font" Target="fonts/{{regularName}}"/>
  <Relationship Id="rIdBold" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/font" Target="fonts/{{boldName}}"/>
</Relationships>
""";

    private static string BuildContentTypesXml(IReadOnlyList<WordImageOverlay> overlays)
    {
        var imageTypes = string.Join(
            Environment.NewLine,
            overlays
                .GroupBy(item => item.Extension, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"  <Default Extension=\"{group.Key}\" ContentType=\"{group.First().ContentType}\"/>"));
        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Default Extension="html" ContentType="text/html"/>
  <Default Extension="odttf" ContentType="application/vnd.openxmlformats-officedocument.obfuscatedFont"/>
{{imageTypes}}
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/fontTable.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.fontTable+xml"/>
  <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
</Types>
""";
    }

    private static string BuildDocumentRelationshipsXml(IReadOnlyList<WordImageOverlay> overlays)
    {
        var imageRelationships = string.Join(
            Environment.NewLine,
            overlays.Select((item, index) => $"  <Relationship Id=\"rIdImage{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{index + 1}.{item.Extension}\"/>"));
        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rIdHtml" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/aFChunk" Target="afchunk.html"/>
  <Relationship Id="rIdFonts" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/fontTable" Target="fontTable.xml"/>
  <Relationship Id="rIdSettings" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
{{imageRelationships}}
</Relationships>
""";
    }

    private static string BuildDocumentXml(IReadOnlyList<WordImageOverlay> overlays)
    {
        var drawings = string.Join(Environment.NewLine, overlays.Select(BuildDrawingXml));
        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"><w:body><w:altChunk r:id="rIdHtml"/>{{drawings}}<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="567" w:right="476" w:bottom="567" w:left="901" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr></w:body></w:document>
""";
    }

    private static string BuildDrawingXml(WordImageOverlay overlay, int index)
    {
        var id = index + 1;
        var x = PointsToEmu(overlay.X);
        var y = PointsToEmu(overlay.Y);
        var width = PointsToEmu(overlay.Width);
        var height = PointsToEmu(overlay.Height);
        return $$"""
<w:p><w:pPr><w:spacing w:before="0" w:after="0" w:line="1" w:lineRule="exact"/></w:pPr><w:r><w:drawing><wp:anchor distT="0" distB="0" distL="0" distR="0" simplePos="0" relativeHeight="{{overlay.ZOrder}}" behindDoc="0" locked="0" layoutInCell="0" allowOverlap="1"><wp:simplePos x="0" y="0"/><wp:positionH relativeFrom="page"><wp:posOffset>{{x}}</wp:posOffset></wp:positionH><wp:positionV relativeFrom="page"><wp:posOffset>{{y}}</wp:posOffset></wp:positionV><wp:extent cx="{{width}}" cy="{{height}}"/><wp:effectExtent l="0" t="0" r="0" b="0"/><wp:wrapNone/><wp:docPr id="{{id}}" name="{{overlay.Name}}"/><wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr><a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture"><pic:pic><pic:nvPicPr><pic:cNvPr id="{{id}}" name="{{overlay.Name}}"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed="rIdImage{{id}}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{{width}}" cy="{{height}}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r></w:p>
""";
    }

    private static long PointsToEmu(double points) => checked((long)Math.Round(points * 12_700d));

    private const string PackageRelationshipsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rIdOfficeDocument" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
""";

    private const string SettingsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:embedTrueTypeFonts/><w:saveSubsetFonts w:val="false"/></w:settings>
""";
}

public sealed record WordImageOverlay(
    byte[] Content,
    string ContentType,
    string Extension,
    double X,
    double Y,
    double Width,
    double Height,
    string Name,
    int ZOrder);
