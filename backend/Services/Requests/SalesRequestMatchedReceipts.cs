using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Requests;

/// <summary>Summarizes explicitly matched receipts and refunds, not cash-order copies or payment status flags.</summary>
/// <remarks>The caller must establish the document currency, load all relevant operations with financial access and hold appropriate locks. This result alone does not authorize a transition.</remarks>
public static class SalesRequestMatchedReceipts
{
    public const int MaximumRows = 5000;

    public static SalesRequestMatchedReceiptsResult Evaluate(int invoiceId, string? currency, decimal requiredAmount, IReadOnlyCollection<MoneyOperation> operations)
    {
        var code = currency?.Trim();
        if (invoiceId <= 0 || string.IsNullOrWhiteSpace(code) || requiredAmount <= 0m || !HasCentPrecision(requiredAmount) || operations.Count > MaximumRows
            || operations.Any(o => o.Id <= 0 || o.LinkedInvoiceId != invoiceId)
            || operations.Select(o => o.Id).Distinct().Count() != operations.Count
            || operations.Any(o => !Enum.IsDefined(o.MatchStatus)
                || (o.MatchStatus == MoneyOperationMatchStatus.Matched
                    && (o.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense) || o.AccountingType != MoneyOperationAccountingType.Fact))))
            return new(false, 0m);

        var matched = operations.Where(o => o.MatchStatus == MoneyOperationMatchStatus.Matched).ToArray();
        if (matched.Any(o => !o.MatchedAt.HasValue || o.Amount <= 0m || !HasCentPrecision(o.Amount) || string.IsNullOrWhiteSpace(o.Currency)
            || !string.Equals(o.Currency.Trim(), code, StringComparison.OrdinalIgnoreCase)
            || !Enum.IsDefined(o.Source)))
            return new(false, 0m);

        var movementKeys = matched.Where(o => !string.IsNullOrWhiteSpace(o.SourceMovementKey)).Select(o => o.SourceMovementKey!.Trim()).ToArray();
        if (movementKeys.Distinct(StringComparer.Ordinal).Count() != movementKeys.Length)
            return new(false, 0m);

        var amount = Math.Max(0m, Math.Round(matched.Sum(o => o.Direction == MoneyOperationDirection.Income ? o.Amount : -o.Amount), 2, MidpointRounding.AwayFromZero));
        var required = Math.Round(requiredAmount, 2, MidpointRounding.AwayFromZero);
        return new(required > 0m && amount >= required, amount);
    }

    private static bool HasCentPrecision(decimal amount) => decimal.Round(amount, 2) == amount;
}

public sealed record SalesRequestMatchedReceiptsResult(bool HasEnoughMatchedAmount, decimal MatchedAmount);
