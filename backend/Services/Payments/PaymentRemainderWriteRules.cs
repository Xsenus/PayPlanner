using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Payments;

// Check requested values before normalization can conceal a forbidden change.
// All callers already own their payment transaction and current row lock.
internal static class PaymentRemainderWriteRules
{
    public static string BuildConfirmationToken(Payment payment)
    {
        // An opaque optimistic snapshot, not a permission/secret. Only mapped
        // scalar inputs participate; read-model names and related graphs do not.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            payment.Id,
            Binding = PaymentBalanceHistoryRules.Binding.From(payment),
            payment.Amount,
            payment.PaidAmount,
            payment.Status,
            payment.IsPaid,
            payment.Date,
            payment.PlannedDate,
            payment.PaidDate,
            payment.LastPaymentDate,
            payment.Account,
            payment.AccountDate,
            payment.LegacyAccountNumber,
            payment.LegacyAccountDate,
            payment.ContractId,
            payment.ClientCaseId,
            payment.DealTypeId,
            payment.IncomeTypeId,
            payment.ServiceId,
            payment.PaymentSourceId,
            payment.PaymentStatusId,
            payment.SellerCompanyId,
            payment.SellerBranchId,
            payment.AdvanceReviewStatus,
            payment.Description,
            payment.Notes,
            payment.SystemNotes
        });
        return "remainder-v1:" + Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static async Task<string?> ValidateUpdateAsync(PaymentContext db, Payment current, Payment model, CancellationToken ct)
    {
        var amountChanged = Math.Abs(model.Amount - current.Amount) > 0.009m;
        var paidChanged = Math.Abs(model.PaidAmount - current.PaidAmount) > 0.009m;
        var statusChanged = model.Status != current.Status || model.IsPaid != current.IsPaid;
        var cancelling = model.Status == PaymentStatus.Cancelled && current.Status != PaymentStatus.Cancelled;
        var requestedBinding = PaymentBalanceHistoryRules.Binding.From(model) with { Kind = model.DocumentKind == default ? DocumentKind.Payment : model.DocumentKind };
        var bindingChanged = PaymentBalanceHistoryRules.Binding.From(current) != requestedBinding
            || Normalize(current.Account) != Normalize(model.Account) || current.AccountDate?.Date != model.AccountDate?.Date;
        if (!amountChanged && !paidChanged && !statusChanged && !bindingChanged) return null;

        if ((amountChanged || cancelling || bindingChanged) && await IsRemainderAsync(db, current, ct))
        {
            if (bindingChanged) return PaymentRemainderTransferRules.BindingUpdateBlockedMessage;
            return amountChanged ? PaymentRemainderTransferRules.RemainderAmountUpdateBlockedMessage : PaymentRemainderTransferRules.RemainderCancelBlockedMessage;
        }
        if (await HasTransferredRemainderAsync(db, current, ct))
        {
            if (bindingChanged) return PaymentRemainderTransferRules.BindingUpdateBlockedMessage;
            if (amountChanged || paidChanged) return PaymentRemainderTransferRules.SourceAmountUpdateBlockedMessage;
            if (statusChanged) return PaymentRemainderTransferRules.SourceStatusUpdateBlockedMessage;
        }
        return null;
    }

    public static async Task<string?> ValidateStatusAsync(PaymentContext db, Payment current, PaymentStatus next, CancellationToken ct)
    {
        if (next == PaymentStatus.Cancelled && await IsRemainderAsync(db, current, ct))
            return PaymentRemainderTransferRules.RemainderCancelBlockedMessage;
        // The status action also rewrites paid fields even when the requested
        // status is unchanged. Preserve inconsistent older source rows too.
        var paidAfter = next == PaymentStatus.Completed ? current.Amount
            : next == PaymentStatus.Cancelled || current.PaidAmount >= current.Amount - 0.009m ? 0m : current.PaidAmount;
        var changesSource = next != current.Status || current.IsPaid != (next == PaymentStatus.Completed)
            || Math.Abs(paidAfter - current.PaidAmount) > 0.009m;
        return changesSource && await HasTransferredRemainderAsync(db, current, ct) ? PaymentRemainderTransferRules.SourceStatusUpdateBlockedMessage : null;
    }

    public static async Task<string?> ValidateDeleteAsync(PaymentContext db, Payment current, CancellationToken ct)
    {
        if (await IsRemainderAsync(db, current, ct)) return PaymentRemainderTransferRules.RemainderDeleteBlockedMessage;
        return await HasTransferredRemainderAsync(db, current, ct) ? PaymentRemainderTransferRules.SourceDeleteBlockedMessage : null;
    }

    private static Task<bool> IsRemainderAsync(PaymentContext db, Payment current, CancellationToken ct)
        => PaymentRemainderTransferRules.IsPartialRemainderPayment(current) ? Task.FromResult(true)
            : db.PaymentRemainderTransfers.AsNoTracking().AnyAsync(link => link.RemainderPaymentId == current.Id, ct);

    internal static async Task<bool> HasTransferredRemainderAsync(PaymentContext db, Payment current, CancellationToken ct)
    {
        if (PaymentRemainderTransferRules.IsPartialRemainderSourcePayment(current)
            || await db.PaymentRemainderTransfers.AsNoTracking().AnyAsync(link => link.SourcePaymentId == current.Id, ct)) return true;
        // Old transfers can have only a child marker. Stream the narrow scalar
        // candidates, then parse the exact source ID to reject partial matches.
        var token = current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var candidates = db.Payments.AsNoTracking().ApplyUnambiguousInvoiceLinks()
            .Where(payment => payment.SystemNotes != null && payment.SystemNotes.Contains(PaymentRemainderTransferRules.PartialRemainderMarker)
                && payment.SystemNotes.Contains(token)).Select(payment => payment.SystemNotes);
        await foreach (var notes in candidates.AsAsyncEnumerable().WithCancellation(ct))
            if (PaymentRemainderTransferRules.TryReadMarker(notes, out var marker) && marker.SourcePaymentId == current.Id) return true;
        return false;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
