using System.Globalization;

namespace PayPlanner.Api.Services.Notifications;

internal sealed record CaseCourtLegacyKey(int CaseId, string Kind, int SourceId, int UserId)
{
    internal static bool IsCandidate(string? key) => key?.StartsWith("case:", StringComparison.OrdinalIgnoreCase) == true
        && (key.Contains(":court-event:", StringComparison.OrdinalIgnoreCase)
            || key.Contains(":procedural-deadline:", StringComparison.OrdinalIgnoreCase)
            || key.Contains(":hearing-date-changed:", StringComparison.OrdinalIgnoreCase)
            || key.Contains(":decision-date-changed:", StringComparison.OrdinalIgnoreCase));

    internal static bool TryRead(string? key, int userId, out CaseCourtLegacyKey source)
    {
        source = null!;
        if (key is null || key.Length > 512 || userId <= 0) return false;
        var retry = key.IndexOf(":retry:", StringComparison.Ordinal);
        if (retry >= 0) key = key[..retry];
        var parts = key.Split(':');
        if (parts.Length < 5 || parts[0] != "case" || !Id(parts[1], out var caseId)) return false;
        var kind = parts[2] switch
        {
            "court-event" => "event",
            "procedural-deadline" => "deadline",
            "hearing-date-changed" => "hearing-date",
            "decision-date-changed" => "decision-date",
            _ => null
        };
        if (kind is null) return false;
        var caseDate = CaseCourtNotificationVersion.IsCaseDateKind(kind);
        if (caseDate ? parts.Length != 5 : parts.Length is not (6 or 7)) return false;
        if ((caseDate || parts.Length == 7) && (!Id(parts[^1], out var keyUser) || keyUser != userId)) return false;
        var sourceId = caseId;
        if (!caseDate && !Id(parts[3], out sourceId)) return false;
        var version = parts[caseDate ? 3 : 5];
        if (!long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var stamp) || stamp <= 0
            || stamp.ToString(CultureInfo.InvariantCulture) != version) return false;
        if (!caseDate && string.IsNullOrWhiteSpace(parts[4])) return false;
        source = new(caseId, kind, sourceId, userId);
        return true;
    }

    private static bool Id(string text, out int id) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id)
        && id > 0 && id.ToString(CultureInfo.InvariantCulture) == text;
}
