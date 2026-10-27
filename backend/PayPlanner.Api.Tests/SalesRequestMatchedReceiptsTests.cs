using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SalesRequestMatchedReceiptsTests
{
    private static MoneyOperation Receipt(int id, decimal amount, MoneyOperationSource source = MoneyOperationSource.Bank) => new()
    {
        Id = id,
        LinkedInvoiceId = 10,
        Direction = MoneyOperationDirection.Income,
        Source = source,
        MatchStatus = MoneyOperationMatchStatus.Matched,
        MatchedAt = DateTime.UtcNow,
        Currency = "RUB",
        Amount = amount,
    };

    [Theory]
    [InlineData(MoneyOperationSource.Bank)]
    [InlineData(MoneyOperationSource.Cash)]
    [InlineData(MoneyOperationSource.Card)]
    [InlineData(MoneyOperationSource.EMoney)]
    public void MatchingReceiptSourcesCount(MoneyOperationSource source)
    {
        var result = SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [Receipt(1, 100m, source)]);
        Assert.True(result.HasEnoughMatchedAmount);
        Assert.Equal(100m, result.MatchedAmount);
    }

    [Fact]
    public void PartialUnmatchedAndExpenseCannotProduceFullReceipt()
    {
        var first = Receipt(1, 30m);
        var ignored = Receipt(2, 100m); ignored.MatchStatus = MoneyOperationMatchStatus.Ignored;
        var expense = Receipt(3, 100m); expense.Direction = MoneyOperationDirection.Expense;
        var result = SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, ignored, expense]);
        Assert.False(result.HasEnoughMatchedAmount);
        Assert.Equal(0m, result.MatchedAmount);
        result = SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, ignored]);
        Assert.False(result.HasEnoughMatchedAmount);
        Assert.Equal(30m, result.MatchedAmount);
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [Receipt(4, 100m), expense]).HasEnoughMatchedAmount);
    }

    [Fact]
    public void MixedCurrencyUnknownCurrencyAndUnmatchedDateFailClosed()
    {
        var receipt = Receipt(1, 100m);
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, null, 100m, [receipt]).HasEnoughMatchedAmount);
        receipt.Currency = "USD";
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [receipt]).HasEnoughMatchedAmount);
        receipt.Currency = "RUB"; receipt.MatchedAt = null;
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [receipt]).HasEnoughMatchedAmount);
    }

    [Fact]
    public void DuplicateOperationMovementOrWrongInvoiceFailClosed()
    {
        var first = Receipt(1, 100m); first.SourceMovementKey = "bank-unique";
        var second = Receipt(2, 100m); second.SourceMovementKey = "bank-unique";
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, first]).HasEnoughMatchedAmount);
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, second]).HasEnoughMatchedAmount);
        second.LinkedInvoiceId = 11; second.SourceMovementKey = null;
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, second]).HasEnoughMatchedAmount);
    }

    [Fact]
    public void MultiplePartialReceiptsCanCoverInvoice()
    {
        var result = SalesRequestMatchedReceipts.Evaluate(10, "rub", 100m, [Receipt(1, 40m), Receipt(2, 60m, MoneyOperationSource.Cash)]);
        Assert.True(result.HasEnoughMatchedAmount);
        Assert.Equal(100m, result.MatchedAmount);
    }

    [Fact]
    public void RefundReducesReceivedAmountWithoutHidingAdditionalReceipts()
    {
        var first = Receipt(1, 120m);
        var refund = Receipt(2, 20m); refund.Direction = MoneyOperationDirection.Expense;
        var result = SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, refund]);
        Assert.True(result.HasEnoughMatchedAmount);
        Assert.Equal(100m, result.MatchedAmount);
        refund.Amount = 21m;
        result = SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [first, refund]);
        Assert.False(result.HasEnoughMatchedAmount);
        Assert.Equal(99m, result.MatchedAmount);
    }

    [Theory]
    [InlineData(MoneyOperationAccountingType.Plan)]
    [InlineData((MoneyOperationAccountingType)99)]
    public void PlannedOrUnknownAccountingTypeCannotProvePayment(MoneyOperationAccountingType accountingType)
    {
        var planned = Receipt(2, 100m);
        planned.AccountingType = accountingType;
        var result = SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [Receipt(1, 100m), planned]);
        Assert.False(result.HasEnoughMatchedAmount);
        Assert.Equal(0m, result.MatchedAmount);
    }

    [Fact]
    public void FractionalCentsAreNotRoundedIntoProofOfPayment()
    {
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100m, [Receipt(1, 99.999m)]).HasEnoughMatchedAmount);
        Assert.False(SalesRequestMatchedReceipts.Evaluate(10, "RUB", 100.001m, [Receipt(1, 100m)]).HasEnoughMatchedAmount);
    }
}
