using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

internal sealed class MoneyBalanceReconciliationService(PaymentContext db, ILogger logger)
{
    public async Task<MoneyBalanceReconciliationRunResultDto> RunAsync(MoneyReconciliationScope scope,
        DateTime? from, DateTime? to, MoneyOperationSource? source, string? accountName,
        Func<PaymentContext, CancellationToken, Task<bool>> verifyAuthority, CancellationToken ct)
    {
        if (from.HasValue && to.HasValue && to.Value.Date < from.Value.Date)
            throw new ArgumentException("Конец периода сверки не может быть раньше начала.");

        for (var attempt = 0; ; attempt++)
        {
            var ownsTransaction = db.Database.CurrentTransaction is null;
            var transaction = db.Database.CurrentTransaction ?? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var savepoint = ownsTransaction ? null : "money_balance_" + Guid.NewGuid().ToString("N");
            if (savepoint is not null) await transaction.CreateSavepointAsync(savepoint, ct);
            try
            {
                var checkedAt = DateTime.UtcNow;
                if (!await VerifyAuthorityAsync(verifyAuthority, ct)) throw new UnauthorizedAccessException();
                // Money/account mutations lease their company first. Holding the
                // shared company lease protects all its accounts without N updates
                // for a reconciliation covering many accounts.
                foreach (var companyId in scope.CompanyIds)
                    if (await db.LegalEntities.Where(item => item.Id == companyId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ShortName, item => item.ShortName), ct) != 1)
                        throw new UnauthorizedAccessException();
                if (!await VerifyAuthorityAsync(verifyAuthority, ct)) throw new UnauthorizedAccessException();
                var balances = await scope.CreateQuery(db).BuildDailyBalanceRowsAsync(from, to, source, accountName,
                    MoneyJournalVisibility.AllScopedRows, ct, scope);
                if (balances.Count > 25_000) throw new ArgumentException("Сократите период или выберите отдельный счёт для сверки.");

                var keys = balances.Select(item => (Key: item.BalanceKey!, Date: item.Date.Date)).ToHashSet();
                var existing = new Dictionary<(string Key, DateTime Date), MoneyBalanceReconciliationResult>();
                if (keys.Count > 0)
                {
                    var firstDate = keys.Min(item => item.Date);
                    var lastDate = keys.Max(item => item.Date);
                    foreach (var chunk in keys.Select(item => item.Key).Distinct().Chunk(200))
                    {
                        var rows = await db.MoneyBalanceReconciliationResults
                            .Where(item => item.ScopeKey != null && chunk.Contains(item.ScopeKey)
                                && item.Date >= firstDate && item.Date <= lastDate).ToListAsync(ct);
                        foreach (var row in rows)
                            if (keys.Contains((row.ScopeKey!, row.Date.Date))) existing.Add((row.ScopeKey!, row.Date.Date), row);
                    }
                }

                var created = 0;
                var updated = 0;
                var stored = new List<MoneyBalanceReconciliationResult>(balances.Count);
                foreach (var balance in balances)
                {
                    var key = (balance.BalanceKey!, balance.Date.Date);
                    if (!existing.TryGetValue(key, out var row))
                    {
                        row = new MoneyBalanceReconciliationResult { ScopeKey = key.Item1, Date = key.Item2 };
                        db.MoneyBalanceReconciliationResults.Add(row);
                        created++;
                    }
                    else updated++;
                    row.CompanyId = balance.CompanyId;
                    row.MoneyAccountId = balance.MoneyAccountId;
                    row.Currency = balance.Currency;
                    row.Source = balance.Source;
                    row.AccountName = balance.AccountName;
                    row.OpeningBalance = balance.OpeningBalance;
                    row.Income = balance.Income;
                    row.Expense = balance.Expense;
                    row.ClosingBalance = balance.ClosingBalance;
                    row.IncomeCount = balance.IncomeCount;
                    row.ExpenseCount = balance.ExpenseCount;
                    row.IsBalanced = balance.IsBalanced;
                    row.IssueCount = balance.IsBalanced ? 0 : 1;
                    row.CheckedAt = checkedAt;
                    row.CheckedByUserId = scope.ActorId;
                    stored.Add(row);
                }
                await db.SaveChangesAsync(ct);
                if (!await VerifyAuthorityAsync(verifyAuthority, ct)) throw new UnauthorizedAccessException();
                if (ownsTransaction) await transaction.CommitAsync(ct);
                else await transaction.ReleaseSavepointAsync(savepoint!, ct);
                return new()
                {
                    CheckedAt = checkedAt,
                    Created = created,
                    Updated = updated,
                    IssueCount = stored.Sum(item => item.IssueCount),
                    Items = stored.Select(Map).ToList(),
                };
            }
            catch (Exception ex)
            {
                if (ownsTransaction) await transaction.RollbackAsync(CancellationToken.None);
                else await transaction.RollbackToSavepointAsync(savepoint!, CancellationToken.None);
                db.ChangeTracker.Clear();
                if (!ownsTransaction || attempt >= 3 || !IsRetryable(ex)) throw;
                logger.LogWarning(ex, "Retrying balance reconciliation after a concurrent write, attempt {Attempt}.", attempt + 1);
            }
            finally
            {
                if (ownsTransaction) await transaction.DisposeAsync();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100 * (1 << attempt)), ct);
        }
    }

    private async Task<bool> VerifyAuthorityAsync(Func<PaymentContext, CancellationToken, Task<bool>> verify, CancellationToken ct)
    {
        // PostgreSQL's serializable snapshot must not conceal a late permission
        // revocation. SQLite serializes writes while this transaction is active.
        if (!db.Database.IsNpgsql()) return await verify(db, ct);
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(db.Database.GetConnectionString()).Options;
        await using var current = new PostgreSqlPaymentContext(options);
        return await verify(current, ct);
    }

    private static bool IsRetryable(Exception exception)
    {
        // EF's Npgsql execution strategy also wraps serialization failures in
        // InvalidOperationException, including ExecuteUpdate before SaveChanges.
        var error = exception;
        while ((error is DbUpdateException or InvalidOperationException) && error.InnerException is { } inner)
            error = inner;
        return error is PostgresException postgres && (postgres.SqlState is "40001" or "40P01"
                || postgres.SqlState == "23505" && postgres.ConstraintName == "IX_MoneyBalanceReconciliationResults_ScopeKey_Date")
            || error is SqliteException sqlite && (sqlite.SqliteErrorCode == 5
                || sqlite.SqliteExtendedErrorCode == 2067 && sqlite.Message.Contains("ScopeKey", StringComparison.Ordinal));
    }

    public static MoneyBalanceReconciliationResultDto Map(MoneyBalanceReconciliationResult row) => new()
    {
        Id = row.Id,
        Date = row.Date,
        Source = row.Source,
        AccountName = row.AccountName,
        CompanyId = row.CompanyId,
        MoneyAccountId = row.MoneyAccountId,
        Currency = row.Currency,
        BalanceKey = row.ScopeKey,
        OpeningBalance = row.OpeningBalance,
        Income = row.Income,
        Expense = row.Expense,
        ClosingBalance = row.ClosingBalance,
        IncomeCount = row.IncomeCount,
        ExpenseCount = row.ExpenseCount,
        IsBalanced = row.IsBalanced,
        IssueCount = row.IssueCount,
        CheckedAt = row.CheckedAt,
        CheckedByUserId = row.CheckedByUserId,
    };
}
