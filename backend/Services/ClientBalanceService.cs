using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientBalanceService
{
    private const decimal AmountEpsilon = 0.009m;
    private const int DescriptionMaxLength = 500;

    public static Task<decimal> GetBalanceAsync(PaymentContext db, int clientId, CancellationToken ct = default)
        => GetBalanceAsync(db, clientId, null, ct);

    public static Task<decimal> GetLegalEntityBalanceAsync(PaymentContext db, int legalEntityId, CancellationToken ct = default)
        => GetBalanceAsync(db, null, legalEntityId, ct);

    private static Task<decimal> GetBalanceAsync(PaymentContext db, int? clientId, int? legalEntityId, CancellationToken ct = default)
        => db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item => item.ClientId == clientId && item.LegalEntityId == legalEntityId)
            .SumAsync(item => item.Direction == ClientBalanceDirection.Credit ? item.Amount : -item.Amount, ct);

    public static Task<decimal> GetAppliedCreditAmountAsync(PaymentContext db, int paymentId, CancellationToken ct = default)
        => db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item =>
                item.PaymentId == paymentId
                && item.Type == ClientBalanceTransactionType.CreditApplied
                && item.Direction == ClientBalanceDirection.Debit)
            .SumAsync(item => item.Amount, ct);

    public static async Task UpsertPaymentOverpaymentAsync(
        PaymentContext db,
        Payment payment,
        decimal rawPaidAmount,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        if (!HasValidCounterpartyOwner(payment) || payment.Type != PaymentType.Income)
        {
            return;
        }

        var overpaymentAmount = NormalizeAmount(rawPaidAmount - payment.Amount);
        var externalKey = BuildPaymentOverpaymentKey(payment.Id);
        var existing = await db.ClientBalanceTransactions
            .FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);

        if (overpaymentAmount <= AmountEpsilon)
        {
            if (existing is not null)
            {
                db.ClientBalanceTransactions.Remove(existing);
            }
            return;
        }

        var actor = httpContext?.User.GetDisplayName();
        var actorUserId = httpContext?.User.GetUserId();
        var actorTimeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, actorUserId, ct);
        var description = $"Переплата по платежу #{payment.Id}";

        if (existing is null)
        {
            db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
            {
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                InvoiceId = payment.InvoiceId,
                PaymentId = payment.Id,
                Amount = overpaymentAmount,
                Type = ClientBalanceTransactionType.Overpayment,
                Direction = ClientBalanceDirection.Credit,
                Description = description,
                ExternalKey = externalKey,
                CreatedAt = utcNow,
                CreatedByUserId = actorUserId,
                CreatedByUserName = actor,
            });
            return;
        }

        existing.ClientId = payment.ClientId;
        existing.LegalEntityId = payment.LegalEntityId;
        existing.InvoiceId = payment.InvoiceId;
        existing.PaymentId = payment.Id;
        existing.Amount = overpaymentAmount;
        existing.Type = ClientBalanceTransactionType.Overpayment;
        existing.Direction = ClientBalanceDirection.Credit;
        existing.Description = description;
    }

    public static async Task<(bool Success, string? Error, decimal AppliedAmount)> ApplyCreditAsync(
        PaymentContext db,
        int clientId,
        int paymentId,
        decimal requestedAmount,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default,
        string? description = null,
        int? sourcePaymentId = null,
        string? externalKey = null)
        => await ApplyCounterpartyCreditAsync(db, clientId, null, paymentId, requestedAmount, utcNow, httpContext, ct, description, sourcePaymentId, externalKey);

    public static async Task<(bool Success, string? Error, decimal AppliedAmount)> ApplyLegalEntityCreditAsync(
        PaymentContext db,
        int legalEntityId,
        int paymentId,
        decimal requestedAmount,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default,
        string? description = null,
        int? sourcePaymentId = null,
        string? externalKey = null)
        => await ApplyCounterpartyCreditAsync(db, null, legalEntityId, paymentId, requestedAmount, utcNow, httpContext, ct, description, sourcePaymentId, externalKey);

    public static async Task<(bool Success, string? Error, decimal AppliedAmount)> ApplyCounterpartyCreditAsync(
        PaymentContext db,
        int? clientId,
        int? legalEntityId,
        int paymentId,
        decimal requestedAmount,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default,
        string? description = null,
        int? sourcePaymentId = null,
        string? externalKey = null)
    {
        if (!HasSingleOwner(clientId, legalEntityId))
        {
            return (false, "Counterparty owner is required.", 0m);
        }

        var amount = NormalizeAmount(requestedAmount);
        if (amount <= AmountEpsilon)
        {
            return (false, "Amount must be greater than zero.", 0m);
        }

        var payment = await db.Payments.FirstOrDefaultAsync(item => item.Id == paymentId, ct);
        if (payment is null)
        {
            return (false, "Payment not found.", 0m);
        }

        if (payment.ClientId != clientId || payment.LegalEntityId != legalEntityId)
        {
            return (false, "Payment does not belong to this counterparty.", 0m);
        }

        PaymentDomainService.Normalize(payment);
        var outstanding = NormalizeAmount(payment.Amount - payment.PaidAmount);
        if (outstanding <= AmountEpsilon)
        {
            return (false, "Payment is already paid.", 0m);
        }

        var balance = await GetBalanceAsync(db, clientId, legalEntityId, ct);
        if (balance <= AmountEpsilon)
        {
            return (false, "Counterparty balance is empty.", 0m);
        }

        var availableBalance = balance;
        decimal? selectedSourceBalance = null;
        if (sourcePaymentId.HasValue)
        {
            if (sourcePaymentId.Value == paymentId)
            {
                return (false, "Advance source payment cannot be the target payment.", 0m);
            }

            var sourceBalance = await GetUnallocatedAdvanceAvailableAmountAsync(db, sourcePaymentId.Value, clientId, legalEntityId, ct);
            if (sourceBalance <= AmountEpsilon)
            {
                return (false, "Selected advance source has no available balance.", 0m);
            }

            selectedSourceBalance = sourceBalance;
            availableBalance = Math.Min(balance, sourceBalance);
        }

        var applied = Math.Min(amount, Math.Min(availableBalance, outstanding));
        applied = NormalizeAmount(applied);
        if (applied <= AmountEpsilon)
        {
            return (false, "Nothing to apply.", 0m);
        }

        var actor = httpContext?.User.GetDisplayName();
        var actorUserId = httpContext?.User.GetUserId();
        var actorTimeZone = await CalendarTimeZoneHelper.ResolveUserTimeZoneAsync(db, actorUserId, ct);
        var actorToday = PaymentDomainService.ResolveLocalToday(utcNow, actorTimeZone);

        payment.PaidAmount = NormalizeAmount(payment.PaidAmount + applied);
        payment.LastPaymentDate ??= actorToday;
        PaymentDomainService.ApplyStatusRules(payment, utcNow, actorTimeZone);

        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = clientId,
            LegalEntityId = legalEntityId,
            InvoiceId = payment.InvoiceId,
            PaymentId = payment.Id,
            Amount = applied,
            Type = ClientBalanceTransactionType.CreditApplied,
            Direction = ClientBalanceDirection.Debit,
            Description = NormalizeDescription(!string.IsNullOrWhiteSpace(description)
                ? description
                : sourcePaymentId.HasValue
                    ? $"Зачет аванса по платежу #{sourcePaymentId.Value} в платеж #{payment.Id}"
                    : $"Зачет баланса в платеж #{payment.Id}"),
            ExternalKey = NormalizeExternalKey(externalKey)
                ?? (sourcePaymentId.HasValue
                    ? BuildUnallocatedAdvanceApplicationKey(sourcePaymentId.Value, payment.Id, utcNow)
                    : null),
            CreatedAt = utcNow,
            CreatedByUserId = actorUserId,
            CreatedByUserName = actor,
        });

        PaymentDomainService.AppendSystemNote(
            payment,
            $"Зачтено с баланса контрагента: {applied:N2}",
            utcNow,
            actor,
            actorTimeZone);

        if (sourcePaymentId.HasValue
            && selectedSourceBalance.HasValue
            && selectedSourceBalance.Value - applied <= AmountEpsilon)
        {
            var sourcePayment = await db.Payments.FirstOrDefaultAsync(item => item.Id == sourcePaymentId.Value, ct);
            if (sourcePayment?.AdvanceReviewStatus == PaymentAdvanceReviewStatus.AcceptedAsAdvance)
            {
                PaymentAdvanceReviewService.MarkAllocated(
                    sourcePayment,
                    $"Аванс полностью распределен по платежу #{payment.Id}",
                    utcNow,
                    httpContext);
            }
        }

        db.SetAuditReason(sourcePaymentId.HasValue
            ? $"Контроль аванса: аванс #{sourcePaymentId.Value} зачтен в платеж #{payment.Id} на сумму {applied:N2}."
            : $"Зачет баланса контрагента в платеж #{payment.Id} на сумму {applied:N2}.");

        await InvoicePaymentStateService.RefreshAfterPaymentChangeAsync(db, payment, utcNow, ct);

        return (true, null, applied);
    }

    public static async Task UpsertCancelledInvoiceAdvanceAsync(
        PaymentContext db,
        Payment payment,
        decimal requestedAmount,
        string reason,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        if (!HasValidCounterpartyOwner(payment) || payment.Type != PaymentType.Income)
        {
            return;
        }

        var amount = NormalizeAmount(requestedAmount);
        if (amount <= AmountEpsilon)
        {
            return;
        }

        var externalKey = BuildCancelledInvoiceAdvanceKey(payment.Id);
        var existing = await db.ClientBalanceTransactions
            .FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);

        var actor = httpContext?.User.GetDisplayName();
        var actorUserId = httpContext?.User.GetUserId();
        var description = $"Аванс по аннулированному счёту №{payment.Account}. Причина: {reason}";

        if (existing is null)
        {
            db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
            {
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                InvoiceId = payment.InvoiceId,
                PaymentId = payment.Id,
                Amount = amount,
                Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice,
                Direction = ClientBalanceDirection.Credit,
                Description = description,
                ExternalKey = externalKey,
                CreatedAt = utcNow,
                CreatedByUserId = actorUserId,
                CreatedByUserName = actor,
            });
            return;
        }

        existing.ClientId = payment.ClientId;
        existing.LegalEntityId = payment.LegalEntityId;
        existing.InvoiceId = payment.InvoiceId;
        existing.PaymentId = payment.Id;
        existing.Amount = amount;
        existing.Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice;
        existing.Direction = ClientBalanceDirection.Credit;
        existing.Description = description;
    }

    public static async Task UpsertUnallocatedAdvanceAsync(
        PaymentContext db,
        Payment payment,
        decimal requestedAmount,
        string? reason,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        if (!HasValidCounterpartyOwner(payment) || payment.Type != PaymentType.Income)
        {
            return;
        }

        var amount = NormalizeAmount(requestedAmount);
        var externalKey = BuildUnallocatedAdvanceKey(payment.Id);
        var existing = await db.ClientBalanceTransactions
            .FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);

        if (amount <= AmountEpsilon)
        {
            if (existing is not null)
            {
                db.ClientBalanceTransactions.Remove(existing);
            }
            return;
        }

        var actor = httpContext?.User.GetDisplayName();
        var actorUserId = httpContext?.User.GetUserId();
        var description = string.IsNullOrWhiteSpace(reason)
            ? $"Нераспределенный аванс по платежу #{payment.Id}"
            : $"Нераспределенный аванс по платежу #{payment.Id}. {reason.Trim()}";

        if (existing is null)
        {
            db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
            {
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                InvoiceId = payment.InvoiceId,
                PaymentId = payment.Id,
                Amount = amount,
                Type = ClientBalanceTransactionType.UnallocatedAdvance,
                Direction = ClientBalanceDirection.Credit,
                Description = description,
                ExternalKey = externalKey,
                CreatedAt = utcNow,
                CreatedByUserId = actorUserId,
                CreatedByUserName = actor,
            });
            return;
        }

        existing.ClientId = payment.ClientId;
        existing.LegalEntityId = payment.LegalEntityId;
        existing.InvoiceId = payment.InvoiceId;
        existing.PaymentId = payment.Id;
        existing.Amount = amount;
        existing.Type = ClientBalanceTransactionType.UnallocatedAdvance;
        existing.Direction = ClientBalanceDirection.Credit;
        existing.Description = description;
    }

    public static async Task RemoveUnallocatedAdvanceAsync(PaymentContext db, int paymentId, CancellationToken ct = default)
    {
        var externalKey = BuildUnallocatedAdvanceKey(paymentId);
        var existing = await db.ClientBalanceTransactions
            .FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);
        if (existing is not null)
        {
            db.ClientBalanceTransactions.Remove(existing);
        }
    }

    public static async Task<(bool Success, string? Error, decimal RefundedAmount)> RefundUnallocatedAdvanceAsync(
        PaymentContext db,
        Payment payment,
        string? description,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        if (!HasValidCounterpartyOwner(payment) || payment.AdvanceReviewStatus != PaymentAdvanceReviewStatus.AcceptedAsAdvance)
        {
            return (false, "Вернуть можно только платеж, принятый как нераспределенный аванс.", 0m);
        }

        var available = await GetUnallocatedAdvanceAvailableAmountAsync(db, payment.Id, payment.ClientId, payment.LegalEntityId, ct);
        if (available <= AmountEpsilon)
        {
            return (false, "У выбранного аванса нет доступного остатка для возврата.", 0m);
        }

        var externalKey = BuildUnallocatedAdvanceRefundKey(payment.Id);
        var existing = await db.ClientBalanceTransactions
            .FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);

        var actor = httpContext?.User.GetDisplayName();
        var actorUserId = httpContext?.User.GetUserId();
        var refundDescription = string.IsNullOrWhiteSpace(description)
            ? $"Возврат нераспределенного аванса по платежу #{payment.Id}"
            : description.Trim();

        if (existing is null)
        {
            db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
            {
                ClientId = payment.ClientId,
                LegalEntityId = payment.LegalEntityId,
                PaymentId = payment.Id,
                Amount = available,
                Type = ClientBalanceTransactionType.Refund,
                Direction = ClientBalanceDirection.Debit,
                Description = refundDescription,
                ExternalKey = externalKey,
                CreatedAt = utcNow,
                CreatedByUserId = actorUserId,
                CreatedByUserName = actor,
            });
        }
        else
        {
            existing.ClientId = payment.ClientId;
            existing.LegalEntityId = payment.LegalEntityId;
            existing.PaymentId = payment.Id;
            existing.Amount = available;
            existing.Type = ClientBalanceTransactionType.Refund;
            existing.Direction = ClientBalanceDirection.Debit;
            existing.Description = refundDescription;
        }

        return (true, null, available);
    }

    public static string BuildPaymentOverpaymentKey(int paymentId) => $"payment-overpayment:{paymentId}";

    public static string BuildCancelledInvoiceAdvanceKey(int paymentId) => $"invoice-cancellation-advance:{paymentId}";

    public static string BuildCancelledInvoiceReallocationKey(int sourcePaymentId, int targetPaymentId)
        => $"invoice-cancellation-reallocate:{sourcePaymentId}:{targetPaymentId}";

    public static string BuildUnallocatedAdvanceKey(int paymentId) => $"payment-unallocated-advance:{paymentId}";

    public static string BuildUnallocatedAdvanceApplicationKeyPrefix(int sourcePaymentId)
        => $"payment-unallocated-advance-apply:{sourcePaymentId}:";

    public static string BuildUnallocatedAdvanceApplicationKey(int sourcePaymentId, int targetPaymentId, DateTime utcNow)
        => $"{BuildUnallocatedAdvanceApplicationKeyPrefix(sourcePaymentId)}{targetPaymentId}:{utcNow.Ticks}";

    public static string BuildUnallocatedAdvanceRefundKey(int sourcePaymentId)
        => $"payment-unallocated-advance-refund:{sourcePaymentId}";

    public static async Task<decimal> GetUnallocatedAdvanceAvailableAmountAsync(
        PaymentContext db,
        int sourcePaymentId,
        int? clientId,
        int? legalEntityId,
        CancellationToken ct = default)
    {
        if (!HasSingleOwner(clientId, legalEntityId))
        {
            return 0m;
        }

        var credited = await db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item =>
                item.ClientId == clientId
                && item.LegalEntityId == legalEntityId
                && item.PaymentId == sourcePaymentId
                && item.Type == ClientBalanceTransactionType.UnallocatedAdvance
                && item.Direction == ClientBalanceDirection.Credit)
            .SumAsync(item => item.Amount, ct);

        var externalKeyPrefix = BuildUnallocatedAdvanceApplicationKeyPrefix(sourcePaymentId);
        var applied = await db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item =>
                item.ClientId == clientId
                && item.LegalEntityId == legalEntityId
                && item.Type == ClientBalanceTransactionType.CreditApplied
                && item.Direction == ClientBalanceDirection.Debit
                && item.ExternalKey != null
                && item.ExternalKey.StartsWith(externalKeyPrefix))
            .SumAsync(item => item.Amount, ct);

        var refundKey = BuildUnallocatedAdvanceRefundKey(sourcePaymentId);
        var refunded = await db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item =>
                item.ClientId == clientId
                && item.LegalEntityId == legalEntityId
                && item.Type == ClientBalanceTransactionType.Refund
                && item.Direction == ClientBalanceDirection.Debit
                && item.ExternalKey == refundKey)
            .SumAsync(item => item.Amount, ct);

        return NormalizeAmount(Math.Max(credited - applied - refunded, 0m));
    }

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool HasValidCounterpartyOwner(Payment payment)
        => HasSingleOwner(payment.ClientId, payment.LegalEntityId);

    private static bool HasSingleOwner(int? clientId, int? legalEntityId)
        => (clientId.HasValue && clientId.Value > 0 && !legalEntityId.HasValue)
            || (legalEntityId.HasValue && legalEntityId.Value > 0 && !clientId.HasValue);

    private static string NormalizeDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description) ? string.Empty : description.Trim();
        return normalized.Length <= DescriptionMaxLength ? normalized : normalized[..DescriptionMaxLength];
    }

    private static string? NormalizeExternalKey(string? externalKey)
    {
        var normalized = string.IsNullOrWhiteSpace(externalKey) ? null : externalKey.Trim();
        return normalized;
    }
}
