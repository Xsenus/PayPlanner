using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SalesRequestInvoiceSettlementTests
{
    private static Payment Invoice(decimal amount = 100m, decimal paid = 0m) => new()
    {
        Id = 1,
        InvoiceId = 10,
        ClientId = 20,
        Type = PaymentType.Income,
        DocumentKind = DocumentKind.Invoice,
        Amount = amount,
        PaidAmount = paid,
    };

    [Theory]
    [InlineData(0, false)]
    [InlineData(99.99, false)]
    [InlineData(100, true)]
    public void SingleInvoice_RequiresActualFullPaidAmount(decimal paid, bool expected)
    {
        var invoice = Invoice(paid: paid);
        invoice.Status = PaymentStatus.Completed;
        Assert.Equal(expected, SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice]).HasFullRecordedAmount);
    }

    [Fact]
    public void DifferentInvoiceOrCustomerAndExpenseAreNeverAccepted()
    {
        var invoice = Invoice(paid: 100m);
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(11, 20, [invoice]).HasFullRecordedAmount);
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 21, [invoice]).HasFullRecordedAmount);
        invoice.Type = PaymentType.Expense;
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice]).HasFullRecordedAmount);
    }

    [Fact]
    public void DuplicateRowsCancelledOrAmbiguousInvoiceFailClosed()
    {
        var invoice = Invoice(paid: 100m);
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, invoice]).HasFullRecordedAmount);
        var other = Invoice(paid: 100m); other.Id = 2;
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, other]).HasFullRecordedAmount);
        invoice.Status = PaymentStatus.Cancelled;
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice]).HasFullRecordedAmount);
    }

    [Theory]
    [InlineData(50, false)]
    [InlineData(100, true)]
    public void Installments_RequireActualTotalWithoutCountingInvoiceTwice(decimal paid, bool expected)
    {
        var invoice = Invoice(paid: 100m);
        var first = new Payment { Id = 2, InvoiceId = 10, ClientId = 20, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 50m, PaidAmount = 50m, SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker };
        var second = new Payment { Id = 3, InvoiceId = 10, ClientId = 20, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 50m, PaidAmount = paid - 50m, SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker };
        Assert.Equal(expected, SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, first, second]).HasFullRecordedAmount);
    }

    [Fact]
    public void PaidOriginalAmountDoesNotHideAnUnpaidInstallmentSurcharge()
    {
        var invoice = Invoice();
        var first = new Payment { Id = 2, InvoiceId = 10, ClientId = 20, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 100m, PaidAmount = 100m, SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker };
        var second = new Payment { Id = 3, InvoiceId = 10, ClientId = 20, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 50m, PaidAmount = 0m, SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker };
        var result = SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, first, second]);
        Assert.False(result.HasFullRecordedAmount);
        Assert.Equal(150m, result.Total);
    }

    [Fact]
    public void MissingOrMixedIdentityAndZeroAmountAreNotAccepted()
    {
        var invoice = Invoice(paid: 100m);
        invoice.InvoiceId = null;
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice]).HasFullRecordedAmount);
        invoice.InvoiceId = 10;
        var unrelated = new Payment { Id = 2, InvoiceId = 11, ClientId = 20, Type = PaymentType.Income, Amount = 100m, PaidAmount = 100m };
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, unrelated]).HasFullRecordedAmount);
        invoice.Amount = 0m;
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice]).HasFullRecordedAmount);
    }

    [Fact]
    public void CancelledInstallmentDoesNotProvidePayment()
    {
        var invoice = Invoice();
        var cancelled = new Payment { Id = 2, InvoiceId = 10, ClientId = 20, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 100m, PaidAmount = 100m, Status = PaymentStatus.Cancelled, SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker };
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, cancelled]).HasFullRecordedAmount);
        invoice.PaidAmount = 100m;
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [invoice, cancelled]).HasFullRecordedAmount);
    }

    [Fact]
    public void FractionalCentsAreNotRoundedIntoFullPayment()
    {
        Assert.False(SalesRequestInvoiceSettlement.Evaluate(10, 20, [Invoice(paid: 99.999m)]).HasFullRecordedAmount);
    }
}
