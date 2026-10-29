using System.Buffers.Binary;
using PayPlanner.Api.Models;
using SkiaSharp;

namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Проверяет только новое содержимое профиля; исторические изображения и снимки документов не переоценивает.
/// </summary>
public static class CompanyImageUploadValidator
{
    public const int MaxDataUrlLength = 350_000;
    public const int MaxDimension = 4096;
    public const int MaxPixelCount = 4_000_000;
    private const string Base64Marker = ";base64,";

    public static string? ValidateNewValue(string? currentValue, string? nextValue, string kind, CancellationToken ct = default)
    {
        if (kind is not (CompanyBrandImageKinds.Logo or CompanyBrandImageKinds.Seal or CompanyBrandImageKinds.Signature))
            throw new ArgumentOutOfRangeException(nameof(kind));

        // Та же нормализация, что у профиля. Удаление не является новой загрузкой.
        var value = nextValue?.Trim();
        if (string.IsNullOrEmpty(value) || string.Equals(currentValue?.Trim(), value, StringComparison.Ordinal))
            return null;

        ct.ThrowIfCancellationRequested();
        if (value.Length > MaxDataUrlLength)
            return "Изображение слишком большое для сохранения. Уменьшите его и попробуйте снова.";
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return "Изображение должно быть передано как data-URL.";
        var markerIndex = value.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return "Изображение должно быть закодировано в base64.";

        var mime = value[5..markerIndex].Trim();
        var expectedFormat = mime.ToLowerInvariant() switch
        {
            "image/png" => SKEncodedImageFormat.Png,
            "image/jpeg" when kind == CompanyBrandImageKinds.Logo => SKEncodedImageFormat.Jpeg,
            _ => (SKEncodedImageFormat?)null,
        };
        if (expectedFormat is null)
            return kind == CompanyBrandImageKinds.Logo
                ? "Для логотипа допустимы только PNG и JPG."
                : "Для печати и подписи нужен PNG с прозрачным фоном.";

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value[(markerIndex + Base64Marker.Length)..]);
        }
        catch (FormatException)
        {
            return "Содержимое изображения повреждено.";
        }
        if (bytes.Length == 0)
            return "Файл изображения пустой.";

        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return "Не удалось прочитать изображение. Выберите исправный PNG или JPG.";
        if (codec.EncodedFormat != expectedFormat.Value)
            return "Содержимое изображения не соответствует указанному формату.";

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        // Метаданные до буфера; long исключает переполнение произведения сторон.
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixelCount)
            return "Изображение слишком большое: максимум 4096 пикселей по стороне и 4 мегапикселя всего. Уменьшите его.";
        // Некоторые native-сборки показывают APNG как статичный PNG с FrameCount == 0.
        if (HasPngAnimation(bytes) || codec.FrameCount > 1)
            return "Анимированные изображения не поддерживаются. Выберите обычный PNG или JPG.";

        ct.ThrowIfCancellationRequested();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        // Не более 16 МиБ на изображение, обработка трёх полей последовательная.
        var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        if (codec.GetPixels(info, pixels) != SKCodecResult.Success)
            return "Изображение повреждено или загружено не полностью. Выберите исправный файл.";
        ct.ThrowIfCancellationRequested();

        if (kind == CompanyBrandImageKinds.Logo)
            return null;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] < byte.MaxValue)
                return null;
        }
        return "У печати и подписи должен быть прозрачный фон. Выберите PNG с прозрачными областями.";
    }

    private static bool HasPngAnimation(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.StartsWith(signature)) return false;
        // Только обход контейнера, без распаковки/декодирования; границы ограничены входным файлом.
        for (var offset = signature.Length; bytes.Length - offset >= 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12) return false;
            var type = bytes.Slice(offset + 4, 4);
            if (type.SequenceEqual("acTL"u8)) return true;
            if (type.SequenceEqual("IEND"u8)) return false;
            offset += (int)length + 12;
        }
        return false;
    }
}
