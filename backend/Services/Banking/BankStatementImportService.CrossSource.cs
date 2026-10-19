using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using static PayPlanner.Api.Services.Banking.BankStatementImportRules;

namespace PayPlanner.Api.Services.Banking;

public sealed partial class BankStatementImportService
{
    private const int MaxCrossSourceCandidates = 1000;

    private async Task<HashSet<BankStatementImportItem>> FindCrossSourceOverlapsAsync(
        BankStatementImportContext context, IReadOnlyCollection<BankStatementImportItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return [];
        var native = new Dictionary<string, List<BankStatementImportItem>>(StringComparer.Ordinal);
        var file = new Dictionary<string, List<BankStatementImportItem>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var requested = item.BankStatementOperationId.HasValue ? native : file;
            var key = BuildBankStatementImportKey(item.Date, item.Direction, item.Amount, null, item.CounterpartyName);
            if (!requested.TryGetValue(key, out var group)) requested.Add(key, group = []);
            group.Add(item);
        }

        var hasNative = native.Count != 0;
        var hasFile = file.Count != 0;
        var from = items.Min(item => item.Date).Date;
        var lastDay = items.Max(item => item.Date).Date;
        DateTime? to = lastDay < DateTime.MaxValue.Date ? lastDay.AddDays(1) : null;
        var minAmount = items.Min(item => item.Amount);
        var maxAmount = items.Max(item => item.Amount);
        var names = items.Select(item => item.CounterpartyName ?? string.Empty).Distinct().ToArray();
        var directions = items.Select(item => item.Direction).Distinct().ToArray();
        var operations = db.MoneyOperations.IgnoreQueryFilters().AsNoTracking();
        if (context.Access is not null)
            operations = BankStatementImportAccess.ScopeMoneyOperations(db, operations, context.Access);

        // A confirmed company/account and a persisted source are required. A
        // matching name must never attribute unknown legacy money to this bank.
        // Archived facts still occupy their origin; this only yields a review
        // hint, never an association or an instruction to restore money.
        var candidates = await operations
            .Where(operation => operation.MoneyAccountId == context.Account.Id
                && operation.Source == MoneyOperationSource.Bank
                && operation.AccountingType == MoneyOperationAccountingType.Fact
                && operation.Currency == context.Account.Currency
                && operation.SourceMovementKey != null && operation.BankStatementImportBatch != null
                && operation.BankStatementImportBatch.CompanyId == context.CompanyId
                && operation.BankStatementImportBatch.MoneyAccountId == context.Account.Id
                && (hasNative && operation.BankStatementImportBatch.Source == BankStatementImportSources.File
                    || hasFile && (operation.BankStatementImportBatch.Source == BankStatementImportSources.ManualBank
                        || operation.BankStatementImportBatch.Source == BankStatementImportSources.BackgroundBank))
                && operation.Date >= from && (!to.HasValue || operation.Date < to.Value)
                && operation.Amount >= minAmount && operation.Amount <= maxAmount
                && directions.Contains(operation.Direction) && names.Contains(operation.CounterpartyName ?? string.Empty))
            .OrderBy(operation => operation.Id).Take(MaxCrossSourceCandidates + 1)
            .Select(operation => new
            {
                operation.Date,
                operation.Direction,
                operation.Amount,
                operation.CounterpartyName,
                IsFile = operation.BankStatementImportBatch!.Source == BankStatementImportSources.File,
            }).ToListAsync(ct);

        // An incomplete search cannot establish that new money is safe. Keep
        // these bounded source rows for review instead of accepting a truncated
        // list or loading the entire history of the account.
        if (candidates.Count > MaxCrossSourceCandidates) return items.ToHashSet();
        var found = new HashSet<BankStatementImportItem>();
        foreach (var candidate in candidates)
        {
            var requested = candidate.IsFile ? native : file;
            var key = BuildBankStatementImportKey(candidate.Date, candidate.Direction, candidate.Amount, null, candidate.CounterpartyName);
            if (requested.Remove(key, out var group)) found.UnionWith(group);
        }
        return found;
    }
}
