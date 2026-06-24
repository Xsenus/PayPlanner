using System.Text;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class LegacyExpensePaymentRecipientBackfillService
{
    private const string LawyerAlias = "\u042e\u0420\u0418\u0421\u0422";

    public static async Task<int> EnsureRecognizedRecipientsAsync(PaymentContext db, CancellationToken ct = default)
    {
        var recipients = await LoadRecipientCandidatesAsync(db, ct);
        if (recipients.Count == 0)
        {
            return 0;
        }

        var payments = await db.Payments
            .Include(payment => payment.Responsibles)
            .Where(payment => payment.Type == PaymentType.Expense)
            .Where(payment => string.IsNullOrWhiteSpace(payment.CounterpartyName)
                || !payment.Responsibles.Any())
            .ToListAsync(ct);

        var changed = 0;
        foreach (var payment in payments)
        {
            var recipient = ResolveRecipient(payment, recipients);
            if (recipient is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(payment.CounterpartyName))
            {
                payment.CounterpartyName = recipient.DisplayName;
                changed++;
            }

            if (!payment.Responsibles.Any(item => item.UserId == recipient.UserId))
            {
                payment.Responsibles.Add(new PaymentResponsible
                {
                    PaymentId = payment.Id,
                    UserId = recipient.UserId,
                });
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private static async Task<List<RecipientCandidate>> LoadRecipientCandidatesAsync(PaymentContext db, CancellationToken ct)
    {
        var users = await db.Users
            .AsNoTracking()
            .Where(user => user.IsActive && user.IsApproved && user.IsExternalContractor)
            .Select(user => new
            {
                user.Id,
                user.FullName,
                user.FirstName,
                user.LastName,
            })
            .ToListAsync(ct);

        var firstNameCounts = users
            .Select(user => FirstNotEmpty(user.FirstName, GetNamePart(user.FullName, 1)))
            .Where(value => value is not null)
            .Select(value => NormalizeForSearch(value!))
            .Where(value => value.Length > 0)
            .GroupBy(value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return users
            .Select(user =>
            {
                var displayName = FirstNotEmpty(user.FullName, user.LastName, user.FirstName);
                if (displayName is null)
                {
                    return null;
                }

                var firstName = FirstNotEmpty(user.FirstName, GetNamePart(user.FullName, 1));
                var lastName = FirstNotEmpty(user.LastName, GetNamePart(user.FullName, 0));
                var aliases = new HashSet<string>(StringComparer.Ordinal);
                AddAlias(aliases, user.FullName);
                AddAlias(aliases, JoinNameParts(firstName, lastName));
                AddAlias(aliases, JoinNameParts(lastName, firstName));
                AddAlias(aliases, lastName);

                if (firstName is not null
                    && firstNameCounts.TryGetValue(NormalizeForSearch(firstName), out var count)
                    && count == 1)
                {
                    AddAlias(aliases, $"{firstName} {LawyerAlias}");
                }

                return aliases.Count == 0
                    ? null
                    : new RecipientCandidate(user.Id, displayName.Trim(), aliases.OrderByDescending(alias => alias.Length).ToList());
            })
            .Where(candidate => candidate is not null)
            .Cast<RecipientCandidate>()
            .ToList();
    }

    private static RecipientCandidate? ResolveRecipient(Payment payment, IReadOnlyCollection<RecipientCandidate> recipients)
    {
        foreach (var source in new[] { payment.Description, payment.Notes })
        {
            var normalized = NormalizeForSearch(source);
            if (normalized.Length == 0)
            {
                continue;
            }

            var matches = recipients
                .Where(candidate => candidate.Aliases.Any(alias => StartsWithAlias(normalized, alias)))
                .ToList();
            if (matches.Count == 1)
            {
                return matches[0];
            }
        }

        return null;
    }

    private static bool StartsWithAlias(string normalizedText, string alias)
        => normalizedText.Equals(alias, StringComparison.Ordinal)
           || normalizedText.StartsWith($"{alias} ", StringComparison.Ordinal);

    private static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var rawChar in value.Trim().ToUpperInvariant())
        {
            var currentChar = rawChar == '\u0401' ? '\u0415' : rawChar;
            if (char.IsLetterOrDigit(currentChar))
            {
                builder.Append(currentChar);
                previousWasSpace = false;
                continue;
            }

            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static string? FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? GetNamePart(string? fullName, int index)
    {
        var parts = fullName?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts is not null && parts.Length > index ? parts[index] : null;
    }

    private static string? JoinNameParts(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            ? $"{first} {second}"
            : null;

    private static void AddAlias(HashSet<string> aliases, string? value)
    {
        var normalized = NormalizeForSearch(value);
        if (normalized.Length > 0)
        {
            aliases.Add(normalized);
        }
    }

    private sealed record RecipientCandidate(int UserId, string DisplayName, IReadOnlyCollection<string> Aliases);
}
