using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SalesRequestServiceInvoicePaymentTests
{
    private static Payment Invoice() => new() { Id = 1, InvoiceId = 10, ClientId = 20, SellerCompanyId = 30, Type = PaymentType.Income, DocumentKind = DocumentKind.Invoice, Amount = 100m };
    private static MoneyAccount Account() => new() { Id = 40, CompanyId = 30, Source = MoneyOperationSource.Bank, Currency = "RUB" };
    private static MoneyOperation Receipt() => new() { Id = 50, LinkedInvoiceId = 10, MoneyAccountId = 40, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, MatchStatus = MoneyOperationMatchStatus.Matched, MatchedAt = DateTime.UtcNow, Currency = "RUB", Amount = 100m };
    private static bool Check(Payment invoice, MoneyOperation operation, MoneyAccount account, string? currency = "RUB")
        => SalesRequestServiceInvoicePayment.IsFullyPaid(10, 20, currency, [invoice], [operation], [account]);

    [Fact]
    public void FullReceiptDoesNotDependOnRecordedStatus()
    {
        var invoice = Invoice();
        invoice.PaidAmount = 0m;
        Assert.True(Check(invoice, Receipt(), Account()));
        var receipt = Receipt(); receipt.Amount = 99.99m;
        invoice.PaidAmount = 100m;
        Assert.False(Check(invoice, receipt, Account()));
    }

    [Fact]
    public void RequiresTheSameCompanyAccountCurrencyAndSource()
    {
        var invoice = Invoice(); var receipt = Receipt(); var account = Account();
        account.CompanyId = 31; Assert.False(Check(invoice, receipt, account));
        account.CompanyId = 30; account.Currency = "USD"; Assert.False(Check(invoice, receipt, account));
        account.Currency = "RUB"; account.Source = MoneyOperationSource.Cash; Assert.False(Check(invoice, receipt, account));
        account.Source = MoneyOperationSource.Bank; receipt.MoneyAccountId = null; Assert.False(Check(invoice, receipt, account));
        receipt.MoneyAccountId = 40; Assert.False(Check(invoice, receipt, account, null));
    }

    [Fact]
    public void WrongClientPlanAndDuplicateAccountFailClosed()
    {
        var invoice = Invoice(); var receipt = Receipt(); var account = Account();
        invoice.ClientId = 21; Assert.False(Check(invoice, receipt, account));
        invoice.ClientId = 20; receipt.AccountingType = MoneyOperationAccountingType.Plan; Assert.False(Check(invoice, receipt, account));
        receipt.AccountingType = MoneyOperationAccountingType.Fact;
        Assert.False(SalesRequestServiceInvoicePayment.IsFullyPaid(10, 20, "RUB", [invoice], [receipt], [account, account]));
    }
}
