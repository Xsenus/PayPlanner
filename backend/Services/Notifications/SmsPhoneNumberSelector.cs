namespace PayPlanner.Api.Services.Notifications;

public static class SmsPhoneNumberKinds
{
    public const string Mobile = "mobile";
    public const string Work = "work";
    public const string Backup = "backup";
    public const string Unspecified = "unspecified";
}

public sealed record SmsPhoneNumberCandidate(
    string? Value,
    string Kind,
    bool IsPrimary = false);

public sealed record SmsPhoneNumberSelectionResult(
    string? E164,
    string? Kind,
    int ValidCandidates,
    int RejectedCandidates)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(E164);
}

public static class SmsPhoneNumberSelector
{
    public static SmsPhoneNumberSelectionResult Select(
        IEnumerable<SmsPhoneNumberCandidate> candidates,
        SmsPhoneNumberPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        SelectedCandidate? selected = null;
        var validNumbers = new HashSet<string>(StringComparer.Ordinal);
        var rejectedCandidates = 0;
        var sourceOrder = 0;

        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                rejectedCandidates++;
                sourceOrder++;
                continue;
            }

            var normalized = SmsPhoneNumberNormalizer.Normalize(candidate.Value, policy);
            if (!normalized.IsValid || normalized.E164 is null)
            {
                rejectedCandidates++;
                sourceOrder++;
                continue;
            }

            var kind = NormalizeKind(candidate.Kind);
            var current = new SelectedCandidate(
                normalized.E164,
                kind,
                KindPriority(kind),
                candidate.IsPrimary ? 0 : 1,
                sourceOrder);
            if (selected is null || current.CompareTo(selected) < 0)
            {
                selected = current;
            }

            validNumbers.Add(normalized.E164);
            sourceOrder++;
        }

        return new SmsPhoneNumberSelectionResult(
            selected?.E164,
            selected?.Kind,
            validNumbers.Count,
            rejectedCandidates);
    }

    private static string NormalizeKind(string? kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant();
        return normalized is SmsPhoneNumberKinds.Mobile
            or SmsPhoneNumberKinds.Work
            or SmsPhoneNumberKinds.Backup
            ? normalized
            : SmsPhoneNumberKinds.Unspecified;
    }

    private static int KindPriority(string kind)
        => kind switch
        {
            SmsPhoneNumberKinds.Mobile => 0,
            SmsPhoneNumberKinds.Work => 1,
            SmsPhoneNumberKinds.Backup => 2,
            _ => 3,
        };

    private sealed record SelectedCandidate(
        string E164,
        string Kind,
        int KindPriority,
        int PrimaryPriority,
        int SourceOrder)
    {
        public int CompareTo(SelectedCandidate other)
        {
            var comparison = KindPriority.CompareTo(other.KindPriority);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = PrimaryPriority.CompareTo(other.PrimaryPriority);
            return comparison != 0
                ? comparison
                : SourceOrder.CompareTo(other.SourceOrder);
        }
    }
}
