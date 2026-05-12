using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>
/// Пересчитывает сохранённое состояние счёта после изменений связанных платежей.
/// </summary>
public static class InvoicePaymentStateService
{
    private const decimal AmountEpsilon = 0.009m;

    public static async Task RefreshAfterPaymentChangeAsync(
        PaymentContext db,
        Payment payment,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        if (payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
        {
            if (payment.DocumentKind == DocumentKind.Invoice)
            {
                RefreshRepresentativeFromSources(payment, new[] { payment }, utcNow);
                return;
            }

            await RefreshInvoiceAggregateAsync(db, payment.InvoiceId.Value, utcNow, ct);
            return;
        }

        if (payment.DocumentKind == DocumentKind.Invoice)
        {
            RefreshRepresentativeFromSources(payment, new[] { payment }, utcNow);
        }
    }

    public static async Task RefreshInvoiceAggregatesAsync(
        PaymentContext db,
        IEnumerable<int> invoiceIds,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(invoiceIds);

        foreach (var invoiceId in invoiceIds.Where(id => id > 0).Distinct())
        {
            await RefreshInvoiceAggregateAsync(db, invoiceId, utcNow, ct);
        }
    }

    public static async Task RefreshInvoiceAggregateAsync(
        PaymentContext db,
        int invoiceId,
        DateTime utcNow,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (invoiceId <= 0)
        {
            return;
        }

        var payments = await db.Payments
            .Where(payment => payment.InvoiceId == invoiceId)
            .ToListAsync(ct);

        if (payments.Count == 0)
        {
            return;
        }

        var representative = SelectRepresentativeInvoice(payments);
        if (representative is null)
        {
            return;
        }

        var statusSources = SelectStatusSourcePayments(payments, representative.Id, representative.Amount);
        ApplySnapshot(
            representative,
            CalculateRepresentativeState(representative, statusSources, utcNow));
    }

    public static InvoicePaymentStateEvaluation? EvaluateInvoiceAggregate(
        IReadOnlyCollection<Payment> payments,
        DateTime utcNow)
    {
        if (payments.Count == 0)
        {
            return null;
        }

        var representative = SelectRepresentativeInvoice(payments);
        if (representative is null)
        {
            return null;
        }

        var statusSources = SelectStatusSourcePayments(payments, representative.Id, representative.Amount);
        var current = CaptureState(representative);
        var suggested = CalculateRepresentativeState(representative, statusSources, utcNow);

        return new InvoicePaymentStateEvaluation(
            representative.InvoiceId,
            representative.Id,
            representative.Account,
            representative.AccountDate,
            statusSources.Count,
            current,
            suggested,
            !StatesEqual(current, suggested));
    }

    public static InvoicePaymentStateSnapshot CalculateRepresentativeState(
        Payment representative,
        IReadOnlyCollection<Payment> statusSources,
        DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(representative);

        var draft = CloneState(representative);
        RefreshRepresentativeFromSources(draft, statusSources, utcNow);
        return CaptureState(draft);
    }

    public static bool ApplySnapshot(Payment representative, InvoicePaymentStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(representative);
        ArgumentNullException.ThrowIfNull(snapshot);

        var current = CaptureState(representative);
        if (StatesEqual(current, snapshot))
        {
            return false;
        }

        representative.PaidAmount = snapshot.PaidAmount;
        representative.Status = snapshot.Status;
        representative.InvoiceWorkflowStatus = snapshot.WorkflowStatus;
        representative.IsPaid = snapshot.IsPaid;
        representative.Date = snapshot.Date;
        representative.PlannedDate = snapshot.PlannedDate;
        representative.PaidDate = snapshot.PaidDate;
        representative.LastPaymentDate = snapshot.LastPaymentDate;
        return true;
    }

    public static InvoicePaymentStateSnapshot CaptureState(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return new InvoicePaymentStateSnapshot(
            NormalizeMoneyAmount(payment.PaidAmount),
            payment.Status,
            InvoiceWorkflowStatus.Normalize(payment.InvoiceWorkflowStatus) ?? InvoiceWorkflowStatus.Draft,
            payment.IsPaid,
            payment.Date.Date,
            EffectivePlannedDate(payment),
            payment.PaidDate?.Date,
            payment.LastPaymentDate?.Date);
    }

    public static bool StatesEqual(InvoicePaymentStateSnapshot left, InvoicePaymentStateSnapshot right)
    {
        return AreAmountsEqual(left.PaidAmount, right.PaidAmount)
               && left.Status == right.Status
               && string.Equals(left.WorkflowStatus, right.WorkflowStatus, StringComparison.Ordinal)
               && left.IsPaid == right.IsPaid
               && left.Date.Date == right.Date.Date
               && left.PlannedDate.Date == right.PlannedDate.Date
               && NullableDatesEqual(left.PaidDate, right.PaidDate)
               && NullableDatesEqual(left.LastPaymentDate, right.LastPaymentDate);
    }

    public static List<Payment> SelectStatusSourcePayments(
        IReadOnlyCollection<Payment> payments,
        int representativePaymentId,
        decimal representativeAmount)
    {
        if (payments.Count == 0)
        {
            return new List<Payment>();
        }

        var ordered = payments
            .OrderBy(EffectivePlannedDate)
            .ThenBy(payment => payment.Id)
            .ToList();

        if (InvoiceManagedPaymentRules.ShouldHideRepresentativeInvoice(ordered))
        {
            return ordered
                .Where(payment => payment.Id != representativePaymentId)
                .ToList();
        }

        var linkedPayments = ordered
            .Where(payment => payment.DocumentKind != DocumentKind.Invoice)
            .ToList();

        return linkedPayments.Count > 0 ? linkedPayments : ordered;
    }

    public static PaymentStatus DeriveInvoiceStatus(
        IReadOnlyCollection<Payment> payments,
        decimal invoiceAmount,
        PaymentStatus fallbackStatus,
        DateTime? fallbackDueDate,
        DateTime utcNow)
    {
        if (payments.Count == 0)
        {
            return fallbackStatus switch
            {
                PaymentStatus.Completed => PaymentStatus.Completed,
                PaymentStatus.Cancelled => PaymentStatus.Cancelled,
                PaymentStatus.Processing => PaymentStatus.Processing,
                PaymentStatus.Overdue => PaymentStatus.Overdue,
                _ when fallbackDueDate.HasValue && fallbackDueDate.Value.Date < utcNow.Date => PaymentStatus.Overdue,
                _ => PaymentStatus.Pending,
            };
        }

        var paidAmount = NormalizeMoneyAmount(payments.Sum(ResolvePaidAmount));
        var outstandingAmount = NormalizeMoneyAmount(payments.Sum(ResolveOutstandingAmount));
        var normalizedInvoiceAmount = NormalizeMoneyAmount(invoiceAmount);

        if (payments.All(payment => payment.Status == PaymentStatus.Cancelled))
        {
            return PaymentStatus.Cancelled;
        }

        if (normalizedInvoiceAmount > 0m && paidAmount + AmountEpsilon >= normalizedInvoiceAmount)
        {
            return PaymentStatus.Completed;
        }

        if (payments.Any(payment => payment.Status == PaymentStatus.Overdue) && outstandingAmount > AmountEpsilon)
        {
            return PaymentStatus.Overdue;
        }

        if (fallbackDueDate.HasValue
            && fallbackDueDate.Value.Date < utcNow.Date
            && outstandingAmount > AmountEpsilon
            && paidAmount <= AmountEpsilon)
        {
            return PaymentStatus.Overdue;
        }

        if (paidAmount > AmountEpsilon
            || payments.Any(payment => payment.Status == PaymentStatus.Completed))
        {
            return PaymentStatus.Processing;
        }

        return PaymentStatus.Pending;
    }

    public static decimal ResolvePaidAmount(Payment payment)
        => ResolvePaidAmount(payment.Amount, payment.PaidAmount, payment.Status);

    public static decimal ResolveOutstandingAmount(Payment payment)
        => ResolveOutstandingAmount(payment.Amount, payment.PaidAmount, payment.Status);

    public static DateTime EffectivePlannedDate(Payment payment)
        => payment.PlannedDate == default ? payment.Date.Date : payment.PlannedDate.Date;

    private static void RefreshRepresentativeFromSources(
        Payment representative,
        IReadOnlyCollection<Payment> statusSources,
        DateTime utcNow)
    {
        if (statusSources.Count == 0)
        {
            representative.Status = DeriveInvoiceStatus(
                statusSources,
                representative.Amount,
                representative.Status,
                representative.Date,
                utcNow);
            representative.InvoiceWorkflowStatus = ResolveWorkflowStatus(
                representative,
                representative.Status,
                representative.PaidAmount);
            representative.IsPaid = representative.Status == PaymentStatus.Completed;
            PaymentDomainService.Normalize(representative);
            return;
        }

        representative.PaidAmount = NormalizeMoneyAmount(statusSources.Sum(ResolvePaidAmount));
        representative.Status = DeriveInvoiceStatus(
            statusSources,
            representative.Amount,
            representative.Status,
            statusSources.Select(EffectivePlannedDate).DefaultIfEmpty(representative.Date).Max(),
            utcNow);
        representative.InvoiceWorkflowStatus = ResolveWorkflowStatus(
            representative,
            representative.Status,
            representative.PaidAmount);
        representative.IsPaid = representative.Status == PaymentStatus.Completed;

        var lastPlannedDate = statusSources
            .Select(EffectivePlannedDate)
            .DefaultIfEmpty(representative.Date)
            .Max();
        representative.Date = lastPlannedDate;
        representative.PlannedDate = lastPlannedDate;

        representative.PaidDate = statusSources
            .Where(payment => payment.PaidDate.HasValue)
            .Select(payment => payment.PaidDate!.Value.Date)
            .DefaultIfEmpty()
            .Max();
        if (representative.PaidDate.HasValue && representative.PaidDate.Value == default)
        {
            representative.PaidDate = null;
        }

        representative.LastPaymentDate = statusSources
            .Where(payment => payment.LastPaymentDate.HasValue || payment.PaidDate.HasValue)
            .Select(payment => (payment.LastPaymentDate ?? payment.PaidDate)!.Value.Date)
            .DefaultIfEmpty()
            .Max();
        if (representative.LastPaymentDate.HasValue && representative.LastPaymentDate.Value == default)
        {
            representative.LastPaymentDate = null;
        }

        if (representative.Status == PaymentStatus.Completed)
        {
            representative.PaidDate ??= representative.LastPaymentDate ?? representative.Date;
            representative.LastPaymentDate ??= representative.PaidDate;
        }

        PaymentDomainService.Normalize(representative);
    }

    private static Payment? SelectRepresentativeInvoice(IEnumerable<Payment> payments)
    {
        return payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(payment => payment.Amount)
            .ThenBy(payment => payment.Id)
            .FirstOrDefault();
    }

    private static string ResolveWorkflowStatus(
        Payment representative,
        PaymentStatus status,
        decimal paidAmount)
    {
        if (status == PaymentStatus.Cancelled)
        {
            return InvoiceWorkflowStatus.Cancelled;
        }

        var normalizedCurrent = InvoiceWorkflowStatus.Normalize(representative.InvoiceWorkflowStatus);
        if (normalizedCurrent is not null && InvoiceWorkflowStatus.IsDocumentSigningStatus(normalizedCurrent))
        {
            return normalizedCurrent;
        }

        return InvoiceWorkflowStatus.FromPaymentStatus(status, paidAmount, representative.Amount);
    }

    private static decimal ResolvePaidAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        var normalizedAmount = NormalizeMoneyAmount(amount);
        var normalizedPaidAmount = Math.Min(NormalizeMoneyAmount(paidAmount), normalizedAmount);
        if (normalizedPaidAmount > AmountEpsilon)
        {
            return normalizedPaidAmount;
        }

        return status == PaymentStatus.Completed ? normalizedAmount : 0m;
    }

    private static decimal ResolveOutstandingAmount(decimal amount, decimal paidAmount, PaymentStatus status)
    {
        if (status is PaymentStatus.Completed or PaymentStatus.Cancelled)
        {
            return 0m;
        }

        var normalizedAmount = NormalizeMoneyAmount(amount);
        var resolvedPaidAmount = ResolvePaidAmount(normalizedAmount, paidAmount, status);
        return NormalizeMoneyAmount(Math.Max(normalizedAmount - resolvedPaidAmount, 0m));
    }

    private static decimal NormalizeMoneyAmount(decimal amount)
        => Math.Round(Math.Max(amount, 0m), 2, MidpointRounding.AwayFromZero);

    private static bool AreAmountsEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= 0.01m;

    private static bool NullableDatesEqual(DateTime? left, DateTime? right)
        => left?.Date == right?.Date;

    private static Payment CloneState(Payment payment)
        => new()
        {
            Account = payment.Account,
            AccountDate = payment.AccountDate,
            InvoiceId = payment.InvoiceId,
            Amount = payment.Amount,
            InvoiceWorkflowStatus = payment.InvoiceWorkflowStatus,
            Date = payment.Date,
            PlannedDate = payment.PlannedDate,
            PaidAmount = payment.PaidAmount,
            IsPaid = payment.IsPaid,
            LastPaymentDate = payment.LastPaymentDate,
            PaidDate = payment.PaidDate,
            Status = payment.Status,
            Type = payment.Type,
            ExpenseClientId = payment.ExpenseClientId,
            ExpenseLegalEntityId = payment.ExpenseLegalEntityId,
        };
}

public sealed record InvoicePaymentStateSnapshot(
    decimal PaidAmount,
    PaymentStatus Status,
    string WorkflowStatus,
    bool IsPaid,
    DateTime Date,
    DateTime PlannedDate,
    DateTime? PaidDate,
    DateTime? LastPaymentDate);

public sealed record InvoicePaymentStateEvaluation(
    int? InvoiceId,
    int RepresentativePaymentId,
    string? Account,
    DateTime? AccountDate,
    int SourcePaymentCount,
    InvoicePaymentStateSnapshot Current,
    InvoicePaymentStateSnapshot Suggested,
    bool HasChanges);
