namespace PayPlanner.Api.Services.Companies;

/// <summary>
/// Проверка фирменных изображений компании (логотип, печать, подпись), которые хранятся как data-URL.
/// Допускаются только растровые форматы PNG, JPEG и WebP: SVG и прочие типы могут содержать скрипты
/// и ненадёжно рендерятся в печатных формах. Содержимое должно быть корректным base64.
/// </summary>
public static class InlineImageDataUrlValidator
{
    private static readonly string[] AllowedMimeTypes = ["image/png", "image/jpeg", "image/webp"];
    private const string Prefix = "data:";
    private const string Base64Marker = ";base64,";

    /// <returns><c>null</c>, если значение пустое или допустимое; иначе описание проблемы.</returns>
    public static string? Validate(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
        {
            return null;
        }

        var value = dataUrl.Trim();
        if (!value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return "Изображение должно быть передано как data-URL.";
        }

        var markerIndex = value.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return "Изображение должно быть закодировано в base64.";
        }

        var mime = value[Prefix.Length..markerIndex].Trim().ToLowerInvariant();
        if (!AllowedMimeTypes.Contains(mime))
        {
            return "Допустимые форматы изображения: PNG, JPEG и WebP.";
        }

        var payload = value[(markerIndex + Base64Marker.Length)..];
        if (payload.Length == 0)
        {
            return "Файл изображения пустой.";
        }

        var buffer = new byte[(payload.Length * 3 / 4) + 3];
        return Convert.TryFromBase64String(payload, buffer, out _)
            ? null
            : "Содержимое изображения повреждено.";
    }
}
