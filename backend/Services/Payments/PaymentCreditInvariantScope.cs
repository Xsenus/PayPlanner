using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

internal sealed class PaymentCreditInvariantScope : IDisposable
{
    public const string RejectionMessage = "Сохранение уменьшило бы оплату связанного платежа или счёта ниже зачтённого баланса. Измените распределение оплаты.";
    public const string CancellationProtectedMessage = "Платёж связан с аннулированным счётом или компенсацией оплаты. Денежные поля и связи защищены; требуется сверка истории.";
    private const decimal Tolerance = 0.009m;
    private readonly PaymentContext db;
    private readonly HashSet<Payment> changed = new(ReferenceEqualityComparer.Instance);

    public PaymentCreditInvariantScope(PaymentContext db)
    {
        this.db = db;
        db.ChangeTracker.StateChanged += OnStateChanged;
        db.ChangeTracker.Tracked += OnTracked;
    }

    public void IncludePayments(IEnumerable<Payment> payments) => changed.UnionWith(payments);

    public static Task<bool> IsCancellationProtectedAsync(PaymentContext db, int paymentId, CancellationToken ct)
        => CancellationProtectedPayments(db).AnyAsync(payment => payment.Id == paymentId, ct);

    public static IQueryable<Payment> CancellationProtectedPayments(PaymentContext db)
        => db.Payments.Where(payment => ((payment.DocumentKind == DocumentKind.Invoice
                    && (payment.Status == PaymentStatus.Cancelled || payment.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled))
                || (payment.InvoiceId.HasValue
                    && db.Payments.Any(root => root.InvoiceId == payment.InvoiceId && root.DocumentKind == DocumentKind.Invoice
                        && (root.Status == PaymentStatus.Cancelled || root.InvoiceWorkflowStatus == InvoiceWorkflowStatus.Cancelled))
                    && !db.Payments.Any(root => root.InvoiceId == payment.InvoiceId && root.DocumentKind == DocumentKind.Invoice
                        && root.Status != PaymentStatus.Cancelled && root.InvoiceWorkflowStatus != InvoiceWorkflowStatus.Cancelled))
                || db.ClientBalanceTransactions.Any(item => item.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice
                    && (item.PaymentId == payment.Id || payment.InvoiceId.HasValue && item.InvoiceId == payment.InvoiceId))
                || (payment.InvoiceId.HasValue && db.MoneyOperations.Any(item => item.LinkedInvoiceId == payment.InvoiceId
                    && item.Direction == MoneyOperationDirection.Expense && item.MatchComment == ClientBalanceService.CancelledInvoiceRefundComment))));

    private void OnStateChanged(object? sender, EntityStateChangedEventArgs args)
    {
        if ((args.OldState == EntityState.Modified || args.NewState == EntityState.Modified)
            && args.Entry.Entity is Payment payment) changed.Add(payment);
    }

    private void OnTracked(object? sender, EntityTrackedEventArgs args)
    {
        if (args.Entry.State == EntityState.Modified && args.Entry.Entity is Payment payment) changed.Add(payment);
    }

    public async Task<bool> KeepsAppliedCreditAsync(CancellationToken ct)
    {
        var payments = changed.ToArray().Where(p => p.Id > 0 && db.Entry(p).State is not (EntityState.Detached or EntityState.Deleted))
            .ToDictionary(p => p.Id);
        foreach (var ids in payments.Keys.Chunk(500))
        {
            var applied = await db.ClientBalanceTransactions.AsNoTracking()
                .Where(t => t.PaymentId.HasValue && ids.Contains(t.PaymentId.Value)
                    && t.Type == ClientBalanceTransactionType.CreditApplied && t.Direction == ClientBalanceDirection.Debit)
                .GroupBy(t => t.PaymentId!.Value)
                .Select(group => new { Id = group.Key, Amount = group.Sum(t => t.Amount) }).ToArrayAsync(ct);
            foreach (var credit in applied)
                if (!Covers(payments[credit.Id], credit.Amount)) return false;
        }

        // A representative includes its child payments. Independent debits on both
        // levels must be covered together, rather than reusing one paid amount twice.
        foreach (var invoiceIds in payments.Values.Where(p => p.InvoiceId is > 0).Select(p => p.InvoiceId!.Value).Distinct().Chunk(500))
        {
            // Include an unchanged representative too. Rank it exactly like the
            // invoice aggregate: active first, then largest amount, then lowest ID.
            // An anti-join also supports SQLite decimal comparisons without sorting.
            var representatives = await db.Payments.AsNoTracking()
                .Where(p => p.InvoiceId.HasValue && invoiceIds.Contains(p.InvoiceId.Value) && p.DocumentKind == DocumentKind.Invoice)
                .Where(p => !db.Payments.Any(other => other.InvoiceId == p.InvoiceId && other.DocumentKind == DocumentKind.Invoice
                    && ((p.Status == PaymentStatus.Cancelled && other.Status != PaymentStatus.Cancelled)
                        || (p.Status == PaymentStatus.Cancelled) == (other.Status == PaymentStatus.Cancelled)
                        && (other.Amount > p.Amount || other.Amount >= p.Amount && other.Id < p.Id))))
                .Select(p => new { InvoiceId = p.InvoiceId!.Value, p.PaidAmount, p.Amount })
                .ToDictionaryAsync(p => p.InvoiceId, ct);
            if (representatives.Count == 0) continue;
            var totals = await db.ClientBalanceTransactions.AsNoTracking()
                .Where(t => t.Type == ClientBalanceTransactionType.CreditApplied && t.Direction == ClientBalanceDirection.Debit)
                .Join(db.Payments.AsNoTracking().Where(p => p.InvoiceId.HasValue && invoiceIds.Contains(p.InvoiceId.Value)),
                    t => t.PaymentId, p => (int?)p.Id, (t, p) => new { InvoiceId = p.InvoiceId!.Value, t.Amount })
                .GroupBy(t => t.InvoiceId).Select(group => new { Id = group.Key, Amount = group.Sum(t => t.Amount) }).ToArrayAsync(ct);
            foreach (var total in totals)
                if (representatives.TryGetValue(total.Id, out var representative)
                    && (representative.PaidAmount + Tolerance < total.Amount || representative.Amount + Tolerance < total.Amount)) return false;
        }
        return true;
    }

    private static bool Covers(Payment payment, decimal amount)
        => payment.PaidAmount + Tolerance >= amount && payment.Amount + Tolerance >= amount;

    public void Dispose()
    {
        db.ChangeTracker.StateChanged -= OnStateChanged;
        db.ChangeTracker.Tracked -= OnTracked;
        changed.Clear();
    }
}
