using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Requests;

/// <summary>Combines invoice totals with attributed receipts without treating recorded payment status as a receipt.</summary>
/// <remarks>The caller must supply complete, authorized and concurrency-protected data, and establish the invoice currency.</remarks>
public static class SalesRequestServiceInvoicePayment
{
    public static bool IsFullyPaid(int invoiceId, int clientId, string? currency, IReadOnlyCollection<Payment> payments,
        IReadOnlyCollection<MoneyOperation> operations, IReadOnlyCollection<MoneyAccount> accounts)
    {
        var settlement = SalesRequestInvoiceSettlement.Evaluate(invoiceId, clientId, payments);
        if (settlement.Total <= 0m || string.IsNullOrWhiteSpace(currency)) return false;
        var header = payments.SingleOrDefault(payment => payment.DocumentKind == DocumentKind.Invoice && payment.Status != PaymentStatus.Cancelled);
        if (header?.SellerCompanyId is not > 0 || payments.Any(payment => payment.SellerCompanyId != header.SellerCompanyId
            || payment.SellerBranchId != header.SellerBranchId)) return false;
        if (accounts.Any(account => account.Id <= 0) || accounts.Select(account => account.Id).Distinct().Count() != accounts.Count) return false;
        var accountById = accounts.ToDictionary(account => account.Id);
        foreach (var operation in operations.Where(operation => operation.MatchStatus == MoneyOperationMatchStatus.Matched))
        {
            if (operation.MoneyAccountId is not int accountId || !accountById.TryGetValue(accountId, out var account)
                || account.CompanyId != header.SellerCompanyId || account.Source != operation.Source
                || !string.Equals(account.Currency?.Trim(), currency.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return SalesRequestMatchedReceipts.Evaluate(invoiceId, currency, settlement.Total, operations).HasEnoughMatchedAmount;
    }
}
