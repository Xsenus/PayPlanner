using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Requests;

/// <summary>Checks recorded amounts of an explicitly linked invoice group; this is not proof of actual receipt.</summary>
/// <remarks>The caller must load the complete group under appropriate access and locks, and independently verify the payment provenance before allowing a request to succeed.</remarks>
public static class SalesRequestInvoiceSettlement
{
    public const int MaximumRows = 5000;

    public static SalesRequestInvoiceSettlementResult Evaluate(int invoiceId, int clientId, IReadOnlyCollection<Payment> payments)
    {
        if (invoiceId <= 0 || clientId <= 0 || payments.Count == 0 || payments.Count > MaximumRows
            || payments.Any(p => p.Id <= 0 || p.InvoiceId != invoiceId || p.ClientId != clientId || p.Type != PaymentType.Income
                || decimal.Round(p.Amount, 2) != p.Amount || decimal.Round(p.PaidAmount, 2) != p.PaidAmount)
            || payments.Select(p => p.Id).Distinct().Count() != payments.Count)
            return new(false, 0m, 0m);

        var representatives = payments.Where(p => p.DocumentKind == DocumentKind.Invoice && p.Status != PaymentStatus.Cancelled).ToArray();
        if (representatives.Length != 1 || payments.Any(p => p.DocumentKind == DocumentKind.Invoice && p.Status == PaymentStatus.Cancelled))
            return new(false, 0m, 0m);
        var invoice = representatives[0];
        if (InvoiceWorkflowStatus.Normalize(invoice.InvoiceWorkflowStatus) == InvoiceWorkflowStatus.Cancelled || invoice.Amount <= 0m)
            return new(false, 0m, 0m);
        if (payments.Any(p => p.Id != invoice.Id && p.DocumentKind != DocumentKind.Invoice)
            && !payments.Any(p => p.Id != invoice.Id && p.DocumentKind != DocumentKind.Invoice && p.Status != PaymentStatus.Cancelled))
            return new(false, 0m, 0m);

        var sources = InvoicePaymentStateService.SelectStatusSourcePayments(payments, invoice.Id, invoice.Amount);
        if (sources.Count == 0 || sources.Any(p => p.Status == PaymentStatus.Cancelled || p.Amount < 0m || p.PaidAmount < 0m))
            return new(false, 0m, 0m);

        var installments = payments.Where(p => p.Status != PaymentStatus.Cancelled && InvoiceManagedPaymentRules.IsGeneratedInstallmentPayment(p)).ToArray();
        var scheduledTotal = installments.Length > 0 ? installments.Sum(p => p.Amount) : sources.Sum(p => p.Amount);
        if (installments.Any(p => p.Amount < 0m)) return new(false, 0m, 0m);
        var total = Normalize(Math.Max(invoice.Amount, scheduledTotal));
        var paid = Normalize(sources.Sum(p => Math.Min(p.Amount, p.PaidAmount)));
        return new(total > 0m && paid >= total, total, paid);
    }

    private static decimal Normalize(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}

public sealed record SalesRequestInvoiceSettlementResult(bool HasFullRecordedAmount, decimal Total, decimal Paid);
