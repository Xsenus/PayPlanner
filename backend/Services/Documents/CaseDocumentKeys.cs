using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Documents;

public static class CaseDocumentKeys
{
    // Template codes retain their existing 64-character limit; the derived key
    // also contains "template-". Keep the full identity, including Unicode.
    public static bool IsValid(string? value)
        => value is { Length: > 0 and <= ClientCaseDocument.MaxDocumentKeyLength }
            && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return IsValid(normalized) ? normalized : null;
    }
}
