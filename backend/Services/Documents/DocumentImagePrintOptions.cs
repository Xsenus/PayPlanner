using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services.Documents;

/// <summary>Явный выбор изображений при выдаче документа; отсутствие выбора сохраняет прежний режим.</summary>
public sealed record DocumentImagePrintOptions(bool IncludeLogo, bool IncludeSeal, bool IncludeSignature)
{
    public static DocumentImagePrintOptions? WithDefaults(DocumentImagePrintOptions? defaults,
        bool legacyLogo, bool legacyCombined, bool? combinedOverride,
        bool? includeLogo, bool? includeSeal, bool? includeSignature)
    {
        // An explicit old menu/query choice keeps its meaning, including the legacy logo behavior.
        if (combinedOverride.HasValue || defaults is null)
            return FromOverrides(legacyLogo, combinedOverride ?? legacyCombined, includeLogo, includeSeal, includeSignature);
        return new(includeLogo ?? defaults.IncludeLogo, includeSeal ?? defaults.IncludeSeal, includeSignature ?? defaults.IncludeSignature);
    }

    public static DocumentImagePrintOptions? FromOverrides(bool legacyLogo, bool legacyCombined,
        bool? includeLogo, bool? includeSeal, bool? includeSignature)
        => includeLogo is null && includeSeal is null && includeSignature is null ? null
            : new(includeLogo ?? legacyLogo, includeSeal ?? legacyCombined, includeSignature ?? legacyCombined);

    public string? LogoOf(LegalEntity? seller) => Select(seller?.LogoDataUrl, IncludeLogo);
    public string? SealOf(LegalEntity? seller) => Select(seller?.SealDataUrl, IncludeSeal);
    public string? SignatureOf(LegalEntity? seller) => Select(seller?.DirectorSignatureDataUrl, IncludeSignature);

    // Только прежняя безопасная проверка inline-растра: не применяем новые upload-ограничения к снимкам.
    private static string? Select(string? value, bool enabled)
        => enabled && !string.IsNullOrWhiteSpace(value) && InlineImageDataUrlValidator.Validate(value) is null
            ? value.Trim()
            : null;
}
