using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

internal static class PaymentBalanceHistoryRules
{
    private const decimal Tolerance = 0.009m;
    public const string HistoryMessage = "По платежу есть денежная история. Изменение её основания требует отдельной сверки; суммы и связи сохранены.";
    public readonly record struct Binding(PaymentType Type, DocumentKind Kind, int? Client, int? Company, int? ExpenseClient, int? ExpenseCompany, int? Invoice)
    {
        public static Binding From(Payment p) => new(p.Type, p.DocumentKind, p.ClientId, p.LegalEntityId, p.ExpenseClientId, p.ExpenseLegalEntityId, p.InvoiceId);
    }
    public static async Task<string?> ValidateBindingAsync(PaymentContext db, Payment payment, Binding original, CancellationToken ct)
        => original != Binding.From(payment)
            && (await db.ClientBalanceTransactions.AsNoTracking().AnyAsync(t => t.PaymentId == payment.Id, ct)
                || await HasRelatedDebitAsync(db, payment.Id, ct)) ? HistoryMessage : null;

    public static Task<string?> ValidateSyncAsync(PaymentContext db, Payment payment, CancellationToken ct, decimal? enteredAmount = null)
        => ValidateAdvanceCreditAsync(db, payment, payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance
            && PaymentAdvanceReviewService.IsUnallocatedAdvanceCandidate(payment) ? Math.Max(PaymentAdvanceReviewService.GetAdvanceAmount(payment), enteredAmount ?? 0m) : 0m, ct);

    public static async Task<string?> ValidateAdvanceCreditAsync(PaymentContext db, Payment payment, decimal expectedAmount, CancellationToken ct)
    {
        var credits = await db.ClientBalanceTransactions.AsNoTracking()
            .Where(t => t.PaymentId == payment.Id && t.Type == ClientBalanceTransactionType.UnallocatedAdvance && t.Direction == ClientBalanceDirection.Credit)
            .Select(t => new { t.Amount, t.ClientId, t.LegalEntityId, t.InvoiceId, t.ExternalKey }).Take(2).ToArrayAsync(ct);
        if (credits.Length == 0) return expectedAmount > Tolerance && await HasRelatedDebitAsync(db, payment.Id, ct) ? HistoryMessage : null;
        if ((payment.ClientId is > 0) == (payment.LegalEntityId is > 0) || credits.Length != 1 || credits[0].ExternalKey != ClientBalanceService.BuildUnallocatedAdvanceKey(payment.Id)
            || credits[0].ClientId != payment.ClientId || credits[0].LegalEntityId != payment.LegalEntityId || credits[0].InvoiceId != payment.InvoiceId)
            return HistoryMessage;
        var withdrawal = credits[0].Amount - Math.Round(expectedAmount, 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(withdrawal) <= Tolerance) return null;
        if (await HasRelatedDebitAsync(db, payment.Id, ct)) return HistoryMessage;
        if (withdrawal <= Tolerance) return null;
        var balance = payment.ClientId is { } client ? await ClientBalanceService.GetBalanceAsync(db, client, ct)
            : await ClientBalanceService.GetLegalEntityBalanceAsync(db, payment.LegalEntityId!.Value, ct);
        return balance + Tolerance < withdrawal ? "Остаток баланса уже использован. Изменение аванса создало бы отрицательный остаток; требуется сверка." : null;
    }

    private static Task<bool> HasRelatedDebitAsync(PaymentContext db, int paymentId, CancellationToken ct)
    {
        var prefix = ClientBalanceService.BuildUnallocatedAdvanceApplicationKeyPrefix(paymentId);
        var refundKey = ClientBalanceService.BuildUnallocatedAdvanceRefundKey(paymentId);
        return db.ClientBalanceTransactions.AsNoTracking().AnyAsync(t => t.Direction == ClientBalanceDirection.Debit
            && (t.Type == ClientBalanceTransactionType.CreditApplied && (t.PaymentId == paymentId || t.ExternalKey != null && t.ExternalKey.StartsWith(prefix))
                || t.Type == ClientBalanceTransactionType.Refund && (t.PaymentId == paymentId || t.ExternalKey == refundKey)), ct);
    }

    public readonly record struct OwnerBalance(int? Client, int? Company, decimal Amount);
    public static async Task<OwnerBalance?> CaptureOwnerBalanceAsync(PaymentContext db, int paymentId, CancellationToken ct)
    {
        var owner = await db.Payments.AsNoTracking().Where(p => p.Id == paymentId).Select(p => new { p.ClientId, p.LegalEntityId }).SingleAsync(ct);
        if ((owner.ClientId is > 0) == (owner.LegalEntityId is > 0)) return null;
        return new(owner.ClientId, owner.LegalEntityId, await ReadBalanceAsync(db, owner.ClientId, owner.LegalEntityId, ct));
    }
    public static async Task<bool> KeepsOwnerBalanceAsync(PaymentContext db, OwnerBalance? original, CancellationToken ct)
    {
        if (original is not { } before) return true;
        var amount = await ReadBalanceAsync(db, before.Client, before.Company, ct);
        // Existing inconsistent history may be annotated, but this write may not worsen it.
        return amount >= -Tolerance || amount + Tolerance >= before.Amount;
    }
    private static Task<decimal> ReadBalanceAsync(PaymentContext db, int? client, int? company, CancellationToken ct)
        => client is { } id ? ClientBalanceService.GetBalanceAsync(db, id, ct) : ClientBalanceService.GetLegalEntityBalanceAsync(db, company!.Value, ct);

    public static async Task<string?> ValidateReviewAsync(PaymentContext db, Payment payment, CancellationToken ct)
    {
        if (payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance
            && await ClientBalanceService.GetAppliedCreditAmountAsync(db, payment.Id, ct) > Tolerance) return HistoryMessage;
        var key = ClientBalanceService.BuildUnallocatedAdvanceRefundKey(payment.Id);
        return payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.Refunded
            || await db.ClientBalanceTransactions.AsNoTracking().AnyAsync(t => t.Type == ClientBalanceTransactionType.Refund
                && t.Direction == ClientBalanceDirection.Debit && (t.PaymentId == payment.Id || t.ExternalKey == key), ct)
            ? "Возврат аванса уже есть в истории. Повторное изменение решения требует сверки." : null;
    }
}
