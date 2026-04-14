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
        return payments.Any(IsGeneratedInstallmentPayment);
    }
}
