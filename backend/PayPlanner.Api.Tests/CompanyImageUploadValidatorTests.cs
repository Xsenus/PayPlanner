using System.Buffers.Binary;
using System.Text;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using SkiaSharp;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyImageUploadValidatorTests
{
    [Theory]
    [InlineData(CompanyBrandImageKinds.Logo)]
    [InlineData(CompanyBrandImageKinds.Seal)]
    [InlineData(CompanyBrandImageKinds.Signature)]
    public void NewPng_IsFullyDecoded(string kind)
        => Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.TransparentPng, kind));

    [Fact]
    public void Logo_AcceptsRealJpegAndOpaquePng()
    {
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.Jpeg, CompanyBrandImageKinds.Logo));
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.OpaquePng, CompanyBrandImageKinds.Logo));
    }

    [Theory]
    [InlineData(CompanyBrandImageKinds.Seal)]
    [InlineData(CompanyBrandImageKinds.Signature)]
    public void Stamps_RequirePngAndActualTransparency(string kind)
    {
        Assert.Contains("PNG", CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.Jpeg, kind));
        Assert.Contains("прозрачный фон", CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.OpaquePng, kind));
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.Encode(SKEncodedImageFormat.Png, transparent: true, alpha: 128), kind));
    }

    [Theory]
    [InlineData(CompanyBrandImageKinds.Logo)]
    [InlineData(CompanyBrandImageKinds.Seal)]
    [InlineData(CompanyBrandImageKinds.Signature)]
    public void UnchangedLegacyValuesAndDeletion_AreNotRevalidated(string kind)
    {
        const string legacy = "data:image/webp;base64,QUFBQUFB";
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(legacy, $" {legacy} ", kind));
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(legacy, null, kind));
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(legacy, " ", kind));
        Assert.NotNull(CompanyImageUploadValidator.ValidateNewValue(legacy, legacy + "AA", kind));
    }

    [Theory]
    [InlineData("not-a-data-url", "data-URL")]
    [InlineData("data:image/png,QUFB", "base64")]
    [InlineData("data:image/png;base64,***", "повреждено")]
    [InlineData("data:image/png;base64,", "пустой")]
    [InlineData("data:image/png;base64,QUFBQUFB", "Не удалось прочитать")]
    [InlineData("data:image/svg+xml;base64,QUFB", "только PNG и JPG")]
    [InlineData("data:image/webp;base64,QUFB", "только PNG и JPG")]
    public void NewContent_RejectsInvalidEnvelopeAndNonImages(string value, string expected)
        => Assert.Contains(expected, CompanyImageUploadValidator.ValidateNewValue(null, value, CompanyBrandImageKinds.Logo));

    [Fact]
    public void DeclaredMime_MustMatchTheDecodedFormat()
    {
        var disguisedPng = CompanyImageTestData.TransparentPng.Replace("image/png", "image/jpeg", StringComparison.Ordinal);
        var disguisedJpeg = CompanyImageTestData.Jpeg.Replace("image/jpeg", "image/png", StringComparison.Ordinal);
        Assert.Contains("не соответствует", CompanyImageUploadValidator.ValidateNewValue(null, disguisedPng, CompanyBrandImageKinds.Logo));
        Assert.Contains("не соответствует", CompanyImageUploadValidator.ValidateNewValue(null, disguisedJpeg, CompanyBrandImageKinds.Signature));
    }

    [Fact]
    public void TruncatedPng_IsRejectedEvenWhenItsHeaderCanBeRead()
    {
        var bytes = Convert.FromBase64String(CompanyImageTestData.TransparentPng.Split(',')[1]);
        Assert.Contains("не полностью", CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.DataUrl(bytes[..(bytes.Length / 2)]), CompanyBrandImageKinds.Logo));
    }

    [Theory]
    [InlineData(4097, 1)]
    [InlineData(4096, 1025)]
    [InlineData(8192, 8192)]
    [InlineData(65535, 65535)]
    public void OversizedHeader_IsRejectedBeforeAllocatingPixels(int width, int height)
    {
        var bytes = Convert.FromBase64String(CompanyImageTestData.TransparentPng.Split(',')[1]);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(29, 4), CompanyImageTestData.Crc32(bytes.AsSpan(12, 17)));
        Assert.Contains("4096 пикселей", CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.DataUrl(bytes), CompanyBrandImageKinds.Logo));
    }

    [Fact]
    public void InclusivePixelBoundary_IsDecodedSuccessfully()
        => Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.Encode(SKEncodedImageFormat.Png, width: 2000, height: 2000), CompanyBrandImageKinds.Logo));

    [Fact]
    public void EncodedSize_IsBoundedBeforeParsing()
        => Assert.Contains("слишком большое", CompanyImageUploadValidator.ValidateNewValue(null,
            new string('A', CompanyImageUploadValidator.MaxDataUrlLength + 1), CompanyBrandImageKinds.Logo));

    [Fact]
    public void AnimatedPng_IsRejected()
        => Assert.Contains("Анимированные", CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.AnimatedPng(), CompanyBrandImageKinds.Logo));

    [Fact]
    public void TruncatedJpeg_IsRejected()
    {
        var bytes = Convert.FromBase64String(CompanyImageTestData.Jpeg.Split(',')[1]);
        Assert.NotNull(CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.DataUrl(bytes[..(bytes.Length / 2)], "image/jpeg"), CompanyBrandImageKinds.Logo));
    }

    [Fact]
    public void AnimationTextInsideAnotherChunk_IsNotAnAnimationControl()
    {
        var bytes = Convert.FromBase64String(CompanyImageTestData.TransparentPng.Split(',')[1]);
        var text = CompanyImageTestData.Chunk("tEXt", Encoding.ASCII.GetBytes("comment\0acTL"));
        var file = bytes[..33].Concat(text).Concat(bytes[33..]).ToArray();
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.DataUrl(file), CompanyBrandImageKinds.Seal));
    }

    [Fact]
    public void InvalidChunkLength_DoesNotOverflowTheAnimationScan()
    {
        var bytes = Convert.FromBase64String(CompanyImageTestData.TransparentPng.Split(',')[1]);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33, 4), uint.MaxValue);
        Assert.NotNull(CompanyImageUploadValidator.ValidateNewValue(null, CompanyImageTestData.DataUrl(bytes), CompanyBrandImageKinds.Logo));
    }

    [Fact]
    public void UppercaseHeaderAndBase64Whitespace_AreAccepted()
    {
        var value = CompanyImageTestData.TransparentPng.Replace("data:image/png;base64,", " DATA:IMAGE/PNG;BASE64,\r\n", StringComparison.Ordinal) + " ";
        Assert.Null(CompanyImageUploadValidator.ValidateNewValue(null, value, CompanyBrandImageKinds.Seal));
    }

    [Fact]
    public void CancellationAndInvalidKind_DoNotDecode()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompanyImageUploadValidator.ValidateNewValue(null,
            CompanyImageTestData.TransparentPng, CompanyBrandImageKinds.Seal, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => CompanyImageUploadValidator.ValidateNewValue(null, null, "other"));
    }

    [Fact]
    public async Task ConcurrentValidation_DoesNotSharePixelBuffers()
    {
        await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var opaque = index % 2 == 0;
            var result = CompanyImageUploadValidator.ValidateNewValue(null,
                opaque ? CompanyImageTestData.OpaquePng : CompanyImageTestData.TransparentPng, CompanyBrandImageKinds.Signature);
            Assert.Equal(opaque, result is not null);
        })));
    }
}

internal static class CompanyImageTestData
{
    internal static readonly string TransparentPng = Encode(SKEncodedImageFormat.Png);
    internal static readonly string OpaquePng = Encode(SKEncodedImageFormat.Png, transparent: false);
    internal static readonly string Jpeg = Encode(SKEncodedImageFormat.Jpeg, transparent: false);

    internal static string Encode(SKEncodedImageFormat format, bool transparent = true, int width = 32, int height = 32, byte alpha = 255)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
            using var paint = new SKPaint { Color = new SKColor(20, 120, 60, alpha) };
            canvas.DrawRect(width / 4f, height / 4f, Math.Max(1, width / 2f), Math.Max(1, height / 2f), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 90);
        return DataUrl(encoded.ToArray(), format == SKEncodedImageFormat.Jpeg ? "image/jpeg" : "image/png");
    }

    internal static string DataUrl(byte[] bytes, string mime = "image/png") => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }

    internal static string AnimatedPng()
    {
        // Два настоящих PNG-кадра из native encoder упаковываются в APNG; production-декодер не подменяется.
        var png = Convert.FromBase64String(TransparentPng.Split(',')[1]);
        using var output = new MemoryStream();
        output.Write(png.AsSpan(0, 8));
        output.Write(Chunk("IHDR", png.AsSpan(16, 13)));
        var animation = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(animation, 2);
        output.Write(Chunk("acTL", animation));
        for (var frame = 0; frame < 2; frame++)
        {
            var control = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(control, frame);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(4), 32);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(8), 32);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22), 10);
            output.Write(Chunk("fcTL", control));
            for (var offset = 8; offset < png.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
                if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
                {
                    var content = png.AsSpan(offset + 8, length);
                    if (frame == 0) output.Write(Chunk("IDAT", content));
                    else
                    {
                        var data = new byte[length + 4];
                        BinaryPrimitives.WriteInt32BigEndian(data, 2);
                        content.CopyTo(data.AsSpan(4));
                        output.Write(Chunk("fdAT", data));
                    }
                }
                offset += length + 12;
            }
        }
        output.Write(Chunk("IEND", []));
        return DataUrl(output.ToArray());
    }

    internal static byte[] Chunk(string type, ReadOnlySpan<byte> content)
    {
        var chunk = new byte[content.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, content.Length);
        Encoding.ASCII.GetBytes(type, chunk.AsSpan(4, 4));
        content.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc32(chunk.AsSpan(4, content.Length + 4)));
        return chunk;
    }
}
