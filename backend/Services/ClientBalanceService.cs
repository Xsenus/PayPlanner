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
        => db.ClientBalanceTransactions
            .AsNoTracking()
            .Where(item => item.ClientId == clientId)
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
        if (!payment.ClientId.HasValue || payment.ClientId.Value <= 0 || payment.Type != PaymentType.Income)
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
        var description = $"Переплата по платежу #{payment.Id}";

        if (existing is null)
        {
            db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
            {
                ClientId = payment.ClientId.Value,
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

        existing.ClientId = payment.ClientId.Value;
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
    {
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

        if (payment.ClientId != clientId)
        {
            return (false, "Payment does not belong to this client.", 0m);
        }

        PaymentDomainService.Normalize(payment);
        var outstanding = NormalizeAmount(payment.Amount - payment.PaidAmount);
        if (outstanding <= AmountEpsilon)
        {
            return (false, "Payment is already paid.", 0m);
        }

        var balance = await GetBalanceAsync(db, clientId, ct);
        if (balance <= AmountEpsilon)
        {
            return (false, "Client balance is empty.", 0m);
        }

        var applied = Math.Min(amount, Math.Min(balance, outstanding));
        applied = NormalizeAmount(applied);
        if (applied <= AmountEpsilon)
        {
            return (false, "Nothing to apply.", 0m);
        }

        payment.PaidAmount = NormalizeAmount(payment.PaidAmount + applied);
        payment.LastPaymentDate ??= utcNow.Date;
        PaymentDomainService.ApplyStatusRules(payment, utcNow);

        var actor = httpContext?.User.GetDisplayName();
        var actorUserId = httpContext?.User.GetUserId();
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = clientId,
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
            $"Зачтено с баланса клиента: {applied:N2}",
            utcNow,
            actor);

        return (true, null, applied);
    }

    public static string BuildPaymentOverpaymentKey(int paymentId) => $"payment-overpayment:{paymentId}";

    private static decimal NormalizeAmount(decimal value)
        => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
