using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class InvoiceManagedPaymentRules
{
    public const string InstallmentPaymentMarker = "[pp:installment-payment]";
    public const string UpdateBlockedMessage = "Платежи счета и график рассрочки редактируются только через раздел \"Счета\".";
    public const string DeleteBlockedMessage = "Счет и график рассрочки удаляются только через раздел \"Счета\".";

    public static bool IsGeneratedInstallmentPayment(Payment payment)
    {
        return !string.IsNullOrWhiteSpace(payment.SystemNotes)
               && payment.SystemNotes.Contains(InstallmentPaymentMarker, StringComparison.Ordinal);
    }

    public static bool IsInvoiceManagedPayment(Payment payment)
    {
        return payment.DocumentKind == DocumentKind.Invoice || IsGeneratedInstallmentPayment(payment);
    }

    public static bool ShouldHideRepresentativeInvoice(IEnumerable<Payment> payments)
    {
        var snapshot = payments
            .Where(payment => payment is not null)
            .ToList();

        if (snapshot.Count == 0)
        {
            return false;
        }

        return snapshot.Any(IsGeneratedInstallmentPayment)
            || IsLegacyBrokenInstallmentAggregate(snapshot);
    }

    private static bool IsLegacyBrokenInstallmentAggregate(IReadOnlyCollection<Payment> payments)
    {
        if (payments.Count < 3 || payments.Any(IsGeneratedInstallmentPayment))
        {
            return false;
        }

        var invoiceRows = payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .ToList();
        if (invoiceRows.Count < 2)
        {
            return false;
        }

        var nonInvoiceCount = payments.Count - invoiceRows.Count;
        if (nonInvoiceCount > 1)
        {
            return false;
        }

        var representativeInvoice = invoiceRows
            .OrderByDescending(payment => payment.Amount)
            .ThenBy(payment => payment.Id)
            .FirstOrDefault();
        if (representativeInvoice is null)
        {
            return false;
        }

        var maxSiblingAmount = payments
            .Where(payment => payment.Id != representativeInvoice.Id)
            .Select(payment => payment.Amount)
            .DefaultIfEmpty(0m)
            .Max();

        return representativeInvoice.Amount > maxSiblingAmount;
    }
}
