using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class ClientBalanceService
{
    private const decimal AmountEpsilon = 0.009m;

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
        CancellationToken ct = default)
        => await ApplyCounterpartyCreditAsync(db, clientId, null, paymentId, requestedAmount, utcNow, httpContext, ct);

    public static async Task<(bool Success, string? Error, decimal AppliedAmount)> ApplyLegalEntityCreditAsync(
        PaymentContext db,
        int legalEntityId,
        int paymentId,
        decimal requestedAmount,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
        => await ApplyCounterpartyCreditAsync(db, null, legalEntityId, paymentId, requestedAmount, utcNow, httpContext, ct);

    public static async Task<(bool Success, string? Error, decimal AppliedAmount)> ApplyCounterpartyCreditAsync(
        PaymentContext db,
        int? clientId,
        int? legalEntityId,
        int paymentId,
        decimal requestedAmount,
        DateTime utcNow,
        HttpContext? httpContext,
        CancellationToken ct = default)
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

        var applied = Math.Min(amount, Math.Min(balance, outstanding));
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
            Description = $"Зачет баланса в платеж #{payment.Id}",
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

    public static string BuildPaymentOverpaymentKey(int paymentId) => $"payment-overpayment:{paymentId}";

    public static string BuildCancelledInvoiceAdvanceKey(int paymentId) => $"invoice-cancellation-advance:{paymentId}";

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool HasValidCounterpartyOwner(Payment payment)
        => HasSingleOwner(payment.ClientId, payment.LegalEntityId);

    private static bool HasSingleOwner(int? clientId, int? legalEntityId)
        => (clientId.HasValue && clientId.Value > 0 && !legalEntityId.HasValue)
            || (legalEntityId.HasValue && legalEntityId.Value > 0 && !clientId.HasValue);
}
