using PayPlanner.Api.Services.Tax;
using Xunit;

namespace PayPlanner.Api.Tests;

public class VatCalculatorTests
{
    [Theory]
    [InlineData(1220, "22", 220.00)]
    [InlineData(110, "10", 10.00)]
    [InlineData(105, "5", 5.00)]
    [InlineData(107, "7", 7.00)]
    [InlineData(1200, "20", 200.00)]
    [InlineData(1000, "0", 0.00)]
    [InlineData(1000, "none", 0.00)]
    [InlineData(1000, null, 0.00)]
    [InlineData(0, "22", 0.00)]
    public void IncludedVat_ExtractsShareFromGrossAmount(double total, string? rate, double expected)
        => Assert.Equal((decimal)expected, VatCalculator.IncludedVat((decimal)total, rate));

    [Fact]
    public void IncludedVat_RoundsHalfKopeckAwayFromZero()
    {
        // 0,05 * 22 / 122 = 0,009016… -> 0,01 ; 1,00 * 22 / 122 = 0,18033 -> 0,18
        Assert.Equal(0.01m, VatCalculator.IncludedVat(0.05m, "22"));
        Assert.Equal(0.18m, VatCalculator.IncludedVat(1.00m, "22"));
    }

    [Fact]
    public void Calculate_RoundsEachLineAndSumsTheRoundedValues()
    {
        // Три позиции по 0,10 при 22%: построчно 0,02 + 0,02 + 0,02 = 0,06, а от итога 0,30 было бы 0,05.
        var totals = VatCalculator.Calculate(
        [
            new VatLine(1m, 0.10m, "22"),
            new VatLine(1m, 0.10m, "22"),
            new VatLine(1m, 0.10m, "22"),
        ]);

        Assert.Equal(0.30m, totals.Total);
        Assert.Equal(0.06m, totals.Vat);
        Assert.True(totals.HasRates);
    }

    [Fact]
    public void Calculate_HandlesFractionalQuantityAndMixedRates()
    {
        var totals = VatCalculator.Calculate(
        [
            new VatLine(2.5m, 400m, "22"),   // 1000,00 -> 180,33
            new VatLine(1m, 110m, "10"),      // 110,00 -> 10,00
            new VatLine(1m, 500m, "none"),
            new VatLine(1m, 300m, null),
        ]);

        Assert.Equal(1910m, totals.Total);
        Assert.Equal(190.33m, totals.Vat);
        Assert.False(totals.AllWithoutVat);
    }

    [Fact]
    public void Calculate_FlagsDocumentWithoutRatesAndAllWithoutVat()
    {
        var legacy = VatCalculator.Calculate([new VatLine(1m, 100m, null)]);
        var exempt = VatCalculator.Calculate([new VatLine(1m, 100m, "none"), new VatLine(2m, 50m, "none")]);

        Assert.False(legacy.HasRates);
        Assert.True(exempt.AllWithoutVat);
        Assert.Equal(0m, exempt.Vat);
    }

    [Theory]
    [InlineData("22", "22")]
    [InlineData("22%", "22")]
    [InlineData(" 10 % ", "10")]
    [InlineData("Без НДС", "none")]
    [InlineData("NONE", "none")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("18", null)]
    public void Normalize_AcceptsCommonSpellings(string? input, string? expected)
        => Assert.Equal(expected, VatRates.Normalize(input));

    [Theory]
    [InlineData(null, true)]
    [InlineData("22", true)]
    [InlineData("none", true)]
    [InlineData("18", false)]
    [InlineData("22%", false)]
    public void IsValid_RequiresExactCode(string? code, bool expected)
        => Assert.Equal(expected, VatRates.IsValid(code));

    [Fact]
    public void ZeroRateAndNoVatAreDifferentLabels()
    {
        Assert.Equal("0%", VatRates.Label("0"));
        Assert.Equal("Без НДС", VatRates.Label("none"));
        Assert.Equal("—", VatRates.Label(null));
    }
}
