using System.Buffers.Binary;
using System.Net;

namespace PayPlanner.Api.Services.Documents;

/// <summary>Размеры для новой печатной копии; исторический растр не декодируется и не изменяется.</summary>
public static class DocumentPrintImage
{
    private const int MaximumHeaderCharacters = 131072;

    public static string Render(string? safeDataUrl, string alt, string cssClass, int maximumWidth, int maximumHeight)
    {
        if (string.IsNullOrWhiteSpace(safeDataUrl)) return string.Empty;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumHeight);
        var (width, height) = Fit(safeDataUrl, maximumWidth, maximumHeight);
        return $"<img src=\"{WebUtility.HtmlEncode(safeDataUrl)}\" alt=\"{WebUtility.HtmlEncode(alt)}\" class=\"{WebUtility.HtmlEncode(cssClass)}\" width=\"{width}\" height=\"{height}\" style=\"width: {width}px; height: {height}px; object-fit: contain;\" />";
    }

    private static (int Width, int Height) Fit(string dataUrl, int maximumWidth, int maximumHeight)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return (maximumWidth, maximumHeight);
        var payload = dataUrl.AsSpan(comma + 1);
        var header = ReadPrefix(payload, 64);
        var dimensions = ReadDimensions(header);
        if (dimensions is null && header.AsSpan().StartsWith(new byte[] { 0xff, 0xd8 }))
            dimensions = ReadDimensions(ReadPrefix(payload, MaximumHeaderCharacters));
        if (dimensions is not { Width: > 0, Height: > 0 } size) return (maximumWidth, maximumHeight);
        var scale = Math.Min((double)maximumWidth / size.Width, (double)maximumHeight / size.Height);
        return (Math.Clamp((int)Math.Round(size.Width * scale), 1, maximumWidth),
            Math.Clamp((int)Math.Round(size.Height * scale), 1, maximumHeight));
    }

    // Только ограниченный заголовок: не распаковываем пиксели, ICC, EXIF и текстовые блоки.
    private static byte[] ReadPrefix(ReadOnlySpan<char> payload, int limit)
    {
        var encoded = new char[Math.Min(payload.Length, limit)];
        var count = 0;
        foreach (var character in payload[..Math.Min(payload.Length, limit)])
            if (!char.IsWhiteSpace(character)) encoded[count++] = character;
        count -= count % 4;
        var bytes = new byte[count / 4 * 3];
        return Convert.TryFromBase64Chars(encoded.AsSpan(0, count), bytes, out var written)
            ? bytes[..written] : [];
    }

    private static (uint Width, uint Height)? ReadDimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13 && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            return (BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]));

        if (bytes.Length >= 25 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            var chunk = bytes.Slice(12, 4);
            if (chunk.SequenceEqual("VP8X"u8) && bytes.Length >= 30 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) >= 10)
                return (ReadUInt24(bytes[24..]) + 1, ReadUInt24(bytes[27..]) + 1);
            if (chunk.SequenceEqual("VP8L"u8) && bytes[20] == 0x2f && BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) >= 5)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]);
                return ((bits & 0x3fff) + 1, ((bits >> 14) & 0x3fff) + 1);
            }
            if (chunk.SequenceEqual("VP8 "u8) && bytes.Length >= 30 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]) >= 10
                && (bytes[20] & 1) == 0 && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9d, 1, 0x2a }))
                return ((uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[26..]) & 0x3fff),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(bytes[28..]) & 0x3fff));
        }

        if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8) return null;
        var offset = 2;
        while (offset + 3 < bytes.Length)
        {
            if (bytes[offset++] != 0xff) return null;
            while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
            if (offset >= bytes.Length) return null;
            var marker = bytes[offset++];
            if (marker is 0xda or 0xd9) return null;
            if (marker is 0x01 or >= 0xd0 and <= 0xd7) continue;
            if (offset + 2 > bytes.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            if (length < 2 || length > bytes.Length - offset) return null;
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc) && length >= 8)
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 5)..]),
                    BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 3)..]));
            offset += length;
        }
        return null;
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> bytes) => (uint)(bytes[0] | bytes[1] << 8 | bytes[2] << 16);
}
