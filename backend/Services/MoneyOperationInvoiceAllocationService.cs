using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// Общий расчёт оплаты счёта из денежной операции, включая его платежи рассрочки.
/// Проверка прав, повторного применения и транзакция выполняются вызывающим механизмом.
/// </summary>
public static class MoneyOperationInvoiceAllocationService
{
    // Existing CRM invoices are denominated in rubles. A missing operation
    // currency is not evidence that the bank movement was in rubles.
    public static bool HasConfirmedRubleCurrency(MoneyOperation operation)
        => string.Equals(operation.Currency?.Trim(), "RUB", StringComparison.OrdinalIgnoreCase);

    public static async Task ApplyAsync(
        PaymentContext db,
        MoneyOperation operation,
        int invoiceId,
        CancellationToken ct = default,
        IReadOnlyCollection<Payment>? loadedPayments = null)
    {
        if (operation.Direction is not (MoneyOperationDirection.Income or MoneyOperationDirection.Expense) || operation.Amount <= 0m)
        {
            return;
        }
        if (!HasConfirmedRubleCurrency(operation))
            throw new InvalidOperationException("Валюта денежной операции не подтверждена как рубли; сверка со счётом недоступна.");

        var targetType = operation.Direction == MoneyOperationDirection.Expense ? PaymentType.Expense : PaymentType.Income;
        var payments = loadedPayments is null
            ? await db.Payments.Where(item => item.InvoiceId == invoiceId && item.Type == targetType && item.Status != PaymentStatus.Cancelled).ToListAsync(ct)
            : loadedPayments.Where(item => item.InvoiceId == invoiceId && item.Type == targetType && item.Status != PaymentStatus.Cancelled).ToList();
        var representative = payments.Where(item => item.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(item => item.Amount).ThenBy(item => item.Id).FirstOrDefault();
        if (representative is null) return;

        var sources = InvoicePaymentStateService.SelectStatusSourcePayments(payments, representative.Id, representative.Amount);
        var remaining = Math.Round(operation.Amount, 2, MidpointRounding.AwayFromZero);
        var applied = 0m;
        Payment? lastApplied = null;
        var sourceLabel = operation.Source switch
        {
            MoneyOperationSource.Cash => "касса",
            MoneyOperationSource.Card => "карты",
            MoneyOperationSource.EMoney => "e-money",
            _ => "банк",
        };
        var movementLabel = targetType == PaymentType.Expense ? "списание" : "поступление";
        var now = DateTime.UtcNow;

        foreach (var payment in sources.OrderBy(InvoicePaymentStateService.EffectivePlannedDate).ThenBy(item => item.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (remaining <= 0m) break;
            var outstanding = InvoicePaymentStateService.ResolveOutstandingAmount(payment);
            var amount = Math.Round(Math.Min(remaining, outstanding), 2, MidpointRounding.AwayFromZero);
            if (amount <= 0m) continue;

            payment.PaidAmount = Math.Round(Math.Max(payment.PaidAmount, 0m) + amount, 2, MidpointRounding.AwayFromZero);
            payment.LastPaymentDate = operation.Date;
            payment.PaymentDocumentDate ??= operation.RecognitionDate ?? operation.Date;
            payment.PaymentOrderNumber ??= operation.PaymentOrderNumber;
            if (payment.PaidAmount + 0.009m >= payment.Amount)
            {
                payment.PaidAmount = payment.Amount;
                payment.Status = PaymentStatus.Completed;
                payment.IsPaid = true;
                payment.PaidDate ??= operation.Date;
            }
            else
            {
                payment.Status = PaymentStatus.Processing;
                payment.IsPaid = false;
                payment.PaidDate = null;
            }

            AppendNote(payment, $"Системно: {now:dd.MM.yyyy HH:mm:ss} — зачтено {movementLabel} из раздела \"Деньги\" ({sourceLabel}) на сумму {amount.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))}.");
            remaining -= amount;
            applied += amount;
            lastApplied = payment;
        }

        // Переплата возникает только после покрытия всех платежей счёта, а не первого взноса.
        var clientId = lastApplied?.ClientId ?? representative.ClientId;
        if (applied > 0m && targetType == PaymentType.Income && remaining > 0.009m && clientId.HasValue && operation.Id > 0)
        {
            var externalKey = $"money-operation-overpayment:{operation.Id}";
            var balance = await db.ClientBalanceTransactions.FirstOrDefaultAsync(item => item.ExternalKey == externalKey, ct);
            if (balance is null)
            {
                balance = new ClientBalanceTransaction { ExternalKey = externalKey, CreatedAt = now };
                db.ClientBalanceTransactions.Add(balance);
            }
            balance.ClientId = clientId.Value;
            balance.InvoiceId = invoiceId;
            balance.PaymentId = lastApplied!.Id;
            balance.Amount = remaining;
            balance.Type = ClientBalanceTransactionType.Overpayment;
            balance.Direction = ClientBalanceDirection.Credit;
            balance.Description = $"Переплата по денежной операции #{operation.Id}";
            AppendNote(lastApplied, $"Системно: {now:dd.MM.yyyy HH:mm:ss} — переплата {remaining.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"))} зачислена на баланс клиента.");
        }

        if (applied > 0m)
        {
            InvoicePaymentStateService.ApplySnapshot(representative,
                InvoicePaymentStateService.CalculateRepresentativeState(representative, sources, now));
        }
    }

    private static void AppendNote(Payment payment, string note)
        => payment.SystemNotes = string.IsNullOrWhiteSpace(payment.SystemNotes) ? note : $"{payment.SystemNotes.Trim()}\n{note}";
}
