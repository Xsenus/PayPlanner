using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class PaymentAdvanceReviewService
{
    private const decimal AmountEpsilon = 0.009m;
    private static readonly Func<Payment, bool> UnallocatedAdvanceCandidateEvaluator =
        UnallocatedAdvanceCandidatePredicate().Compile();

    public static Expression<Func<Payment, bool>> UnallocatedAdvanceCandidatePredicate()
        => payment => payment.Type == PaymentType.Income
            && payment.DocumentKind == DocumentKind.Payment
            && payment.Status != PaymentStatus.Cancelled
            && ((payment.ClientId.HasValue && payment.ClientId.Value > 0 && !payment.LegalEntityId.HasValue)
                || (payment.LegalEntityId.HasValue && payment.LegalEntityId.Value > 0 && !payment.ClientId.HasValue))
            && (payment.PaidAmount > AmountEpsilon
                || ((payment.IsPaid || payment.Status == PaymentStatus.Completed) && payment.Amount > AmountEpsilon))
            && !payment.InvoiceId.HasValue
            && !payment.ContractId.HasValue
            && (payment.Account == null || payment.Account.Trim() == string.Empty)
            && (payment.LegacyAccountNumber == null || payment.LegacyAccountNumber.Trim() == string.Empty);

    public static bool IsUnallocatedAdvanceCandidate(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return UnallocatedAdvanceCandidateEvaluator(payment);
    }

    public static decimal GetAdvanceAmount(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var amount = payment.PaidAmount > AmountEpsilon
            ? payment.PaidAmount
            : payment.IsPaid || payment.Status == PaymentStatus.Completed
                ? payment.Amount
                : 0m;

        return NormalizeAmount(amount);
    }

    public static void ApplyAutomaticReviewState(Payment payment, DateTime utcNow, HttpContext? httpContext)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.AdvanceReviewStatus is PaymentAdvanceReviewStatus.Ignored or PaymentAdvanceReviewStatus.Refunded)
        {
            return;
        }

        if (IsUnallocatedAdvanceCandidate(payment))
        {
            if (payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance)
            {
                StampReviewIfNeeded(payment, utcNow, httpContext, "Принято как аванс");
                return;
            }

            payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
            payment.AdvanceReviewReason = null;
            payment.AdvanceReviewedAt = null;
            payment.AdvanceReviewedByUserId = null;
            payment.AdvanceReviewedByUserName = null;
            return;
        }

        if (payment.AdvanceReviewStatus is PaymentAdvanceReviewStatus.RequiresDecision or PaymentAdvanceReviewStatus.AcceptedAsAdvance)
        {
            payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Allocated;
            payment.AdvanceReviewReason = string.IsNullOrWhiteSpace(payment.AdvanceReviewReason)
                ? "Платеж связан со счетом, договором или документом"
                : payment.AdvanceReviewReason;
            StampReviewIfNeeded(payment, utcNow, httpContext, payment.AdvanceReviewReason);
        }
    }

    public static async Task<(bool Success, string? Error)> AcceptAsAdvanceAsync(
        PaymentContext db,
        Payment payment,
        string? reason,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        PaymentDomainService.Normalize(payment);
        if (!IsUnallocatedAdvanceCandidate(payment))
        {
            await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id, ct);
            return (false, "Платеж уже связан со счетом или договором, либо не является входящим оплаченным платежом.");
        }

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? "Деньги приняты как нераспределенный аванс клиента";
        StampReviewIfNeeded(payment, utcNow, httpContext, payment.AdvanceReviewReason);
        await SyncBalanceAsync(db, payment, utcNow, httpContext, ct);
        return (true, null);
    }

    public static async Task<(bool Success, string? Error)> IgnoreAsync(
        PaymentContext db,
        Payment payment,
        string? reason,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Ignored;
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? "Исключено из контроля нераспределенных авансов";
        StampReviewIfNeeded(payment, utcNow, httpContext, payment.AdvanceReviewReason);
        await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id, ct);
        return (true, null);
    }

    public static async Task SyncBalanceAsync(
        PaymentContext db,
        Payment payment,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.Id <= 0)
        {
            return;
        }

        if (payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance
            && IsUnallocatedAdvanceCandidate(payment))
        {
            await ClientBalanceService.UpsertUnallocatedAdvanceAsync(
                db,
                payment,
                GetAdvanceAmount(payment),
                payment.AdvanceReviewReason,
                utcNow,
                httpContext,
                ct);
            return;
        }

        await ClientBalanceService.RemoveUnallocatedAdvanceAsync(db, payment.Id, ct);
    }

    public static void MarkAllocated(Payment payment, string? reason, DateTime utcNow, HttpContext? httpContext)
    {
        ArgumentNullException.ThrowIfNull(payment);

        payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Allocated;
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? "Аванс полностью распределен";
        payment.AdvanceReviewedAt = utcNow;
        payment.AdvanceReviewedByUserId = httpContext?.User.GetUserId() ?? payment.AdvanceReviewedByUserId;
        payment.AdvanceReviewedByUserName = httpContext?.User.GetDisplayName() ?? payment.AdvanceReviewedByUserName;
    }

    private static void StampReviewIfNeeded(Payment payment, DateTime utcNow, HttpContext? httpContext, string? reason)
    {
        payment.AdvanceReviewReason = NormalizeReason(reason) ?? payment.AdvanceReviewReason;
        payment.AdvanceReviewedAt ??= utcNow;
        payment.AdvanceReviewedByUserId ??= httpContext?.User.GetUserId();
        payment.AdvanceReviewedByUserName ??= httpContext?.User.GetDisplayName();
    }

    private static string? NormalizeReason(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
