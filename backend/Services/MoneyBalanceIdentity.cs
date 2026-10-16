using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

internal static class MoneyBalanceIdentity
{
    public static string? NormalizeCurrency(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    public static string Create(int? companyId, int? accountId, MoneyOperationSource source, string? currency, string? legacyName)
    {
        // A name is used only for explicitly unattributed legacy buckets. A known
        // account keeps its identity when its display name changes.
        var value = JsonSerializer.Serialize(new
        {
            CompanyId = companyId,
            AccountId = accountId,
            Source = source.ToString(),
            Currency = NormalizeCurrency(currency),
            LegacyName = accountId.HasValue ? null : legacyName ?? string.Empty,
        });
        return "v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

// Row visibility is a predicate, not an identity or an authorization grant.
// System callers still supply explicitly company-restricted query sources.
internal sealed record MoneyJournalVisibility(bool SeesAll, int[] VisibleUserIds)
{
    public static MoneyJournalVisibility FromUser(DataVisibilityAccess visibility)
        => new(visibility.SeesAll, visibility.VisibleUserIds.ToArray());
    public static MoneyJournalVisibility AllScopedRows { get; } = new(true, []);
}
