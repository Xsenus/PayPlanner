using System.Globalization;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BankStatementAmountRulesTests
{
    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("1234.56", "1234.56")]
    [InlineData("1234,56", "1234.56")]
    [InlineData("1 234,56", "1234.56")]
    [InlineData("1\u00a0234,56", "1234.56")]
    [InlineData("1\u202f234.56", "1234.56")]
    [InlineData("1,234.56", "1234.56")]
    [InlineData("1.234,56", "1234.56")]
    [InlineData("1,234,567.89", "1234567.89")]
    [InlineData("1.234.567,89", "1234567.89")]
    [InlineData("1,234,567", "1234567")]
    [InlineData("1.234.567", "1234567")]
    [InlineData("-1 234,56", "-1234.56")]
    [InlineData("+1234.5", "1234.5")]
    [InlineData("1 234,56 ₽", "1234.56")]
    [InlineData("1 234.56 руб.", "1234.56")]
    [InlineData("1 234,56 руб", "1234.56")]
    [InlineData("9999999999999999.99", "9999999999999999.99")]
    [InlineData("-9999999999999999.99", "-9999999999999999.99")]
    [InlineData("0.01", "0.01")]
    [InlineData("0,10", "0.10")]
    [InlineData("0", "0")]
    [InlineData("1 000", "1000")]
    public void UnambiguousAmountsPreserveEveryDigit(string source, string expected)
    {
        Assert.True(BankStatementImportRules.TryParseStatementAmount(source, out var amount));
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), amount);
    }

    [Theory]
    [InlineData("1,234")]
    [InlineData("1.234")]
    [InlineData("1234.567")]
    [InlineData("1234,567")]
    [InlineData("12.34.56")]
    [InlineData("1,23.45")]
    [InlineData("1.23,45")]
    [InlineData("12 34,56")]
    [InlineData("1 23 456")]
    [InlineData("1,234,56")]
    [InlineData("1.234.56")]
    [InlineData("1,234.567")]
    [InlineData("1.234,567")]
    [InlineData("1.2.3")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("123-")]
    [InlineData("--1")]
    [InlineData("10000000000000000")]
    [InlineData("10000000000000000.00")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("1.00руб.50")]
    [InlineData("")]
    [InlineData("руб.")]
    [InlineData("   ")]
    public void AmbiguousMalformedOrUnstorableAmountsAreRejectedWithoutPartialValue(string source)
    {
        Assert.False(BankStatementImportRules.TryParseStatementAmount(source, out var amount));
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void OversizedAmountCellIsRejectedWithoutOverflowOrRounding()
    {
        Assert.False(BankStatementImportRules.TryParseStatementAmount(new string('9', 10_000), out var amount));
        Assert.Equal(0m, amount);
    }
}
