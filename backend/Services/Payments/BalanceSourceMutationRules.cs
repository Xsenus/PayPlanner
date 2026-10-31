using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

internal static class BalanceSourceMutationRules
{
    internal const string HistoryMessage = "Изменение затронуло бы историю источника баланса. Суммы и связи сохранены; требуется сверка истории.";
    private const decimal Tolerance = 0.009m;

    public static async Task<ClientBalanceTransaction?> LoadAsync(PaymentContext db, int paymentId, string key, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null
            || db.Database.IsNpgsql() && db.Database.CurrentTransaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("Изменение источника баланса требует транзакцию вызывающей операции с изоляцией Serializable.");
        if (paymentId <= 0) throw new BalanceSourceHistoryException();
        db.ChangeTracker.DetectChanges();
        if (db.ChangeTracker.Entries<ClientBalanceTransaction>().Any(entry => entry.Entity.ExternalKey == key
            && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)) throw new BalanceSourceHistoryException();
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {paymentId} FOR UPDATE NOWAIT", ct);
        else if (!db.Database.IsSqlite()) throw new NotSupportedException("Balance sources support SQLite and PostgreSQL only.");
        if (!await db.Payments.AsNoTracking().AnyAsync(payment => payment.Id == paymentId, ct)) throw new BalanceSourceHistoryException();
        var existing = await db.ClientBalanceTransactions.FirstOrDefaultAsync(row => row.ExternalKey == key, ct);
        if (existing is null && db.ChangeTracker.Entries<ClientBalanceTransaction>().Any(entry => entry.Entity.ExternalKey == key))
            throw new BalanceSourceHistoryException();
        // A longer-lived context may have an old clean ledger row. Refresh its
        // scalar/shadow values after taking the shared payment lock. Never discard
        // pending work; one source refresh has bounded cost, without graph reads.
        if (existing is not null) await db.Entry(existing).ReloadAsync(ct);
        return existing;
    }

    public static async Task ValidateAsync(PaymentContext db, ClientBalanceTransaction existing, Payment? payment,
        ClientBalanceTransactionType type, decimal amount, CancellationToken ct)
    {
        if (existing.Type != type || existing.Direction != ClientBalanceDirection.Credit || existing.PaymentId is not > 0
            || existing.Amount < 0m || (existing.ClientId is > 0) == (existing.LegalEntityId is > 0)
            || payment is not null && (existing.PaymentId != payment.Id || existing.ClientId != payment.ClientId
                || existing.LegalEntityId != payment.LegalEntityId || existing.InvoiceId != payment.InvoiceId)) throw new BalanceSourceHistoryException();
        var delta = amount - existing.Amount;
        if (Math.Abs(delta) <= Tolerance) return;
        if (type == ClientBalanceTransactionType.UnallocatedAdvance)
        {
            var prefix = ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(existing.PaymentId.Value);
            var refundKey = ClientBalanceService.BuildUnallocatedAdvanceRefundKey(existing.PaymentId.Value);
            if (await db.ClientBalanceTransactions.AsNoTracking().AnyAsync(row => row.Direction == ClientBalanceDirection.Debit
                && (row.Type == ClientBalanceTransactionType.CreditApplied && row.ExternalKey != null && row.ExternalKey.StartsWith(prefix)
                    || row.Type == ClientBalanceTransactionType.Refund && (row.PaymentId == existing.PaymentId || row.ExternalKey == refundKey)), ct))
                throw new BalanceSourceHistoryException();
        }
        if (delta < -Tolerance)
        {
            var balance = existing.ClientId is { } client ? await ClientBalanceService.GetBalanceAsync(db, client, ct)
                : await ClientBalanceService.GetLegalEntityBalanceAsync(db, existing.LegalEntityId!.Value, ct);
            if (balance + delta + Tolerance < Math.Min(0m, balance)) throw new BalanceSourceHistoryException();
        }
    }
}

internal sealed class BalanceSourceHistoryException() : InvalidOperationException(BalanceSourceMutationRules.HistoryMessage);
