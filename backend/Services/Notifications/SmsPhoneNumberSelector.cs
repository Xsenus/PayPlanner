namespace PayPlanner.Api.Services.Notifications;

public static class SmsPhoneNumberKinds
{
    public const string Mobile = "mobile";
    public const string Work = "work";
    public const string Backup = "backup";
    public const string Unspecified = "unspecified";
}

public static class SmsPhoneCandidateRejectionCodes
{
    public const string Unverified = "phone-unverified";
    public const string ServiceNumber = "phone-service-number";
    public const string OptedOut = "phone-opted-out";
}

public sealed record SmsPhoneNumberCandidate(
    string? Value,
    string Kind,
    bool IsPrimary = false,
    bool IsVerified = true,
    bool IsServiceNumber = false,
    bool IsOptedOut = false);

public sealed record SmsPhoneNumberSelectionResult(
    string? E164,
    string? Kind,
    int ValidCandidates,
    int RejectedCandidates,
    IReadOnlyDictionary<string, int> Rejections)
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
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        var sourceOrder = 0;

        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                AddRejection(rejections, SmsPhoneNumberErrorCodes.InvalidFormat);
                sourceOrder++;
                continue;
            }

            if (candidate.IsOptedOut)
            {
                AddRejection(rejections, SmsPhoneCandidateRejectionCodes.OptedOut);
                sourceOrder++;
                continue;
            }

            if (!candidate.IsVerified)
            {
                AddRejection(rejections, SmsPhoneCandidateRejectionCodes.Unverified);
                sourceOrder++;
                continue;
            }

            if (candidate.IsServiceNumber)
            {
                AddRejection(rejections, SmsPhoneCandidateRejectionCodes.ServiceNumber);
                sourceOrder++;
                continue;
            }

            var normalized = SmsPhoneNumberNormalizer.Normalize(candidate.Value, policy);
            if (!normalized.IsValid || normalized.E164 is null)
            {
                AddRejection(
                    rejections,
                    normalized.ErrorCode ?? SmsPhoneNumberErrorCodes.InvalidFormat);
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
            rejections.Values.Sum(),
            rejections);
    }

    private static void AddRejection(IDictionary<string, int> rejections, string code)
        => rejections[code] = rejections.TryGetValue(code, out var count) ? count + 1 : 1;

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
