using System.Text.RegularExpressions;

namespace PayPlanner.Api.Services;

internal static partial class TechnicalEmailHelper
{
    private const string TechnicalDomain = "auth.payplanner.local";

    public static bool IsTechnicalEmail(string? email)
        => !string.IsNullOrWhiteSpace(email)
            && email.Trim().EndsWith($"@{TechnicalDomain}", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidRealEmail(string? email)
    {
        var normalized = NormalizeRealEmail(email);
        return !string.IsNullOrWhiteSpace(normalized)
            && !IsTechnicalEmail(normalized)
            && EmailRegex().IsMatch(normalized);
    }

    public static string? NormalizeRealEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string BuildTechnicalEmail(string provider, string providerUserId)
    {
        var normalizedProvider = NormalizeProvider(provider);
        var normalizedUserId = Slugify(providerUserId);
        return $"{normalizedProvider}-{normalizedUserId}@{TechnicalDomain}";
    }

    public static string NormalizeProvider(string? provider)
        => (provider ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "google" => "google",
            "vk" or "vkontakte" => "vk",
            "yandex" => "yandex",
            "telegram" => "telegram",
            _ => string.Empty
        };

    private static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.NewGuid().ToString("N");
        }

        var result = NonAlphaNumericRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(result))
        {
            result = Guid.NewGuid().ToString("N");
        }

        return result.Length <= 80 ? result : result[..80];
    }

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumericRegex();
}
