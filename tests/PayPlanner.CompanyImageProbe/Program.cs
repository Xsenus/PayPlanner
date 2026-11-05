using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using SkiaSharp;

// Refuse unsupported execution before touching Skia or constructing any image fixture.
if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64
    || Environment.GetEnvironmentVariable("PAYPLANNER_IMAGE_PROBE") != "isolated-linux"
    || Environment.GetEnvironmentVariable("Email__Enabled") != "false"
    || Environment.GetEnvironmentVariable("BackgroundServices__Enabled") != "false")
{
    Console.WriteLine(JsonSerializer.Serialize(new { status = "refused", reason = "Requires isolated linux-x64 probe runner; no decoder executed." }));
    return 64;
}

return Probe.Run();

internal static class Probe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static int Run()
    {
        var cases = new List<object>();
        var passed = true;
        var clock = Stopwatch.StartNew();
        void Check(string name, string value, string kind, bool? expectedAccepted)
        {
            var elapsed = Stopwatch.StartNew();
            var error = CompanyImageUploadValidator.ValidateNewValue(null, value, kind);
            var peak = Process.GetCurrentProcess().PeakWorkingSet64;
            var ok = (expectedAccepted is null || (error is null) == expectedAccepted)
                && peak <= 384L * 1024 * 1024 && elapsed.Elapsed < TimeSpan.FromSeconds(15);
            passed &= ok;
            var result = new { name, scope = expectedAccepted is null ? "resource-only" : "validation", passed = ok, accepted = error is null, error, peakWorkingSetBytes = peak, elapsedMs = elapsed.ElapsedMilliseconds };
            cases.Add(result);
            Console.WriteLine(JsonSerializer.Serialize(new { type = "case", result }));
        }

        try
        {
            var png = Fixture.Png();
            var opaque = Fixture.Png(opaque: true);
            var jpeg = Fixture.Jpeg();
            foreach (var kind in new[] { CompanyBrandImageKinds.Logo, CompanyBrandImageKinds.Seal, CompanyBrandImageKinds.Signature })
                Check($"transparent-png-{kind}", Fixture.Url(png), kind, true);
            Check("jpeg-logo", Fixture.Url(jpeg, "image/jpeg"), CompanyBrandImageKinds.Logo, true);
            Check("opaque-seal", Fixture.Url(opaque), CompanyBrandImageKinds.Seal, false);
            Check("opaque-signature", Fixture.Url(opaque), CompanyBrandImageKinds.Signature, false);
            Check("jpeg-disguised-as-png", Fixture.Url(jpeg), CompanyBrandImageKinds.Logo, false);
            Check("png-disguised-as-jpeg", Fixture.Url(png, "image/jpeg"), CompanyBrandImageKinds.Logo, false);
            Check("random-bytes", Fixture.Url([65, 65, 65, 65]), CompanyBrandImageKinds.Logo, false);
            Check("truncated-png", Fixture.Url(png[..(png.Length / 2)]), CompanyBrandImageKinds.Logo, false);
            Check("truncated-jpeg", Fixture.Url(jpeg[..(jpeg.Length / 2)]), CompanyBrandImageKinds.Logo, false);
            Check("apng", Fixture.Url(Fixture.Animated(png)), CompanyBrandImageKinds.Logo, false);
            var bomb = (byte[])png.Clone();
            BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(16), 65535);
            BinaryPrimitives.WriteInt32BigEndian(bomb.AsSpan(20), 65535);
            BinaryPrimitives.WriteUInt32BigEndian(bomb.AsSpan(29), Fixture.Crc32(bomb.AsSpan(12, 17)));
            Check("oversized-header", Fixture.Url(bomb), CompanyBrandImageKinds.Logo, false);
            Check("inclusive-four-megapixels", Fixture.Url(Fixture.Png(width: 2000, height: 2000)), CompanyBrandImageKinds.Logo, true);
            // These adversarial metadata cases qualify resource handling, not the metadata's semantic validity.
            Check("compressed-icc-128mib", Fixture.Url(Fixture.WithCompressedMetadata(png, "iCCP", 128 * 1024 * 1024)), CompanyBrandImageKinds.Logo, null);
            Check("compressed-text-128mib", Fixture.Url(Fixture.WithCompressedMetadata(png, "zTXt", 128 * 1024 * 1024)), CompanyBrandImageKinds.Logo, null);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = passed ? "passed" : "failed",
                platform = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                skiaAssemblyVersion = typeof(SKCodec).Assembly.GetName().Version?.ToString(),
                elapsedMs = clock.ElapsedMilliseconds,
                cases,
            }, JsonOptions));
            return passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "failed",
                exception = error.GetType().Name,
                message = error.Message,
                hResult = error.HResult,
                innerMessage = error.InnerException?.Message,
                runtimeFile = CheckRuntimeFile(),
                cases
            }, JsonOptions));
            return 1;
        }
    }

    private static object CheckRuntimeFile()
    {
        var file = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Linq.dll");
        var descriptor = Open(file, 0);
        var error = descriptor < 0 ? Marshal.GetLastPInvokeError() : 0;
        if (descriptor >= 0) Close(descriptor);
        return new { name = "System.Linq.dll", exists = File.Exists(file), openError = error };
    }

    [DllImport("libc.so.6", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc.so.6", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}

internal static class Fixture
{
    internal static byte[] Png(bool opaque = false, int width = 32, int height = 32)
        => Encode(SKEncodedImageFormat.Png, opaque, width, height);

    internal static byte[] Jpeg() => Encode(SKEncodedImageFormat.Jpeg, true, 32, 32);

    private static byte[] Encode(SKEncodedImageFormat format, bool opaque, int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.Green };
            canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    internal static string Url(byte[] bytes, string mime = "image/png") => $"data:{mime};base64,{Convert.ToBase64String(bytes)}";

    internal static byte[] WithCompressedMetadata(byte[] png, string type, int expandedBytes)
    {
        using var content = new MemoryStream();
        content.Write(Encoding.ASCII.GetBytes("probe\0"));
        content.WriteByte(0); // PNG compression method 0, shared by iCCP and zTXt.
        using (var compressor = new ZLibStream(content, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var buffer = new byte[16 * 1024];
            for (var written = 0; written < expandedBytes; written += buffer.Length)
            {
                if (type == "zTXt") Array.Fill(buffer, (byte)'A');
                else if (written == 0)
                {
                    // Plausible ICC header prevents a trivial zero-length-header rejection.
                    BinaryPrimitives.WriteInt32BigEndian(buffer, expandedBytes);
                    BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8), 0x04300000);
                    Encoding.ASCII.GetBytes("mntrRGB XYZ ", buffer.AsSpan(12, 12));
                    Encoding.ASCII.GetBytes("acsp", buffer.AsSpan(36, 4));
                }
                else Array.Clear(buffer);
                compressor.Write(buffer, 0, Math.Min(buffer.Length, expandedBytes - written));
            }
        }
        var file = png[..33].Concat(Chunk(type, content.ToArray())).Concat(png[33..]).ToArray();
        if (Url(file).Length > CompanyImageUploadValidator.MaxDataUrlLength)
            throw new InvalidOperationException("Metadata fixture would be rejected before reaching the native decoder.");
        return file;
    }

    internal static byte[] Animated(byte[] png)
    {
        using var result = new MemoryStream();
        result.Write(png.AsSpan(0, 33));
        var animation = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(animation, 2);
        result.Write(Chunk("acTL", animation));
        for (var frame = 0; frame < 2; frame++)
        {
            var control = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(control, frame);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(4), 32);
            BinaryPrimitives.WriteInt32BigEndian(control.AsSpan(8), 32);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(20), 1);
            BinaryPrimitives.WriteUInt16BigEndian(control.AsSpan(22), 10);
            result.Write(Chunk("fcTL", control));
            var sequence = 2;
            for (var offset = 8; offset + 12 <= png.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
                if (Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
                {
                    if (frame == 0) result.Write(Chunk("IDAT", png.AsSpan(offset + 8, length)));
                    else
                    {
                        var data = new byte[length + 4];
                        BinaryPrimitives.WriteInt32BigEndian(data, sequence++);
                        png.AsSpan(offset + 8, length).CopyTo(data.AsSpan(4));
                        result.Write(Chunk("fdAT", data));
                    }
                }
                offset += length + 12;
            }
        }
        result.Write(Chunk("IEND", []));
        return result.ToArray();
    }

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

    private static byte[] Chunk(string type, ReadOnlySpan<byte> content)
    {
        var bytes = new byte[content.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(bytes, content.Length);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
        content.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), Crc32(bytes.AsSpan(4, content.Length + 4)));
        return bytes;
    }
}
