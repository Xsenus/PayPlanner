using System.Text.Json;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public static class InvoiceManagedPaymentRules
{
    public const string InstallmentPaymentMarker = "[pp:installment-payment]";
    public const string UpdateBlockedMessage = "Платежи счета и график рассрочки редактируются только через раздел \"Счета\".";
    public const string DeleteBlockedMessage = "Счет и график рассрочки удаляются только через раздел \"Счета\".";

    public static IReadOnlyCollection<Payment> FilterVisiblePayments(IEnumerable<Payment> payments)
    {
        var snapshot = payments
            .Where(payment => payment is not null)
            .ToList();

        if (snapshot.Count == 0)
        {
            return snapshot;
        }

        var hiddenRepresentativeIds = CollectRepresentativeInvoiceIdsToHide(snapshot);
        if (hiddenRepresentativeIds.Count == 0)
        {
            return snapshot;
        }

        return snapshot
            .Where(payment => !hiddenRepresentativeIds.Contains(payment.Id))
            .ToList();
    }

    public static bool IsGeneratedInstallmentPayment(Payment payment)
    {
        return !string.IsNullOrWhiteSpace(payment.SystemNotes)
               && payment.SystemNotes.Contains(InstallmentPaymentMarker, StringComparison.Ordinal);
    }

    public static bool IsGeneratedInvoiceRemainderPayment(Payment payment)
    {
        return payment.InvoiceId.HasValue
               && payment.InvoiceId.Value > 0
               && PaymentRemainderTransferRules.IsPartialRemainderPayment(payment);
    }

    public static bool IsInvoiceManagedPayment(Payment payment)
    {
        return IsLinkedRepresentativeInvoicePayment(payment)
               || IsGeneratedInstallmentPayment(payment)
               || IsGeneratedInvoiceRemainderPayment(payment);
    }

    private static bool IsLinkedRepresentativeInvoicePayment(Payment payment)
        => payment.DocumentKind == DocumentKind.Invoice
           && payment.InvoiceId.HasValue
           && payment.InvoiceId.Value > 0;

    public static bool ShouldHideRepresentativeInvoice(IEnumerable<Payment> payments)
    {
        var snapshot = payments
            .Where(payment => payment is not null)
            .ToList();

        if (snapshot.Count == 0)
        {
            return false;
        }

        if (IsConvertedLegacyInstallmentAggregate(snapshot))
        {
            return false;
        }

        return snapshot.Any(payment => payment.DocumentKind != DocumentKind.Invoice)
            || snapshot.Any(IsGeneratedInstallmentPayment)
            || IsLegacyBrokenInstallmentAggregate(snapshot);
    }

    private static HashSet<int> CollectRepresentativeInvoiceIdsToHide(IEnumerable<Payment> payments)
    {
        var hiddenIds = new HashSet<int>();

        foreach (var group in payments.GroupBy(BuildAggregateKey, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = group.ToList();
            if (!ShouldHideRepresentativeInvoice(snapshot))
            {
                continue;
            }

            var representativeInvoice = SelectRepresentativeInvoice(snapshot);
            if (representativeInvoice is not null)
            {
                hiddenIds.Add(representativeInvoice.Id);
            }
        }

        return hiddenIds;
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

        var representativeInvoice = SelectRepresentativeInvoice(invoiceRows);
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

    private static bool IsConvertedLegacyInstallmentAggregate(IReadOnlyCollection<Payment> payments)
    {
        if (payments.Count < 3)
        {
            return false;
        }

        var representativeInvoice = SelectRepresentativeInvoice(payments);
        if (representativeInvoice is null || representativeInvoice.Amount <= 0m)
        {
            return false;
        }

        var generatedInstallments = payments
            .Where(payment => payment.Id != representativeInvoice.Id && IsGeneratedInstallmentPayment(payment))
            .ToList();
        var generatedInstallmentTotal = generatedInstallments.Sum(payment => payment.Amount);

        // During legacy repair some installment rows were converted to generated
        // payments, while the selected representative stayed as another
        // installment. Normal installment plans can also exceed the invoice
        // amount because of overpay, so treat the aggregate as converted only
        // when marker metadata does not describe the generated total.
        return generatedInstallmentTotal > representativeInvoice.Amount
               && !HasConsistentInstallmentToPay(generatedInstallments, generatedInstallmentTotal);
    }

    private static bool HasConsistentInstallmentToPay(IEnumerable<Payment> payments, decimal expectedTotal)
        => payments.Any(payment => TryReadInstallmentToPay(payment.SystemNotes, out var toPay)
                                   && AreAmountsEqual(toPay, expectedTotal));

    private static bool TryReadInstallmentToPay(string? systemNotes, out decimal toPay)
    {
        toPay = 0m;
        if (string.IsNullOrWhiteSpace(systemNotes))
        {
            return false;
        }

        var markerLine = systemNotes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(InstallmentPaymentMarker, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(markerLine))
        {
            return false;
        }

        var json = markerLine[InstallmentPaymentMarker.Length..].Trim();
        if (string.IsNullOrWhiteSpace(json) || json[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryGetDecimalProperty(document.RootElement, "ToPay", out toPay);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetDecimalProperty(JsonElement element, string name, out decimal value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetDecimal(out value))
            {
                return true;
            }

            break;
        }

        value = 0m;
        return false;
    }

    private static bool AreAmountsEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= 0.01m;

    private static Payment? SelectRepresentativeInvoice(IEnumerable<Payment> payments)
    {
        return payments
            .Where(payment => payment.DocumentKind == DocumentKind.Invoice)
            .OrderByDescending(payment => payment.Amount)
            .ThenBy(payment => payment.Id)
            .FirstOrDefault();
    }

    private static string BuildAggregateKey(Payment payment)
    {
        if (payment.InvoiceId.HasValue && payment.InvoiceId.Value > 0)
        {
            return $"entity:{payment.InvoiceId.Value}";
        }

        var legacyGroupKey = BuildLegacyGroupKey(payment);
        return legacyGroupKey is not null ? $"legacy:{legacyGroupKey}" : $"payment:{payment.Id}";
    }

    private static string? BuildLegacyGroupKey(Payment payment)
    {
        var normalizedAccount = NormalizeAccount(payment.LegacyAccountNumber ?? payment.Account);
        var effectiveClientId = ResolveAggregateClientId(payment);
        if (normalizedAccount is null || !effectiveClientId.HasValue || effectiveClientId.Value <= 0)
        {
            return null;
        }

        var accountYear = (payment.LegacyAccountDate ?? payment.AccountDate ?? payment.Date).Year;
        return $"{normalizedAccount}::{accountYear}::client:{effectiveClientId.Value}::case:{payment.ClientCaseId?.ToString() ?? "none"}::type:{payment.Type}";
    }

    private static int? ResolveAggregateClientId(Payment payment)
        => payment.Type == PaymentType.Expense
            ? payment.ExpenseClientId ?? payment.ClientId
            : payment.ClientId;

    private static string? NormalizeAccount(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
