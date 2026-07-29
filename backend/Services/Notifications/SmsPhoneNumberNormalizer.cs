using System.Text;

namespace PayPlanner.Api.Services.Notifications;

public static class SmsPhoneNumberErrorCodes
{
    public const string Empty = "phone-empty";
    public const string InvalidFormat = "phone-invalid-format";
    public const string InvalidLength = "phone-invalid-length";
    public const string CountryNotAllowed = "phone-country-not-allowed";
}

public sealed class SmsPhoneNumberPolicy
{
    private readonly string[] _allowedCountryCallingCodes;

    public SmsPhoneNumberPolicy(IEnumerable<string> allowedCountryCallingCodes)
    {
        ArgumentNullException.ThrowIfNull(allowedCountryCallingCodes);
        _allowedCountryCallingCodes = allowedCountryCallingCodes
            .Select(NormalizeCallingCode)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(code => code.Length)
            .ThenBy(code => code, StringComparer.Ordinal)
            .ToArray();

        if (_allowedCountryCallingCodes.Length == 0)
        {
            throw new ArgumentException("At least one country calling code is required.", nameof(allowedCountryCallingCodes));
        }
    }

    public static SmsPhoneNumberPolicy RussianOnly { get; } = new(["7"]);

    internal bool IsAllowed(string digits)
        => _allowedCountryCallingCodes.Any(code => digits.StartsWith(code, StringComparison.Ordinal));

    private static string NormalizeCallingCode(string? value)
    {
        var normalized = value?.Trim().TrimStart('+');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length is < 1 or > 3
            || normalized[0] == '0'
            || normalized.Any(ch => ch is < '0' or > '9'))
        {
            throw new ArgumentException("Country calling code must contain 1 to 3 ASCII digits and must not start with zero.");
        }

        return normalized;
    }
}

public sealed record SmsPhoneNumberNormalizationResult(
    bool IsValid,
    string? E164,
    string? ErrorCode);

public static class SmsPhoneNumberNormalizer
{
    private const int MinE164Digits = 8;
    private const int MaxE164Digits = 15;

    public static SmsPhoneNumberNormalizationResult Normalize(
        string? value,
        SmsPhoneNumberPolicy? policy = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Invalid(SmsPhoneNumberErrorCodes.Empty);
        }

        var compact = RemoveFormatting(value);
        if (compact is null)
        {
            return Invalid(SmsPhoneNumberErrorCodes.InvalidFormat);
        }

        var digits = NormalizePrefix(compact);
        if (digits is null)
        {
            return Invalid(SmsPhoneNumberErrorCodes.InvalidFormat);
        }

        if (digits.Length is < MinE164Digits or > MaxE164Digits || digits[0] == '0')
        {
            return Invalid(SmsPhoneNumberErrorCodes.InvalidLength);
        }

        if (digits[0] == '7' && digits.Length != 11)
        {
            return Invalid(SmsPhoneNumberErrorCodes.InvalidLength);
        }

        var effectivePolicy = policy ?? SmsPhoneNumberPolicy.RussianOnly;
        if (!effectivePolicy.IsAllowed(digits))
        {
            return Invalid(SmsPhoneNumberErrorCodes.CountryNotAllowed);
        }

        return new SmsPhoneNumberNormalizationResult(true, $"+{digits}", null);
    }

    private static string? RemoveFormatting(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (ch is >= '0' and <= '9' || ch == '+')
            {
                builder.Append(ch);
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch is '(' or ')' or '-' or '.' or '/')
            {
                continue;
            }

            return null;
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? NormalizePrefix(string compact)
    {
        if (compact.StartsWith('+'))
        {
            var digits = compact[1..];
            return digits.Length > 0 && !digits.Contains('+', StringComparison.Ordinal)
                ? digits
                : null;
        }

        if (compact.StartsWith("00", StringComparison.Ordinal))
        {
            var digits = compact[2..];
            return digits.Length > 0 && !digits.Contains('+', StringComparison.Ordinal)
                ? digits
                : null;
        }

        if (compact.Contains('+', StringComparison.Ordinal))
        {
            return null;
        }

        if (compact.Length == 10)
        {
            return $"7{compact}";
        }

        if (compact.Length == 11 && compact[0] == '8')
        {
            return $"7{compact[1..]}";
        }

        return compact.Length == 11 && compact[0] == '7'
            ? compact
            : null;
    }

    private static SmsPhoneNumberNormalizationResult Invalid(string errorCode)
        => new(false, null, errorCode);
}
